# Nimbus

[![Stars](https://img.shields.io/github/stars/trevorftp/Nimbus?style=flat&logo=github)](https://github.com/trevorftp/Nimbus/stargazers)
[![Issues](https://img.shields.io/github/issues/trevorftp/Nimbus?logo=github)](https://github.com/trevorftp/Nimbus/issues)
[![Last commit](https://img.shields.io/github/last-commit/trevorftp/Nimbus?logo=github)](https://github.com/trevorftp/Nimbus/commits)
[![Discord](https://img.shields.io/badge/chat-on%20discord-5865F2?logo=discord&logoColor=white)](https://discord.gg/pd24fawhsD)
[![License](https://img.shields.io/badge/license-blue)](LICENSE)
[![Support on Ko-fi](https://img.shields.io/badge/Support_on_Ko--fi-ff5f5f?logo=ko-fi&logoColor=white)](https://ko-fi.com/imtsubaki)

A [Velocity](https://papermc.io/software/velocity)-style proxy for [Vintage Story](https://www.vintagestory.at/).

Put many backends behind one address, move players between them on demand, and present the whole thing as a single entry on the server list.

## Status

Nimbus is working proxy infrastructure, not a finished public network stack. The default shape is now one `Nimbus.Proxy` process with the registry embedded in-process. A standalone `Nimbus.Registry` exe still exists for multi-proxy or shared-control-plane deployments.

Current production-facing pieces:

- TCP and UDP proxying for Vintage Story clients.
- Named backend pool with ordered `try` failover.
- Drain and undrain, with drain flags persisted to disk by default.
- Redirect transfers through vanilla reconnect plus sticky routing.
- Embedded or remote HMAC registry for backend heartbeats, reservations, and transfer intents.
- Line-JSON admin socket with command permissions.
- `nimctl` admin client.
- Plugin loading from `plugins/*.dll`.
- Prometheus text metrics on `http://127.0.0.1:42500/metrics` by default.

Still planned:

- Proxy-side direct-connect status response.
- Typed command parsing and an in-game command path.
- Plugin metadata and dependency resolution.
- Nimbus client/server mod protocol for true mid-session world handoff.

Two transfer modes:

- **redirect** (default): forge a vanilla `Redirect` packet. The client reconnects to the proxy, and a sticky route sends it to the chosen backend. Requires the [RedirectFix](https://github.com/trevorftp/redirectfix) client mod to avoid a vanilla `ExitAndSwitchServer` crash.
- **seamless** (opt-in, planned to require the Nimbus client+server mod): splice a fresh upstream TCP into the live client session without disconnecting. Off by default until the mod ships.

## How it fits together

Nimbus runs as a single process by default: **Nimbus.Proxy** with the registry hosted in-process.

- **Proxy** - TCP/UDP relay. Fronts every backend on one address. Handles routing, drain, redirect transfers, admin commands, plugins, metrics, and the embedded registry host.
- **Registry** - the control plane. Available as a library (`Nimbus.Registry.Core`) used by the embedded host inside the proxy, and as a thin standalone `Nimbus.Registry` exe for multi-proxy or shared-control-plane setups. Backends heartbeat in. Mints short-lived reservations so a player can land on a specific backend without re-running auth.
- **`nimctl`** - CLI for the proxy's admin socket. List sessions, swap a player, drain a backend.

When advertising is enabled, the registry can post the whole network as one entry on the public VS server list, with aggregated player count and required mods pulled from live backends.

## Building

Requires the .NET 10 SDK.

```powershell
dotnet build Nimbus.slnx -c Release
```

## Running

Single-process (default):

```powershell
cd Nimbus.Proxy; dotnet run -c Release
```

With a standalone registry (multi-proxy or shared control plane):

```powershell
cd Nimbus.Registry; dotnet run -c Release
cd Nimbus.Proxy;    dotnet run -c Release
```

First run writes a TOML config next to each binary (`nimbus.proxy.toml`, `nimbus.registry.toml`). The proxy config is Velocity-shaped: top-level `bind`, a `[servers]` dict of `name = "host:port"`, a top-level `try = ["name", ...]` list, and sections for `[transfers]`, `[admin]`, `[registry]`, `[metrics]`, and `[persistence]`.

Startup validates config before sockets open. Set `embedded_shared_secret` before exposing embedded registry HTTP, set `shared_secret` for remote registry mode, and set `admin.secret` before binding admin off loopback. A legacy `nimbus.proxy.json` is renamed to `.json.obsolete` on first run.

Useful defaults:

- Admin: `127.0.0.1:42499`
- Metrics: `http://127.0.0.1:42500/metrics`
- Drain state: `nimbus.drain-state.json` next to the proxy binary

## Backend integration

Backends need to do two things:

1. Heartbeat to the registry (`BackendHeartbeat` in `Nimbus.Shared`).
2. On login, ask the registry to consume a reservation for the joining player and accept the join if it matches.

`Nimbus.Shared` carries the DTOs and the HMAC signer; you don't have to reimplement either. Every registry request needs the four `X-Nimbus-*` headers.

There is no drop-in mod for stock servers yet. Nimbus runs against a private fork; a public integration layer is on the list.

## Caveats

- One player per source IP for UDP (NAT'd LAN parties not supported yet).
- Redirect transfers rely on clients having RedirectFix.
- Seamless transfer is still gated behind `transfers.allow_seamless` and is not the recommended mode until the Nimbus client/server mod exists.

## Discord

Questions, bug reports, design arguments: [discord.gg/pd24fawhsD](https://discord.gg/pd24fawhsD).

## License

See [LICENSE](LICENSE). Source-available, no resale, no rebrand-and-redistribute.

Nimbus is an unofficial third-party project. Not affiliated with or endorsed by Anego Studios. "Vintage Story" is a trademark of Anego Studios. See [NOTICE](NOTICE) for the full attribution.
