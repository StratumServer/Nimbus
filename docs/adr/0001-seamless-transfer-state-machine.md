# ADR 0001: Seamless transfer state machine

Status: accepted. Date: 2026-07-27. Relates to issue #19.

## Context

The seamless path was half-wired: the source backend sent `NimbusSeamlessPrepare` and
waited for the client's `NimbusSeamlessReady`, but `NimbusSeamlessCommit` was defined and
never sent, so a client had no signal that the transfer it prepared for actually
finished. The open question was who should send the commit, and when.

The structural constraint: the SOURCE backend cannot send the commit. Both transfer
implementations (the visual redirect and the unsafe live splice) end with the client's
connection to the source being torn down, so by the time the transfer has succeeded, the
source has no channel to the client anymore. Only the TARGET backend still talks to the
player.

## Decision

The reservation is the vehicle that carries the handshake identity across the network,
and the target backend closes the loop:

```
source backend          client              proxy               registry        target backend
     |--- Prepare(id) --->|                   |                    |                  |
     |<--- Ready(id) -----|                   |                    |                  |
     |--- TransferIntent(id) ---------------------------------->---|                  |
     |                    |                   |<-- drain intents --|                  |
     |                    |                   |--- mint reservation(uid, id) -------->|
     |                    |<== transfer (redirect or splice) ==|   |                  |
     |                    |                   |                    |                  |
     |                    |                   |     join; consume reservation(uid) -->|
     |                    |<------------------------------------------ Commit(id) ---|
```

- `TransferIntentRequest.ClientTransferId` already carried the id from the source
  backend to the registry.
- The proxy's intent dispatcher now threads it through `RequestTransferAsync` into the
  reservation mint, and `TransferReservation.ClientTransferId` persists it (additive
  JSON field, older peers ignore it).
- When the target backend consumes the reservation on join and finds a non-empty
  `ClientTransferId`, it sends `NimbusSeamlessCommit { TransferId }` to the player from
  the game thread.

`NimbusSeamlessAbort` remains the source's pre-transfer failure signal (prepare
timeout, registry rejection, internal error). After the transfer has started the source
can no longer signal anything; a transfer that dies mid-flight surfaces to the client as
a disconnect, which is also what the vanilla path would do.

### Known gap: dispatch failures signal nothing

The client contract is "exactly one of Commit or Abort" only for failures the source can
see synchronously. `PostTransferIntentAsync` confirms the registry *accepted* the intent,
not that the transfer happened. The proxy dispatches it later, and an unknown server id, a
stale or maintenance target, or the dispatcher's ready-wait timeout all just log and
return: `TransferIntentResponse` is fire-and-forget once queued, so nothing reaches back to
the source backend.

That leaves a real window, after `Ready` and before the redirect or splice begins, where
the client sits veiled and receives neither packet. It predates this design (the
dispatcher has always been fire-and-forget) and closing it needs a path from the proxy
back to the source backend, most likely through the registry. Tracked separately as #51 rather
than papered over: a client mod should treat a prepare with no resolution as expiring
after a timeout, not assume one of the two packets always arrives.

## Consequences

- The client mod's contract is now complete: `Prepare` -> freeze/veil, `Ready` -> ack,
  then exactly one of `Commit` (from the NEW server, end the veil) or `Abort` (from the
  OLD server, cancel and stay). A client must accept the commit from a different server
  session than the one that sent the prepare; the `TransferId` is the correlation key.
- Unmodded clients are unaffected: they never register the Nimbus channel, so the
  commit packet is never delivered to anything.
- Reservations gain a correlation field that is empty for plain transfers; no registry
  API version bump is needed (additive field over JSON).
- The commit fires for both seamless implementations, including the default
  redirect-under-veil, which is exactly the case where the client needs to know the
  reconnect it just lived through was the transfer completing.
