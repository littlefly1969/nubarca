# Party attendance

Who actually arrived at a party — recorded by the host, or by a guest's own group
from its personal invitation. The same feature serves an open party with no guest
list, an invited party, and a mixed one; none of them is a party "type".

This document states what the domain is, what it deliberately is not, and the
rules that are easy to break by accident. The guest list and RSVP it builds on are
described in [party-rsvp.md](party-rsvp.md).

## Four facts about people, and they stay four

| | What it is | Written by | Where it lives |
|---|---|---|---|
| **RSVP** | What an invited person *declared* before the party | The group, on its personal invitation | `PartyRsvp` |
| **Guest arrival** | That a person *on the guest list* arrived | The host, or the person's own group while the party is live | `PartyGuestAttendance` |
| **Other arrival** | That somebody *not on the guest list* arrived | The host | `PartyAttendanceGuest` |
| **Participant** | The anonymous *browser* that uses the public party | The public party runtime | `PartyParticipant` |

```text
PartyGuest ≠ PartyAttendanceGuest ≠ PartyParticipant
RSVP attending ≠ arrived
arrived ≠ PartyParticipant
public QR ≠ attendance
```

- **An arrival never changes an RSVP.** `attending + arrived`, `attending + not
  arrived`, `pending + arrived` and `declined + arrived` are all valid. A guest who
  declined and came keeps `declined` as the historical answer and gains an arrival.
- **Scanning the party's QR is not an arrival.** The phone that scans it becomes a
  `PartyParticipant` through the existing browser-based mechanism, and nothing else.
- **Nothing binds a person to a browser.** No arrival is ever created, looked up or
  linked by name, email, phone, QR, cookie, IP or device; no `PartyGuestId` exists
  on `PartyParticipant` and no participant id on either attendance table.

## The guest list is optional

There is no `PartyType`, and no open/invite/hybrid enum. What a party is follows
from its rows:

```text
0 invitation groups                      → open party
invitation groups                        → invited party
invitation groups + PartyAttendanceGuest → mixed party
```

No Party feature requires an invitation group, a guest or an RSVP. An open party's
QR, public page, uploads, game, print and TV work exactly as they always did, and
the host may still record people by hand. An invited party becomes a mixed one the
moment somebody not on the list is recorded, with no mode to switch.

## The schema

One migration, `AddPartyAttendance`, two tables, both foreign keys `Restrict`:

- `party_guest_attendance` — `PartyGuestId` (primary key and foreign key to
  `party_guests`), `CheckedInAt`, `Source`, `CreatedAt`. One row = arrived; no row =
  not recorded as arrived. The key is what makes a person arrive once. `Source` is a
  closed vocabulary held by a check constraint: `owner` or `invitation`. A future
  source (a scanner, a kiosk) is a migration that widens the constraint and a slice
  that decides what it may do — never a free string.
- `party_attendance_guests` — `Id`, `PartyId` (foreign key to `parties`), `Name`
  (1–120 code points), `ClientRequestId`, `CheckedInAt`, `Version` (≥ 1),
  `CreatedAt`, `UpdatedAt`; a unique index on `(PartyId, ClientRequestId)`.

Deliberately absent: status, check-out, `LeftAt`, "inside", occupancy, participant
id, token, device and IP. This records **arrivals**, not presence.

**The migration needs manual release review.** It is classified `automated: false`,
`previousApplicationCompatible: false`: additive, but once an arrival is recorded
the previous backend's teardown, group removal and named-guest removal do not know
these tables and their restricting keys refuse its deletes. Cascades are not
introduced to paper over it. The guided updater refuses it by design.

## Lifecycle

| Party | Read (host) | Host writes | Group's "Sono qui" |
|---|---|---|---|
| Draft | yes | `409 attendance_not_open` | the personal link opens nothing (404) |
| Published | yes | `409 attendance_not_open` | `409 attendance_not_open` |
| Live | yes | yes | yes |
| Ended | yes | yes — corrections and forgotten arrivals | `409 attendance_not_open` |

