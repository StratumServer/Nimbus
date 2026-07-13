# Nimbus panel eggs

Server definitions ("eggs") for [Pelican](https://pelican.dev) and
[Pterodactyl](https://pterodactyl.io), so hosts can deploy Nimbus-backed Vintage Story
servers straight from their panel. The two panels share the egg schema and the Wings
daemon protocol, so each file imports into either one unchanged.

## egg-vintage-story-nimbus-backend.json

A Vintage Story dedicated server with the Nimbus backend mod preinstalled: the install
script downloads the game server from the official CDN and the `Nimbus.ServerMod` folder
mod from the Nimbus release zip, then writes an initial `nimbus-server.json`. On every
boot the panel re-stamps that config from the egg variables, so the panel stays the
source of truth for:

| Variable | Meaning |
|----------|---------|
| `VS_VERSION` | Game version to install (1.19+ for Nimbus; the mod is tested on 1.22) |
| `NIMBUS_DOWNLOAD_URL` | Nimbus release zip to pull the mod from |
| `NIMBUS_SERVER_ID` | This backend's id; must match the proxy's `[servers]` entry |
| `NIMBUS_REGISTRY_URL` | The registry (the proxy's embedded bind, or standalone) |
| `NIMBUS_SHARED_SECRET` | The network's HMAC secret |
| `NIMBUS_PUBLIC_HOST` | Host this backend advertises to the registry |
| `NIMBUS_RESERVATION_REQUIRED` | Kick players that bypass the proxy (recommended) |

The game port comes from the panel's primary allocation (`--port` at startup, and
`PublicPort` in the mod config).

Import it via Admin → Nests/Eggs → Import Egg (Pterodactyl) or Admin → Eggs → Import
(Pelican), assign an allocation, set the variables, done. Runs on
`ghcr.io/parkervcp/yolks:dotnet_10` (Vintage Story 1.22+ requires the .NET 10 runtime).

## Maintaining

`install-vs-nimbus.sh` is the readable source of the egg's embedded install script; if
you change it, regenerate the JSON (the script is embedded verbatim in
`scripts.installation.script`).

Not covered yet: an egg for the proxy process itself and one for the standalone
registry. Both are plain .NET console apps, so they are straightforward follow-ups once
this shape is agreed on.
