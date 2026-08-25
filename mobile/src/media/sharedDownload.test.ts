// Regression tests for the shared-album ORIGINAL download lifecycle
// (MOBILE-SHARED-DOWNLOAD-HYGIENE): every file created for ONE share attempt
// must disappear when that attempt ends — success, dismissed sheet, share
// failure, or a download/move failure after partial artifacts exist — and two
// identically-named originals must never collide on one deterministic path.
//
// The orchestrator takes its whole filesystem/sharing surface through
// SharedDownloadIo, so these tests drive an in-memory fs that behaves like
// expo's legacy API (recursive delete, idempotent delete) instead of
// asserting on source text.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  makeSharedDownloadOperationId,
  runSharedAlbumOriginalDownload,
  SHARE_DIR_PREFIX,
  type SharedDownloadIo,
} from './sharedDownload.ts';

/** Minimal in-memory stand-in for expo's legacy filesystem semantics. */
class FakeFs {
  private entries = new Map<string, 'dir' | 'file'>();

  exists(path: string): boolean {
    return this.entries.has(path);
  }

  createDir(path: string): void {
    this.entries.set(path, 'dir');
  }

  createFile(path: string): void {
    this.entries.set(path, 'file');
  }

  move(from: string, to: string): void {
    if (!this.entries.has(from)) throw new Error(`move: source missing ${from}`);
    this.entries.delete(from);
    this.entries.set(to, 'file');
  }

  /** Recursive; tolerant of an absent path ONLY when idempotent. */
  delete(path: string, options?: { idempotent?: boolean }): void {
    if (!this.entries.has(path)) {
      if (options?.idempotent) return;
      throw new Error(`delete: path does not exist ${path}`);
    }
    for (const key of [...this.entries.keys()]) {
      if (key === path || key.startsWith(`${path}/`)) this.entries.delete(key);
    }
  }
}

const OK_JPEG = {
  status: 200,
  headers: {
    'content-type': 'image/jpeg',
    'content-disposition': 'attachment; filename="foto.jpg"',
  },
};

const REQUEST = {
  source: {
    uri: 'https://unit.test/api/shared-albums/alb-1/items/item-1/original',
    headers: { cookie: 'NubArca.Auth=tok' },
  },
  kindFallbackExtension: 'jpg',
  dialogTitle: 'Scarica',
};

interface RecordedShare {
  uri: string;
  mimeType?: string;
  dialogTitle?: string;
  fileExistedAtShareTime: boolean;
}

/** Harness: fake fs + recording io with per-step failure injection. */
function createHarness(options: {
  ids: string[];
  result?: { status: number; headers: Record<string, string> };
  failMkdirWith?: Error;
  failDownloadWith?: (tempUri: string) => Error;
  failMoveWith?: Error;
  failShareWith?: Error;
  failDeleteWith?: Error;
}) {
  const fs = new FakeFs();
  const downloads: Array<{ uri: string; targetUri: string; cookie: string | null }> = [];
  const moves: Array<{ from: string; to: string }> = [];
  const shares: RecordedShare[] = [];
  const deletes: Array<{ path: string; idempotent?: boolean }> = [];
  let idIndex = 0;

  const io: SharedDownloadIo = {
    cacheDirectory: '/cache/',
    makeOperationId: () => {
      const id = options.ids[idIndex] ?? `unplanned-${idIndex}`;
      idIndex += 1;
      return id;
    },
    async makeDirectoryAsync(path) {
      if (options.failMkdirWith) throw options.failMkdirWith;
      fs.createDir(path);
    },
    async downloadAsync(uri, targetUri, init) {
      downloads.push({ uri, targetUri, cookie: init?.headers?.cookie ?? null });
      fs.createFile(targetUri); // bytes hit disk before any outcome is known
      if (options.failDownloadWith) throw options.failDownloadWith(targetUri);
      return options.result ?? OK_JPEG;
    },
    async moveAsync(move) {
      moves.push(move);
      if (options.failMoveWith) throw options.failMoveWith;
      fs.move(move.from, move.to);
    },
    async deleteAsync(path, deleteOptions) {
      deletes.push({ path, idempotent: deleteOptions?.idempotent });
      if (options.failDeleteWith) throw options.failDeleteWith;
      fs.delete(path, deleteOptions);
    },
    async shareAsync(uri, shareOptions) {
      shares.push({
        uri,
        mimeType: shareOptions?.mimeType,
        dialogTitle: shareOptions?.dialogTitle,
        fileExistedAtShareTime: fs.exists(uri),
      });
      if (options.failShareWith) throw options.failShareWith;
      return 'dismissedAction';
    },
  };

  return { fs, io, downloads, moves, shares, deletes };
}

const dirFor = (id: string): string => `/cache/${SHARE_DIR_PREFIX}${id}`;

