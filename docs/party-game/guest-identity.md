# Party guest identity contract

**One anonymous browser guest per `PartyAlbumLink`.**

Every capability at a party — reading the album, contributing a photograph,
sending a greeting, joining the game, voting, printing a keepsake, and whatever
is added next — is performed by that one guest. Not one per capability, not one
per token, not one per page.

## Five words that are not synonyms

Party has five separate concerns that are easy to collapse into each other, and
almost every mistake in this area is one of these standing in for another.

| | What it answers | Where it lives |
| --- | --- | --- |
| **Capability** | *What* may this browser do? | The token in the URL — printed on a QR, held by everyone at the party, identifies nobody |
| **Identity** | *Who*, anonymously, is doing it? | `PartyParticipant`, resolved from the browser cookie |
| **Activity** | *What happened?* | `PartyUploadItem`, `PartyMessage`, `PartyGameVote`, `PartyPrintRequest` — each its own domain record |
| **Quota** | How much of a *finite thing* may this guest have? | Counters on `PartyParticipant`, ceilings on `PartyAlbumLink`. Refused with **409** |
| **Rate limit** | Are the requests arriving *too fast*? | The rate limiter. Refused with **429** |

Two consequences worth stating outright:

- **A capability token is never an identity.** It says a browser may vote; it
  cannot say who is voting, because everyone at the party holds it.
- **A quota is not a rate limit.** A quota is a product decision the host made
  and no amount of waiting changes it. A rate limit is about pace. They get
  different status codes because a client has to be able to tell them apart.

## How the identity works

A random **browser token** is issued by the server, stored nowhere, and held in
one HttpOnly cookie for the whole `/api/party` surface — so every capability
sees the same cookie. The row it resolves to is keyed by

```
SHA-256( HMAC-SHA256( serverSecret, browserToken ++ partyAlbumLinkId ) )
```

Three properties follow from that shape, and they are the reason for it:

- **One browser at one party is one guest**, whichever capability it arrives
  through, because the key does not depend on the token in the URL.
- **One browser at two parties is two unrelated guests**, because the link id is
  inside the derivation. No counter, quota or vote can cross between parties.
- **A database read is not an impersonation.** Presenting requires the raw
  browser token; deriving requires the server secret. A dump has neither.

### What it is not

An anonymous **browser** identity for one event — not a person. Clearing site
data or switching device produces a new guest, and that is deliberate: the
alternatives are fingerprinting, IP identity, or asking for a name, and this
codebase does none of them. There is no login, no profile, and nothing
owner-visible beyond the name a guest chose to type on a greeting.

## Who may mint an identity

> **Operations that establish a guest session may mint identity. Privileged or
> finite actions must not manufacture an identity merely because they were
> invoked.**

| Operation | Mints? |
| --- | --- |
| Opening a party surface, contributing, sending a greeting, joining the game, submitting a print | yes — a guest is arriving |
| Reading the game snapshot | no — a television polls it, and a display must not become a voter |
| **Casting a vote** — hosted game *or* challenge deck | **no** — resolve-only |

**VOTE NEVER MINTS IDENTITY**, and that does not belong to one voting system.
Both the hosted game's vote and the older challenge vote/unvote are
resolve-only; a caller with no guest session gets a machine-readable conflict
(`not_joined` — `code` in the game's envelope, `error` in the challenge API's)
and nothing is written: no participant, no vote, no budget counter, no cookie.

The ordinary flow is unchanged, because it was always how a guest arrives:

```
GET /challenges  →  guest session established  →  PUT/DELETE vote
```

Both endpoints used to resolve-or-create, so a fresh cookie was a fresh voter
and the right to spend a budget and move a ranking was available to anyone who
could set a header. Rate limiting does not fix that: a limiter bounds how many
requests an identity may make, and has no opinion on whether something should
have been an identity.

