import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import {
  INITIAL_TV_GRID_FOCUS,
  resolveTvGridRestoreIndex,
  tvGridFocusOnMenuClose,
  tvGridFocusOnMenuOpen,
  tvGridFocusOnTile,
  tvGridFocusRestoreTo,
  type TvGridFocusMemory,
} from './mediaMenuFocus.ts';

const ITEMS = [{ id: 'p0' }, { id: 'p1' }, { id: 'p2' }, { id: 'p3' }, { id: 'p4' }];

function focusedOn(index: number): TvGridFocusMemory {
  return tvGridFocusOnTile(INITIAL_TV_GRID_FOCUS, index, ITEMS[index].id);
}

test('focusing a tile records it and clears the one-shot restore request', () => {
  const memory = focusedOn(2);
  assert.equal(memory.index, 2);
  assert.equal(memory.id, 'p2');
  // A preferred-focus flag left set on a tile is what lets a remounting
  // FlatList row pull focus back to it during D-pad scrolling.
  assert.equal(memory.restoreIndex, null);
  assert.equal(memory.menuOwnsFocus, false);
  // Idempotent: re-reporting the same tile must not churn identity.
  assert.equal(tvGridFocusOnTile(memory, 2, 'p2'), memory);
});

test('MENU hands focus to the rail and leaves no tile asking for focus', () => {
  const open = tvGridFocusOnMenuOpen(focusedOn(2));
  assert.equal(open.menuOwnsFocus, true);
  assert.equal(open.restoreIndex, null);
  // The exact item is still remembered while the rail owns focus.
  assert.equal(open.id, 'p2');
  assert.equal(open.index, 2);
});

test('closing the menu restores the EXACT tile the user came from', () => {
  const closed = tvGridFocusOnMenuClose(tvGridFocusOnMenuOpen(focusedOn(2)), ITEMS);
  assert.equal(closed.menuOwnsFocus, false);
  assert.equal(closed.restoreIndex, 2);
  assert.equal(closed.id, 'p2');
});

test('the grid cannot redefine the restore target while the rail owns focus', () => {
  // A stale focus report from a row being torn down under the overlay must not
  // move the user somewhere else when the overlay closes.
  const open = tvGridFocusOnMenuOpen(focusedOn(2));
  const strayed = tvGridFocusOnTile(open, 4, 'p4');
  assert.equal(strayed, open);
  assert.equal(tvGridFocusOnMenuClose(strayed, ITEMS).restoreIndex, 2);
});

test('MENU, BACK and the idle auto-hide are the same restoration', () => {
  // All three close the overlay, so all three take the identical transition —
  // there is no path that closes the menu without restoring media focus.
  const open = tvGridFocusOnMenuOpen(focusedOn(3));
  const byMenu = tvGridFocusOnMenuClose(open, ITEMS);
  const byBack = tvGridFocusOnMenuClose(open, ITEMS);
  const byAutoHide = tvGridFocusOnMenuClose(open, ITEMS);
  assert.deepEqual(byMenu, byBack);
  assert.deepEqual(byMenu, byAutoHide);
  assert.equal(byMenu.restoreIndex, 3);
});

test('an item that moved while the menu was open is followed by id', () => {
  // A live Party upload lands at the head of the list: the same photo is now
  // at a different index, and the restore must follow the PHOTO.
  const open = tvGridFocusOnMenuOpen(focusedOn(2));
  const shifted = [{ id: 'new' }, ...ITEMS];
  const closed = tvGridFocusOnMenuClose(open, shifted);
  assert.equal(closed.restoreIndex, 3);
  assert.equal(closed.id, 'p2');
});

test('an item that disappeared while the menu was open falls back deterministically', () => {
  const open = tvGridFocusOnMenuOpen(focusedOn(4));
  // A face filter cut the list down while the overlay was up.
  const filtered = [{ id: 'p0' }, { id: 'p1' }];
  const closed = tvGridFocusOnMenuClose(open, filtered);
  // Nearest still-valid index, not a silent jump back to the first tile.
  assert.equal(closed.restoreIndex, 1);
  assert.equal(closed.id, 'p1');
});

test('closing onto an empty grid asks for no focus at all', () => {
  const closed = tvGridFocusOnMenuClose(tvGridFocusOnMenuOpen(focusedOn(2)), []);
  assert.equal(closed.restoreIndex, null);
  assert.equal(closed.menuOwnsFocus, false);
  // The remembered photo is kept: it may come back on the next poll.
  assert.equal(closed.id, 'p2');
});

test('restore resolution prefers the id, then the clamped index, then the first tile', () => {
  const memory = focusedOn(3);
  assert.equal(resolveTvGridRestoreIndex(memory, ITEMS), 3);
  // Id gone, index still addressable → nearest valid index.
  assert.equal(resolveTvGridRestoreIndex(memory, [{ id: 'z0' }, { id: 'z1' }]), 1);
  // Nothing addressable at all → the first tile.
  assert.equal(resolveTvGridRestoreIndex(memory, [{ id: 'z0' }]), 0);
  assert.equal(resolveTvGridRestoreIndex(memory, []), null);
  // No item was ever focused: the mount-time request still points at the first.
  assert.equal(resolveTvGridRestoreIndex(INITIAL_TV_GRID_FOCUS, ITEMS), 0);
});

test('a programmatic restore both moves the memory and asks for the focus', () => {
  // Face-filter swap, viewer close, bulk trash: the app chose the tile.
  const restored = tvGridFocusRestoreTo(focusedOn(2), 4, 'p4');
  assert.equal(restored.index, 4);
  assert.equal(restored.id, 'p4');
  assert.equal(restored.restoreIndex, 4);
});

