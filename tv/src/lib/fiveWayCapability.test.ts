import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import {
  capabilitiesForScreen, FIVE_WAY_KEYS, menuOnlyCapabilities, TV_CAPABILITIES,
} from './fiveWayCapability.ts';

const read = (path: string) => readFileSync(new URL(path, import.meta.url), 'utf8');
const code = (source: string) => source
  .replace(/\/\*[\s\S]*?\*\//g, '')
  .split('\n').filter((l) => !l.trimStart().startsWith('//')).join('\n');

const library = code(read('../screens/PersonalLibraryScreen.tsx'));
const albumItems = code(read('../screens/AlbumItemsScreen.tsx'));
const beautyLab = code(read('../screens/BeautyLabScreen.tsx'));
const launcher = code(read('../components/TvContextActionsLauncher.tsx'));

const SCREENS = { library, albumItems, beautyLab };

// ------------------------------------------------------------- the matrix

test('no product function requires MENU or a transport key', () => {
  // THE RULE. A remote with only UP/DOWN/LEFT/RIGHT/SELECT/BACK must lose
  // nothing — which is most generic Android TV remotes, and every gamepad.
  const offenders = menuOnlyCapabilities();
  assert.deepEqual(offenders, [],
    `these functions have no five-way route: ${offenders.map((o) => `${o.screen}/${o.action}`).join(', ')}`);
});

test('every capability declares a non-empty five-way route', () => {
  for (const capability of TV_CAPABILITIES) {
    assert.ok(capability.fiveWayRoute.trim().length > 0,
      `${capability.screen}/${capability.action} has no route`);
  }
});

test('accelerators are extra routes, never the only one', () => {
  for (const capability of TV_CAPABILITIES) {
    if (capability.accelerators.length === 0) continue;
    assert.ok(capability.fiveWayRoute.trim().length > 0,
      `${capability.screen}/${capability.action} is accelerator-only`);
  }
});

test('the formerly MENU-only functions are all covered', () => {
  // The specific actions the audit found behind a MENU overlay.
  const required: [string, string][] = [
    ['PersonalLibrary', 'select kind: All'],
    ['PersonalLibrary', 'select kind: Photos'],
    ['PersonalLibrary', 'select kind: Videos'],
    ['PersonalLibrary', 'open filters'],
    ['AlbumItems', 'start the slideshow'],
    ['AlbumItems', 'show all photos (exit face filter)'],
    ['BeautyLab', 'add images'],
    ['BeautyLab', 'enter selection mode'],
  ];
  for (const [screen, action] of required) {
    const found = capabilitiesForScreen(screen).find((c) => c.action === action);
    assert.ok(found, `${screen}/${action} is not in the matrix`);
    assert.match(found!.fiveWayRoute, /Actions|SELECT|BACK|UP|DOWN/,
      `${screen}/${action} has no five-way route`);
  }
});

test('the five-way alphabet is exactly the six keys', () => {
  assert.deepEqual([...FIVE_WAY_KEYS], ['up', 'down', 'left', 'right', 'select', 'back']);
});

// ------------------------------------------------------- the launcher wiring

test('every command screen exposes a five-way Actions entry', () => {
  for (const [name, source] of Object.entries(SCREENS)) {
    assert.match(source, /<TvContextActionsLauncher/, `${name} has no Actions entry`);
    assert.match(source, /onOpen=\{openActions\}/, `${name} does not open the shared surface`);
  }
});

test('MENU and the launcher call the SAME open path', () => {
  for (const [name, source] of Object.entries(SCREENS)) {
    // One function, two entrances. Copying the rail's buttons into a second
    // menu would be two implementations of "start the slideshow", and the
    // second one is the one that rots.
    assert.match(source, /const openActions = useCallback\(/, name);
    assert.match(source, /else openActions\(\);/, `${name}: MENU must reuse openActions`);
    // Declaration + the MENU branch + the launcher prop: three references to
    // ONE function is what "two entrances, one transition" looks like.
    const references = [...source.matchAll(/openActions/g)].length;
    assert.ok(references >= 3,
      `${name}: openActions has ${references} references, expected declaration + MENU + launcher`);
  }
});

test('there is exactly one command rail per screen', () => {
  for (const [name, source] of Object.entries(SCREENS)) {
    const rails = [...source.matchAll(/<MenuCommandRail/g)].length;
    assert.equal(rails, 1, `${name} has ${rails} command rails — actions must not be duplicated`);
  }
});

test('the launcher goes inert while the rail owns focus', () => {
  // Two focus authorities in one mode is how a direction escapes a modal.
  for (const [name, source] of Object.entries(SCREENS)) {
    assert.match(source, /focusable=\{gridFocusable\}/, `${name}: launcher not gated`);
  }
  assert.match(library, /const gridFocusable = gridInteractive && !overlayVisible;/);
  assert.match(albumItems, /const gridFocusable = !overlayVisible;/);
  assert.match(beautyLab, /const gridFocusable = !overlay\.visible;/);
});

test('the launcher disables rather than unmounts', () => {
  // Unmounting it would reflow the grid underneath an open modal, and a grid
  // that moves while a modal is open is how focus restoration loses its place.
  assert.match(launcher, /disabled=\{!focusable\}/);
  assert.match(launcher, /pointerEvents=\{focusable \? 'auto' : 'none'\}/);
});

test('the grid is non-focusable while the rail owns focus', () => {
  assert.match(library, /focusable=\{gridFocusable\}/);
  assert.match(albumItems, /focusable=\{gridFocusable\}/);
  assert.match(beautyLab, /focusable=\{gridFocusable\}/);
});

test('focus restoration keys on media identity, not coordinates', () => {
  for (const [name, source] of Object.entries(SCREENS)) {
    assert.match(source, /restoreIndex/, `${name} does not restore focus`);
    // The preferred-focus request must be gated on focusability, however the
    // screen's tile component spells the prop — otherwise a stale request sits
    // on a row that is currently non-focusable or off-screen.
    assert.match(source, /(preferred|hasTVPreferredFocus)=\{gridFocusable && restoreIndex !== null/,
      `${name}: a preferred-focus request must not survive on a non-focusable grid`);
  }
});

// --------------------------------------------------------- what must NOT exist

test('no JS directional navigation was introduced', () => {
  for (const [name, source] of Object.entries({ ...SCREENS, launcher })) {
    for (const forbidden of [
      /nextFocusUp/, /nextFocusDown/, /nextFocusLeft/, /nextFocusRight/,
      /eventType === 'up'/, /eventType === 'down'/,
      /requestFocus\(/, /setTimeout\([^)]*focus/i,
    ]) {
      assert.doesNotMatch(source, forbidden,
        `${name} introduces a JS focus authority (${forbidden})`);
    }
  }
});

test('the tranche-1 transport-key policy is intact', () => {
  for (const [name, source] of Object.entries(SCREENS)) {
    assert.match(source, /if \(isMediaTransportEvent\(eventType\)\) return;/, name);
    assert.match(source, /actionableEventType\(evt\)/, name);
  }
});

test('MENU still works as an accelerator and still closes the surface', () => {
  for (const [name, source] of Object.entries(SCREENS)) {
    assert.match(source, /eventType === 'menu'/, `${name} dropped MENU support`);
    assert.match(source, /(hideOverlay|overlay\.hide)\(\);/, `${name}: MENU must still close`);
  }
});

test('Beauty Lab no longer instructs the user to press a key it may not have', () => {
  const it = read('../i18n/it.ts');
  const en = read('../i18n/en.ts');
  // The empty state used to say "Press MENU" — on a remote without one, the
  // whole feature was unreachable and the copy was the only instruction.
  assert.doesNotMatch(it, /beautyLab\.empty':[^\n]*MENU/);
  assert.doesNotMatch(en, /beautyLab\.empty':[^\n]*MENU/);
  assert.match(it, /beautyLab\.empty':[^\n]*Azioni/);
  assert.match(en, /beautyLab\.empty':[^\n]*Actions/);
});

test('Beauty Lab keeps the Actions entry in its empty state', () => {
  // The one screen where the empty state is exactly when the action matters
  // most: with no images, "Add images" is the only thing worth doing.
  const launcherAt = beautyLab.indexOf('<TvContextActionsLauncher');
  const emptyAt = beautyLab.indexOf("items.length === 0 && !loading");
  assert.ok(launcherAt > 0 && launcherAt < emptyAt,
    'the launcher must render before (and outside) the empty/grid branch');
});
