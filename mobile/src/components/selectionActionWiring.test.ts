// Selection action WIRING (§21, §22, §24, §38).

import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';

const here = dirname(fileURLToPath(import.meta.url));
const read = async (p: string) => code(await readFile(join(here, p), 'utf8'));

test('the offer comes from the shared capability matrix, not a device check', async () => {
  const bar = await read('MediaSelectionBar.tsx');
  const photos = await read('../../app/(tabs)/photos.tsx');
  assert.match(bar, /MediaSelectionCapabilities/);
  assert.match(photos, /getMediaSelectionCapabilities\(\{/);
  // §38: the shape being replaced.
  for (const source of [bar, photos]) {
    assert.doesNotMatch(source, /isMobile|Platform\.OS === 'android' \? show/);
  }
});

test('the bar keeps no catalogue of its own: each action needs a capability', async () => {
  const source = await read('MediaSelectionBar.tsx');
  // Only the block that BUILDS the list; the type union above it names the
  // same ids and would make the ordering check meaningless.
  const body = source.slice(source.indexOf('const actions: SelectionAction[] = [];'));
  for (const [capability, action] of [
    ['canAddToAlbum', 'add-to-album'],
    ['canRemoveFromCurrentAlbum', 'remove-from-album'],
    ['canRestore', 'restore'],
    ['canTrash', 'trash'],
  ] as const) {
    const guard = body.indexOf(`capabilities.${capability}`);
    const push = body.indexOf(`id: '${action}'`);
    assert.ok(guard !== -1, `${capability} is not consulted`);
    assert.ok(push !== -1, `${action} is not offered at all`);
    assert.ok(guard < push, `${action} is offered before its capability is checked`);
  }
});

test('removing from an album and trashing say DIFFERENT things (§24)', async () => {
  // The rule a user can get badly wrong: album removal is membership only.
  const it = await readFile(join(here, '../i18n/it.ts'), 'utf8');
  const removal = it.match(/'selection\.removeFromAlbumConfirmBody': '([^']*)'/);
  assert.ok(removal, 'the album-removal confirmation is missing');
  assert.match(removal[1], /restano nella tua libreria/,
    'the wording must state that the files survive');
  const trash = it.match(/'selection\.trashConfirmBody': '([^']*)'/);
  assert.ok(trash && trash[1] !== removal[1], 'the two confirmations must not be the same text');
});

test('every removing action is confirmed before it runs', async () => {
  const bar = await read('MediaSelectionBar.tsx');
  const trashBlock = bar.slice(bar.indexOf("id: 'trash'"), bar.indexOf("id: 'trash'") + 400);
  assert.match(trashBlock, /confirm: \{/);
  assert.match(trashBlock, /destructive: true/);
  assert.match(bar, /if \(action\.confirm === undefined\) return action\.run\(\);/);
});

test('a partial result is reported, never presented as success', async () => {
  const photos = await read('../../app/(tabs)/photos.tsx');
  assert.match(photos, /if \(result\.failed > 0\)/);
  assert.match(photos, /t\('selection\.partial'/);
});

test('TV visibility uses its own route, so a rename cannot flip it', async () => {
  const albums = await read('../api/albums.ts');
  assert.match(albums, /albumTvSettingsPath\(albumId\), \{ showOnTv \}/);
  // updateAlbum must not carry showOnTv along with name/description.
  const update = albums.slice(albums.indexOf('export function updateAlbum'),
    albums.indexOf('export function setAlbumTvVisibility'));
  assert.doesNotMatch(update, /showOnTv/);
});
