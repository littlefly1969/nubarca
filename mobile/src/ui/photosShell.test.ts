import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const PHOTOS = code(readFileSync(resolve(ROOT, 'app', '(tabs)', 'photos.tsx'), 'utf8'));

// Photos is the validation consumer for the new shell: a real, dense media
// screen. BRAND-APP-02 migrates its CHROME and nothing else, so these
// assertions are mostly about what did not move.

test('the header actions invoke the same callbacks they always did', () => {
  assert.match(PHOTOS, /accessibilityLabel=\{t\('filters\.open'\)\}[\s\S]{0,80}?onPress=\{\(\) => setFiltersOpen\(true\)\}/);
  assert.match(PHOTOS, /<AccountButton \/>/);
});

test('normal browsing offers no Select control', () => {
  // NUBARCA-UX-01 §6. Selection begins with a long-press on an item, which is
  // where the user's hand already is; a Select button in the header asks them
  // to travel to the top of the screen to say what they want to do to
  // something at the bottom of it.
  assert.doesNotMatch(PHOTOS, /t\('selection\.select'\)/);
  assert.doesNotMatch(PHOTOS, /checkmark-circle-outline/);
  // The long-press entry is unchanged and still atomic.
  assert.match(PHOTOS, /onLongPressItem=\{\(item\) => selectionState\.beginWith\(item\.id\)\}/);
});

test('the gallery owns the viewport and its chrome floats', () => {
  assert.match(PHOTOS, /<ImmersiveGalleryShell/);
  assert.match(PHOTOS, /bottomOverlayHeight=\{TAB_BAR_CONTENT_HEIGHT\}/);
  // The applied-query strip travels with the chrome instead of being pinned
  // over the gallery as a second header.
  assert.ok(
    PHOTOS.indexOf('<MediaFilterChips') < PHOTOS.indexOf('{(scroll) => ('),
    'the filter chips are not in the collapsible chrome',
  );
  assert.match(PHOTOS, /onScroll=\{scroll\.onScroll\}/);
  assert.match(PHOTOS, /contentPaddingBottom=\{scroll\.contentPaddingBottom\}/);
});

test('the chrome uses the shared control, not hand-rolled Pressables', () => {
  assert.match(PHOTOS, /<IconButton\b/);
  assert.doesNotMatch(PHOTOS, /<Pressable\b/);
  assert.doesNotMatch(PHOTOS, /styles\.iconBtn/);
});

test('the media pipeline is untouched', () => {
  // Pagination, filtering, selection, the viewer and the album sheet are all
  // out of this slice's scope; the grid still receives the same wiring.
  assert.match(PHOTOS, /usePagedList<MediaItem>/);
  assert.match(PHOTOS, /useMediaFilters\('image', PAGE_SIZE\)/);
  assert.match(PHOTOS, /<MediaGrid\b/);
  assert.match(PHOTOS, /viewer\.open\(ownedSlides\(snapshot\.items\), item\.id, GALLERY_SCOPE\)/);
  assert.match(PHOTOS, /router\.push\(`\/media\/\$\{item\.id\}`\)/);
  assert.match(PHOTOS, /onLongPressItem=\{\(item\) => selectionState\.beginWith\(item\.id\)\}/);
  assert.match(PHOTOS, /<AddToAlbumSheet\b/);
  assert.match(PHOTOS, /<MediaFilterChips\b/);
  assert.match(PHOTOS, /<MediaSelectionBar\b/);
});

test('the screen states no colour, radius or type of its own', () => {
  assert.doesNotMatch(PHOTOS, /#[0-9A-Fa-f]{3,8}\b|\brgba?\(/);
  assert.doesNotMatch(PHOTOS, /\bradii\.|colors\.surfaceMuted/);
  assert.doesNotMatch(PHOTOS, /fontSize: \d/);
  assert.match(PHOTOS, /size=\{iconSizes\.\w\}/);
});