Everything goes through
[`PartyGuestSession`](../../src/NubArca.Api/Endpoints/PartyGuestSession.cs) and
[`IPartyParticipantService`](../../src/NubArca.Api/Party/IPartyParticipantService.cs).
No endpoint implements its own version.

## Adding a guest action later

Point at the actor and keep your own record:

```csharp
public sealed class PartyDjRequest
{
    public Guid PartyParticipantId { get; set; }   // the actor
    public string TrackTitle { get; set; }         // your domain
}
```

**Do not** create `DjGuest`, `GameGuest`, `PrintGuest` or another
anonymous-session table. A second session table is a second identity, and a
second identity is a second allowance — which is exactly the defect this
contract exists to have ended. The domain records stay separate; they share only
the actor's foreign key.

The exception is a genuinely different actor. If something is not "an anonymous
guest at this party", it should not be a `PartyParticipant`.

**A new foreign key must be added to the fold.** While legacy rows can still
arrive, anything pointing at a participant has to move when that participant is
folded, or the new feature will be the one that sees a guest as two. If the
record has a uniqueness rule involving the participant, reconcile before
re-parenting and state which side wins; if it does not, re-parent it. The list
lives in `PartyParticipantService.FoldAsync` and the invariant is checked by
`PartyGuestFoldActorTests`, which asserts one record per referencing table so it
cannot pass by finding nothing.

### Face search is deliberately not attached

`PartyFaceSearchSession` carries no `PartyParticipantId`, and should not acquire
one for the sake of symmetry. It is the most privacy-sensitive thing a guest
touches — a photograph of their face — and linking it to a durable per-event
identity would turn a momentary search into a record of who looked for whom.
Attach it only when a concrete need appears (a quota, abuse control, a
guest-owned lifecycle) that cannot be met otherwise, and say which one.

## The legacy cookie, and what could not be reconciled

Before this, the cookie was scoped to `/api/party/{capabilityToken}`. The
browser therefore sent a *different* cookie to each capability, and each
capability quietly grew its own participant with its own counters.

A party that is running right now must not notice the change, so the old cookie
is still read — never written — and every old row is **folded** into the
canonical guest, which is always the row keyed by the derivation:

1. find-or-create the canonical row for `(link, derived key)`;
2. if the request also presents an old cookie whose row is not that one, fold it:
   **retire it, move its activity onto the canonical guest, then move its
   counters**.

### A fold moves the ACTOR, not just the allowance

`PartyParticipantId` means *the guest who did this*. Moving only the counters
would satisfy the letter of "one identity per browser" and break the point of
it: a challenge the guest had already voted would look **unvoted** to the
identity that now represents them, so they could vote it again. One browser, two
votes — exactly what an anonymous identity exists to prevent, reintroduced by
the migration meant to end it.

Four foreign keys name a participant, and they need two treatments:

| Record | Participant uniqueness | Treatment |
| --- | --- | --- |
| `PartyChallengeVote` | `(link, participant, challenge)` | reconcile, then re-parent |
| `PartyGameVote` | `(round, participant)` | reconcile, then re-parent |
| `PartyMessage` | none | re-parent |
| `PartyUploadItem` | `FileItemId` only | re-parent |

Print holds **no** participant row of its own — it reads the counters — which is
why those have to land correctly rather than being the whole job.

**Where two identities answered the same question, the canonical answer wins.**
It was given after the new session existed, so it is the guest's later answer,
and a fold must never overwrite what a guest did with what they did earlier. The
alias's copy is deleted. Reconciling *before* the re-parent is what keeps the
unique index a safety net rather than a mechanism: by the time the update runs,
no row that would collide is still there.

**The vote budget is recomputed, not summed.** `ChallengeVoteCount` is a budget,
and reconciliation can remove a vote — so summing would charge a guest twice for
a vote they hold once, and quietly cost them a vote later in the evening.
Counted from the rows that now exist, it is exact by construction and stays
exact whichever order two folds arrive in. The other five counters are summed,
because nothing above reduces them.