Recording an arrival never publishes or starts a party.

## The host's routes

All require `party.access` and are owner-scoped in every query; a foreign party,
guest or recorded person is the same 404 as a missing one, including another
party's guest id on the host's own party.

```text
GET    /api/parties/{partyId}/attendance
PUT    /api/parties/{partyId}/attendance/guests/{guestId}
DELETE /api/parties/{partyId}/attendance/guests/{guestId}
POST   /api/parties/{partyId}/attendance/other-guests
PUT    /api/parties/{partyId}/attendance/other-guests/{id}
DELETE /api/parties/{partyId}/attendance/other-guests/{id}
```

- **`PUT` a guest = "this person arrived".** Idempotent: the first successful
  check-in sets `CheckedInAt` and `Source`, and every later one — from the host or
  from the group — changes neither. Two concurrent check-ins leave one row; the
  primary key is the boundary.
- **`DELETE` a guest = "that arrival was recorded by mistake"**, whoever recorded
  it. Never "they left". A second `DELETE` changes nothing.
- **`POST` an other arrival** carries `{ name, clientRequestId }`. The browser mints
  the id once per add and reuses it for that add's retries, so a double tap or a
  lost answer names the person once (the unique index is the boundary); a different
  name is a different add. Two people may share a name.
- **`PUT` an other arrival** renames it quoting `version`; a stale version is
  `409 version_conflict` carrying the attendance as it is now. `DELETE` removes a
  mistaken one.
- Every answer is the whole attendance; every refusal that describes a state is a
  `409` carrying it as `attendance`. A client that pages the guest list adds
  `Prefer: return=minimal` and receives `{ changed, summary, guest | otherGuest }`
  instead — what changed, the counts, and the one person as the record now reads
  them — so a tap at the door never downloads a thousand groups. The route, its
  authorization, its audit and its refusals are the same either way.

### The projection

`partyId`, `partyStatus`, `canEdit`, `summary`, `groups` (each with `groupId`,
`label` and its people: `guestId`, `name`, `isAdditionalGuest`, `rsvpStatus`,
`checkedInAt`, `checkInSource`) and `otherGuests` (`id`, `name`, `checkedInAt`,
`version`, latest first). No email, phone, dietary note or answer: the door needs
a name and whether that person is here.

### The counts, one definition each (`PartyAttendanceSummaryDto`)

| Count | Definition |
|---|---|
| `expectedPeople` | guests (named or +1) whose RSVP is `attending` |
| `expectedArrived` | of those, arrived |
| `expectedMissing` | of those, not arrived |
| `unexpectedKnownGuests` | guests on the list who arrived with RSVP `pending` or `declined` |
| `otherArrivals` | `PartyAttendanceGuest` rows |
| `totalArrivals` | every guest who arrived + every other arrival |

An open party has `expected* = 0` and `totalArrivals = otherArrivals`. This is a
separate projection from `PartyRsvpSummaryDto`, which describes what was declared.

## Cooperative check-in: "Sono qui"

While the party is **live**, a group's personal invitation shows each of its own
people with **Sono qui**, and — once recorded — the time and, for the group's own
mark, **Annulla**:

```text
PUT    /api/party-invitations/{token}/attendance/guests/{guestId}
DELETE /api/party-invitations/{token}/attendance/guests/{guestId}
```

- The token resolves exactly as the invitation's other routes do; the guest id must
  belong to **that token's group**. Another group's person, another party's, and
  an unknown id are one generic 404.
- It writes the **same** `PartyGuestAttendance` row the host's check-in writes, with
  `Source = invitation`; host and group converge on one row, even concurrently.
- A group takes back only **its own** mark. An arrival the host recorded is refused
  with `409 attendance_recorded_by_host` — it is the host's record to correct.
- The answer is the invitation view: the group's own people, each with its own
  `checkedInAt` and `checkInSource`. Never the summary, another group, anybody
  recorded at the door, or anybody else's arrival.
- It creates, reads and binds no `PartyParticipant`, sets no cookie, grants no new
  capability, and is rate-limited like a reply (`party-rsvp`, per IP).

