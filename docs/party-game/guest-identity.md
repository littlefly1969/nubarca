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
| **Casting a game vote** | **no** — resolve-only |

The vote endpoint is the load-bearing case. It used to resolve-or-create, so a
fresh cookie was a fresh voter and the right to change a result was available to
anyone who could set a header. Rate limiting does not fix that: a limiter bounds
how many requests an identity may make, and has no opinion on whether something
should have been an identity.

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
is still read — never written — and carried over:

- the **first** post-change request from a capability finds no derived row and
  **adopts** the old one, counters intact;
- **later** ones find the derived row and **fold** the old one into it, summing
  the counters and retiring the old row.

Summing is exact rather than generous, because a capability only ever
incremented its own counters: the non-zero counters of two old rows are
disjoint. A retired row is invisible to every lookup, so it can never be counted
twice, and it is retired rather than deleted because uploads, messages and votes
point at it — the evening it describes really happened.

**The one case that cannot be reconciled** is a capability the browser never
returns to after the upgrade. Its old row keeps its counters and is never
folded in, because the browser only ever presents the cookie for the path it is
requesting — the server cannot prove the others exist. Nothing is duplicated and
nothing the guest is *using* is reset; a capability they abandon simply keeps a
row nobody reads. Adoption also belongs to session-establishing operations only,
so a legacy cookie can never turn a vote into a voter.
