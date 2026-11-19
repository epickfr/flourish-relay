# Deploying the Flourish relay on Render

This folder is a standalone ASP.NET Core WebSocket relay. It lets 2016 clients
play together when the host cannot expose a UDP port, without pretending that
Render can run `RCCService.exe`.

## One-click deploy from the launcher

In **CLOUD RELAY · RENDER**, press **Deploy relay** and paste a GitHub token
with the `repo` scope plus a Render API key. The launcher creates a public
GitHub repository for these files, creates or reuses a free Render Docker web
service, waits for the first deploy, and fills in the `wss://` URL. The tokens
are saved in `%AppData%\Flourish\launcher.json` on that PC.

The manual steps below remain available when you want to control the
repository or service yourself.

## What the relay does

- The host launcher keeps running the real 2016 server (`RCCService.exe`) on
  the host PC and opens a WebSocket **host** connection to `/room/{code}`.
- Every player's launcher opens a WebSocket **client** connection to the same
  room and a local UDP listener. The 2016 client connects to
  `127.0.0.1:<local port>` as if the server were local.
- The host launcher wraps each remote player in its own local UDP socket, so
  the 2016 server sees normal RakNet peers and the roster stays accurate.

Render only carries WebSocket frames. It never executes the game server, and
the `.rbxl` place still comes from the host's local content folder.

## Deploy

### Option A: Render Blueprint

1. Put this `render-relay` folder in its own Git repository (or a repository
   whose root is this folder).
2. In Render, choose **New > Blueprint** and select the repository.
3. Render reads `render.yaml` and creates the `flourish-relay` Docker service.
4. Wait for the deploy, then open
   `https://<service-name>.onrender.com/healthz` and confirm it returns `ok`.

### Option B: Manual Docker service

1. **New > Web Service**, connect the repository.
2. Runtime: **Docker**. Dockerfile path: `Dockerfile`.
3. Plan: Free for testing, Starter for sessions that must stay warm.
4. Health check path: `/healthz`.
5. Deploy. Render injects `PORT`; the relay listens on it automatically.

The service exposes:

| Route | Purpose |
| --- | --- |
| `GET /` | Human-readable status text |
| `GET /healthz` | Health check used by Render |
| `WS /room/{code}?role=host` | One host per room |
| `WS /room/{code}?role=client` | Any number of players per room |

Room codes are 3-24 letters, digits, or dashes. The first `host` connection
owns the room; a second host is rejected. When the host leaves, the room closes
and clients are disconnected. Empty rooms are removed automatically.

## Use it from the launcher

Host:

1. Select a place on the Home tab.
2. In **CLOUD RELAY · RENDER**, paste the relay URL, for example
   `wss://flourish-relay.onrender.com`.
3. Enter a room code or leave it blank to generate one.
4. Press **Host through relay**. It starts the local 2016 server if needed and
   then publishes it. The host PC must stay online and the launcher must stay
   open.
5. Press **Copy invite** and send the text to friends (Discord, etc.).

Friend:

1. Open the same relay URL and room code.
2. Press **Join through relay**. The launcher opens a local UDP tunnel and then
   launches the normal 2016 client.
3. The roster updates when the 2016 server accepts the connection.

The host can play too: press **Join through relay** on the host PC. It uses the
same route as everyone else, so roster counts remain honest.

## Free-plan caveats

- Render free web services spin down after about 15 minutes without traffic.
  Active play keeps the WebSocket busy, so sessions stay up. The **first**
  connection after a spin-down can take 30-60 seconds and may exceed the
  launcher's 20-second connect timeout. Open `https://.../healthz` in a browser
  first, wait for `ok`, then press **Join through relay**.
- Free instances have limited CPU. The relay only forwards small UDP datagrams,
  but a busy room still benefits from a paid instance.
- WebSocket connections are capped by Render's per-service limits; a friend
  group of 10 is fine.
- Render does not provide a static outbound IP. Nothing in this protocol needs
  one.

## Security notes

- TLS protects each hop. The relay can see game traffic because it terminates
  both WebSockets; do not treat a room code as strong authentication.
- Anyone with the room code can join while the host is connected. Use a fresh
  code for each session.
- The relay never stores place files, player names, or chat. It only routes
  binary frames for the lifetime of a room.
- Do not run the 2016 server directly on a public IP without isolation. The
  relay is the safer option for friends because only the host's outbound
  WebSocket is exposed.

## Local smoke test

```powershell
dotnet run --project .\FlourishRelay.csproj -c Release --urls http://127.0.0.1:8099
```

Then run the launcher self-test:

```powershell
$env:FLOURISH_MP_RELAY_URL = 'ws://127.0.0.1:8099'
$env:FLOURISH_MP_RELAY_ROOM = 'selftest'
& '..\bin\AgentTest\FlourishLauncher.exe' --mp-test
```

A passing run prints `RESULT: PASS` and exercises
`0/10 -> 1/10 -> 2/10 -> 1/10 -> 0/10` through the relay.
