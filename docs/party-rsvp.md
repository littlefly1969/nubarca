# Party guest list, personal invitations and RSVP

The Before half of a party: the host keeps a guest list, emails each group a
personal invitation, and each group answers — person by person, with its +1s,
its dietary notes and the host's questions — without a NubArca account. When the
party goes live, nothing about the live party changes: the party's own QR and the
anonymous browser identity behind it work exactly as they did.

This document states what the domain is, what it deliberately is not, and the
rules that are easy to break by accident.

## Three identities, and they stay three

| | What it is | What it opens | Where it lives |
|---|---|---|---|
| **Invitation group** | Who the host invited: "Mario", "Mario e Laura", "Famiglia Rossi" | That group's own invitation and reply — nothing else | `PartyInvitationGroup` + its personal token |
| **Public party capability** | The party's QR | The public party experience, phase by phase | `PartyAlbumLink` |
| **Runtime browser identity** | The anonymous phone at the party | Quotas for uploads, greetings, votes, prints | `PartyParticipant` |

- The guest list — groups, people, RSVPs, answers, the delivery ledger — is a set
  of **private Before-domain facts**, owned by the host.
- `PartyParticipant` **remains the anonymous runtime browser identity**. Opening a
  personal invitation or replying to it creates no participant and sets no cookie.
- **No automatic binding exists** between a `PartyGuest` and a `PartyParticipant`.
  Nothing infers one from an email, a name, an IP, a cookie or a device. The person
  who answered "Mario, attending" and the phone that scans the room's QR are two
  separate facts. `PartyGuest.Id` is stable, so a future, proof-based binding can
  be added when a feature needs one; there is deliberately no unused column for it.

## The schema

Six tables, one additive migration (`AddPartyGuestListRsvp`), every foreign key
`Restrict`:

- `party_invitation_groups` — label, recipient email, optional phone (owner
  records only, nothing sends to it), `MaxAdditionalGuests` (0–10), the current
  capability generation (`CapabilityId`, `TokenHash`, `CapabilityIssuedAt`) and
  `Version`, the concurrency boundary of the whole RSVP aggregate.
- `party_guests` — one person; `IsAdditionalGuest` marks a +1 the group added.
  Membership IS the invited fact, so there is no `Invited` flag.
- `party_rsvps` — one per person (the key is the guest id): `pending`,
  `attending`, `declined`, dietary notes, `RespondedAt` (first answer only).
- `party_rsvp_questions` — the host's questions, closed kinds, frozen once answered.
- `party_rsvp_answers` — one per group per question, canonical JSON.
- `party_invitation_deliveries` — the outbound ledger: kind, status, the capability
  generation the email carried, the caller's request id. No address, no body, no
  SMTP reply, no token.

Check constraints hold every closed vocabulary, the +1 ceiling, the 64-character
token hash, "options exist exactly for a single choice", and "a completed delivery
has a completion time". A unique index on the token hash is the public lookup; a
unique index on `(group, request id)` is the idempotency rule.

## The personal token

- `raw = base64url(HMAC-SHA256(Party:TokenSecret, CapabilityId ‖ "invitation-rsvp"))`.
  Keyed like the party's own tokens, bound to its purpose by the context string,
  and derived from a random id that is never exposed. It is **not** the party's
  public token and shares nothing with `PartyAlbumLink`.
- **The raw token never persists and is never logged**, and neither is its hash
  or a URL containing it. The database stores `SHA-256(raw)` only.
- **Rotation** replaces the capability id and hash; every link sent before stops
  resolving at once. The owner can rotate explicitly, and **changing the recipient
  email rotates automatically** (case-insensitive: the case of an address is not a
  different mailbox). Removing a group removes its hash with it.
- The guest route is `/api/party-invitations/{token}` and the page is
  `/party/invite/{token}` — never under `/api/party/{token}`, so no route, cookie
  path or log line can mistake one capability for the other.

### What the token opens, checked on every request

