// Regression tests for the Media Library UX reconciliation: paging totals,
// the People picker policy, fixed-editor layout, and the wiring that connects
// them to the screens.
//
// The source-level assertions here exist because the components are .tsx and
// cannot be imported by node --test. They are written against CODE with
// comments stripped, so a comment merely MENTIONING a retired construct can
// never satisfy them — the failure mode a previous version of the
// back-navigation test actually had.

import assert from 'node:assert/strict';
import test from 'node:test';
import { read } from '../testing/sourceText.ts';
import {
  displayTotal, formatPosition, formatTotal, isUnknownTotal,
  mergePagedTotal, TOTAL_UNCHANGED,
} from './pagingTotals.ts';
import {
  filterPeopleByName, focusAfterSearch, personItemLayout, personMetaText,
  PEOPLE_LIST_TUNING, PERSON_META_FLEX, PERSON_NAME_FLEX, PERSON_ROW_HEIGHT,
  reconcileFocusViewport, visibleRowCount,
} from './peoplePicker.ts';
import { fixedEditorLayout, TV_VIEWPORTS, usableHeight } from '../lib/panelLayout.ts';

const src = (path: string) => read(import.meta.url, path);

const peoplePanel = src('../screens/library/LibraryPeoplePanel.tsx');
const filterPanel = src('../screens/library/LibraryFilterPanel.tsx');
const panelShell = src('../screens/gallery/PanelShell.tsx');
const filterRow = src('../screens/library/FilterRow.tsx');
const viewer = src('../screens/library/PersonalMediaViewer.tsx');
const libraryScreen = src('../screens/PersonalLibraryScreen.tsx');
const keyboard = src('../screens/gallery/TvKeyboardPanel.tsx');

// ---------------------------------------------------------------- paging

test('a cursor page that reports the unchanged sentinel keeps the real total', () => {
  // The exact reported scenario: 137, then -1, then -1.
  let total: number | null = null;
  const pages = [
    { items: 50, totalCount: 137, hasMore: true },
    { items: 50, totalCount: TOTAL_UNCHANGED, hasMore: true },
    { items: 37, totalCount: TOTAL_UNCHANGED, hasMore: false },
  ];
  const seen: (number | null)[] = [];
  for (const page of pages) {
    total = mergePagedTotal(total, page.totalCount);
    seen.push(total);
  }
  assert.deepEqual(seen, [137, 137, 137], 'the first page total must survive every later page');
});

test('a genuinely new total replaces the old one, including a smaller one', () => {
  // The first page of a NEW query legitimately reports fewer items.
  assert.equal(mergePagedTotal(137, 4), 4);
  assert.equal(mergePagedTotal(137, 0), 0);
});

test('nothing that is not a usable total is ever accepted as one', () => {
  for (const bad of [-1, -99, null, undefined, Number.NaN, Number.POSITIVE_INFINITY]) {
    assert.equal(isUnknownTotal(bad as number), true, String(bad));
    assert.equal(mergePagedTotal(137, bad as number), 137, String(bad));
  }
});

test('a displayed position never contains the sentinel', () => {
  assert.equal(formatPosition(6, 137), '7 / 137');
  // Defensive: an unknown denominator is DROPPED, never printed.
  for (const bad of [-1, null, Number.NaN]) {
    const rendered = formatPosition(6, bad as number);
    assert.equal(rendered, '7');
    assert.ok(!rendered.includes('-1'), 'the sentinel must never reach the screen');
    assert.ok(!rendered.includes('/'), 'no denominator is better than a false one');
  }
});

test('a denominator smaller than the position is refused rather than shown', () => {
  assert.equal(formatPosition(200, 137), '201');
});

test('badge counts fall back to what is actually loaded', () => {
  assert.equal(displayTotal(null, 50), 50);
  assert.equal(displayTotal(TOTAL_UNCHANGED, 50), 50);
  assert.equal(displayTotal(137, 50), 137);
  assert.equal(formatTotal(TOTAL_UNCHANGED, 12), 12);
});

