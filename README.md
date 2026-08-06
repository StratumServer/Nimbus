# Nimbus

[![Release](https://img.shields.io/github/v/release/StratumServer/Nimbus?logo=github)](https://github.com/StratumServer/Nimbus/releases/latest)
[![CI](https://img.shields.io/github/actions/workflow/status/StratumServer/Nimbus/ci.yml?branch=main&logo=github&label=ci)](https://github.com/StratumServer/Nimbus/actions/workflows/ci.yml)
[![Quality gate](https://sonarcloud.io/api/project_badges/measure?project=StratumServer_Nimbus&metric=alert_status)](https://sonarcloud.io/summary/new_code?id=StratumServer_Nimbus)
[![Coverage](https://sonarcloud.io/api/project_badges/measure?project=StratumServer_Nimbus&metric=coverage)](https://sonarcloud.io/summary/new_code?id=StratumServer_Nimbus)
[![Mod DB](https://img.shields.io/badge/mod%20db-nimbusproxy-90c95b)](https://mods.vintagestory.at/nimbusproxy)
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

## Download

Grab the [latest release](https://github.com/StratumServer/Nimbus/releases/latest). Built and tested against Vintage Story 1.22.6.

| Asset | Contents |
| --- | --- |
| `Nimbus-vX.Y.Z.zip` | The full bundle: proxy, standalone registry, ServerMod, license files. |
| `Nimbus.ServerMod-vX.Y.Z.zip` | Just the server mod, also published on the [mod DB](https://mods.vintagestory.at/nimbusproxy). |

Running on a game panel (Pterodactyl/Pelican)? Ready-made eggs for the proxy, the
registry, and a VS-backend-with-mod server live in [`eggs/`](eggs/).

## Quick start

See the **[Getting Started guide](https://github.com/StratumServer/Nimbus/wiki/Getting-Started)** for a full walkthrough.

The short version:

1. Run `Nimbus.Proxy`: a config file is written on first run, and the proxy starts.
2. Add your VS servers to `[servers]` in `nimbus.proxy.toml`.
3. Install `Nimbus.ServerMod` on each backend and fill in `nimbus-server.json`, including the
   `registry.embedded_shared_secret` the first run generated.
4. Distribute [RedirectFix](https://mods.vintagestory.at/show/mod/52239) to your players.

That first run picks its own `registry.embedded_shared_secret`, so no two installs share one and
nothing published in these docs opens yours. It is the credential every backend authenticates to
the registry with, and copying it into each `nimbus-server.json` is what step 3 above is mostly
about. Nothing regenerates it afterwards. A standalone `Nimbus.Registry` does the same on its own
first run, generating the `shared_secret` in `nimbus.registry.toml` and telling you where the
copies go.

Step 3 is not optional. The backend mod cannot generate a secret of its own, since a value minted
there would match nothing, so the `nimbus-server.json` it writes for you carries a placeholder
naming the file to copy the real one out of. A backend still holding that placeholder, or any of
the older documented ones, logs what is missing and stays off the network rather than heartbeating
with a string anyone can read here.

The rest of the defaults assume one machine: the embedded registry listens on
`registry.embedded_bind = "http://127.0.0.1:8765"`, which nothing off the box can reach. If your
backends run elsewhere, widen that to `http://0.0.0.0:8765`. The proxy refuses to start on a bind
other hosts can reach while the secret is still the documented placeholder, since anyone able to
reach the registry could otherwise mint themselves a reservation onto any backend. The Pterodactyl
eggs write both lines from panel variables, so a panel install starts from the wide bind and the
panel's own `NIMBUS_SHARED_SECRET`. A panel cannot generate that one, because every container
would mint a different value and none of them would authenticate, so the eggs ship a placeholder
and their install scripts refuse to finish while it is still there.

## Shortcut commands

Players would rather type `/hub` than `/server hub`. Each backend's `nimbus-server.json` can
declare its own shortcuts:

```json
"ShortcutCommands": [
  { "Name": "hub", "Targets": [ "hub" ] },
  { "Name": "lobby", "Targets": [ "survival-lobby", "hub" ], "Description": "Back to your lobby" },
  { "Name": "staff", "Targets": [ "staff" ], "Privilege": "controlserver" }
]
```

`Targets` is a fallback chain tried in order, so `/lobby` can mean "this gamemode's lobby, or the
hub if it has none", and a shortcut degrades gracefully when part of the network is down: the
first target that is registered, healthy and not this server wins. `Privilege` defaults to `chat`
(everyone) and takes any Vintage Story privilege, so `/staff` can stay admin-only.

Shortcuts never shadow an existing command, which is why there is no `/tp` shortcut: that is
vanilla teleport.

Vintage Story registers chat commands at startup and cannot unregister one, so what a
`/nimbus reload` can change is limited, and deliberately limited in the safe direction:

| Change | Takes effect |
|---|---|
| Retargeting an existing shortcut | on reload |
| Tightening a privilege (opening it up less) | on reload |
| Loosening a privilege, adding or removing a shortcut | needs a restart |

Tightening applies immediately because the handler re-checks the current privilege on every
call. Loosening cannot, because the engine-level gate registered at boot rejects the caller
before the handler runs. The asymmetry is intentional: a half-applied permission change fails
closed, never open.

## Checking a backend from the inside

`/nimbus status` on a backend reports its own view: config summary, last registry
exchange, snapshot age, and the last seamless handshake it completed as a transfer target.
That last line is worth knowing about, because a seamless transfer that fails is otherwise
invisible from the receiving side, which is where you look when a player reports a stuck
transfer screen.

## Network bans

A ban held by the registry covers the whole network, so a griefer does not have to be banned
once per backend:

```shell
nimctl ban --player Griefer --reason "griefing" --duration 86400
nimctl ban --uid <uid> --server creative     # one backend only
nimctl bans
nimctl unban --uid <uid>
```

Network-wide bans are enforced at the proxy door: the player is dropped while identifying, with
the ban reason, and their login never reaches a backend. (A client that stalls long enough for the
proxy to open an upstream first will have opened a TCP connection to the backend, but not a game
session: the pump drops its traffic the moment the ban is recognised.) The proxy keeps a warm copy of the list
so the check costs nothing per join, and a registry outage leaves the last known bans in force.

Per-backend bans are enforced too, just against that one backend. A player who lands on a backend
they are banned from is dropped at the door, with a message naming that server rather than the
network. A transfer to it is refused, whether it came from `swap`, a shortcut command, a plugin or
a backend asking for the move. A staged reconnect route pointing at it is discarded and the player
is routed normally instead. The rest of the network stays reachable throughout.

None of that can happen before identification: the proxy picks a backend while the client has
still said nothing that names the player, so the ban list can only be consulted once the UID is in
hand. The registry refuses to mint a reservation for a banned pair as well, which is what catches
a proxy whose copy of the ban list is a few seconds out of date.

The list outlives the registry process. Bans and whitelist entries are written to
`nimbus.bans.json` and `nimbus.whitelist.json` as they are made, in the directory named by
`state_dir` (registry config) or `registry.embedded_state_dir` (proxy config, embedded mode),
and read back at startup with anything that expired in the meantime dropped. A state file the
registry cannot parse is renamed with a `.bad` suffix and reported rather than trusted or
silently replaced.

Vanilla per-server `/ban` keeps working and stays local to that savegame.

## Whitelists

The same list read the other way round: who may come in, rather than who may not.

```shell
nimctl whitelist add --player Builder --note "closed beta"
nimctl whitelist add --uid <uid> --server staff     # one backend only
nimctl whitelist list
nimctl whitelist remove <uid>
```

Two scopes, matching bans. A network-wide entry covers every backend, which is the private
server or closed beta case. An entry scoped to one backend covers that backend and nothing else,
which is the staff or build server sitting inside a public network: a player who is not listed
there is refused that backend and keeps the rest of the network.

Enforcement is a proxy switch, not a property of the list:

```toml
[whitelist]
network = false                    # true closes the whole network to unlisted players
servers = [ "staff" ]              # backends closed to unlisted players regardless
fail_open_until_first_sync = false
```

Adding entries changes nothing until one of those is on, and an empty list with enforcement on
means nobody, never everybody. Reading it the other way would turn "the last entry was just
removed" into "the door is now open", which is not a mistake anyone should be able to make by
deleting a row. Bans win over whitelists: a listed player who is also banned stays out, and gets
the ban message.

Enforcement is checked in the four places a scoped ban is, all of them after the ban check: the
connection gate, both transfer methods, and a staged reconnect route (which is discarded in
favour of normal routing rather than carrying the player somewhere they cannot go).

The dangerous case is a cold start. If the proxy has never managed to read the list, an empty
cache is not an answer, and the default is to refuse every join until the registry replies once,
with a single log line rather than one per attempt. Set `fail_open_until_first_sync = true` to
let players in during that window instead, trading a closed network for the chance of a wide-open
one. After the first successful fetch this behaves like bans do: a later outage leaves the last
known list in force.

Vanilla per-server whitelisting still exists and stays local to that savegame, the same way
per-server bans do.

## API tokens for integrations

The registry has one credential otherwise, the network shared secret, and it is all or nothing:
anything holding it can mint reservations, ban any player and read the whole network. A scoped
token is the answer to "can I drive this from a Discord bot" that does not involve handing the bot
the master key.

```shell
nimctl token create --name discord-bot --scopes whitelist:write,whitelist:read
nimctl token list
nimctl token revoke <id>
```

The secret is printed once, by that first command, and never again. What the registry keeps is its
SHA-256, so a leaked state file or a leaked backup exposes nothing that can be replayed. Tokens
carry a `nsk_` prefix so one pasted into a config or a commit is identifiable on sight, by a human
or by a secret scanner, and they expire after 90 days unless `--permanent` was asked for by name.

Five scopes, coarse on purpose: `bans:read`, `bans:write`, `whitelist:read`, `whitelist:write`,
`servers:read`. A route declares the one it needs and a token carries a set. Nothing else is
reachable: heartbeats, reservations, transfer intents and token management itself take HMAC and
only HMAC, whatever scopes a token holds. That caps a leaked bot credential at moderation-list
writes at rate-limit speed. Writes made with a token are attributed to it, so a ban placed by the
bot reads `token:discord-bot` rather than sharing an identity with the operators.

Using one is a bearer header and no signing at all:

```shell
curl -X POST https://registry.example.org/api/whitelist \
  -H "Authorization: Bearer nsk_..." \
  -H "Content-Type: application/json" \
  -d '{"playerUid":"...","note":"invited"}'
```

That simplicity is bought entirely from the transport, so the registry refuses token auth unless
the connection is loopback or arrived over its own TLS listener. `X-Forwarded-Proto` is not
believed by default, because anything that can reach the bind can write that header:

```toml
[api_tokens]
enabled = false                # master switch; bearer auth is refused outright while it is off
rate_limit_per_minute = 60     # per token, on top of any per-IP limit in front
trust_forwarded_proto = false  # only behind a TLS-terminating proxy that is the sole route in
```

Embedded mode reads the same three settings as `registry.api_tokens_enabled`,
`registry.api_tokens_rate_limit_per_minute` and `registry.api_tokens_trust_forwarded_proto` in
`nimbus.proxy.toml`. Issued tokens are written to `nimbus.tokens.json` in the same state directory
the ban list and whitelist use, so a revocation outlives the process that made it. Creating tokens
works with the switch off, which is the order an operator does it in; only authenticating with one
requires it.

A request carrying no `Authorization: Bearer nsk_` header is untouched by any of this and reaches
the HMAC check exactly as before, so every existing backend, proxy and `nimctl` is unaffected.

## Addresses: who connects where

Three different addresses exist in a Nimbus network, and mixing them up is the most
common misconfiguration:

| Setting | Lives in | Means |
|---------|----------|-------|
| `bind` | `nimbus.proxy.toml` | The address **players** connect to. The only address you publish. |
| `PublicHost` / `PublicPort` | `nimbus-server.json` (each backend) | The address **the network** reaches that backend on: the proxy dials it for seamless transfers, admin `swap` uses it, and it is stamped into redirect packets. It must be reachable from the proxy; it does not need to be reachable by players. |
| `identity.public_host` / `public_port` | registry config | The **proxy's** public address, advertised to the VS master server when `advertise_on_master_server` is on. |

Note on redirects: [RedirectFix](https://mods.vintagestory.at/show/mod/52239) clients
reconnect to the proxy's cached address and a staged sticky route sends them to the right
backend, so the host stamped into the redirect packet is not what the client actually
dials today. By default that stamped host is the backend's `PublicHost`, which a future
vanilla client with the redirect crash fixed would follow literally, bypassing the proxy.
Set `transfers.redirect_address` in `nimbus.proxy.toml` to the proxy's player-facing
address to stamp the proxy instead (#18), keep backends unreachable from the internet,
and force everything through the proxy with `ReservationRequired`.

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
