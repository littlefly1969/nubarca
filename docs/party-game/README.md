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

**There is ONE game the guest can perceive.** `/party/{token}/game` holds the
lobby and its pre-game preferences, the watch, the live vote, the wait, the
result, the intermission and the end. The hub used to offer a second card —
"vote the challenges" — for a different vote entirely; that route now redirects
here. A guest already on `/party/{token}` never needs a second QR: the party
itself carries a persistent, non-invasive bar saying what the game is doing
(*choose the activities* / *game in progress* / *vote now* / *game paused*) and
offering one tap. It is a link, never a takeover — the guest decides — and it
reads the public snapshot without joining, because a hub that minted a
participant would inflate the very count the control room reads out loud.

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
discards the match — its rounds, its votes and the host's exclusions — and
returns the session to its lobby. The party link, the QR code, the guests, the
deck, everything they contributed and **the preferences they cast** are
untouched, because they belong to the party rather than to the game. The version
moves FORWARD across the restart, so a command written during the game that just
ended stays stale.

## Two votes, and they never meet

A **preference** is cast before the match, on an activity, and says *I would like
to see this*. A **live vote** is cast during an activity, on a round, and says
*they did it*. They share the anonymous participant the party already had and
nothing else — no table, no budget, no phase, no consequence. Preferences are
ADVISORY: they inform the host's planning in the control room and choose nothing
by themselves, which is why the guest surface shows no counts and the old
"most-voted activity interrupts the slideshow" behaviour is retired.

## Giving the room back to the party

From a result the host may send everybody back to the party rather than starting
the next activity. `return_to_party` puts the game in `INTERMISSION`: the match
stays alive, the television returns to the party slideshow, guests are told the
party continues, and the control room keeps showing the plan — which the host can
still reorder. `next_challenge` resumes exactly where it would have.

## Setting the same evening up again

`Duplica festa` copies a party's CONFIGURATION into a new, independent one:
title, windows, guest slots, the deck, the slideshow timings, the quotas, the
approval modes, the game switches and the print budgets. It copies nothing that
HAPPENED — participants, preferences, votes, rounds, uploads, greetings, prints,
televisions, grants and tokens all stay with the original. The clone is a Draft
with its own album whose membership points at the SAME files, so no byte is
duplicated and either party can be edited without touching the other.
