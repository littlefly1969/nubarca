import type { TvPartyMessage } from '../api/tv';

// Party guest MESSAGES presentation policy — pure, node-testable, no React.
//
// Two surfaces share one feed:
//
//   RIBBON — a quiet band across the bottom holding ONE message at a time,
//            still (never a ticker), swapped on a fixed rotation.
//   HERO   — a full-screen card, inserted BETWEEN two media in an autoplaying
//            slideshow, only for messages the owner or their delegate promoted.
//
// Everything that decides WHEN either appears lives here rather than in the
// screen, because the interesting cases — a Hero falling due during a video, a
// message disappearing mid-rotation, a face filter running — are exactly the
// ones that are impossible to exercise on a device and trivial to exercise as
// functions.

// How long one ribbon message holds the band. Long enough to read 120
// characters at a distance without turning the band into something that
// demands attention.
export const RIBBON_ROTATE_MS = 7_000;

// How often the message feed is polled. Faster than the 15s media poll on
// purpose: "I type on my phone and it appears on the TV" is the whole point of
// the feature, and the response is a few hundred bytes of JSON.
export const MESSAGES_POLL_MS = 5_000;

// A Hero card is inserted after this many media transitions, and holds the
// screen for this long.
export const HERO_EVERY_N_BOUNDARIES = 10;
export const HERO_DURATION_MS = 6_000;

// ------------------------------------------------------------------ the feed

// True when both lists are the same messages in the same order — lets a poll
// skip the state update, and therefore the re-render, when nothing changed.
// Hero promotion is part of the comparison: a message that became a Hero is a
// changed feed even though the id list is identical.
export function sameMessages(a: TvPartyMessage[], b: TvPartyMessage[]): boolean {
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i += 1) {
    if (a[i].id !== b[i].id) return false;
    if (a[i].isHero !== b[i].isHero) return false;
    if (a[i].text !== b[i].text) return false;
    if (a[i].displayName !== b[i].displayName) return false;
  }
  return true;
}

// The messages eligible to be Heroes, in promotion order. `heroPromotedAt` is
// what makes this deterministic: without it two Heroes promoted in the same
// second would rotate in whatever order the feed happened to arrive in.
export function heroCandidates(messages: TvPartyMessage[]): TvPartyMessage[] {
  return messages
    .filter((m) => m.isHero)
    .slice()
    .sort((a, b) => {
      const at = a.heroPromotedAt ?? '';
      const bt = b.heroPromotedAt ?? '';
      if (at !== bt) return at < bt ? -1 : 1;
      // A tie on the timestamp falls back to the id, so the order is total and
      // does not depend on the server's row order.
      return a.id < b.id ? -1 : 1;
    });
}

// --------------------------------------------------------------- the ribbon

// After a refresh, the index that keeps the SAME message on the band. This is
// the rule that stops a new arrival from yanking the ribbon back to the first
// message every five seconds: the poll changes the list, not what is being
// read. When the current message is gone — hidden, rejected, or the party
// revoked — the clamped previous position moves on to the next eligible one.
export function remapRibbonIndex(
  messages: TvPartyMessage[],
  currentId: string | undefined,
  previousIndex: number,
): number {
  if (messages.length === 0) return 0;
  const found = currentId ? messages.findIndex((m) => m.id === currentId) : -1;
  if (found >= 0) return found;
  return Math.min(Math.max(previousIndex, 0), messages.length - 1);
}

// Whether the ribbon should be on screen at all.
//
// The MENU overlay wins: it puts QR codes in the lower corners, which is
// exactly where the band lives, and a party guest who pressed MENU is trying to
// read a QR code. The ribbon comes back when the overlay closes — and because
// the rotation index is held in state rather than reset, it comes back on the
// message they were reading.
export function ribbonVisible(input: {
  partyEnabled: boolean;
  messageCount: number;
  overlayVisible: boolean;
  heroVisible: boolean;
}): boolean {
  return input.partyEnabled
    && input.messageCount > 0
    && !input.overlayVisible
    // A Hero is the same message-shaped content at full size; showing both at
    // once would say the same thing twice.
    && !input.heroVisible;
}

// Whether the ribbon should be rotating. A single message simply stays put:
// crossfading a message into itself is a flicker with no information in it.
export function ribbonRotating(input: {
  visible: boolean;
  messageCount: number;
}): boolean {
  return input.visible && input.messageCount > 1;
}

