# Nimbus

[![Stars](https://img.shields.io/github/stars/StratumServer/Nimbus?style=flat&logo=github)](https://github.com/StratumServer/Nimbus/stargazers)
[![Issues](https://img.shields.io/github/issues/StratumServer/Nimbus?logo=github)](https://github.com/StratumServer/Nimbus/issues)
[![Last commit](https://img.shields.io/github/last-commit/StratumServer/Nimbus?logo=github)](https://github.com/StratumServer/Nimbus/commits)
[![Discord](https://img.shields.io/badge/chat-on%20discord-5865F2?logo=discord&logoColor=white)](https://discord.gg/pd24fawhsD)
[![License](https://img.shields.io/badge/license-blue)](LICENSE)
[![Support on OpenCollective](https://img.shields.io/badge/Support-OpenCollective-7FADF2?logo=opencollective\&logoColor=white)](https://opencollective.com/stratum)

A [Velocity](https://papermc.io/software/velocity)-style proxy for [Vintage Story](https://www.vintagestory.at/). Run many game servers behind one address and move players between them at runtime.

## Components

| Component | Description |
| --- | --- |
| **Nimbus.Proxy** | The proxy process. Fronts all backends on a single address. Handles routing, transfers, admin, plugins, metrics, and the embedded registry. |
| **Nimbus.ServerMod** | VS server-side mod. Installed on each backend. Sends heartbeats, enforces forwarding, and exposes player transfer commands. |
| **Nimbus.Registry** | Standalone registry exe for multi-proxy deployments. For single-proxy setups the registry runs embedded inside the proxy. |
| **nimctl** | CLI for the proxy admin socket. List players, transfer sessions, drain backends, reload config. |

## Quick start

See the **[Getting Started guide](https://github.com/StratumServer/Nimbus/wiki/Getting-Started)** for a full walkthrough.

The short version:

1. Run `Nimbus.Proxy` 0 a config file is written on first run.
2. Add your VS servers to `[servers]` in `nimbus.proxy.toml`.
3. Install `Nimbus.ServerMod` on each backend, fill in `nimbus-server.json`.
4. Distribute [RedirectFix](https://github.com/StratumServer/redirectfix) to your players.

## Addresses: who connects where

Three different addresses exist in a Nimbus network, and mixing them up is the most
common misconfiguration:

| Setting | Lives in | Means |
|---------|----------|-------|
| `bind` | `nimbus.proxy.toml` | The address **players** connect to. The only address you publish. |
| `PublicHost` / `PublicPort` | `nimbus-server.json` (each backend) | The address **the network** reaches that backend on: the proxy dials it for seamless transfers, admin `swap` uses it, and it is stamped into redirect packets. It must be reachable from the proxy; it does not need to be reachable by players. |
| `identity.public_host` / `public_port` | registry config | The **proxy's** public address, advertised to the VS master server when `advertise_on_master_server` is on. |

Note on redirects: the redirect packet carries the backend's `PublicHost`, but
[RedirectFix](https://github.com/StratumServer/redirectfix) clients reconnect to the
proxy's cached address and a staged sticky route sends them to the right backend, so the
stamped host is not what the client actually dials today. Keep backends unreachable from
the internet and force everything through the proxy with `ReservationRequired`; the
client-mod-free path (#18) will revisit what the redirect packet should carry.

## Wiki

Full documentation lives in the [wiki](https://github.com/StratumServer/Nimbus/wiki):

- [Getting Started](https://github.com/StratumServer/Nimbus/wiki/Getting-Started)
- [Configuration reference](https://github.com/StratumServer/Nimbus/wiki/Configuration)
- [Server Mod](https://github.com/StratumServer/Nimbus/wiki/Server-Mod)
- [Transfers](https://github.com/StratumServer/Nimbus/wiki/Transfers)
- [Forwarding](https://github.com/StratumServer/Nimbus/wiki/Forwarding)
- [Admin Commands](https://github.com/StratumServer/Nimbus/wiki/Admin-Commands)
- [Plugin Development](https://github.com/StratumServer/Nimbus/wiki/Plugin-Development)
- [Plugin Examples](https://github.com/StratumServer/Nimbus/wiki/Plugin-Examples)

## Building

Requires the .NET 10 SDK.

```shell
dotnet build Nimbus.slnx -c Release
```

## License

See [LICENSE](LICENSE). Source-available, no resale, no rebrand-and-redistribute.

Nimbus is an unofficial third-party project. Not affiliated with or endorsed by Anego Studios. "Vintage Story" is a trademark of Anego Studios. See [NOTICE](NOTICE) for the full attribution.
