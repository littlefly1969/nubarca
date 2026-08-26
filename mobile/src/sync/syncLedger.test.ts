// Ledger tests run REAL SQL through node:sqlite's DatabaseSync — the same
// dialect and pragmas the expo-sqlite binding executes in production. This
// pins schema versioning, crash recovery, idempotent discovery, account
// namespace isolation and the absence of any credential-shaped column.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { DatabaseSync } from 'node:sqlite';
import {
  ensureLedgerSchema,
  LedgerSchemaError,
  LEDGER_SCHEMA_VERSION,
  SyncLedger,
} from './syncLedger.ts';
import type { SqlConnection, SqlRow, SqlStatement, SqlValue } from './sqlPort.ts';

function nodeConnection(db: DatabaseSync): SqlConnection {
  const adapt = (statement: ReturnType<DatabaseSync['prepare']>): SqlStatement => ({
    run(...params: SqlValue[]) {
      const info = statement.run(...params);
      return { changes: Number(info.changes) };
    },
    get(...params: SqlValue[]): SqlRow | null {
      return (statement.get(...params) as SqlRow | undefined) ?? null;
    },
    all(...params: SqlValue[]): SqlRow[] {
      return statement.all(...params) as SqlRow[];
    },
  });
  return {
    exec(sql) {
      db.exec(sql);
    },
    prepare(sql) {
      return adapt(db.prepare(sql));
    },
    close() {
      db.close();
    },
  };
}

function openLedger(account: string): { conn: SqlConnection; ledger: SyncLedger } {
  const conn = nodeConnection(new DatabaseSync(':memory:'));
  ensureLedgerSchema(conn);
  return { conn, ledger: new SyncLedger(conn, account) };
}

function discovered(id: string, revision = 1000, isVideo = false) {
  return {
    assetId: id,
    revision,
    filename: `${id}.${isVideo ? 'mp4' : 'jpg'}`,
    isVideo,
    operationKey: `sv1.test${id.padEnd(4, '0')}`,
  };
}

test('fresh file creates schema v1; reopening at current version is a no-op', () => {
  const first = openLedger('a');
  const version = Number(
    first.conn.prepare('PRAGMA user_version').get()?.user_version ?? 0,
  );
  assert.equal(version, LEDGER_SCHEMA_VERSION);
  ensureLedgerSchema(first.conn); // second open must not throw or reset
});

test('an unknown NEWER schema fails safely instead of guessing', () => {
  const conn = nodeConnection(new DatabaseSync(':memory:'));
  conn.exec(`PRAGMA user_version = ${LEDGER_SCHEMA_VERSION + 3}`);
  assert.throws(() => ensureLedgerSchema(conn), LedgerSchemaError);
});

test('discovery is idempotent: the same asset never enqueues twice', () => {
  const { ledger } = openLedger('acct');
  assert.equal(ledger.upsertDiscovered([discovered('a1')], 10), 1);
  assert.equal(ledger.upsertDiscovered([discovered('a1')], 20), 0);
  assert.equal(ledger.counts().pending, 1);
});

test('claim flips due rows to uploading atomically and respects limits', () => {
  const { ledger } = openLedger('acct');
  ledger.upsertDiscovered([discovered('a1'), discovered('a2'), discovered('a3')], 10);

  const batch = ledger.claimDue(2, 50);
  assert.equal(batch.length, 2);
  assert.ok(batch.every((row) => row.state === 'uploading'));

  assert.equal(ledger.counts().uploading, 2);
  assert.equal(ledger.claimDue(5, 50).length, 1);
});

test('retry backoff deadlines are honored before an item may be claimed again', () => {
  const { ledger } = openLedger('acct');
  ledger.upsertDiscovered([discovered('a1')], 10);
  ledger.claimDue(1, 10);
  ledger.markRetryableFailure('a1', 900, 100);
  // While backed off, nothing is due — but the deadline is visible.
  assert.equal(ledger.claimDue(5, 899).length, 0);
  assert.equal(ledger.earliestNextAttemptAt(), 900);
  // Once due, the item flows again.
  assert.equal(ledger.claimDue(5, 900).length, 1);
});

