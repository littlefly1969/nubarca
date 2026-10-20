import assert from 'node:assert/strict';
import test from 'node:test';
import {
  defaultOrder,
  emptyIdentity,
  queryFingerprint,
  queryToWire,
} from './mediaWorkspaceQuery.ts';
import { read } from '../testing/sourceText.ts';

// A Personal Area album opens in its OWNER's order (date taken, in the album's
// direction), the same sequence the web, the party wall and the TV party
// slideshow show. The library keeps upload order. An explicit choice from the
// filter panel is sent as asked.

test('an album opens by date taken in the owner\'s direction, both ways', () => {
  for (const direction of ['asc', 'desc'] as const) {
    const identity = emptyIdentity({ kind: 'album', albumId: 'a-1', sortDirection: direction });
    assert.equal(identity.sort, 'datetaken');
    assert.equal(identity.direction, direction);
    const wire = queryToWire(identity, null);
    assert.equal(wire.sort, 'datetaken');
    assert.equal(wire.direction, direction);
  }
});

test('an album card from an older server, with no direction, opens newest taken first', () => {
  assert.deepEqual(defaultOrder({ kind: 'album', albumId: 'a-1' }), { sort: 'datetaken', direction: 'desc' });
});

test('the library still opens in upload order', () => {
  const identity = emptyIdentity({ kind: 'library' });
  assert.equal(identity.sort, 'created');
  assert.equal(identity.direction, 'desc');
});

test('an explicit sort inside an album is sent as asked, and is a different query', () => {
  const opened = emptyIdentity({ kind: 'album', albumId: 'a-1', sortDirection: 'asc' });
  const chosen = { ...opened, sort: 'created' as const, direction: 'desc' as const };
  const wire = queryToWire(chosen, null);
  assert.equal(wire.sort, 'created');
  assert.equal(wire.direction, 'desc');
  assert.notEqual(queryFingerprint(opened), queryFingerprint(chosen));
});

test('the album direction travels from the card to the album source', () => {
  const albums = read(import.meta.url, '../screens/PersonalAlbumsScreen.tsx');
  const app = read(import.meta.url, '../../App.tsx');
  assert.match(albums, /onOpenAlbum\(\{ id: album\.id, name: album\.name, sortDirection: album\.sortDirection \}\)/);
  assert.match(app, /source=\{\{ kind: 'album', albumId: flow\.album\.id, sortDirection: flow\.album\.sortDirection \}\}/);
});

test('the filter panel treats the album order as the baseline and resets to it', () => {
  const panel = read(import.meta.url, '../screens/library/LibraryFilterPanel.tsx');
  assert.match(panel, /const baseOrder = defaultOrder\(applied\.source\)/);
  assert.match(panel, /active=\{sort !== baseOrder\.sort\}/);
  assert.match(panel, /active=\{direction !== baseOrder\.direction\}/);
  assert.match(panel, /setSort\(baseOrder\.sort\);\s*setDirection\(baseOrder\.direction\);/);
  assert.doesNotMatch(panel, /setSort\('created'\)/);
});

test('the slideshow plays the grid\'s own items, so it follows the same album order', () => {
  const screen = read(import.meta.url, '../screens/PersonalLibraryScreen.tsx');
  assert.match(screen, /emptyIdentity\(source\)/);
  assert.match(screen, /<PersonalMediaViewer\s+items=\{items\}/);
});