The host's buttons remain as the fallback and the correction. The host's view
reflects a group's "Sono qui" on its next read (a mutation, a guest-list change or
**Aggiorna**) — there is no realtime, polling loop, SignalR or WebSocket.

## "Entra nel Party"

While the party is live, the invitation view carries `party.partyUrl` — the
party's own public page, `/party/{token}` — **only if that page really opens now**:
it is judged by the same `ResolvePublicAsync` the room's QR goes through, so a
party with no QR, a disabled, revoked or expired link, or a host who may no longer
run parties simply has no button. "Sono qui" never depends on it.

It is navigation, not identity:

```text
invitation → PartyGuest → "Sono qui" → PartyGuestAttendance
→ "Entra nel Party" → public party → PartyParticipant (the ordinary browser way)
```

An invited guest who entered from the invitation and a guest who scanned the QR
hold the same capability, the same quotas and the same rules — it is literally the
same address. The only difference is that the invited guest also has the nominal
fact of an arrival. The link hands over nothing the room's QR does not already
show to everyone at the party.

## Privacy

Attendance is owner-private. It never reaches the public party, the TV, the game,
print, face search, a link preview, guest messages, a log line or an audit record.
The personal invitation sees only its own group's people and their own arrivals.
Audit lines (`party.attendance.check_in`, `.undo`, `.other_create`,
`.other_update`, `.other_delete`) carry the party, a guest or recorded person by id,
and the source of a check-in — never a name. Only a request that changed something
is recorded. Owner attendance JSON and the invitation's are `Cache-Control:
no-store`.

## Duplicate and teardown

- **Duplicating a party copies no attendance** — neither kind.
- **Teardown** (`PartyStateEraser`) erases guest arrivals with the guest list, in
  foreign-key order (`PartyGuestAttendance` → `PartyGuest`), and other arrivals
  before the root (`PartyAttendanceGuest` → `Party`).
- **Removing a group, or editing a named guest off it**, removes that person's
  arrival with them.

## Where it lives in the UI

Inside the owner's **Ospiti** tab (renamed from "Invitati": a party may invite
nobody). There is no separate Attendance tab: the same console shows the guest
list before the party and becomes the door once it is live, reading both from
the guest directory a page at a time (see
[party-rsvp.md](party-rsvp.md#the-guest-directory-the-list-one-page-at-a-time)).

- **Before the party, open** — "La festa è aperta": the QR admits anybody, and the
  guest list is offered as an option, never as a prerequisite.
- **Before the party, invited** — the cards carry the invitation: who is in the
  group, how they answered, and where their invitation stands.
- **Live or Ended, open** — *Presenze registrate: N*, *Aggiungi persona*, and a note
  that this is who was recorded, not a head count of everyone at the party.
- **Live or Ended, invited or mixed** — *Attesi · Arrivati · Mancano · Altri arrivi*,
  one search (guest name, group label, either address or number, an other
  arrival's name) answered by the server, the filters *Tutti · Da arrivare ·
  Arrivati · Inattesi*, and every person one tap from being recorded as arrived —
  from the card itself, without opening the group. Other arrivals are listed
  first, latest first, so somebody just recorded is at the top.

`Altri arrivi` = `unexpectedKnownGuests + otherArrivals` — exactly what the
*Inattesi* filter shows — so *Arrivati* = (*Attesi* − *Mancano*) + *Altri arrivi*.

## Deliberately absent

No personal check-in QR, scanner, barcode, NFC, ticket, kiosk or staff role; no
automatic check-in from the public QR; no check-out, "inside" or occupancy; no
seating; no SMS or WhatsApp; no guest ↔ participant binding; no realtime; no party
type.

## Known limitation

Removing a group (or editing a named guest off it) and a check-in of one of its
people in the same instant are not serialised against each other: if the check-in
commits between the removal's arrival delete and its guest delete, the removal
fails on the restricting key and the host retries it. Closing it would need the
check-in to lock the group row on every tap, for a race nobody has to lose in
practice.
