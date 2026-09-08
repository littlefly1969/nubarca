# Party Game

A hosted game at a party: the host conducts it from their phone, the room
watches a television, and the guests answer on theirs.

| Document | What it settles |
| --- | --- |
| [ux-integration-contract.md](ux-integration-contract.md) | Which existing NubArca components, class families and tokens every surface is built from — and the gaps that were real |
| [runtime.md](runtime.md) | The server-authoritative state machine, concurrency, voting, and what crosses the token boundary |
| [guest-identity.md](guest-identity.md) | One anonymous guest per party link, and why capability, identity, activity, quota and rate limiting are five different things |

## The four surfaces

| Who | Where | Shell |
| --- | --- | --- |
| Host — preparing | album settings → the deck and its composer | app shell, `Modal` |
| Host — conducting | `/albums/{albumId}/party-game` | app shell |
| The room | `/party/{token}/tv` | full-bleed 16:9, no controls |
| A guest | `/party/{token}/game` | `.party-guest-hub`, phone-first |

The last two are reached with the party's existing public view token, so a host
puts the game on a screen by opening a URL — no pairing, no APK, no OTA.

## The five rules

Everything in this feature follows from these, and each is asserted somewhere.

1. **The server is the only authority.** No client holds game state the server
   cannot reproduce, so refresh, reconnect and reload are all the same act:
   read the snapshot again — and every successful poll is consumed, because
   `version` is the owner's command token and not a change feed.
2. **A read never writes.** A game that has not started has no row; the lobby is
   synthesized at version 0. A television polling a party must not begin it.
3. **A refusal carries the truth.** Every `409` returns the state it was
   measured against, so a stale caller ends the request *correct* rather than
   merely told off — which is what turns a double tap into one advance.
4. **The state machine is quoted, never re-implemented.** It is a pure function
   on the server; the control room renders the `availableCommands` the server
   sends, so an illegal command is absent rather than disabled.
5. **Nobody learns the result early.** Participation is safe at any moment; the
   split reaches the host when voting closes, and the room only when the host
   reveals it — and the boundary between "voting" and "closed" is held by the
   database, not by a check the vote path performs and then hopes still holds.

## Running it

The host enables Party mode on an album, switches the game on, prepares
activities in the deck, then opens the control room. The control room carries
the link to the television and a QR for guests. From there the evening is one
button at a time.

A finished game can be played again from the same control room: `restart_game`
discards the match — its rounds and its votes — and returns the session to its
lobby. The party link, the QR code, the guests and everything they contributed
are untouched, because they belong to the party rather than to the game. The
version moves FORWARD across the restart, so a command written during the game
that just ended stays stale.