test('completion persists the server FileItem id and lastSyncAt', () => {
  const { ledger } = openLedger('acct');
  ledger.upsertDiscovered([discovered('a1')], 10);
  ledger.claimDue(1, 10);
  ledger.markCompleted('a1', 'file-uuid-1', 20);
  ledger.setLastSyncAt(25);
  assert.equal(ledger.counts().completed, 1);
  assert.equal(ledger.getLastSyncAt(), 25);
});

test('startup recovery requeues stale uploading rows from a dead process', () => {
  const { ledger } = openLedger('acct');
  ledger.upsertDiscovered([discovered('a1'), discovered('a2')], 10);
  ledger.claimDue(2, 10);
  // Simulated crash: nothing marked completed/released.
  assert.equal(ledger.resetStaleUploadingToPending(99), 2);
  const counts = ledger.counts();
  assert.equal(counts.pending, 2);
  assert.equal(counts.uploading, 0);
});

test('asset removal is recorded as skipped, not as failure', () => {
  const { ledger } = openLedger('acct');
  ledger.upsertDiscovered([discovered('a1')], 10);
  ledger.claimDue(1, 10);
  ledger.markSkipped('a1', 11);
  assert.equal(ledger.counts().skipped, 1);
});

test('account namespaces are disjoint even inside one database file', () => {
  const conn = nodeConnection(new DatabaseSync(':memory:'));
  ensureLedgerSchema(conn);
  const ledgerA = new SyncLedger(conn, 'account-A');
  const ledgerB = new SyncLedger(conn, 'account-B');

  ledgerA.upsertDiscovered([discovered('shared-asset-id')], 10);
  // Same asset id under B is an INDEPENDENT row.
  assert.equal(ledgerB.upsertDiscovered([discovered('shared-asset-id')], 11), 1);

  ledgerA.claimDue(5, 12);
  ledgerA.markCompleted('shared-asset-id', 'file-A', 13);

  assert.equal(ledgerA.counts().completed, 1);
  assert.equal(ledgerB.counts().completed, 0);
  assert.equal(ledgerB.counts().pending, 1);
  assert.equal(ledgerA.earliestNextAttemptAt(), null);
});

test('settings persist per account and default conservatively', () => {
  const conn = nodeConnection(new DatabaseSync(':memory:'));
  ensureLedgerSchema(conn);
  const ledgerA = new SyncLedger(conn, 'account-A');
  const ledgerB = new SyncLedger(conn, 'account-B');

  assert.deepEqual(ledgerA.getSettings(), {
    enabled: false,
    wifiOnly: true,
    includeExisting: false,
  });

  ledgerA.saveSettings({ enabled: true, wifiOnly: false, includeExisting: true });
  assert.equal(ledgerA.getSettings().wifiOnly, false);
  // B still sees defaults.
  assert.deepEqual(ledgerB.getSettings(), {
    enabled: false,
    wifiOnly: true,
    includeExisting: false,
  });
});

test('retrying failures clears them without wiping completed history', () => {
  const { ledger } = openLedger('acct');
  ledger.upsertDiscovered([discovered('bad'), discovered('good')], 10);
  ledger.claimDue(2, 10);
  ledger.markPermanentFailure('bad', 11);
  ledger.claimDue(1, 12).forEach(() => undefined);
  ledger.markCompleted('good', 'file-good', 13);

  assert.equal(ledger.retryFailures(14), 1);
  const counts = ledger.counts();
  assert.equal(counts.permanent, 0);
  assert.equal(counts.completed, 1);
});

test('the ledger carries no credential-like columns by construction', () => {
  const { conn } = openLedger('acct');
  const columns = conn
    .prepare('PRAGMA table_info(items)')
    .all()
    .map((row) => String(row.name));
  for (const forbidden of ['cookie', 'token', 'secret', 'password', 'authorization']) {
    assert.ok(!columns.some((name) => name.includes(forbidden)), `found ${forbidden}`);
  }
});