The token resolves only by the current hash; the party's phase and windows are
judged by the same `PartyGuestExperience` the QR uses (a Draft is nothing), and the
host's `party.access` by the same `IPartyCapabilityPolicy`. Unknown, rotated,
removed, Draft, closed and permission-revoked all collapse to one generic 404.

**The RSVP token grants no live powers.** It opens the party's public face — title,
date, cover, the guest-content slots of the current phase — and that group's own
people, answers and questions. Upload, greetings, the game, printing and face
search do not accept it and are not drawn for it. The cover is the party's own
cover choice (the same `PartyCoverPolicy` the QR's page uses), else the album's
**chosen** cover only — this link was never a way into the album, so it never
falls back to whichever photograph sorts first. Slot photographs and covers are
served on invitation-scoped routes through the one `ServeAuthorizedDerivativeAsync`
path: derived, metadata-stripped, never an original. A party with no album is an
ordinary state: the invitation works from its metadata and its slots.

## Replying

- **Writable only while the party is `published`.** Live and Ended still show the
  invitation and the group's answers, read-only (`409 rsvp_closed` on a write).
- **The whole form, every time.** The server validates the entire graph before
  writing any of it: every named guest exactly once, known statuses, never back to
  `pending` once answered, +1s within the allowance and named, answers only to
  this party's active questions, each of its kind. One transaction.
- **One version spent, first.** The write opens with
  `UPDATE party_invitation_groups SET Version = Version + 1 WHERE Id = @id AND
  Version = @quoted`. On PostgreSQL that takes the row lock; a second reply quoting
  the same version waits, re-evaluates against the committed row, matches nothing,
  and writes nothing — no half of its people, no half of its answers. A stale reply
  is `409 version_conflict` carrying the invitation as it now is.
- **Required means required of a group that is coming.** A group with at least one
  person attending must answer every required active question; a group declining
  whole is not asked which menu it will not eat. (This is a deliberate reading of
  "required answers must be present": enforcing it for a declining group would make
  declining harder than attending.)
- **A +1 comes with somebody**: +1s are refused unless a named guest is attending,
  and a group that declines drops its +1s. Removing a +1 deletes the person and
  their RSVP.
- An omitted optional answer clears the stored one; answers to a question the host
  has since retired stay as private history and are not on the guest's form.

## The host's list

Every owner route requires `party.access` and is owner-scoped in the query: the
party is matched on `OwnerUserId`, and a group or question on both its own id and
that party's, so a foreign object is the same 404 as a missing one.

- The editor states **named guests only**; the group's +1s are kept across every
  edit. Lowering `MaxAdditionalGuests` below the +1s already brought is refused
  (`additional_guests_in_use`).
- Every group mutation — edit, rotate, remove — spends the group's version through
  the same conditional statement a guest's reply uses, so the two can never
  silently overwrite each other.
- Search is client-side, over the list the page already holds.

**The counts, with one definition each** (`PartyRsvpSummaryDto`, contracts'
`PartyRsvpSummary`): *Invitati* = named guests; *Risposte mancanti* = named guests
still pending; *Confermati* = every guest, named or +1, attending; *Assenti* =
named guests who declined; *Persone attese* = Confermati. A group is unanswered
when a named guest is pending. None of it is attendance; nobody checked in.

### Questions

Three closed kinds — `short_text` (≤ 500 code points), `single_choice` (2–20
distinct options of ≤ 120, matched exactly), `yes_no` (a boolean) — at most 20
active per party, prompts ≤ 300. There is no registry, no conditional logic and no
sections. **Once any group has answered a question, its prompt, kind, required
flag and options are frozen**; only activation and position may change, and the
host deactivates it and asks a new one to change what is asked.

## Sending

The existing generic `IEmailSender` / `SmtpEmailSender` / `MailOptions` carry it;
`PartyInvitationEmail` is Party's own composer and recovery templates stay
recovery's. Plain text, no remote image, no tracking pixel, no tracking link, one
URL — the personal link, built on `Mail:PublicOrigin`, never on a request's Host.
The language is the host's persisted UI language; the date is the day in the
installation's timezone, never an hour (the page formats hours in the reader's
timezone, exactly as the link preview reasons).

