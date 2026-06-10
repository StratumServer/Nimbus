# Nimbus

[![Stars](https://img.shields.io/github/stars/trevorftp/Nimbus?style=flat&logo=github)](https://github.com/trevorftp/Nimbus/stargazers)
[![Issues](https://img.shields.io/github/issues/trevorftp/Nimbus?logo=github)](https://github.com/trevorftp/Nimbus/issues)
[![Last commit](https://img.shields.io/github/last-commit/trevorftp/Nimbus?logo=github)](https://github.com/trevorftp/Nimbus/commits)
[![Discord](https://img.shields.io/badge/chat-on%20discord-5865F2?logo=discord&logoColor=white)](https://discord.gg/pd24fawhsD)
[![License](https://img.shields.io/badge/license-blue)](LICENSE)
[![Support on Ko-fi](https://img.shields.io/badge/Support_on_Ko--fi-ff5f5f?logo=ko-fi&logoColor=white)](https://ko-fi.com/imtsubaki)

A [Velocity](https://papermc.io/software/velocity)-style proxy for [Vintage Story](https://www.vintagestory.at/). Run many game servers behind one address and move players between them at runtime.

## Components

| Component | Description |
| --- | --- |
| **Nimbus.Proxy** | The proxy process. Fronts all backends on a single address. Handles routing, transfers, admin, plugins, metrics, and the embedded registry. |
| **Nimbus.ServerMod** | VS server-side mod. Installed on each backend. Sends heartbeats, enforces forwarding, and exposes player transfer commands. |
| **Nimbus.Registry** | Standalone registry exe for multi-proxy deployments. For single-proxy setups the registry runs embedded inside the proxy. |
| **nimctl** | CLI for the proxy admin socket. List players, transfer sessions, drain backends, reload config. |

## Quick start

See the **[Getting Started guide](https://github.com/trevorftp/Nimbus/wiki/Getting-Started)** for a full walkthrough.

The short version:

1. Run `Nimbus.Proxy` 0 a config file is written on first run.
2. Add your VS servers to `[servers]` in `nimbus.proxy.toml`.
3. Install `Nimbus.ServerMod` on each backend, fill in `nimbus-server.json`.
4. Distribute [RedirectFix](https://github.com/trevorftp/redirectfix) to your players.

## Wiki

Full documentation lives in the [wiki](https://github.com/trevorftp/Nimbus/wiki):

- [Getting Started](https://github.com/trevorftp/Nimbus/wiki/Getting-Started)
- [Configuration reference](https://github.com/trevorftp/Nimbus/wiki/Configuration)
- [Server Mod](https://github.com/trevorftp/Nimbus/wiki/Server-Mod)
- [Transfers](https://github.com/trevorftp/Nimbus/wiki/Transfers)
- [Forwarding](https://github.com/trevorftp/Nimbus/wiki/Forwarding)
- [Admin Commands](https://github.com/trevorftp/Nimbus/wiki/Admin-Commands)
- [Plugin Development](https://github.com/trevorftp/Nimbus/wiki/Plugin-Development)
- [Plugin Examples](https://github.com/trevorftp/Nimbus/wiki/Plugin-Examples)

## Building

Requires the .NET 10 SDK.

```shell
dotnet build Nimbus.slnx -c Release
```

## License

See [LICENSE](LICENSE). Source-available, no resale, no rebrand-and-redistribute.

Nimbus is an unofficial third-party project. Not affiliated with or endorsed by Anego Studios. "Vintage Story" is a trademark of Anego Studios. See [NOTICE](NOTICE) for the full attribution.