test('the library screen merges totals instead of assigning them', () => {
  // The actual defect: `totalCount: page.totalCount` in loadMore.
  assert.match(libraryScreen, /totalCount: mergePagedTotal\(s\.totalCount, page\.totalCount\)/);
  assert.doesNotMatch(libraryScreen, /totalCount: page\.totalCount/,
    'a raw assignment overwrites a valid total with the unchanged sentinel');
  assert.match(libraryScreen, /displayTotal\(load\.totalCount, items\.length\)/);
});

test('the fix introduces no additional count request', () => {
  // Retrieval is chosen once by `fetchPage`, which the screen calls exactly
  // twice: the first page and the next page. The count assertion is about
  // there being no SEPARATE total query, and that is still what it checks.
  const requests = [...libraryScreen.matchAll(/fetchPage\(identityRef\.current/g)].length;
  assert.equal(requests, 2, 'exactly the first-page and next-page requests, nothing new');
  assert.doesNotMatch(libraryScreen, /countOnly|totalOnly|fetchCount|\/count\b/i);
});

test('the viewer badge renders through the same policy', () => {
  assert.match(viewer, /formatPosition\(clamped, totalCount\)/);
  assert.doesNotMatch(viewer, /\{clamped \+ 1\} \/ \{totalCount\}/);
});

// ---------------------------------------------------------------- people

test('the People picker is virtualized, not an eager map', () => {
  assert.match(peoplePanel, /<FlatList\s/);
  // The rows come from FlatList data, never from a mapped array in JSX. Note
  // the panel legitimately calls people.map elsewhere to build the id Set for
  // the stale-selection count — what must not exist is a mapped RENDER.
  assert.doesNotMatch(peoplePanel, /\{\s*(people|visible)\.map\(/,
    'rendering the whole owner projection in JSX is what this replaced');
  assert.match(peoplePanel, /data=\{visible\}/);
});

test('the FlatList is the only scroll owner in that panel', () => {
  assert.match(filterPanel, /editor\.kind === 'people'[\s\S]*?body="custom"/);
  assert.doesNotMatch(peoplePanel, /<ScrollView/, 'no nested scroll container');
  // JSX elements only — `useRef<FlatList<...>>` is a type, not a scroll owner.
  const lists = [...peoplePanel.matchAll(/<FlatList\s/g)].length;
  assert.equal(lists, 1, 'exactly one scrolling list');
});

test('entering People keeps one stable native panel host', () => {
  assert.doesNotMatch(peoplePanel, /<PanelShell|<Modal/,
    'the People body must not replace or nest the filter flow native window');
  assert.match(filterPanel,
    /editor\.kind === 'people'[\s\S]*?<PanelShell[\s\S]*?<LibraryPeoplePanel/,
    'LibraryFilterPanel must remain the panel host while its body changes');
});

test('rows are keyed by stable person id', () => {
  assert.match(peoplePanel, /keyExtractor=\{\(person\) => person\.id\}/);
});

test('item layout is deterministic, so scrolling needs no timeout', () => {
  assert.match(peoplePanel, /getItemLayout=\{\(_, index\) => personItemLayout\(index\)\}/);
  assert.deepEqual(personItemLayout(0), { length: PERSON_ROW_HEIGHT, offset: 0, index: 0 });
  assert.deepEqual(personItemLayout(10),
    { length: PERSON_ROW_HEIGHT, offset: PERSON_ROW_HEIGHT * 10, index: 10 });
  assert.doesNotMatch(peoplePanel, /setTimeout|setInterval|requestAnimationFrame/,
    'deterministic geometry means no readiness timer is needed');
});

test('native onFocus drives the viewport — JavaScript never navigates', () => {
  assert.match(peoplePanel, /onFocus=\{\(\) => onRowFocus\(person\.id, index\)\}/);
  assert.match(peoplePanel, /reconcileFocusViewport\(/);
  for (const forbidden of [/nextFocusUp/, /nextFocusDown/, /nextFocusLeft/, /nextFocusRight/,
    /useTVEventHandler/, /eventType === 'up'/, /eventType === 'down'/]) {
    assert.doesNotMatch(peoplePanel, forbidden,
      'a JS focus authority will eventually disagree with Android about the focused row');
  }
});

test('a row already inside the safe band causes no scroll', () => {
  for (const focusedIndex of [2, 3, 4, 5, 6]) {
    assert.equal(
      reconcileFocusViewport({ focusedIndex, firstVisibleIndex: 0, visibleCount: 8, total: 200 }),
      null,
      `index ${focusedIndex} is comfortably visible`,
    );
  }
});

test('a row crossing the viewport boundary scrolls deterministically', () => {
  assert.deepEqual(
    reconcileFocusViewport({ focusedIndex: 7, firstVisibleIndex: 0, visibleCount: 8, total: 200 }),
    { index: 7, viewPosition: 0.5 });
  assert.deepEqual(
    reconcileFocusViewport({ focusedIndex: 40, firstVisibleIndex: 0, visibleCount: 8, total: 200 }),
    { index: 40, viewPosition: 0.5 });
});

test('the ends of the list are not treated as boundary violations', () => {
  // There is no room to centre row 0; scrolling for it would just jitter.
  assert.equal(
    reconcileFocusViewport({ focusedIndex: 0, firstVisibleIndex: 0, visibleCount: 8, total: 200 }),
    null);
  assert.equal(
    reconcileFocusViewport({ focusedIndex: 199, firstVisibleIndex: 192, visibleCount: 8, total: 200 }),
    null);
  // And a list that fits entirely never scrolls.
  assert.equal(
    reconcileFocusViewport({ focusedIndex: 5, firstVisibleIndex: 0, visibleCount: 8, total: 6 }),
    null);
});

test('a 200-person list never mounts 200 focusables', () => {
  assert.ok(PEOPLE_LIST_TUNING.initialNumToRender <= 20);
  assert.ok(PEOPLE_LIST_TUNING.maxToRenderPerBatch <= 20);
  assert.ok(PEOPLE_LIST_TUNING.windowSize <= 10);
  assert.equal(visibleRowCount(8 * PERSON_ROW_HEIGHT), 8);
  // A clipped view is DETACHED on Android TV, and a detached view cannot hold
  // focus — which is the "focus vanishes while scrolling" defect.
  assert.equal(PEOPLE_LIST_TUNING.removeClippedSubviews, false);
  assert.match(peoplePanel, /removeClippedSubviews=\{PEOPLE_LIST_TUNING\.removeClippedSubviews\}/);
});

const PEOPLE = [
  { id: 'a', name: 'Marco Rossi', faceCount: 12 },
  { id: 'b', name: 'giulia bianchi', faceCount: 3 },
  { id: 'c', name: null, faceCount: 0 },
  { id: 'd', name: 'Marco Verdi', faceCount: 7 },
];

test('local search narrows the list, case-insensitively', () => {
  assert.deepEqual(filterPeopleByName(PEOPLE, 'marco').map((p) => p.id), ['a', 'd']);
  assert.deepEqual(filterPeopleByName(PEOPLE, 'BIANCHI').map((p) => p.id), ['b']);
  assert.deepEqual(filterPeopleByName(PEOPLE, 'zzz').map((p) => p.id), []);
});

test('clearing the search restores the whole list', () => {
  assert.deepEqual(filterPeopleByName(PEOPLE, '').map((p) => p.id), ['a', 'b', 'c', 'd']);
  assert.deepEqual(filterPeopleByName(PEOPLE, '   ').map((p) => p.id), ['a', 'b', 'c', 'd']);
});

test('the unnamed label is searchable, so that row is reachable too', () => {
  assert.deepEqual(filterPeopleByName(PEOPLE, 'senza', 'Senza nome').map((p) => p.id), ['c']);
});

test('picker search never mutates include/exclude', () => {
  // It is navigation, not a filter: it must not appear in any commit path.
  assert.doesNotMatch(peoplePanel, /applySearch[\s\S]{0,400}onChange\(/);
  assert.match(peoplePanel, /const visible = useMemo\(\s*\(\) => filterPeopleByName/);
  // The stale count is computed against the WHOLE projection, or typing a name
  // would invent stale selections.
  assert.match(peoplePanel, /const known = new Set\(people\.map/);
});

test('search hands focus on deterministically when it removes the focused row', () => {
  assert.equal(focusAfterSearch(PEOPLE, 'b', 'search'), 'b');
  assert.equal(focusAfterSearch([PEOPLE[0], PEOPLE[3]], 'b', 'search'), 'a');
  assert.equal(focusAfterSearch([], 'b', 'search'), 'search');
  assert.match(peoplePanel, /focusAfterSearch\(nextVisible, focusRef\.current, SEARCH_KEY\)/);
});

// ------------------------------------------------------- person row layout

test('the person name gets the majority of the row, not a third', () => {
  assert.ok(PERSON_NAME_FLEX > PERSON_META_FLEX);
  assert.ok(PERSON_NAME_FLEX / (PERSON_NAME_FLEX + PERSON_META_FLEX) >= 0.65);
});

test('the name column width does not depend on the trailing state', () => {
  // The defect: the same name truncating at a different character in each of
  // Off / Include / Exclude, so the row appeared to change identity.
  assert.match(filterRow, /labelPerson: \{ flex: PERSON_NAME_FLEX, minWidth: 0, flexGrow: 1/);
  assert.match(filterRow, /valuePerson: \{ flex: PERSON_META_FLEX, flexGrow: 0, flexShrink: 0 \}/);
  const meta = (state: 'off' | 'include' | 'exclude') =>
    personMetaText(state, 12, (s) => ({ off: '—', include: 'Con', exclude: 'Senza' }[s]));
  // The three states differ in TEXT but share one bounded column.
  assert.deepEqual([meta('off'), meta('include'), meta('exclude')],
    ['— · 12', 'Con · 12', 'Senza · 12']);
});

test('the face count is trailing meta, never part of the truncatable name', () => {
  assert.doesNotMatch(peoplePanel, /label=\{`\$\{name\} \(\$\{person\.faceCount\}\)`\}/);
  assert.match(peoplePanel, /label=\{name\}/);
  assert.equal(personMetaText('off', 0, () => '—'), '—', 'no count when there are no faces');
});

test('the name truncates with a tail ellipsis and one line', () => {
  assert.match(filterRow, /numberOfLines=\{1\}\s*\n\s*ellipsizeMode="tail"/);
});

test('accessibility gets the full, untruncated name', () => {
  assert.match(peoplePanel, /accessibilityLabel=\{t\('filters\.rowA11y', \{ label: name, value: meta \}\)\}/);
});

// ------------------------------------------------------------- panel shell

test('PanelShell no longer scrolls unconditionally', () => {
  assert.match(panelShell, /export type PanelBodyMode = 'scroll' \| 'fixed' \| 'custom'/);
  assert.match(panelShell, /body === 'scroll' \? \(/);
  assert.match(panelShell, /body = 'scroll'/, 'existing row panels keep their behaviour');
});

test('full-screen panels own a native window above the media grid', () => {
  assert.match(panelShell, /<Modal\s/);
  assert.match(panelShell, /hardwareAccelerated/);
  assert.match(panelShell, /onRequestClose=\{requestClose\}/);
  assert.match(panelShell, /statusBarTranslucent/);
  assert.match(panelShell, /accessibilityViewIsModal/);
  assert.match(panelShell, /container:\s*\{\s*flex:\s*1/);
  assert.doesNotMatch(panelShell, /zIndex|elevation/,
    'an ordinary sibling layer is not a reliable overlay on physical Fire TV');
  assert.doesNotMatch(panelShell, /BackHandler/,
    'Android delivers BACK through Modal.onRequestClose while the modal is open');
});

test('opening any library panel also makes the media grid non-interactive', () => {
  assert.match(libraryScreen,
    /const gridInteractive = panel === 'none' && viewerIndex === null/);
  assert.match(libraryScreen, /const gridFocusable = gridInteractive && !overlayVisible/);
});

test('bounded editors are sized to fit rather than made scrollable', () => {
  assert.match(keyboard, /body="fixed"/);
  assert.match(keyboard, /fixedEditorLayout\(viewport, \{/);
  assert.doesNotMatch(keyboard, /<ScrollView/);
});

test('every fixed editor fits both TV viewports with all controls visible', () => {
  const editors = {
    'text keyboard': { rows: 5, columns: 10, headerLines: 2, actionRows: 0 },
    'date pad': { rows: 5, columns: 3, headerLines: 2, actionRows: 0 },
    'numeric pad': { rows: 4, columns: 3, headerLines: 1, actionRows: 1 },
  };
  for (const [name, request] of Object.entries(editors)) {
    for (const viewport of TV_VIEWPORTS) {
      const layout = fixedEditorLayout(viewport, request);
      assert.equal(layout.fits, true,
        `${name} does not fit ${viewport.width}x${viewport.height}: ` +
        `${layout.contentHeight} > ${usableHeight(viewport)}`);
      assert.ok(layout.keyHeight >= 28, `${name} keys unreadable at ${viewport.height}`);
      assert.ok(layout.fontSize >= 15, `${name} font unreadable at ${viewport.height}`);
    }
  }
});

test('1280x720 and 1920x1080 are both covered', () => {
  const sizes = TV_VIEWPORTS.map((v) => `${v.width}x${v.height}`);
  assert.ok(sizes.includes('1280x720'));
  assert.ok(sizes.includes('1920x1080'));
});

// ------------------------------------------------------------ viewer chrome

test('viewer chrome is gated, not permanent', () => {
  assert.match(viewer, /\{chrome\.visible && \(/);
  for (const element of [/styles\.counter/, /styles\.name/, /styles\.pill/]) {
    assert.match(viewer, element);
  }
  // All three ambient elements live inside the ONE visibility gate.
  const gated = viewer.slice(viewer.indexOf('{chrome.visible && ('));
  for (const element of ['styles.counter', 'styles.name', 'styles.pill']) {
    assert.ok(gated.includes(element), `${element} must follow chrome visibility`);
  }
});

test('the viewer reuses the shared overlay controller, with no second timer', () => {
  assert.match(viewer, /useMenuOverlay\(\)/);
  assert.doesNotMatch(viewer, /OVERLAY_IDLE_MS/, 'the shared controller owns the window');
  const timers = [...viewer.matchAll(/setTimeout\(/g)].length;
  assert.equal(timers, 1, 'only the slideshow timer — the chrome timer is the shared one');
});

test('MENU toggles the chrome and a slideshow tick does not re-arm it', () => {
  assert.match(viewer, /case 'toggle-overlay': chrome\.toggle\(\); return;/);
  // bump() is reached from the REMOTE handler only. If the slideshow effect
  // called it, a 9s slide under a 10s window would keep the overlay forever.
  const slideEffect = viewer.slice(
    viewer.indexOf('if (!slideshow || isVideo) return;'),
    viewer.indexOf('const onTVEvent'));
  assert.ok(!slideEffect.includes('chrome.'), 'the slideshow clock must not re-arm the overlay');
});

test('the chrome is shown once on entry, not once per item', () => {
  assert.match(viewer, /useEffect\(\(\) => \{ showChrome\(\); \}, \[showChrome\]\)/);
});

test('BACK still leaves the viewer in one press', () => {
  assert.match(viewer, /controlsRef\.current\?\.stop\(\);\s*\n\s*onClose\(indexRef\.current\)/);
});

// ---------------------------------------------------------------- kind state

test('the selected media kind is visible when focus moves away', () => {
  assert.match(libraryScreen, /selected=\{kind === identity\.mediaKind\}/);
  assert.match(src('../components/FocusableButton.tsx'),
    /\{focused \? '▸ ' : ''\}\{selected \? '✓ ' : ''\}\{label\}/);
});

test('identity.mediaKind stays the only authority', () => {
  for (const forbidden of [/selectedKind/, /activeTab/, /useState<MediaKindScope>/]) {
    assert.doesNotMatch(libraryScreen, forbidden, 'a second kind authority will drift');
  }
});

test('selection is marked by more than colour', () => {
  const button = src('../components/FocusableButton.tsx');
  assert.match(button, /labelSelected: \{ color: colors\.text, fontWeight: '800' \}/);
  assert.match(button, /accessibilityState=\{\{ selected \}\}/);
});