**Send commands are idempotent through the delivery ledger.** The protocol, in
order: ownership; the `(group, ClientRequestId)` row — if it exists, it is the
answer and SMTP is not called, whatever its state; whether mail is available at
all (`mail_unavailable`, with nothing moved); whether this send is allowed now; a
`pending` row committed **before** SMTP; SMTP; the outcome. A process that dies
between SMTP and the outcome leaves `pending`, which the host sees as "esito non
confermato"; a retry of the same click still sends nothing, and pressing resend is
a new click, a new id and a new attempt. There is no repair scheduler. Once the
row exists, SMTP and the outcome finish whatever the browser does.

- `initial` when no invitation (initial or resend) has succeeded for the **current
  link generation**, `resend` otherwise. A rotation means old deliveries no longer
  count as "sent" for the new link.
- **The first send from a Draft publishes the party** through `PartyLifecycle`
  (`IPartyService.TransitionAsync`), quoting the party version the page read; a
  stale version is `party_version_conflict` and publishes and sends nothing. The
  response carries the party so the page adopts its status and version. If SMTP
  then fails, the party stays published: publication is lifecycle state, not proof
  of delivery. A send never starts the party.
- A **reminder** is refused unless the party is `published`, the current link
  generation has a successful invitation, and a named guest is still pending.
- Sends and reminders stop once the party is Live or Ended (`invitations_closed`).
- **A delivery never touches an RSVP**, whether it succeeds, fails or is uncertain.

## Rate limits

| Policy | Partition | Default | Configuration |
|---|---|---|---|
| `party-rsvp` (guest reply) | remote IP | 30 / 60 s | `RateLimits:PartyRsvp:*` |
| `party-invitation-send` (send, remind) | host account | 60 / 600 s | `RateLimits:PartyInvitationSend:*` |
| `party-public` (guest read) | remote IP | existing | existing |
| `party-public-media` (invitation media) | remote IP | existing | existing |

One call sends one email, so the send limiter bounds SMTP output. There is no
queue behind it.

## Privacy

Names, addresses, phone numbers, dietary notes and answers are owner-private,
except a group's own data on its own personal link. They never reach
`/api/party/{token}`, the TV, the game, the print studio, face search, public
media DTOs, a link preview, a log line or an audit record. Audit lines name the
party and, where relevant, a group by id, a delivery's kind and status, or a
question's kind — never its prompt. `PartyGuestListLifecycleTests` serializes the
public party context, its items, the game snapshot, the link preview, the TV
session and TV album surfaces and every audit line, and asserts none of the seeded
PII appears. `/party/invite/{token}` is a two-segment path, so the frontend's
link-preview rule (single-segment `/party/{token}` only) never draws a personalised
card for it.

Personal invitation and RSVP JSON are `Cache-Control: no-store`.

## Duplicate and teardown

- **Duplicating a party copies active question definitions only** — new ids, same
  prompt, kind, required flag, options and order, version 1, no answers. Never a
  group, a name, an address, a phone, a +1, an RSVP, a note, an answer, a personal
  link or a delivery.
- **Teardown explicitly erases the new rows** in foreign-key order —
  deliveries, answers, RSVPs, guests, groups, then questions — through
  `PartyStateEraser`, the single explicit list of what a party owns. Removing one
  group uses the same `EraseInvitationGroupsAsync`, and touches neither the party's
  public capability nor any participant.

## Deliberately absent

No scheduled or automatic reminders, no SMS or WhatsApp provider, no campaign or
newsletter system, no mail queue, no CSV import, no "maybe", no check-in, no
seating, no generic form builder, no sub-events, no automatic guest ↔ participant
binding, and no second invitation app: the personal invitation is the party's own
invitation surface (`PartyBeforeHome`) with the group's reply composed into it.

## Known limitation

A question's "frozen once answered" check and a guest's first answer to it are not
serialised against each other: an owner edit that commits in the same instant as
the very first answer can still change the question's wording. The window is the
duration of one statement; closing it would need a lock the guest's reply takes on
every question, for a race nobody has to lose in practice.
