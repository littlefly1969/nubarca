# The Party workspace

How the host's side of a party is organised, and the decisions that are easy to
undo by accident. Guest-facing behaviour is described by
[party-rsvp.md](party-rsvp.md), [party-attendance.md](party-attendance.md) and
[party-game/README.md](party-game/README.md); this is the owner's product.

## One map, from the first draft to the last photograph

A party offers **seven sections**, in this order, in every phase:

| Section | What a host comes here to do |
| --- | --- |
| Riepilogo | Find out what to do now |
| Esperienza | Write what the guests will see |
| Ospiti | The guest list, the invitations, and the door |
| Foto | Where the photographs live, and who may add to them |
| Attività | The game and the greetings |
| Schermi e stampa | The television, a paired display, the printer |
| Impostazioni | The party's own details, its closing times, removing it |

**Live** is added while the party is happening and removed when it ends. It is
the only section that comes and goes.

The point of a stable map is that a host learns it once. What changes with the
lifecycle is which section they land on, what each one leads with, and which
steps are still open — never the shape of the product. There is deliberately no
`PartyType`, no mode switch and no "advanced" layout.

`frontend/src/party/workspace/partyWorkspaceModel.ts` decides all of it as pure
functions over the party, its album settings, its guest content and its guest
counts. Two surfaces therefore cannot disagree about whether the invitation has
been written, and the information architecture is testable without rendering
anything.

### Where a host lands

`draft`, `published` and `ended` open on **Riepilogo**. `live` opens on
**Live**: standing in a room full of people, the door is the page.

`?section=` carries the open section. The pre-release `?tab=` values still land
(`overview`→Riepilogo, `before`/`after`→Esperienza, `live`→Attività), because
the old "Live" tab was the evening's *configuration*, which now lives in the
sections that own each decision.

## Riepilogo answers one question

**What do I do now.** It is not a dashboard: there is no metric on it a host
cannot act on, and no card that exists to fill a column.

1. **The next move**, and only one. `primaryIntent` never offers a move the
   party cannot make — a draft with no album is not one button away from a
   party, so the summary offers the step that actually unblocks it. While the
   album's settings have not arrived it says `unknown` and renders a
   placeholder, because "publish it" would be a guess.
2. **The state, said separately from the action.** "Pubblicata" and "Avvia la
   festa" are two different facts and are allowed to disagree. A host must
   never have to read a button to learn where their evening is.
3. **What is wrong**, as distinct from what is not done yet. A draft missing
   its album is a *step*; a published party missing one is a *problem*. That
   difference is what stops the summary crying wolf on every new party.
4. **What is left to do**, including what is already done — a checklist that
   hides its completed items cannot be used to confirm the evening is ready.
5. **The link**, once there is one to give away.

## An open party is a complete party

A guest list is optional. A party with no invitation groups, no RSVPs and an
open door is a first-class shape, not an unfinished one:

- the guest list is described as optional rather than missing;
- while the party runs, the number offered is **presenze registrate**, and
  "Attesi 0 · Mancano 0" is never shown;
- "invitations sent" is an optional step when there is nobody to invite.

A **mixed** party — a guest list, some RSVPs, and people who simply turned up —
needs no mode and no switch. It is what the same surfaces show when all three
are present.

## Live is a console, not a badge

While the party is on, the Live section leads with the arrivals, large, because
"how many are here" is the question a host is asked every ten minutes and
cannot answer from memory. Below that: one tap to the door, the guest console's
**own filters** as shortcuts into it, and everything else as a list of places
each saying what is waiting there.

It rebuilds none of the guest list. Recording arrivals is the console's job;
Live links into it, already filtered.

Cooperative attendance is stated where it matters: an invited guest can record
their own arrival from their personal invitation, and the host's check-in is
the fallback and the correction — not a reception desk two hundred people have
to queue at.

## What the workspace asks the guest directory for

**Numbers, and names only when the host opens Ospiti.**

