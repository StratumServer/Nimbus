# Nimbus.ServerMod.Tests

Integration scenarios for `Nimbus.ServerMod`'s reservation gating (the inbound half of a
transfer), running on a real embedded Vintage Story server via
[Atlas](https://github.com/Pixnop/Atlas). No proxy process and no real network client:
the registry is faked on a loopback HTTP port (`FakeRegistry`) and players join through
Atlas's in-memory dummy client.

## Running

Requirements: .NET 10 SDK and a Vintage Story **1.22.x** install, with `VINTAGE_STORY`
pointing at the folder containing `VintagestoryAPI.dll`:

```sh
VINTAGE_STORY=/path/to/vintagestory dotnet test Nimbus.ServerMod.Tests
```

Note on versions: Nimbus targets VS 1.19+, but Atlas's floor is VS 1.22.0 (it hooks the
1.22 server exit lifecycle). The ServerMod only uses stable server APIs, so running the
suite on 1.22 is representative; earlier game versions stay manual-test territory.

## What is covered

- Valid reservation on join → player admitted, `GetForwardedPlayer` exposes the real
  client IP.
- No reservation + `ReservationRequired=true` → player is kicked.
- No reservation + `ReservationRequired=false` → direct join allowed, no forwarding data.
- The consume call carries all four `X-Nimbus-*` headers and its HMAC verifies against an
  independent reimplementation of the canonical string (so a signing bug cannot
  self-validate).
- Characterization: registry unreachable + `ReservationRequired=true` currently admits
  the player (fail-open). If fail-closed is the intended trade-off, flip that test.

Out of scope by design (single embedded server, in-memory sockets, no client-side code):
two-server transfers, proxy frame handling, and the seamless client handshake.

## Design notes

- **Reflection, not references** (`NimbusHarness`): the game's ModLoader loads a *copy*
  of the staged `Nimbus.ServerMod.dll`, so its types are never identity-equal to
  compile-time references. The harness finds the ModSystem by name and drives it by
  reflection.
- **Post-boot rewiring**: the mod reads `nimbus-server.json` once in `StartServerSide`
  and only creates its registry client there; the harness swaps the private
  `config`/`registry` fields after boot. Making `/nimbus reload` re-create the registry
  client would remove most of the harness (and fix the operator-facing gap where
  changing `RegistryUrl` + reload has no effect until restart).
- **Kick detection**: a kicked dummy player never closes its in-memory socket, so the
  reliable in-process signal is the `PlayerDisconnect` server event, the same one the
  mod uses to clear its per-player state.
