// The guest book on the party's television — pure, testable without a DOM.
//
// A PORT of tv/src/lib/partyGuestbook.ts, held to it by `nativeParity.test.ts`.
// When the regia puts the book on the screen the television shows ONE memory
// at a time: the photograph framed as its author framed it, the dedication in
// full, the signature. What decides HOW is the same on both televisions:
//
//   * how long a memory holds the screen, and what makes a dedication "long";
//   * how large the words are drawn (a DENSITY tier), so a long dedication is
//     shown whole instead of cut — nothing here ever truncates;
//   * the frame the template gives the photograph, and where the photograph
//     sits inside it (the guest's crop — the same maths as the party print);
//   * how the deck follows the book while it is on screen: new memories join,
//     hidden ones leave, and the one being read is never pulled away by a poll.
//
// Nothing here decides WHETHER the book is on the screen. That is the
// server's presentation, read from the control plane like the game's.

import { cropFor } from '../../pages/partyPrintGeometry';
import { guestbookTemplateFor } from '../../party/partyGuestbookTemplates';

/** How often the deck is re-read while the book is on the screen. */
export const GUESTBOOK_TV_POLL_MS = 10_000;

/** How long a memory holds the screen. */
export const GUESTBOOK_DWELL_MS = 12_000;
/** …and a memory whose dedication is long enough to need longer to read. */
export const GUESTBOOK_LONG_DWELL_MS = 18_000;

/**
 * A dedication is LONG past either bound: this many characters (code points,
 * the unit the server counts), or this many lines.
 */
export const GUESTBOOK_LONG_DEDICATION_CHARS = 220;
export const GUESTBOOK_LONG_DEDICATION_LINES = 5;

/** The cross-fade between two memories. Zero when the viewer asked for less motion. */
export const GUESTBOOK_TRANSITION_MS = 700;

function codePoints(text: string): number {
  return Array.from(text).length;
}

function lineCount(text: string): number {
  return text.length === 0 ? 0 : text.split('\n').length;
}

export function isLongDedication(body: string): boolean {
  return codePoints(body) > GUESTBOOK_LONG_DEDICATION_CHARS
    || lineCount(body) > GUESTBOOK_LONG_DEDICATION_LINES;
}

/** How long THIS memory holds the screen. */
export function guestbookDwellMs(body: string): number {
  return isLongDedication(body) ? GUESTBOOK_LONG_DWELL_MS : GUESTBOOK_DWELL_MS;
}

// ── How large the words are drawn ───────────────────────────────────────────

export type GuestbookDensity = 'airy' | 'regular' | 'compact' | 'dense';

/**
 * Each line break costs as much room as this many characters: a dedication of
 * short lines fills the column faster than its length says.
 */
export const GUESTBOOK_LINE_WEIGHT = 32;

/** The upper bound of each tier, in weighted characters. Above the last: dense. */
export const GUESTBOOK_DENSITY_LIMITS = { airy: 90, regular: 260, compact: 560 } as const;

/** The dedication's size as the column feels it. */
export function guestbookTextWeight(body: string): number {
  return codePoints(body) + Math.max(0, lineCount(body) - 1) * GUESTBOOK_LINE_WEIGHT;
}

export function guestbookDensity(body: string): GuestbookDensity {
  const weight = guestbookTextWeight(body);
  if (weight <= GUESTBOOK_DENSITY_LIMITS.airy) return 'airy';
  if (weight <= GUESTBOOK_DENSITY_LIMITS.regular) return 'regular';
  if (weight <= GUESTBOOK_DENSITY_LIMITS.compact) return 'compact';
  return 'dense';
}

/**
 * The dedication's type size on a 1080-line screen, per tier. A renderer
 * scales it to its own height and then SHRINKS it further if the words still
 * do not fit — a dedication is always shown whole.
 */
export const GUESTBOOK_TV_FONT_PX = { airy: 52, regular: 42, compact: 33, dense: 26 } as const;

export function guestbookTvFontPx(stageHeight: number, density: GuestbookDensity): number {
  return Math.round(GUESTBOOK_TV_FONT_PX[density] * (stageHeight / 1080));
}

/**
 * How tall the words' column is. As tall as the photograph — the composition
 * the four designs were drawn for — except for a DENSE dedication, which gets
 * the whole stage height beside a centred photograph: a long text needs room
 * more than it needs symmetry.
 */
export function guestbookTvWordsHeight(
  stageHeight: number, photoHeight: number, density: GuestbookDensity,
): number {
  return density === 'dense' ? Math.max(photoHeight, Math.round(stageHeight)) : photoHeight;
}

/**
 * The widest share of the stage the words may take, per tier. A short
 * dedication beside a tall photograph makes a compact card centred on the
 * screen, instead of a line lost in an empty column.
 */
export const GUESTBOOK_TV_WORDS_SHARE = { airy: 0.4, regular: 0.45, compact: 0.52, dense: 0.58 } as const;

/** How wide the words' column is: what the photograph leaves, up to the tier's share. */
export function guestbookTvWordsWidth(
  stageWidth: number, photoWidth: number, density: GuestbookDensity,
): number {
  return Math.round(Math.max(0, Math.min(stageWidth - photoWidth, stageWidth * GUESTBOOK_TV_WORDS_SHARE[density])));
}

/**
 * The measured fit: while the words do not fit their column they are drawn
 * this much smaller, down to this floor. The floor is far below every tier —
 * a technical bound so the loop ends, never a target: only a dedication made
 * mostly of blank lines ever gets near it, and it is still shown whole.
 */
export const GUESTBOOK_TV_FIT_STEP = 0.9;
export const GUESTBOOK_TV_MIN_FONT_PX = 2;

