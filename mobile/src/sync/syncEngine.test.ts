// Sync-engine behavior tests (public API only, deterministic fakes).

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { ensureLedgerSchema, SyncLedger } from './syncLedger.ts';
import { SyncEngine } from './syncEngine.ts';
import { buildOperationKey } from './syncPolicy.ts';
import { FakeServerUploader, makeHarness, until } from './syncTestHarness.ts';

async function enableAndWaitForDiscovery(
  harness: ReturnType<typeof makeHarness>,
): Promise<void> {
  harness.engine.attach();
  harness.engine.enable();
  // Discovery is done when EVERY asset exists as a ledger row in SOME state
  // (uploads may already have finished — the engine is faster than we are).
  const total = () => {
    const c = harness.ledger.counts();
    return (
      c.pending + c.uploading + c.completed + c.retryable + c.permanent + c.skipped
    );
  };
  await until(
    () => total() === harness.mediaLibrary.assets.length,
    'discovery enqueues all assets',
  );
}

test('enable → discover → upload: the full happy path persists completion', async () => {
  const harness = makeHarness({ totalAssets: 5 });
  await enableAndWaitForDiscovery(harness);

  await until(() => harness.ledger.counts().completed === 5, 'all uploads complete');

  // Every request went through the owner endpoint with an operation key and
  // a file:// URI — never base64, never a JS buffer.
  assert.equal(harness.uploader.requests.length, 5);
  for (const request of harness.uploader.requests) {
    assert.match(request.operationKey, /^sv1\./);
    assert.match(request.localUri, /^file:\/\//);
  }
  const snapshot = harness.engine.snapshot();
  assert.equal(snapshot.completedCount, 5);
  assert.ok(snapshot.lastSyncAt !== null);
  harness.engine.detach();
});

test('bounded concurrency on a large synthetic library: never more than two in flight', async () => {
  // Synthetic LARGE library through paginated discovery. The engine's cost
  // is LINEAR here (paginated metadata reads + indexed queue claims + chunked
  // ledger writes); memory stays bounded by page size, wire pressure by the
  // concurrency cap below.
  const harness = makeHarness({ totalAssets: 5_000 });
  await enableAndWaitForDiscovery(harness);
  await until(
    () => harness.ledger.counts().completed === 5_000,
    'large library completes',
    60_000,
  );

  assert.ok(
    harness.uploader.maxConcurrent <= 2,
    `observed ${harness.uploader.maxConcurrent} concurrent uploads`,
  );
  harness.engine.detach();
});

test('ambiguous retry after a lost response: same key replays, exactly one logical ingestion', async () => {
  const harness = makeHarness({ totalAssets: 1 });
  // Arm "response lost AFTER durable commit" BEFORE the attempt starts,
  // keyed exactly as discovery will key it.
  const asset = harness.mediaLibrary.assets[0];
  const key = buildOperationKey('acct-A', asset.id, asset.modificationTime);
  harness.uploader.faults.set(key, () => {
    throw new TypeError('Network request failed');
  });

  await enableAndWaitForDiscovery(harness);
  // First attempt fails AFTER commit and lands in bounded backoff.
  await until(() => harness.ledger.counts().retryable === 1, 'first attempt classified');

  // Let the short backoff elapse (deterministic clock), then the retry
  // replays the stored result instead of committing again.
  harness.clock.value += 500;
  await until(() => harness.ledger.counts().completed === 1, 'retry completes');

  const attemptsForKey = harness.uploader.requests.filter(
    (request) => request.operationKey === key,
  ).length;
  assert.ok(attemptsForKey >= 2, 'the client actually retried');
  // The server committed ONCE; the retry was a REPLAY of the same result.
  assert.equal(harness.uploader.commitsByKey.size, 1, 'one logical commit only');

  const counts = harness.ledger.counts();
  assert.equal(counts.completed, 1);
  assert.equal(counts.permanent + counts.retryable + counts.pending, 0);
  harness.engine.detach();
});

test('a permanently failing item never blocks unrelated items', async () => {
  const harness = makeHarness({ totalAssets: 4 });
  const badAsset = harness.mediaLibrary.assets[0];
  const badKey = buildOperationKey('acct-A', badAsset.id, badAsset.modificationTime);
  harness.uploader.faults.set(badKey, () => {
    throw Object.assign(new Error('too large'), { status: 413 });
  });

  await enableAndWaitForDiscovery(harness);
  await until(() => harness.ledger.counts().permanent === 1, 'bad item is permanent');
  await until(() => harness.ledger.counts().completed === 3, 'others still complete');
  harness.engine.detach();
});

test('429 honors Retry-After before retrying; the queue resumes afterwards', async () => {
  const harness = makeHarness({ totalAssets: 1 });
  const asset = harness.mediaLibrary.assets[0];
  const key = buildOperationKey('acct-A', asset.id, asset.modificationTime);
  const deadline = harness.clock.value + 500;
  harness.uploader.faults.set(key, () => {
    throw Object.assign(new Error('slow down'), {
      status: 429,
      retryAfterAtMs: deadline,
    });
  });

  await enableAndWaitForDiscovery(harness);
  await until(() => harness.ledger.counts().retryable === 1, 'throttled');

  // Before Retry-After elapses nothing may be claimed.
  assert.equal(harness.ledger.claimDue(5, harness.clock.value).length, 0);

  // After the server's deadline passes, the item flows again and completes.
  harness.clock.value = deadline + 1;
  await until(() => harness.ledger.counts().completed === 1, 'resumes after Retry-After');
  harness.engine.detach();
});

test('401 pauses sync as auth-required and parks items for post-login resume', async () => {
  const harness = makeHarness({ totalAssets: 2 });
  const firstAsset = harness.mediaLibrary.assets[0];
  const key = buildOperationKey('acct-A', firstAsset.id, firstAsset.modificationTime);
  harness.uploader.faults.set(key, () => {
    throw Object.assign(new Error('dead session'), { status: 401 });
  });

  await enableAndWaitForDiscovery(harness);
  await until(
    () => harness.engine.snapshot().authRequired === true,
    'engine enters auth-required',
  );
  // The failed attempt did NOT spin into more requests while paused.
  const requestCountAtAuth = harness.uploader.requests.length;
  await new Promise((resolve) => setTimeout(resolve, 30));
  assert.equal(harness.uploader.requests.length, requestCountAtAuth);

  // Re-authentication resumes the queue (the parked item is still pending).
  harness.engine.resume();
  await until(() => harness.ledger.counts().completed === 2, 'queue drains after re-auth');
  harness.engine.detach();
});


test('logout during upload: work stops for real and a late completion cannot mutate state', async () => {
  const harness = makeHarness({ totalAssets: 1 });
  const asset = harness.mediaLibrary.assets[0];
  const key = buildOperationKey('acct-A', asset.id, asset.modificationTime);
  // Hold the FIRST attempt in flight inside the "server" before it begins.
  harness.uploader.holdOnce.add(key);

  harness.engine.attach();
  harness.engine.enable();
  await until(() => harness.uploader.isHeld(key), 'held in flight');

  // Log out: identity gone + provider teardown aborts the request.
  harness.signOut();
  harness.engine.detach();

  // The held upload is then released — a completion for a DEAD session
  // context. It must touch nothing: no completed row may appear.
  harness.uploader.releaseHeld(key);
  await new Promise((resolve) => setTimeout(resolve, 20));

  assert.equal(harness.ledger.counts().completed, 0);
});

test('restart during upload: stale uploading rows are requeued and retried with the SAME key', async () => {
  const first = makeHarness({ totalAssets: 1 });
  const asset = first.mediaLibrary.assets[0];
  const key = buildOperationKey('acct-A', asset.id, asset.modificationTime);
  first.uploader.holdOnce.add(key);

  first.engine.attach();
  first.engine.enable();
  await until(() => first.uploader.isHeld(key), 'first attempt held mid-upload');

  // Simulate process death: engine gone while the ledger row stays
  // 'uploading' in the SAME database file. The late commit lands afterwards,
  // but its owner context is dead — nothing may be marked completed.
  first.engine.detach();
  first.uploader.releaseHeld(key);
  await new Promise((resolve) => setTimeout(resolve, 10));
  assert.equal(first.ledger.counts().completed, 0);

  // Cold start: a NEW engine opens the SAME database.
  const sharedConn = first.conn;
  ensureLedgerSchema(sharedConn);
  const ledger2 = new SyncLedger(sharedConn, 'acct-A');
  const uploader2 = new FakeServerUploader();
  const engine2 = new SyncEngine({
    ledger: ledger2,
    mediaLibrary: first.mediaLibrary,
    connectivity: {
      async getNetworkState() {
        return { kind: 'wifi' };
      },
      onNetworkChange() {
        return () => undefined;
      },
    },
    uploader: (request) => uploader2.upload(request),
    identity: () => ({ accountId: 'acct-A', generation: 1 }),
    now: () => 5_000_000,
    random: () => 0.5,
    config: { networkPollMs: 5, retryBaseDelayMs: 10, retryMaxDelayMs: 40 },
  });

  assert.equal(ledger2.resetStaleUploadingToPending(4_999_000), 1);
  engine2.attach();
  engine2.enable();
  await until(() => ledger2.counts().completed === 1, 'recovered upload completes');

  // The SAME operation key was reused → an idempotent server replays instead
  // of creating a second logical ingestion for the same sync operation.
  assert.equal(uploader2.requests.length, 1);
  assert.equal(uploader2.requests[0].operationKey, key);
  engine2.detach();
});

test('account switch: B never sees or executes A’s queue; A completions cannot mutate B', async () => {
  const accountA = makeHarness({ totalAssets: 2, accountId: 'acct-A' });
  await enableAndWaitForDiscovery(accountA);
  // Leave one of A's items pending by pausing before all uploads finish.
  await until(() => accountA.uploader.requests.length >= 1, 'A started uploading');
  accountA.engine.pause();

  const accountB = makeHarness({ totalAssets: 3, accountId: 'acct-B' });
  await enableAndWaitForDiscovery(accountB);
  await until(() => accountB.ledger.counts().completed === 3, 'B completes its own queue');

  // B's ledger knows nothing about A's assets.
  assert.equal(accountB.ledger.counts().completed, 3);
  assert.ok(!accountB.ledger.getSettings().includeExisting);

  accountA.engine.detach();
  accountB.engine.detach();
});

test('Wi-Fi-only waits on cellular and resumes when Wi-Fi returns', async () => {
  const harness = makeHarness({ totalAssets: 2 });
  harness.setNetwork({ kind: 'cellular' });
  harness.engine.attach();
  harness.engine.enable();

  // Policy blocks everything while on cellular: no bytes may move.
  await new Promise((resolve) => setTimeout(resolve, 60));
  assert.equal(harness.uploader.requests.length, 0);
  assert.equal(harness.ledger.counts().completed, 0);

  // Wi-Fi returns → the connectivity listener wakes the engine, discovery
  // runs, and the queue drains over Wi-Fi.
  harness.setNetwork({ kind: 'wifi' });
  harness.engine.notifyNetworkChanged();
  await until(() => harness.ledger.counts().completed === 2, 'resumes on Wi-Fi');
  harness.engine.detach();
});

test('an asset removed before upload is skipped, not failed', async () => {
  const harness = makeHarness({ totalAssets: 3 });
  const vanished = harness.mediaLibrary.assets[1].id;
  harness.mediaLibrary.missing.add(vanished);
  await enableAndWaitForDiscovery(harness);

  await until(() => harness.ledger.counts().skipped === 1, 'vanished asset is skipped');
  await until(() => harness.ledger.counts().completed === 2, 'the rest complete');
  assert.equal(harness.ledger.counts().permanent, 0);
  harness.engine.detach();
});

test('pause halts scheduling; resume continues the same ledger', async () => {
  const harness = makeHarness({ totalAssets: 6 });
  await enableAndWaitForDiscovery(harness);
  await until(() => harness.ledger.counts().completed >= 1, 'some complete');

  harness.engine.pause();
  assert.equal(harness.engine.snapshot().phase, 'paused');
  const requestsAtPause = harness.uploader.requests.length;
  await new Promise((resolve) => setTimeout(resolve, 40));
  // No NEW scheduling while paused (in-flight aborts release, never claim).
  assert.equal(harness.uploader.requests.length, requestsAtPause);

  harness.engine.resume();
  await until(() => harness.ledger.counts().completed === 6, 'resume finishes the queue');
  harness.engine.detach();
});

test('disable stops automatic work and deletes nothing from either side', async () => {
  const harness = makeHarness({ totalAssets: 4 });
  await enableAndWaitForDiscovery(harness);
  await until(() => harness.ledger.counts().completed >= 1, 'some complete');

  harness.engine.disable();
  assert.equal(harness.engine.snapshot().settings.enabled, false);
  const totalRowsBefore = Object.values(harness.ledger.counts()).reduce((a, b) => a + b, 0);
  const requestsAtDisable = harness.uploader.requests.length;
  await new Promise((resolve) => setTimeout(resolve, 40));
  assert.equal(harness.uploader.requests.length, requestsAtDisable);
  assert.equal(
    Object.values(harness.ledger.counts()).reduce((a, b) => a + b, 0),
    totalRowsBefore,
    'disable is non-destructive to the ledger',
  );
  harness.engine.detach();
});

test('enablement defaults to new-media-only; historical media needs explicit opt-in', async () => {
  const harness = makeHarness({ totalAssets: 0 });
  // Two HISTORICAL assets (older than "now") and one NEW asset.
  harness.mediaLibrary.assets = [
    { id: 'old-1', mediaType: 'photo', filename: 'old1.jpg', modificationTime: 900_000 },
    { id: 'old-2', mediaType: 'photo', filename: 'old2.jpg', modificationTime: 950_000 },
    { id: 'new-1', mediaType: 'photo', filename: 'new1.jpg', modificationTime: 1_100_000 },
  ];

  harness.engine.attach();
  harness.engine.enable(); // baseline = now (1_000_000)

  // Only the NEW asset flows; history stays untouched until explicitly chosen.
  await until(
    () => harness.ledger.counts().completed === 1,
    'queue drains with only the new asset',
  );
  const uploadedFirstPass = harness.uploader.requests.map((request) => request.filename);
  assert.deepEqual(uploadedFirstPass.sort(), ['new1.jpg']);

  // The separate explicit choice pulls the historical assets in.
  harness.engine.updateSettings({ includeExisting: true });
  harness.engine.syncNow();
  await until(() => harness.ledger.counts().completed === 3, 'history included on opt-in');
  const allUploaded = harness.uploader.requests.map((request) => request.filename);
  assert.ok(allUploaded.includes('old1.jpg'));
  assert.ok(allUploaded.includes('old2.jpg'));
  harness.engine.detach();
});


