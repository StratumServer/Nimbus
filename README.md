# Nimbus

[![Discord](https://img.shields.io/badge/discord-join-5865F2?logo=discord&logoColor=white)](https://discord.gg/pd24fawhsD)
[![Stars](https://img.shields.io/github/stars/trevorftp/Nimbus?style=flat&logo=github)](https://github.com/trevorftp/Nimbus/stargazers)
[![Issues](https://img.shields.io/github/issues/trevorftp/Nimbus?logo=github)](https://github.com/trevorftp/Nimbus/issues)
[![Last commit](https://img.shields.io/github/last-commit/trevorftp/Nimbus?logo=github)](https://github.com/trevorftp/Nimbus/commits)
[![License](https://img.shields.io/badge/license-source--available-blue)](LICENSE)

A [Velocity](https://papermc.io/software/velocity)-style proxy for [Vintage Story](https://www.vintagestory.at/).

Put many backends behind one address, move players between them on demand, and present the whole thing as a single entry on the server list. The transfer is a quick disconnect/reconnect to the next backend; the vanilla protocol doesn't give us a soft-respawn, so a Velocity-grade seamless swap isn't on the table. Everything else is.

## How it fits together

- **Registry** — small ASP.NET Core service. Backends heartbeat in. Mints short-lived reservations so a player can land on a specific backend without re-running auth.
- **Proxy** — TCP/UDP relay. Fronts every backend on one address. Health-aware routing, drain, swap, redirect, disconnect-transfer. Forwards the real client IP to the backend over the reservation.
- **`nimctl`** — CLI for the proxy's admin socket. List sessions, swap a player, drain a backend.

The registry can also advertise the whole network as one entry on the public VS server list, with aggregated player count and required mods pulled from live backends.

## Building

Requires the .NET 10 SDK.

```powershell
dotnet build Nimbus.slnx -c Release
```

## Running

```powershell
cd Nimbus.Registry; dotnet run -c Release
cd Nimbus.Proxy;    dotnet run -c Release
```

First run writes a config next to each binary. Change the `SharedSecret` before exposing anything.

## Backend integration

Backends need to do two things:

1. Heartbeat to the registry (`BackendHeartbeat` in `Nimbus.Shared`).
2. On login, ask the registry to consume a reservation for the joining player and accept the join if it matches.

`Nimbus.Shared` carries the DTOs and the HMAC signer; you don't have to reimplement either. Every registry request needs the four `X-Nimbus-*` headers.

There is no drop-in mod for stock servers yet. Nimbus runs against a private fork; a public integration layer is on the list.

## Caveats

- The vanilla client has a bug in `ClientMain.ExitAndSwitchServer` that crashes the next session after a redirect. Until upstream patches it, clients need [RedirectFix](https://github.com/trevorftp/redirectfix). It auto-installs from the backend's required-mods list on first join, so for players this is transparent.
- One player per source IP for UDP (NAT'd LAN parties not supported yet).

## Discord

Questions, bug reports, design arguments: [discord.gg/pd24fawhsD](https://discord.gg/pd24fawhsD).

## License

See [LICENSE](LICENSE). Source-available, no resale, no rebrand-and-redistribute.

Nimbus is an unofficial third-party project. Not affiliated with or endorsed by Anego Studios. "Vintage Story" is a trademark of Anego Studios. See [NOTICE](NOTICE) for the full attribution.
