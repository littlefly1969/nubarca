import assert from 'node:assert/strict';
import test from 'node:test';
import {
  GUESTBOOK_DWELL_MS,
  GUESTBOOK_LONG_DEDICATION_CHARS,
  GUESTBOOK_LONG_DEDICATION_LINES,
  GUESTBOOK_LONG_DWELL_MS,
  guestbookDensity,
  guestbookDwellMs,
  guestbookFrameAspect,
  guestbookPhotoPlacement,
  guestbookTemplateKey,
  guestbookTvFontPx,
  guestbookTvPhotoBox,
  isLongDedication,
  nextGuestbookMemory,
  reconcileGuestbookDeck,
} from './partyGuestbook.ts';
import { readFileSync } from 'node:fs';
import { read } from '../testing/sourceText.ts';

// The guest book on the party's television: the rules both televisions draw
// it with. The browser's port is held to these by nativeParity.test.ts.

test('a memory holds the screen for twelve seconds, a long dedication for eighteen', () => {
  assert.equal(GUESTBOOK_DWELL_MS, 12_000);
  assert.equal(GUESTBOOK_LONG_DWELL_MS, 18_000);
  assert.equal(guestbookDwellMs('Auguri!'), 12_000);
  assert.equal(guestbookDwellMs('a'.repeat(GUESTBOOK_LONG_DEDICATION_CHARS + 1)), 18_000);
});

test('"long" is a deterministic threshold on characters or lines', () => {
  assert.equal(isLongDedication('a'.repeat(GUESTBOOK_LONG_DEDICATION_CHARS)), false);
  assert.equal(isLongDedication('a'.repeat(GUESTBOOK_LONG_DEDICATION_CHARS + 1)), true);
  // Counted in code points, as the server counts: an emoji is one character.
  assert.equal(isLongDedication('😀'.repeat(GUESTBOOK_LONG_DEDICATION_CHARS)), false);
  const lines = (n: number) => Array.from({ length: n }, (_, i) => `riga ${i}`).join('\n');
  assert.equal(isLongDedication(lines(GUESTBOOK_LONG_DEDICATION_LINES)), false);
  assert.equal(isLongDedication(lines(GUESTBOOK_LONG_DEDICATION_LINES + 1)), true);
  // Blank lines are lines: a dedication keeps them, and they take room.
  assert.equal(isLongDedication('a\n\n\n\n\nb'), true);
});

test('the words get smaller as there is more to read, and line breaks count', () => {
  assert.equal(guestbookDensity('Auguri!'), 'airy');
  assert.equal(guestbookDensity('x'.repeat(200)), 'regular');
  assert.equal(guestbookDensity('x'.repeat(500)), 'compact');
  assert.equal(guestbookDensity('x'.repeat(1000)), 'dense');
  // The same few words over many lines are denser than on one.
  assert.equal(guestbookDensity('a b c d e'), 'airy');
  assert.equal(guestbookDensity('a\nb\nc\nd\ne'), 'regular');
  // Type size follows the screen: 720p is two thirds of 1080p.
  assert.equal(guestbookTvFontPx(1080, 'airy'), 52);
  assert.equal(guestbookTvFontPx(720, 'airy'), 35);
  assert.ok(guestbookTvFontPx(1080, 'dense') < guestbookTvFontPx(1080, 'compact'));
});

test('every template frames the photograph as the web registry does, and falls back the same way', () => {
  const landscape = 1.5;
  const portrait = 0.66;
  assert.equal(guestbookFrameAspect('nubarca', 1, landscape), 4 / 3);
  assert.equal(guestbookFrameAspect('nubarca', 1, portrait), 4 / 5);
  assert.equal(guestbookFrameAspect('nubarca', 1, 1), 1);
  assert.equal(guestbookFrameAspect('polaroid', 1, landscape), 1);
  assert.equal(guestbookFrameAspect('editorial', 1, landscape), 3 / 2);
  assert.equal(guestbookFrameAspect('editorial', 1, portrait), 3 / 4);
  assert.equal(guestbookFrameAspect('celebration', 1, portrait), 4 / 5);
  // A version this app does not know: the same key's current design.
  assert.equal(guestbookTemplateKey('polaroid', 7), 'polaroid@1');
  // A key it does not know: the default.
  assert.equal(guestbookTemplateKey('mystery', 1), 'nubarca@1');
});