test('the grid starts by asking for focus on its first tile', () => {
  assert.equal(INITIAL_TV_GRID_FOCUS.restoreIndex, 0);
  assert.equal(INITIAL_TV_GRID_FOCUS.menuOwnsFocus, false);
  assert.equal(INITIAL_TV_GRID_FOCUS.id, null);
});

// The remaining contract is wiring: it lives in the screens, and a regression
// there is silent (the app still builds, focus just leaks again). These assert
// the wiring itself, the way appBackNavigation.test.ts asserts App.tsx.

const SCREENS = {
  'AlbumItemsScreen.tsx': readFileSync(new URL('../screens/AlbumItemsScreen.tsx', import.meta.url), 'utf8'),
  'PersonalLibraryScreen.tsx': readFileSync(new URL('../screens/PersonalLibraryScreen.tsx', import.meta.url), 'utf8'),
};

test('every native media wall uses the one shared focus graph + focus memory', () => {
  for (const [name, source] of Object.entries(SCREENS)) {
    assert.match(source, /useTvMediaGridFocus\(/, name);
    assert.match(source, /gridFocus\.targetsFor\(item\.id\)/, name);
    assert.match(source, /useTvGridFocusMemory\(/, name);
    // No screen may keep its own vertical-geometry rules, and none may bring
    // back the lane state whose re-render the key-press path could outrun.
    assert.doesNotMatch(source, /buildTvMediaFocusLinks|verticalTarget|preferredX/, name);
  }
});

test('no media wall debounces or throttles the D-pad', () => {
  // The Fire remote's auto-repeat stream is valid user input: every accepted
  // repeat must perform exactly one predictable step. Swallowing repeats would
  // hide the divergence instead of removing it, and would make a held button
  // feel broken.
  for (const [name, source] of Object.entries(SCREENS)) {
    assert.doesNotMatch(source, /debounce|throttle/i, name);
    // A timer gating navigation is the same mistake wearing a different name.
    assert.doesNotMatch(source, /setTimeout\([^)]*(?:navigate|move|focus)/i, name);
  }
});

test('every focusable command overlay is a trapping focus scope', () => {
  const rail = readFileSync(new URL('../components/MenuCommandRail.tsx', import.meta.url), 'utf8');
  assert.match(rail, /TVFocusGuideView/);
  for (const prop of ['autoFocus', 'trapFocusUp', 'trapFocusDown', 'trapFocusLeft', 'trapFocusRight']) {
    assert.match(rail, new RegExp(`\\n\\s*${prop}\\n`), prop);
  }
  // Every surface with a focusable command overlay, not only the media walls:
  // one escaping rail is enough to make the remote feel unpredictable.
  const railed = {
    'screens/AlbumItemsScreen.tsx': /<View style=\{\[styles\.commandBar/,
    'screens/PersonalLibraryScreen.tsx': /<View style=\{\[styles\.commandBar/,
    'screens/BeautyLabScreen.tsx': /<View style=\{styles\.menu\}/,
  };
  for (const [path, plainView] of Object.entries(railed)) {
    const source = readFileSync(new URL(`../${path}`, import.meta.url), 'utf8');
    assert.match(source, /<MenuCommandRail/, path);
    // The bar is never a plain View again.
    assert.doesNotMatch(source, plainView, path);
  }
});

test('the grid stops being a focus destination while the rail owns focus', () => {
  assert.match(SCREENS['AlbumItemsScreen.tsx'], /const gridFocusable = !overlayVisible;/);
  assert.match(
    SCREENS['PersonalLibraryScreen.tsx'],
    /const gridFocusable = gridInteractive && !overlayVisible;/,
  );
  for (const name of ['AlbumItemsScreen.tsx', 'PersonalLibraryScreen.tsx']) {
    const source = SCREENS[name as keyof typeof SCREENS];
    assert.match(source, /focusable=\{gridFocusable && rowReady\}/, name);
    // A tile may only ASK for focus when it could accept it — a preferred-focus
    // flag on a tile behind the rail is exactly how focus used to leak back.
    assert.match(source, /preferred=\{gridFocusable && rowReady && restoreIndex !== null/, name);
  }
});

test('BACK closes the overlay first and does not navigate away', () => {
  for (const name of ['AlbumItemsScreen.tsx', 'PersonalLibraryScreen.tsx']) {
    const source = SCREENS[name as keyof typeof SCREENS];
    assert.match(
      source,
      /if \(overlayVisibleRef\.current\) \{\s*hideOverlay\(\);\s*return true;/,
      name,
    );
    assert.match(source, /addEventListener\('hardwareBackPress', onBackPress\)/, name);
  }
});

test('no media wall detaches rows that vertical focus links point at', () => {
  // `removeClippedSubviews` detaches already-rendered rows just outside the
  // viewport; a detached row cannot be resolved as an explicit nextFocusDown
  // target, so vertical navigation falls back to geometric focus search.
  for (const [name, source] of Object.entries(SCREENS)) {
    // Anchored to a JSX prop line, so the comments explaining the removal do
    // not satisfy the assertion by accident.
    assert.doesNotMatch(source, /^\s*removeClippedSubviews\s*(\{|$)/m, name);
    // Windowing stays on. Nearby rows mount their previews progressively, and
    // the focus barrier blocks any row that has not settled yet.
    assert.match(source, /windowSize=\{11\}/, name);
  }
});