**Presence excludes retired rows.** The room count and the voting `eligible`
figure are *current guest* questions, so an alias must not answer them: a folded
guest would otherwise be counted twice in the number a host reads out loud.

**The alias keeps nothing.** Its counters are zeroed once they have moved, so
"no second allowance" rests on the data rather than on a `WHERE` clause, and no
aggregate over the table can double-count. It is retired rather than deleted
because it is evidence a guest was here — but nothing points at it any more, so
there is no alias→canonical lookup to provide and none is needed.

**The retirement is the claim.** A conditional update sets `RetiredAt` only
while it is still null, so of any number of requests looking at the same old row
exactly one is told it affected a row — and only that one adds the counters.
It also **locks** that row for the rest of the transaction, which is what lets
the counters be read from the row itself rather than from an entity loaded
before the fold began.

All of it — claim, reconciliation, re-parenting, counters — is **one
transaction**, so a failure anywhere leaves the alias live and unfolded rather
than half-moved: retired with its votes still on it, or re-parented with its
counters lost. The counters move by atomic increment, never by
read-modify-write, so two capabilities folding two different old rows onto the
same guest both land.

**Adopting was the obvious alternative and is not safe.** Rewriting an old row's
key to the derived value means two capabilities of one browser arriving together
both claim the same key, and the unique index surfaces an ordinary race to a
guest as a `500`. The index stays as a safety net — the create path treats a
violation as the expected race *only* when the elected row is then findable, and
re-throws otherwise, so a real database fault never hides behind a retry.

Summing the other five is exact rather than generous, because a capability only
ever incremented its own counters: the non-zero counters of two old rows are
disjoint. A retired row is invisible to every lookup, so it can never be folded
twice.

**The one case that cannot be reconciled** is a capability the browser never
returns to after the upgrade. Its old row keeps its counters and is never
folded in, because the browser only ever presents the cookie for the path it is
requesting — the server cannot prove the others exist. Nothing is duplicated and
nothing the guest is *using* is reset; a capability they abandon simply keeps a
row nobody reads, with its own activity still on it.

A second, narrower one: a request that resolved the old identity *before* the
fold and writes *after* it commits has its increment applied to a row that is
now retired and zeroed, so that one write is lost. It requires a request
in flight across the exact instant of a guest's first post-upgrade request, and
it costs at most one counted action. Adoption also belongs to session-establishing operations only,
so a legacy cookie can never turn a vote into a voter.


## Integrating with the Print work on `main`

This stack is based on `6be5b64`; `main` has since gained the per-guest print
share (`38cb7e5` onward). Those changes must survive integration intact — the
merge replaces **only** the participant resolver.

`main`'s `PartyPrintEndpoints` resolves a guest in **two** places:

| Endpoint | On `main` | After integration |
| --- | --- | --- |
| `GET /api/party/{printToken}/print` — the manifest that computes `RemainingForYou` | `PartyEndpoints.ResolvePartyParticipantAsync` | `PartyGuestSession.ResolveOrCreateAsync` |
| `POST /api/party/{printToken}/print` — the submission | `PartyEndpoints.ResolvePartyParticipantAsync` | `PartyGuestSession.ResolveOrCreateAsync` |

Reading the manifest is a guest arriving, and `RemainingForYou` is meaningless
without an identity, so both are session-establishing. Nothing else in the print
stack changes: **per-guest print remaining, sheet orientation, idempotency,
print capability security and the existing Print tests all stay exactly as they
are.**

One behavioural improvement falls out rather than being added: on `main` the
print guest was a *different* guest from the upload and message guest, so
`RemainingForYou` described a per-capability identity. Afterwards it describes
the party-wide one, which is what a guest means by "my share". For a party
mid-flight across the upgrade the fold carries `AcceptedPhotoPrintCount` and
`AcceptedStripPrintCount` over with everything else, so nobody's remaining
share is duplicated or reset.
