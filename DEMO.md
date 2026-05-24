# Local browser multiplayer demo

Proves the existing authoritative server kernel can be driven by real browser
clients over a WebSocket adapter. Everything is in-process and in-memory — no AWS,
Redis, database, auth, or scaling.

## Run

```
dotnet run --project src/GameServer.Host
```

The host listens on `http://localhost:5000` and serves the static client from
`wwwroot`. The WebSocket endpoint is `ws://localhost:5000/ws`.

## Steps

1. Run the host with the command above.
2. Open `http://localhost:5000` in two browser tabs (tab A and tab B).
3. Click **Connect** in both tabs (each gets a `ServerWelcome`). Each tab auto-generates a random player id.
4. Keep the defaults `tenant-a` / `demo-game` / `room-1` in both tabs, then click **Join Room** in both.
5. Click **Move Right** in tab A.
6. Both tabs show tab A's player X increase by 1 in the authoritative state table.
7. Click **Move Right** in tab B.
8. Both tabs show tab B's player X increase by 1.

The authoritative state table is keyed by player id, so each tab sees every
player's server-confirmed position, not just its own.

## Validation commands

```
dotnet test
```

```
dotnet run --project src/GameServer.Host
```