// ----------------------------------------------------------------- the hero

// May a Hero be inserted right now?
//
// Every clause is a case that would otherwise produce a wrong interruption:
//
//   slideshowMode/playing — a Hero belongs to the AUTOPLAY wall. Someone who
//       opened one photo to look at it, or paused the wall, did not ask for an
//       editorial card to appear over it.
//   faceFilterActive — a guest asked to see the photographs they are in. That
//       is an explicit request, and a greeting card is not an answer to it.
//       The ribbon may keep running; only the full-screen interruption stops.
//   candidates — nothing to show.
export function heroEligible(input: {
  partyEnabled: boolean;
  slideshowMode: boolean;
  playing: boolean;
  faceFilterActive: boolean;
  candidateCount: number;
}): boolean {
  return input.partyEnabled
    && input.slideshowMode
    && input.playing
    && !input.faceFilterActive
    && input.candidateCount > 0;
}

// The Hero rotation cursor. `lastShownId` is the only thing carried between
// appearances, which is what makes the rotation survive a feed that changes
// under it: the next Hero is simply the one AFTER that id in promotion order,
// and if that message is gone the position it used to hold is.
export interface HeroRotation {
  readonly lastShownId: string | null;
}

export function beginHeroRotation(): HeroRotation {
  return { lastShownId: null };
}

export interface HeroPick {
  readonly message: TvPartyMessage | null;
  readonly rotation: HeroRotation;
}

// Pick the next Hero, fairly. The whole cycle of promoted messages is shown
// before any of them repeats — so the most recently promoted one does not
// monopolise the wall, and one promoted at the start of the evening does not
// starve behind it.
export function nextHero(
  rotation: HeroRotation,
  messages: TvPartyMessage[],
): HeroPick {
  const candidates = heroCandidates(messages);
  if (candidates.length === 0) {
    // Keep the cursor: a Hero demoted and re-promoted mid-party should resume
    // the cycle rather than restart it.
    return { message: null, rotation };
  }

  const lastIndex = rotation.lastShownId
    ? candidates.findIndex((m) => m.id === rotation.lastShownId)
    : -1;
  // A cursor pointing at a message that no longer qualifies lands on -1, and
  // -1 + 1 is 0 — the cycle restarts from the beginning rather than stopping,
  // which is what keeps a demotion from silently ending the Hero rotation.
  const next = candidates[(lastIndex + 1) % candidates.length];
  return { message: next, rotation: { lastShownId: next.id } };
}

// ------------------------------------------------------- the boundary policy

// What a media transition should do. The screen calls this at EVERY point the
// slideshow would advance — a photo's dwell elapsing, a video ending, a video
// reaching its cap — and does what it is told.
export type BoundaryOutcome =
  // Advance to the next media, as usual.
  | { readonly kind: 'advance'; readonly boundariesSinceHero: number }
  // Hold this media and put a Hero card over it for HERO_DURATION_MS. The media
  // index is NOT touched, which is what guarantees the carousel resumes exactly
  // where it was with nothing lost and nothing repeated: when the card
  // finishes, the screen advances normally.
  | { readonly kind: 'hero'; readonly boundariesSinceHero: number };

// Decide what happens at one media boundary.
//
// `currentIsVideo` is deliberately NOT consulted here: this function is called
// AT a boundary, and a boundary on a video is its natural end (or its
// configured cap), so the video has already finished playing. There is no
// timer anywhere that shortens a video to make room for a message — the Hero
// simply waits for the boundary the video was going to reach anyway, which may
// be several media later if the wall is showing a run of long clips.
export function onMediaBoundary(input: {
  boundariesSinceHero: number;
  eligible: boolean;
}): BoundaryOutcome {
  const count = input.boundariesSinceHero + 1;
  if (input.eligible && count >= HERO_EVERY_N_BOUNDARIES) {
    // The counter resets when the card is SHOWN, not when it next falls due, so
    // a long ineligible stretch (a face filter, a paused wall) does not bank up
    // a queue of Heroes that all fire the moment it ends.
    return { kind: 'hero', boundariesSinceHero: 0 };
  }
  return { kind: 'advance', boundariesSinceHero: count };
}