test('GIVEN download+share succeed THEN the generated private path no longer exists', async () => {
  const h = createHarness({ ids: ['op-a'] });

  await runSharedAlbumOriginalDownload(h.io, REQUEST);

  // The share sheet was offered the SERVER-DERIVED name inside the unique
  // operation directory, and the file still existed at that moment.
  const dir = dirFor('op-a');
  assert.deepEqual(h.shares, [
    {
      uri: `${dir}/foto.jpg`,
      mimeType: 'image/jpeg',
      dialogTitle: 'Scarica',
      fileExistedAtShareTime: true,
    },
  ]);
  assert.deepEqual(h.moves, [{ from: `${dir}/original`, to: `${dir}/foto.jpg` }]);

  // THEN nothing survives the operation — temp target, final file, directory.
  assert.equal(h.fs.exists(`${dir}/original`), false);
  assert.equal(h.fs.exists(`${dir}/foto.jpg`), false);
  assert.equal(h.fs.exists(dir), false);
  assert.deepEqual(h.deletes, [{ path: dir, idempotent: true }]);
});

test('the authenticated download keeps the exact album-scoped URI and cookie', async () => {
  const h = createHarness({ ids: ['op-auth'] });

  await runSharedAlbumOriginalDownload(h.io, REQUEST);

  assert.deepEqual(h.downloads, [
    {
      uri: REQUEST.source.uri,
      targetUri: `${dirFor('op-auth')}/original`,
      cookie: 'NubArca.Auth=tok',
    },
  ]);
});

test('GIVEN shareAsync throws THEN the file is deleted AND the share error is preserved', async () => {
  const h = createHarness({
    ids: ['op-b'],
    failShareWith: new Error('share sheet exploded'),
  });

  await assert.rejects(
    runSharedAlbumOriginalDownload(h.io, REQUEST),
    /share sheet exploded/,
  );

  const dir = dirFor('op-b');
  assert.equal(h.shares[0].fileExistedAtShareTime, true); // deleted only AFTER share returned
  assert.equal(h.fs.exists(dir), false);
  assert.deepEqual(h.deletes, [{ path: dir, idempotent: true }]);
});

test('GIVEN the download fails after partial bytes hit disk THEN all artifacts are removed', async () => {
  const h = createHarness({
    ids: ['op-c'],
    failDownloadWith: (tempUri) => new Error(`network dropped mid-transfer (${tempUri})`),
  });

  await assert.rejects(
    runSharedAlbumOriginalDownload(h.io, REQUEST),
    /network dropped mid-transfer/,
  );

  assert.equal(h.fs.exists(dirFor('op-c')), false);
  assert.deepEqual(h.deletes, [{ path: dirFor('op-c'), idempotent: true }]);
});

test('GIVEN an HTTP error status THEN the downloaded bytes are still cleaned', async () => {
  const h = createHarness({ ids: ['op-d'], result: { status: 500, headers: {} } });

  await assert.rejects(
    runSharedAlbumOriginalDownload(h.io, REQUEST),
    /download failed with status 500/,
  );

  assert.equal(h.fs.exists(dirFor('op-d')), false);
});

test('GIVEN the move/rename fails THEN both temp and final state are removed', async () => {
  const h = createHarness({ ids: ['op-e'], failMoveWith: new Error('move refused') });

  await assert.rejects(runSharedAlbumOriginalDownload(h.io, REQUEST), /move refused/);

  const dir = dirFor('op-e');
  assert.equal(h.fs.exists(dir), false);
  assert.deepEqual(h.deletes, [{ path: dir, idempotent: true }]);
});

test('GIVEN mkdir itself fails THEN cleanup is still attempted without masking', async () => {
  const h = createHarness({
    ids: ['op-f'],
    failMkdirWith: new Error('disk full'),
  });

  await assert.rejects(runSharedAlbumOriginalDownload(h.io, REQUEST), /disk full/);

  // Nothing was ever created; the idempotent delete of the absent directory
  // is attempted anyway and must not turn into a second error.
  assert.deepEqual(h.deletes, [{ path: dirFor('op-f'), idempotent: true }]);
});

test('GIVEN two operations resolve the same filename THEN they use distinct paths', async () => {
  const h = createHarness({ ids: ['first', 'second'] });

  await runSharedAlbumOriginalDownload(h.io, REQUEST);
  await runSharedAlbumOriginalDownload(h.io, REQUEST);

  // Same server filename ("foto.jpg") twice — never one shared deterministic path.
  assert.notEqual(h.shares[0].uri, h.shares[1].uri);
  assert.match(h.shares[0].uri, /\/nubarca-share-first\/foto\.jpg$/);
  assert.match(h.shares[1].uri, /\/nubarca-share-second\/foto\.jpg$/);
  // Each run cleaned only its OWN directory.
  assert.deepEqual(h.deletes, [
    { path: dirFor('first'), idempotent: true },
    { path: dirFor('second'), idempotent: true },
  ]);
  assert.equal(h.fs.exists(dirFor('second')), false);
});

test('a failing cleanup never masks the original user-visible operation error', async () => {
  const h = createHarness({
    ids: ['op-g'],
    failShareWith: new Error('share sheet exploded'),
    failDeleteWith: new Error('EBUSY: resource busy'),
  });

  // The SHARE failure surfaces — not the cleanup failure.
  await assert.rejects(
    runSharedAlbumOriginalDownload(h.io, REQUEST),
    /share sheet exploded/,
  );
  assert.deepEqual(h.deletes, [{ path: dirFor('op-g'), idempotent: true }]);
});

test('makeSharedDownloadOperationId is fresh per invocation', () => {
  const first = makeSharedDownloadOperationId();
  const second = makeSharedDownloadOperationId();
  assert.notEqual(first, second);
  assert.match(first, /^[a-z0-9]+-[a-z0-9]+$/);
});

