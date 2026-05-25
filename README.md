# Nimbus

A proxy and control plane for running multiple [Vintage Story](https://www.vintagestory.at/) servers behind one address.

Nimbus is early. Phase 1 (registry + reservations + vanilla redirect packet) is working and in use. Phase 2 (the real TCP/UDP proxy) is functional but not production-ready yet. The project takes a lot of inspiration from [Velocity](https://github.com/PaperMC/Velocity), but it isn't anywhere near that level of maturity.

## What it does

- **Registry.** A small ASP.NET Core service that backends heartbeat into. Tracks online players, capacity, tags, and version info per backend.
- **Reservations.** Short-lived single-use tokens that let a player join a specific backend without re-running auth against the wider network.
- **Redirect path.** Uses the vanilla `Packet_ServerRedirect` so unmodded clients can be sent between backends with no mod install.
- **Proxy (phase 2).** TCP/UDP relay that speaks the VS protocol, so a player keeps one address and can be swapped between backends without a full world reload.
- **`nimctl`.** A small CLI to drive the proxy's admin socket (list sessions, swap a player, drain a backend, etc).

## What it doesn't do

- No cross-server inventory or character sync. Each backend keeps its own savegame.
- It doesn't replace Vintage Story's auth. Vanilla auth still runs on every backend. Reservations are an extra gate on top.
- It is not a one-click install. You need a backend integration layer that hooks the server's login path (see below).

## Layout

```
Nimbus.Shared/     protocol DTOs, HMAC signing, version constants
Nimbus.Registry/   ASP.NET Core service. backends heartbeat in, reservations get minted here
Nimbus.Proxy/      TCP/UDP proxy
Nimbus.Cli/        small admin client (nimctl) for the proxy
```

## Building

Requires the .NET 10 SDK.

```powershell
dotnet build Nimbus.slnx -c Release
```

Or build a single project:

```powershell
dotnet build .\Nimbus.Registry\Nimbus.Registry.csproj -c Release
dotnet build .\Nimbus.Proxy\Nimbus.Proxy.csproj -c Release
```

## Running the registry

```powershell
cd Nimbus.Registry
dotnet run -c Release
```

The first run writes `nimbus.registry.json` next to the binary with a default `SharedSecret`. Change it before exposing the port.

Default bind is `http://0.0.0.0:8765`. Put it behind a reverse proxy with TLS if it's reachable from the public internet.

## Running the proxy

```powershell
cd Nimbus.Proxy
dotnet run -c Release
```

Edit `nimbus.proxy.json` to point at your registry and set the listen ports.

## Backend integration

Backends have to do two things:

1. Send a heartbeat to the registry every few seconds with their current player count, capacity, tags, and version. The payload is `BackendHeartbeat` in `Nimbus.Shared`.
2. Hook the server's login path so that when `RequireReservationForJoin` is on, the backend asks the registry to consume a matching reservation before accepting the join. If the registry says no, the backend rejects the connection.

`Nimbus.Shared` carries the DTOs and HMAC signer so you don't have to reimplement either. Every request to the registry needs the four `X-Nimbus-*` headers (`Signature`, `Timestamp`, `Nonce`, `Protocol`).

There isn't a drop-in mod for vanilla servers yet. I run this against a private fork; getting it to work against a stock server build is on the list.

## Security model

- HMAC-SHA256 on every registry request. Signature covers method, path, protocol version, timestamp, nonce, and SHA-256 of the body.
- 30 second clock skew window. Requests outside that are rejected even with a valid signature.
- Nonces are remembered for 90 seconds. Replay returns 401.
- Secret rotation: put the new secret in `AcceptedSecrets`, redeploy backends, promote it to `SharedSecret`, drop the old one.
- Reservations are single-use and pinned to one target backend.

## Status

| Area | State |
|------|-------|
| Registry + HMAC + nonces | working |
| Reservations (mint, consume by id, consume by uid) | working |
| Vanilla redirect packet path | working |
| `nimctl` over the proxy admin socket | working |
| TCP proxy (frame parsing, identification, swap) | working, needs more soak |
| UDP relay | working, needs more soak |
| Multi-player-per-IP UDP | not yet (single player per source IP assumed) |
| Cross-server character/inventory sync | not planned here |

## Discussion

If you find something broken or want to talk about a design choice, open an issue.

## License

See [LICENSE](LICENSE). Source-available, no resale, no rebrand-and-redistribute.
