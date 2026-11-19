using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Render injects PORT; local runs can pass --urls or use 8080.
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(20)
});

var rooms = new ConcurrentDictionary<string, Room>(StringComparer.OrdinalIgnoreCase);

app.MapGet("/", () => Results.Text(
    "Flourish relay is running. Connect a launcher to /room/{code}?role=host|client"));
app.MapGet("/healthz", () => Results.Text("ok"));

app.Map("/room/{code}", async (HttpContext context, string code) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket required.");
        return;
    }

    if (!Room.IsValidCode(code))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Room codes use 3-24 letters, digits, or dashes.");
        return;
    }

    var role = context.Request.Query["role"].ToString();
    var isHost = string.Equals(role, "host", StringComparison.OrdinalIgnoreCase);
    if (!isHost && !string.Equals(role, "client", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("role must be host or client.");
        return;
    }

    var socket = await context.WebSockets.AcceptWebSocketAsync();
    var room = rooms.GetOrAdd(code, _ => new Room(code));

    if (isHost)
    {
        if (!room.TryAttachHost(socket))
        {
            await socket.CloseAsync(
                WebSocketCloseStatus.PolicyViolation,
                "This room already has a host.",
                CancellationToken.None);
            return;
        }

        await room.RunHostAsync(socket, context.RequestAborted);
    }
    else
    {
        var client = await room.AttachClientAsync(socket);
        if (client is not null)
        {
            await room.RunClientAsync(client, context.RequestAborted);
        }
    }

    if (room.IsEmpty)
    {
        rooms.TryRemove(code, out _);
    }
});

app.Run();

internal sealed class Room
{
    private readonly object _gate = new();
    private readonly Dictionary<uint, Peer> _clients = new();
    private Peer? _host;
    private uint _nextClientId = 1;

    public Room(string code)
    {
        Code = code;
    }

    public string Code { get; }

    public bool IsEmpty
    {
        get
        {
            lock (_gate)
            {
                return _host is null && _clients.Count == 0;
            }
        }
    }

    public static bool IsValidCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length is < 3 or > 24)
        {
            return false;
        }

        foreach (var character in code)
        {
            if (!char.IsLetterOrDigit(character) && character != '-')
            {
                return false;
            }
        }

        return true;
    }

    public bool TryAttachHost(WebSocket socket)
    {
        lock (_gate)
        {
            if (_host is not null)
            {
                return false;
            }

            _host = new Peer(0, socket, isHost: true);
            return true;
        }
    }

    public async Task<Peer?> AttachClientAsync(WebSocket socket)
    {
        Peer client;
        Peer? host;

        lock (_gate)
        {
            host = _host;
            if (host is null)
            {
                return null;
            }

            var id = _nextClientId++;
            client = new Peer(id, socket, isHost: false);
            _clients[id] = client;
        }

        await host.SendTextAsync(JsonSerializer.Serialize(new
        {
            type = "client-joined",
            clientId = client.Id
        }));

        await client.SendTextAsync(JsonSerializer.Serialize(new
        {
            type = "welcome",
            room = Code,
            clientId = client.Id
        }));

        return client;
    }

    public async Task RunHostAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[65_536];

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Binary || result.Count < 5)
                {
                    continue;
                }

                // Host -> relay: [0x02][4-byte client id][payload]
                if (buffer[0] != 0x02)
                {
                    continue;
                }

                var clientId = BitConverter.ToUInt32(buffer, 1);
                Peer? client;
                lock (_gate)
                {
                    _clients.TryGetValue(clientId, out client);
                }

                if (client is null)
                {
                    continue;
                }

                var payload = new byte[result.Count - 5];
                Buffer.BlockCopy(buffer, 5, payload, 0, payload.Length);

                // Relay -> client: [0x02][payload]. The client tunnel uses the
                // opcode to tell game traffic apart from future control frames.
                var outFrame = new byte[payload.Length + 1];
                outFrame[0] = 0x02;
                Buffer.BlockCopy(payload, 0, outFrame, 1, payload.Length);

                await client.SendBinaryAsync(outFrame);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch
        {
            // A broken socket just ends the room host.
        }
        finally
        {
            Peer? oldHost;
            lock (_gate)
            {
                oldHost = _host;
                _host = null;
            }

            if (oldHost is not null)
            {
                await oldHost.CloseAsync("Host disconnected.");
            }

            await CloseAllClientsAsync("Host disconnected.");
        }
    }

    public async Task RunClientAsync(Peer client, CancellationToken cancellationToken)
    {
        var buffer = new byte[65_536];

        try
        {
            while (client.Socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await client.Socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Binary || result.Count == 0)
                {
                    continue;
                }

                Peer? host;
                lock (_gate)
                {
                    host = _host;
                }

                if (host is null)
                {
                    continue;
                }

                // Client -> relay: raw payload. Relay -> host:
                // [0x01][4-byte client id][payload]
                var frame = new byte[result.Count + 5];
                frame[0] = 0x01;
                BitConverter.TryWriteBytes(frame.AsSpan(1, 4), client.Id);
                Buffer.BlockCopy(buffer, 0, frame, 5, result.Count);
                await host.SendBinaryAsync(frame);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch
        {
            // The peer went away.
        }
        finally
        {
            lock (_gate)
            {
                _clients.Remove(client.Id);
            }

            Peer? host;
            lock (_gate)
            {
                host = _host;
            }

            if (host is not null)
            {
                await host.SendTextAsync(JsonSerializer.Serialize(new
                {
                    type = "client-left",
                    clientId = client.Id
                }));
            }
        }
    }

    private async Task CloseAllClientsAsync(string reason)
    {
        Peer[] clients;
        lock (_gate)
        {
            clients = _clients.Values.ToArray();
            _clients.Clear();
        }

        foreach (var client in clients)
        {
            await client.CloseAsync(reason);
        }
    }
}

internal sealed class Peer
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public Peer(uint id, WebSocket socket, bool isHost)
    {
        Id = id;
        Socket = socket;
        IsHost = isHost;
    }

    public uint Id { get; }
    public WebSocket Socket { get; }
    public bool IsHost { get; }

    public async Task SendTextAsync(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await SendAsync(bytes, WebSocketMessageType.Text);
    }

    public async Task SendBinaryAsync(byte[] payload)
    {
        await SendAsync(payload, WebSocketMessageType.Binary);
    }

    private async Task SendAsync(byte[] payload, WebSocketMessageType type)
    {
        if (Socket.State != WebSocketState.Open)
        {
            return;
        }

        await _sendGate.WaitAsync();
        try
        {
            if (Socket.State == WebSocketState.Open)
            {
                await Socket.SendAsync(payload, type, endOfMessage: true, CancellationToken.None);
            }
        }
        catch
        {
            // The peer closed while the packet was in flight.
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task CloseAsync(string reason)
    {
        try
        {
            if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
            }
        }
        catch
        {
            // Already gone.
        }
    }
}