test('the photograph is placed inside its frame by the guest\'s framing', () => {
  // A centred, unzoomed landscape photograph in a square frame shows its middle.
  assert.deepEqual(
    guestbookPhotoPlacement(2, 1, { centerX: 0.5, centerY: 0.5, zoom: 1 }),
    { width: 2, height: 1, left: -0.5, top: 0 },
  );
  // Zoom magnifies around the chosen centre, and the frame never leaves the picture.
  const zoomed = guestbookPhotoPlacement(1, 1, { centerX: 0.95, centerY: 0.05, zoom: 2 });
  assert.deepEqual(zoomed, { width: 2, height: 2, left: -1, top: 0 });
  // Zoomed out, the whole photograph, centred, with the photo well above and below.
  assert.deepEqual(
    guestbookPhotoPlacement(2, 1, { centerX: 0.1, centerY: 0.9, zoom: 0.5 }),
    { width: 1, height: 0.5, left: 0, top: 0.25 },
  );
  // Out-of-range zoom is held between contain and 4.
  assert.equal(guestbookPhotoPlacement(1, 1, { centerX: 0.5, centerY: 0.5, zoom: 10 }).width, 4);
  assert.equal(guestbookPhotoPlacement(2, 1, { centerX: 0.5, centerY: 0.5, zoom: 0 }).width, 1);
});

test('the placement is the shared one, case by case', () => {
  // The table packages/contracts checks the browser and the server against.
  const table = JSON.parse(readFileSync(
    new URL('../../../packages/contracts/src/photoPlacement.cases.json', import.meta.url), 'utf8'));
  for (const c of table.cases) {
    const placed = guestbookPhotoPlacement(c.photoAspect, c.frameAspect, c.placement);
    for (const k of ['width', 'height', 'left', 'top'] as const) {
      assert.ok(Math.abs(placed[k] - c.placed[k]) < 1e-9, `${JSON.stringify(c)} ${k}: ${placed[k]}`);
    }
  }
});

test('the photograph is the protagonist, and gives way to a long dedication', () => {
  for (const [w, h] of [[1152, 634], [1728, 950]]) {
    for (const frame of [4 / 5, 1, 4 / 3, 3 / 2]) {
      const airy = guestbookTvPhotoBox(w, h, frame, 'airy');
      const dense = guestbookTvPhotoBox(w, h, frame, 'dense');
      assert.ok(airy.width <= w * 0.6 + 1 && airy.height <= h * 0.78 + 1);
      assert.ok(dense.width <= airy.width);
      assert.ok(dense.width >= w * 0.3, 'never a thumbnail');
      // The box keeps the frame's shape.
      assert.ok(Math.abs(airy.width / airy.height - frame) < 0.02);
    }
  }
});

test('the deck follows the book without pulling a memory away from its readers', () => {
  // Nothing yet: start at the first.
  assert.equal(reconcileGuestbookDeck([], null, ['a', 'b']), 'a');
  // A new memory joins; the one on screen stays.
  assert.equal(reconcileGuestbookDeck(['a', 'b'], 'b', ['a', 'b', 'c']), 'b');
  // The one on screen is hidden: the next one in the room's order.
  assert.equal(reconcileGuestbookDeck(['a', 'b', 'c'], 'b', ['a', 'c']), 'c');
  // …wrapping round when it was the last.
  assert.equal(reconcileGuestbookDeck(['a', 'b', 'c'], 'c', ['a', 'b']), 'a');
  // The last visible memory is hidden: nothing to show — the slideshow returns.
  assert.equal(reconcileGuestbookDeck(['a'], 'a', []), null);
  // Advancing wraps; a single memory stays where it is.
  assert.equal(nextGuestbookMemory(['a', 'b', 'c'], 'c'), 'a');
  assert.equal(nextGuestbookMemory(['a'], 'a'), 'a');
  assert.equal(nextGuestbookMemory([], null), null);
});

test('the native screen reads the book with the session alone, never a grant', () => {
  const screen = read(import.meta.url, '../screens/PartyGuestbookScreen.tsx');
  const app = read(import.meta.url, '../../App.tsx');
  const api = read(import.meta.url, '../api/tv.ts');
  assert.match(api, /tvGet<TvGuestbook>\('\/api\/tv\/party\/guestbook'/);
  assert.match(screen, /getTvGuestbook\(/);
  assert.doesNotMatch(screen, /mintPartyDisplayGrant|grant/i);
  assert.doesNotMatch(screen, /WebView/);
  // Mounted for the guest book presentation, keyed by the assignment.
  assert.match(app, /flow\.name === 'partyGuestbook' && \(\s*<PartyGuestbookScreen\s+key=\{flow\.party\.key\}/);
  // The words are text, never interpreted.
  assert.doesNotMatch(screen, /dangerouslySetInnerHTML|Markdown/);
  // No truncation: no line limits on the dedication.
  assert.doesNotMatch(screen, /numberOfLines|ellipsizeMode/);
});
