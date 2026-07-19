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

## egg-nimbus-proxy.json

The proxy process as a panel server: the install pulls the proxy bundle from the release
zip and writes `nimbus.proxy.toml` from the egg variables (bind on the panel's primary
allocation, a starter `[servers]` entry, the embedded registry's HTTP bind and shared
secret). Wings has no TOML parser, so later changes mean editing that file or
reinstalling; real networks grow the `[servers]` pool in the file directly. The proxy
refuses to start until the shared secret is changed from its default (its config
validator treats a non-loopback registry bind with a default secret as an error), which
makes the panel's variable screen the natural place to set it.

## egg-nimbus-registry.json

The standalone registry, for multi-proxy deployments (single-proxy networks should keep
the proxy's embedded registry and skip this egg). Release zips do not ship it, so the
install builds it from source (`NIMBUS_GIT_REPO` / `NIMBUS_GIT_REF`) in the .NET 10 SDK
container and publishes into the server folder.

## Reaching the registry between containers

Backends heartbeat to `NIMBUS_REGISTRY_URL`, and under Wings every server lives in its
own container, so how that URL resolves depends on your networking:

- **Via the node's address** (the default Wings setup): point `NIMBUS_REGISTRY_URL` at
  `http://<node-ip>:8765` and give the **proxy server a second allocation for port
  8765** in the panel; without that allocation the embedded registry's port is not
  exposed outside the proxy's container.
- **Direct container networking**: if your Wings config puts the containers on a shared
  Docker network, backends can reach the proxy's container directly (e.g.
  `http://<proxy-container-name>:8765`) and no extra allocation is needed.

The standalone registry egg does not have this concern: its bind uses the panel's
primary allocation, so it is already reachable via the node address.

## Maintaining

The `install-*.sh` files are the readable sources of each egg's embedded install
script; after editing one, regenerate the JSON files with:

```sh
cd eggs && python3 generate.py
```