Every section but the console reads the directory with `take: 0`, which is the
counts alone. No card, no person and no name reaches a surface that is not the
console. The console itself reads the list in pages, and its search travels in
a POST body — never in a URL. See
[`partyGuestDirectory.ts`](../packages/contracts/src/partyGuestDirectory.ts).

A pre-release link can still carry `?guestSearch=`. The workspace strips it on
arrival whatever the section, and again whenever the host leaves Ospiti,
replacing the history entry so it is not one Back away either. It is removed,
never read.

## Identity is never inferred

The three identities stay three. Nothing in the workspace binds a
`PartyGuest` to a `PartyParticipant` by name, address, telephone, cookie, device
or QR, and no copy suggests it could:

- the public link opens the ordinary guest party. Whoever follows it joins the
  evening; they are **not** marked as arrived and do **not** join the guest
  list. The share card says so.
- a personal invitation carries RSVP, "Sono qui" while the party is live, and
  then a way into the public party. That last step is navigation.

RSVP and attendance are separate throughout: `attending + arrived`,
`attending + not arrived`, `pending + arrived` and `declined + arrived` are all
real, and a check-in never changes an RSVP.

## Sharing is one link on three channels

Email, WhatsApp and "copy link" hand out the **same** personal invitation link.
They are not three capabilities. Email has `pending` / `sent` / `failed`;
WhatsApp and copy record `shared`, which means NubArca handed the link to the
host — never that a message was sent, delivered or read. The card's status line
and its primary action are allowed to disagree: the line says where the
invitation stands, the button says what pressing it would do.

## The design system

`frontend/src/party/workspace/PartyWorkspace.css` and `workspace/ui.tsx` carry
the party's whole vocabulary: panels, rows, switches, notices, empty states,
steps, badges, statistics, chips and moderation rows. Everything a host sees is
assembled from them, so "a card with a title and two buttons" is one thing in
the product rather than eleven slightly different ones.

Rules kept by construction rather than by review:

- **Mobile first, literally.** Every rule is written for a 320px phone; the
  wider layouts are additions at 48rem and 64rem. There is no desktop grid
  being squeezed.
- **44px targets.** A control that carries a decision gets 48. The switch is a
  full 44px box with its track drawn inside as pseudo-elements, because a
  switch that *looks* 28px tall is 28px tall to a thumb.
- **State is never colour alone.** Every badge and every notice says the word.
- **One primary action per decision**, at most one clearly distinguishable
  secondary, and the rest in an overflow.
- **A number appears only where it is the information**, always in a `<dl>`
  with its label.

Panels written before the workspace — the guest console, the RSVP questions,
the print settings, the game deck — are **re-dressed rather than rewritten**. A
block scoped to `.pw` makes `row-action`, `party-card`, `field` and `muted`
render as their `pw-` equivalents and leaves geometry to whoever owns it, so
the console keeps its own 44px targets. Nothing outside the party is affected.

### Where "the top of the screen" is

On a phone the section rail is itself stuck to the top of `.app-main`, so
anything else that sticks at `top: 0` slides underneath it. The rail publishes
`--pw-sticky-top` and everything sticky inside the workspace offsets by it;
from 64rem the rail becomes a column and the offset goes back to zero. This is
what keeps the guest console's search visible at the door.

## Verifying it

jsdom has no layout engine, so the suite proves structure and behaviour and can
say nothing about overflow, target sizes or gutters. Those are measured in a
real browser:

```bash
cd frontend
PARTY_FIXTURE_DIR=/tmp/party npx vitest run --config vitest.fixtures.config.ts
PARTY_FIXTURE_DIR=/tmp/party node scripts/check-party-workspace-layout.mjs \
  --screenshots /tmp/party-shots
```

The fixtures render the **real** components against mocked responses — twelve
states across the whole lifecycle, including an empty draft, an open party with
no guest list, and the live console — and the script opens each in headless
Chromium with the real stylesheets at 320, 375, 430, 820 and 1440, asserting
that nothing overflows, no target is under 44px, the side gutter holds and two
sticky regions never cover each other. It is a dev/QA tool, not a CI job: it
needs a Chromium binary (`CHROME_BIN`, or Playwright's cache).

The fixtures contain no production data.
