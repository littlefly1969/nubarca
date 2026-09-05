# Party Game — server-authoritative runtime

What is happening at the party right now, and who is allowed to say so.

A `PartyChallenge` is content the host prepared before the party. The runtime
described here is the **performance** of that content: one session per party
link, one round per activity played, one phase saying where the room currently
is. The server owns all of it.

## Why a second session type exists

`PartyChallengeSession` already existed and is untouched. It drives the older
behaviour where the slideshow interrupts itself on a timer, shows the most-voted
dare, and resumes on NEXT — the room chooses what happens, and nobody is
conducting.

`PartyGameSession` is a **hosted game**. The owner conducts it, the deck is
played in the order the owner arranged, and every phase change is a deliberate
command. The two coexist on the same album and the same party link without
interacting; they answer different questions, so merging them would have made
one row mean two things.

## State machine

```
LOBBY --start--> CHALLENGE_REVEAL --start_challenge--> CHALLENGE_ACTIVE
                        ^                                     |
                        |                               open_voting
         next_challenge |                                     v
                        |                               VOTING_OPEN
                        |                                     |
                        |                               close_voting
                        |                                     v
                     RESULT <--reveal_result--     VOTING_CLOSED
                        |
         next_challenge | (no activity left)
                        v
                     FINISHED
```

`skip_challenge` leaves any unresolved round phase — reveal, active,
voting_open, voting_closed — for the next activity, or for FINISHED when there
is none. `finish` is legal from every phase except FINISHED.

The matrix lives in [`PartyGameStateMachine`](../../src/NubArca.Api/Party/PartyGameStateMachine.cs)
as a pure function and is exhausted by
[`PartyGameStateMachineTests`](../../tests/NubArca.Api.Tests/Party/PartyGameStateMachineTests.cs)
over every phase x command x "is there another activity". The service applies
transitions; it never decides one.

### One divergence from the programme brief, recorded

The brief lists **`reveal challenge`** among the commands. There is no such
command. Revealing IS the transition into `CHALLENGE_REVEAL`, and it is
performed by `start` (from the lobby) and by `next_challenge` (from a result). A
third name would have produced a command that is legal nowhere, and a control
room with a primary action that does nothing. The six non-terminal phases map
one-to-one onto six primary commands:

| Phase | Primary command |
| --- | --- |
| `lobby` | `start` |
| `challenge_reveal` | `start_challenge` |
| `challenge_active` | `open_voting` |
| `voting_open` | `close_voting` |
| `voting_closed` | `reveal_result` |
| `result` | `next_challenge` |

## Two rules that are easy to undo by accident

**A read never writes.** A game that has not started has no row at all. The
snapshot is synthesized in the lobby at `version = 0`, and `start` quotes 0.
This is the same shape as an implicit-pending AI artifact status, and it exists
because a television polling a party that has not begun must not begin it. The
older `PartyChallengeSession` deliberately does the opposite —
`EnsureSessionAsync` materialises a row — because its deadline has to start
ticking somewhere. The game's does not.

**A refusal carries the current snapshot.** A `409` body is
`{ code, snapshot }`, never a bare error. The owner is holding a phone in front
of a room, and the useful answer to "that was stale" is the truth. It is also
what stops a double tap from becoming a double advance: the second request is
refused *and* re-renders the caller correctly, with no follow-up fetch to
forget.

## Concurrency

Every command quotes `expectedVersion`. Two authorities enforce it:

1. the in-memory check, which refuses a caller that is visibly behind;
2. `Version` as an EF concurrency token, plus the unique index on
   `PartyAlbumLinkId`, which decides the case where both callers *look* current
   — two tabs, two devices, one instant. The loser gets `version_conflict` and
   the winner's state.

[`PartyGameConcurrencyTests`](../../tests/NubArca.Api.Tests/Party/PartyGameConcurrencyTests.cs)
races two independent connections for the create case, the advance case, and a
skip-against-advance case.

## Realtime

There is none, and that is the design. NubArca has no SignalR, no WebSocket and
no SSE anywhere in application code: the paired television, the TV browser, the
pairing screen and the public party page all poll a snapshot. The game does the
same. A client learns that something changed by seeing a higher `version`, and
recovers from a refresh, a backgrounded tab or a dropped network by reading the
snapshot again — which is the entire reconnection story, and needs no
reconnection code.

## API

| Method | Route | Caller |
| --- | --- | --- |
| `GET` | `/api/albums/{albumId}/party-game` | owner cookie |
| `POST` | `/api/albums/{albumId}/party-game/commands` | owner cookie |
| `GET` | `/api/party/{token}/game` | anonymous, view token |

The owner snapshot carries `availableCommands` — the server's own answer to
"what may I do now" — so a control room can omit an illegal command rather than
disable it, without re-implementing the matrix in TypeScript. It stays advisory:
every command is validated again on arrival.

A missing album, a foreign album, a party that is off, and a game switch that is
off all collapse to `404`, as everywhere else in Party.

### What crosses the token boundary

The public snapshot is a strict subset: album name, status, phase, version,
round number, total activities, the phase deadline, and the activity itself —
the last only in the phases that put it on a screen. No session id, no round
history, no command vocabulary, no next activity, no vote data. The activity's
media URL is built by the endpoint against the caller's own token; the service
returns a token-less sentinel, exactly as the guest challenge list does.

## Schema

`party_game_sessions`

- unique on `PartyAlbumLinkId` — one game per party link, which is also what
  makes two simultaneous `start` commands produce one game. A re-enabled party
  mints a new link, so a new party genuinely is a new game.
- `Version` defaults to 1 and is the concurrency token.
- Check constraints pin `Status` and `Phase` to the vocabulary above.

`party_game_rounds`

- unique on `(session, sequence)` — the order the room experienced the evening.
- unique on `(session, challenge)` — an activity is played at most once per
  game, as a database fact rather than a query somebody remembers to write.
- `Status` is `active`, `completed` or `abandoned`. Abandoned is deliberately
  distinct: it is the difference between "the room decided" and "we moved on".
- `PhaseEndsAt` is the phase deadline. It is always null today; activity
  durations arrive with the composer.

An activity a round has played can no longer be deleted — the restricting
foreign key would refuse anyway, and `PartyChallengeService.DeleteAsync` turns
that into a clean `404` instead of an exception.

## Deliberately not here

A finished game stays finished. Replaying means a new party, which mints a new
link and therefore a new game — the same thing "a new party" already meant
everywhere else in the feature.
