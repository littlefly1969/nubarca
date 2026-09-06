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

**An activity nobody votes on takes one shortcut.** When the running activity's
`VotingMode` is `none`, `CHALLENGE_ACTIVE` goes straight to `RESULT` and
`open_voting` is not offered at all — the alternative is a control room whose
primary action opens a vote that can never receive one. That is the *only* cell
of the matrix a voting mode changes, and a test asserts every other cell is
identical.

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
| `challenge_active` | `open_voting`, or `reveal_result` when the activity is unvoted |
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

## Voting

The room answers one question per round: did they do it? `yes` or `no`, one
current answer per guest, replaced rather than appended when somebody changes
their mind while voting is open.

**The integrity constraint is the database's**, not a code path's: a unique
index on `(round, participant)`. Two taps arriving together cannot both insert,
and the loser is not an error — the guest's answer is recorded either way, so
the service re-reads and reports what the row says.

**The client never decides whether a vote is valid.** The participant, the
session, the round being played, the phase and the activity's own voting mode
are all re-read on every tap. That is also what makes the exact close boundary
clean: a vote that left a phone while voting was open and landed after the host
closed it is refused with `voting_closed`, and nothing is written.

**A vote names the round it answers.** A phone whose poll fell behind must not
land last round's verdict on this round's activity, so the request quotes
`roundId` and a mismatch is `stale_round`. The round id is deliberately used
rather than the version: it is stable for a whole round, so an ordinary lagging
poll never costs somebody their vote, whereas the version moves on every phase
change.

### Who may know the result, and when

| | during `voting_open` | at `voting_closed` | at `result` |
| --- | --- | --- | --- |
| received / eligible | yes | yes | yes |
| yes / no / passed | **no** | **owner only** | yes |

Participation is safe at any moment: it says how many people have answered,
never what they answered. The split is the result, and the host decides when the
room sees it — which is the whole point of having a host. Counts rather than a
percentage, because a percentage is presentation and two surfaces rounding it
differently would show a party two different answers; `passed` is on the server
for the same reason, since whether a tie counts as passing is a product rule
with exactly one answer. (It does not.)

**Eligible** is participants on this link seen within
`PartyGamePresence.WindowSeconds` (180s), floored at the number of votes
received — whoever voted is by definition in the room. It is a soft signal about
a party, not an attendance register.

### Is a screen showing this?

The control room has to answer that honestly, and the server cannot infer it: a
guest who has not joined also polls without a cookie. So a client **says** it is
a display — the stage sends `?display=1` — and the server stamps
`PartyAlbumLink.LastDisplaySeenAt`. Claiming to be a television is not a
capability: it grants nothing, and it is the only way the answer can be true.

The heartbeat lives on the LINK rather than the session, because a screen is
watching before there is a game to watch and because a read of game state must
never create game state. It is written with a single-column `ExecuteUpdate`
outside the change tracker, so a display polling every couple of seconds can
never contend with an owner command for the session's concurrency token.

The owner snapshot carries `displaySeenSecondsAgo` — the elapsed time, computed
server-side — rather than a timestamp, because a control room on a laptop with a
drifting clock would otherwise decide for itself that the television died an
hour ago. `partyGameDisplayState()` turns it into connected (≤15s) / stalled
(≤120s) / gone.

### A television is not a voter

`GET /api/party/{token}/game` resolves an existing guest session but **never
mints one**, because a display polls it too and minting there would inflate the
very count the scene is showing. Refreshing an existing session's presence IS a
write, deliberately: it is what keeps a guest holding the voting screen open
counted as being in the room.

`POST /api/party/{token}/game/join` is the guest saying "I am here" — it mints
the participant cookie, and it is a POST precisely so a polling television can
never do it by accident.

## API

| Method | Route | Caller |
| --- | --- | --- |
| `GET` | `/api/albums/{albumId}/party-game` | owner cookie |
| `POST` | `/api/albums/{albumId}/party-game/commands` | owner cookie |
| `GET` | `/api/party/{token}/game` | anonymous, view token |
| `POST` | `/api/party/{token}/game/join` | anonymous, view token |
| `POST` | `/api/party/{token}/game/vote` | anonymous, view token |

The owner snapshot carries `availableCommands` — the server's own answer to
"what may I do now" — so a control room can omit an illegal command rather than
disable it, without re-implementing the matrix in TypeScript. It stays advisory:
every command is validated again on arrival.

A missing album, a foreign album, a party that is off, and a game switch that is
off all collapse to `404`, as everywhere else in Party.

### What crosses the token boundary

The public snapshot is a strict subset: album name, status, phase, version,
round number, total activities, the phase deadline, the activity itself, the
round id, the participation counts and this caller's own answer — the last
three only where they apply, and the activity only in the phases that put it on
a screen. No session id, no round history, no command vocabulary, no next
activity, and no result until it is revealed. The activity's media URL is built
by the endpoint against the caller's own token; the service returns a token-less
sentinel, exactly as the guest challenge list does.

## Schema

`party_game_sessions`

- unique on `PartyAlbumLinkId` — one game per party link, which is also what
  makes two simultaneous `start` commands produce one game. A re-enabled party
  mints a new link, so a new party genuinely is a new game.
- `Version` defaults to 1 and is the concurrency token.
- Check constraints pin `Status` and `Phase` to the vocabulary above.

`party_game_votes`

- unique on `(round, participant)` — one current answer per guest per round,
  held by the database rather than by whichever code path remembered to check.
- `Value` is `yes` or `no`, pinned by a check constraint.

`party_game_rounds`

- unique on `(session, sequence)` — the order the room experienced the evening.
- unique on `(session, challenge)` — an activity is played at most once per
  game, as a database fact rather than a query somebody remembers to write.
- `Status` is `active`, `completed` or `abandoned`. Abandoned is deliberately
  distinct: it is the difference between "the room decided" and "we moved on".
- `PhaseEndsAt` is the phase deadline, set when `start_challenge` runs on an
  activity that carries a `DurationSeconds`. Only the activity phase gets one: a
  reveal, a vote and a result each last exactly as long as the host leaves them
  on screen.

An activity a round has played can no longer be deleted — the restricting
foreign key would refuse anyway, and `PartyChallengeService.DeleteAsync` turns
that into a clean `404` instead of an exception.

## Deliberately not here

A finished game stays finished. Replaying means a new party, which mints a new
link and therefore a new game — the same thing "a new party" already meant
everywhere else in the feature.
