// Party guest MESSAGES on the wall — pure, testable without a DOM.
//
// A PORT of tv/src/lib/partyMessages.ts: the same ribbon, the same Hero cards,
// the same boundary ledger. `nativeParity.test.ts` runs both against the same
// cases, so the browser and the app cannot disagree about when a greeting is on
// screen.
//
//   RIBBON — a quiet band across the bottom holding ONE message at a time,
//            still (never a ticker), swapped on a fixed rotation.
//   HERO   — a full-screen card, inserted BETWEEN two media in an autoplaying
//            slideshow, only for messages the owner or their delegate promoted.

import type { TvPartyMessage } from '@nubarca/api-client';

/** How long one ribbon message holds the band. */
export const RIBBON_ROTATE_MS = 7_000;

/** How often the message feed is polled — "I type it on my phone and it appears". */
export const MESSAGES_POLL_MS = 5_000;

/** A Hero card after this many media transitions, held for this long. */
export const HERO_EVERY_N_BOUNDARIES = 10;
export const HERO_DURATION_MS = 6_000;

/** Same messages in the same order, Hero promotion included. */
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

/** Promoted messages in promotion order, ties broken by id so the order is total. */
export function heroCandidates(messages: TvPartyMessage[]): TvPartyMessage[] {
  return messages
    .filter((m) => m.isHero)
    .slice()
    .sort((a, b) => {
      const at = a.heroPromotedAt ?? '';
      const bt = b.heroPromotedAt ?? '';
      if (at !== bt) return at < bt ? -1 : 1;
      return a.id < b.id ? -1 : 1;
    });
}

/**
 * After a refresh, the index that keeps the SAME message on the band: the poll
 * changes the list, not what is being read.
 */
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

/**
 * THE RIBBON CURSOR — the band's position AND the message on screen, held in
 * one piece of state, so a refresh reads exactly what was being read whatever
 * order React renders in. (An id kept in a ref written during render is
 * overwritten by the new feed first, and hiding an earlier greeting then skips
 * the one being read.)
 */
export interface RibbonCursor {
  readonly index: number;
  readonly shownId: string | null;
}

export const RIBBON_START: RibbonCursor = { index: 0, shownId: null };

/** The feed changed: stay on the message being read, by id. */
export function ribbonOnFeed(cursor: RibbonCursor, messages: TvPartyMessage[]): RibbonCursor {
  const index = remapRibbonIndex(messages, cursor.shownId ?? undefined, cursor.index);
  return { index, shownId: messages[index]?.id ?? null };
}

/** The rotation moved the band on by one. */
export function ribbonOnRotate(cursor: RibbonCursor, messages: TvPartyMessage[]): RibbonCursor {
  if (messages.length === 0) return RIBBON_START;
  const index = (cursor.index + 1) % messages.length;
  return { index, shownId: messages[index].id };
}

/** The band: something to say, no overlay in the lower corners, no Hero saying it already. */
export function ribbonVisible(input: {
  partyEnabled: boolean;
  messageCount: number;
  overlayVisible: boolean;
  heroVisible: boolean;
}): boolean {
  return input.partyEnabled
    && input.messageCount > 0
    && !input.overlayVisible
    && !input.heroVisible;
}

/** A single message stays put: crossfading a message into itself is a flicker. */
export function ribbonRotating(input: { visible: boolean; messageCount: number }): boolean {
  return input.visible && input.messageCount > 1;
}

/**
 * May a Hero be inserted right now? Only on the AUTOPLAY wall, never over a
 * face filter a guest asked for, and only when there is one to show.
 */
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

/** The Hero rotation cursor. Only the last shown id survives between appearances. */
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

/** The next Hero, fairly: the whole cycle before any repeats. */
export function nextHero(rotation: HeroRotation, messages: TvPartyMessage[]): HeroPick {
  const candidates = heroCandidates(messages);
  if (candidates.length === 0) return { message: null, rotation };
  const lastIndex = rotation.lastShownId
    ? candidates.findIndex((m) => m.id === rotation.lastShownId)
    : -1;
  const next = candidates[(lastIndex + 1) % candidates.length];
  return { message: next, rotation: { lastShownId: next.id } };
}

export type BoundaryOutcome =
  | { readonly kind: 'advance'; readonly boundariesSinceHero: number }
  | { readonly kind: 'hero'; readonly boundariesSinceHero: number };

/**
 * A media advance that a Hero postponed and has not been performed yet. A
 * LEDGER, not a rendering detail: a video that has already ended can produce
 * no further boundary, so whatever ends the card must be able to settle it.
 */
export interface BoundaryDebt {
  readonly owed: boolean;
}

export const NO_BOUNDARY_DEBT: BoundaryDebt = { owed: false };

export function deferBoundary(): BoundaryDebt {
  return { owed: true };
}

export function discardBoundary(): BoundaryDebt {
  return NO_BOUNDARY_DEBT;
}

export interface BoundarySettlement {
  readonly debt: BoundaryDebt;
  readonly advance: boolean;
}

/** THE ONLY function that can spend a debt — so a boundary is consumed at most once. */
export function settleBoundary(
  debt: BoundaryDebt,
  input: { heroVisible: boolean; slideshowMode: boolean; playing: boolean },
): BoundarySettlement {
  if (!debt.owed) return { debt, advance: false };
  if (input.heroVisible) return { debt, advance: false };
  if (!input.slideshowMode || !input.playing) return { debt, advance: false };
  return { debt: NO_BOUNDARY_DEBT, advance: true };
}

/**
 * What one media boundary does. The counter resets when a card is SHOWN, so an
 * ineligible stretch does not bank up a queue of Heroes.
 */
export function onMediaBoundary(input: {
  boundariesSinceHero: number;
  eligible: boolean;
}): BoundaryOutcome {
  const count = input.boundariesSinceHero + 1;
  if (input.eligible && count >= HERO_EVERY_N_BOUNDARIES) {
    return { kind: 'hero', boundariesSinceHero: 0 };
  }
  return { kind: 'advance', boundariesSinceHero: count };
}