/** The next, smaller size to try when the words did not fit at `px`; null at the floor. */
export function guestbookTvShrink(px: number): number | null {
  if (px <= GUESTBOOK_TV_MIN_FONT_PX) return null;
  return Math.max(GUESTBOOK_TV_MIN_FONT_PX, Math.floor(px * GUESTBOOK_TV_FIT_STEP));
}

/**
 * The widest share of the stage the photograph may take, per tier: the more
 * there is to read, the more room the words get. The photograph stays the
 * protagonist — never below two fifths.
 */
export const GUESTBOOK_TV_PHOTO_SHARE = { airy: 0.6, regular: 0.55, compact: 0.48, dense: 0.42 } as const;

/**
 * The safe area kept clear on every side, as a share of the screen — the
 * overscan a television may hide, and breathing room for the memory.
 */
export const GUESTBOOK_TV_SAFE_AREA = { x: 0.05, y: 0.06 } as const;

/** The stage a memory is composed on, inside the safe area of a `width`×`height` screen. */
export function guestbookTvStageSize(width: number, height: number): { width: number; height: number } {
  return {
    width: Math.round(width * (1 - 2 * GUESTBOOK_TV_SAFE_AREA.x)),
    height: Math.round(height * (1 - 2 * GUESTBOOK_TV_SAFE_AREA.y)),
  };
}

/** How much of the stage's height the photograph may take. */
export const GUESTBOOK_TV_PHOTO_HEIGHT_SHARE = 0.78;

/**
 * The photograph's box on a stage of `stageWidth`×`stageHeight`, for a frame
 * of `frameAspect` (width / height). Both renderers size it here, so a memory
 * is composed the same in a browser and on a Fire TV.
 */
export function guestbookTvPhotoBox(
  stageWidth: number, stageHeight: number, frameAspect: number, density: GuestbookDensity,
): { width: number; height: number } {
  const aspect = Number.isFinite(frameAspect) && frameAspect > 0 ? frameAspect : 1;
  const maxWidth = stageWidth * GUESTBOOK_TV_PHOTO_SHARE[density];
  const maxHeight = stageHeight * GUESTBOOK_TV_PHOTO_HEIGHT_SHARE;
  const width = Math.min(maxWidth, maxHeight * aspect);
  return { width: Math.round(width), height: Math.round(width / aspect) };
}

// ── The template's frame, and the photograph inside it ──────────────────────
//
// The browser already has ONE implementation of each — the template registry
// every guest-book surface draws with, and the party print's crop geometry —
// so these delegate to them instead of carrying a second copy. The parity test
// holds the app's copies to these answers.

export interface GuestbookCrop {
  readonly centerX: number;
  readonly centerY: number;
  readonly zoom: number;
}

export interface GuestbookNormalisedCrop {
  readonly cropX: number;
  readonly cropY: number;
  readonly cropWidth: number;
  readonly cropHeight: number;
}

/** The design a memory is drawn with, as `key@version`. */
export function guestbookTemplateKey(key: string, version: number): string {
  const template = guestbookTemplateFor(key, version);
  return `${template.key}@${template.version}`;
}

/** The photograph's frame, width / height, for a photograph of `photoAspect`. */
export function guestbookFrameAspect(key: string, version: number, photoAspect: number): number {
  return guestbookTemplateFor(key, version).frameAspect(photoAspect);
}

/** The part of the photograph the frame shows — the party print's `cropFor`. */
export function guestbookCrop(
  photoAspect: number, frameAspect: number, crop: GuestbookCrop,
): GuestbookNormalisedCrop {
  return cropFor(photoAspect, frameAspect, crop);
}

/** Where the WHOLE photograph sits so the frame shows exactly `crop`, as fractions of the frame. */
export function guestbookPhotoPlacement(
  crop: GuestbookNormalisedCrop,
): { width: number; height: number; left: number; top: number } {
  return {
    width: 1 / crop.cropWidth,
    height: 1 / crop.cropHeight,
    left: -crop.cropX / crop.cropWidth,
    top: -crop.cropY / crop.cropHeight,
  };
}

// ── The deck, while the book is on the screen ───────────────────────────────

/**
 * Which memory to show after the book was read again.
 *
 *   * the one on screen, while it is still in the book — a poll never pulls a
 *     memory away from somebody reading it;
 *   * if it has left the book, the next one AFTER it in the order the room
 *     was following, so the show moves on rather than starting over;
 *   * the first, when nothing was on screen yet;
 *   * nothing, when the book has nothing visible left.
 */
export function reconcileGuestbookDeck(
  previousIds: readonly string[], currentId: string | null, freshIds: readonly string[],
): string | null {
  if (freshIds.length === 0) return null;
  if (currentId === null) return freshIds[0];
  if (freshIds.includes(currentId)) return currentId;
  const fresh = new Set(freshIds);
  const at = previousIds.indexOf(currentId);
  if (at >= 0) {
    for (let step = 1; step < previousIds.length; step += 1) {
      const candidate = previousIds[(at + step) % previousIds.length];
      if (fresh.has(candidate)) return candidate;
    }
  }
  return freshIds[0];
}

/**
 * The memory after `currentId`, wrapping round. With one memory, that memory:
 * it stays on screen, with no fade and nothing re-drawn.
 */
export function nextGuestbookMemory(ids: readonly string[], currentId: string | null): string | null {
  if (ids.length === 0) return null;
  if (currentId === null) return ids[0];
  const at = ids.indexOf(currentId);
  if (at < 0) return ids[0];
  return ids[(at + 1) % ids.length];
}
