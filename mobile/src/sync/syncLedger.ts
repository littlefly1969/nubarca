// The persistent synchronization ledger.
//
// Durability contract: EVERY observable transition (discovered, claimed,
// completed, failed, skipped) is committed to SQLite before the engine
// acknowledges it, so process death at any point resumes from the last truth.
// One database file PER ACCOUNT gives hard namespace isolation; the `account`
// column on every row and in every statement is defense-in-depth, so even a
// wrongly opened file can never leak items across identities.
//
// What is deliberately NEVER stored here: cookies/tokens (SecureStore owns the
// session), raw media bytes or paths (the platform library is re-consulted by
// asset id), EXIF/GPS inventories. Rows carry opaque ids, a revision number,
// sync bookkeeping and — after completion — the server FileItem id.

import type { SqlConnection } from './sqlPort.ts';
import { withTransaction } from './sqlPort.ts';
import type { SyncItemState, SyncSettings } from './syncTypes.ts';
import { DEFAULT_SETTINGS } from './syncTypes.ts';

export const LEDGER_SCHEMA_VERSION = 1;

const SCHEMA_SQL = `
CREATE TABLE IF NOT EXISTS meta (
  account TEXT NOT NULL,
  key TEXT NOT NULL,
  value TEXT NOT NULL,
  PRIMARY KEY (account, key)
);
CREATE TABLE IF NOT EXISTS items (
  account TEXT NOT NULL,
  asset_id TEXT NOT NULL,
  revision INTEGER NOT NULL,
  filename TEXT,
  is_video INTEGER NOT NULL DEFAULT 0,
  state TEXT NOT NULL,
  attempts INTEGER NOT NULL DEFAULT 0,
  next_attempt_at INTEGER,
  operation_key TEXT NOT NULL,
  file_id TEXT,
  updated_at INTEGER NOT NULL,
  PRIMARY KEY (account, asset_id)
);
CREATE INDEX IF NOT EXISTS idx_items_state_due
  ON items(account, state, next_attempt_at);
`;

/** Raised for structural ledger problems (future schema, corruption). */
export class LedgerSchemaError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'LedgerSchemaError';
  }
}

function readUserVersion(conn: SqlConnection): number {
  const row = conn.prepare('PRAGMA user_version').get();
  const value = row?.user_version;
  return typeof value === 'number' ? value : Number(value ?? 0);
}

function writeUserVersion(conn: SqlConnection, version: number): void {
  conn.exec(`PRAGMA user_version = ${Number(version)}`);
}

/**
 * Open (creating or migrating as needed). An unknown NEWER schema fails
 * loudly: an old app must never guess at a newer file's semantics, and a
 * corrupt ownership layout must never be processed under any account.
 */
export function ensureLedgerSchema(conn: SqlConnection): void {
  const version = readUserVersion(conn);
  if (version > LEDGER_SCHEMA_VERSION) {
    throw new LedgerSchemaError(
      `Sync ledger schema v${version} is newer than this app understands (v${LEDGER_SCHEMA_VERSION}).`,
    );
  }
  if (version === LEDGER_SCHEMA_VERSION) return;
  if (version !== 0) {
    throw new LedgerSchemaError(`Unsupported legacy sync ledger schema v${version}.`);
  }
  withTransaction(conn, () => {
    conn.exec(SCHEMA_SQL);
    writeUserVersion(conn, LEDGER_SCHEMA_VERSION);
  });
}

/** Row shape returned to the engine (already app-typed). */
export interface SyncLedgerRow {
  assetId: string;
  revision: number;
  filename: string | null;
  isVideo: boolean;
  state: SyncItemState;
  attempts: number;
  nextAttemptAt: number | null;
  operationKey: string;
  fileId: string | null;
  updatedAt: number;
}

export interface DiscoveredAssetInput {
  assetId: string;
  revision: number;
  filename: string | null;
  isVideo: boolean;
  /** Generated once via deps.newOperationId() by the engine. */
  operationKey: string;
}

interface RawItemRow {
  asset_id: string;
  revision: number;
  filename: string | null;
  is_video: number;
  state: string;
  attempts: number;
  next_attempt_at: number | null;
  operation_key: string;
  file_id: string | null;
  updated_at: number;
}

function toAppRow(raw: RawItemRow): SyncLedgerRow {
  return {
    assetId: raw.asset_id,
    revision: Number(raw.revision),
    filename: raw.filename,
    isVideo: Number(raw.is_video) === 1,
    state: raw.state as SyncItemState,
    attempts: Number(raw.attempts),
    nextAttemptAt:
      raw.next_attempt_at === null ? null : Number(raw.next_attempt_at),
    operationKey: raw.operation_key,
    fileId: raw.file_id,
    updatedAt: Number(raw.updated_at),
  };
}

export class SyncLedger {
  private readonly conn: SqlConnection;
  private readonly account: string;

  constructor(conn: SqlConnection, account: string) {
    this.conn = conn;
    this.account = account;
  }

  // ── settings / meta ────────────────────────────────────────────────────

  getSettings(): SyncSettings {
    const raw = this.getMeta('settings');
    if (!raw) return { ...DEFAULT_SETTINGS };
    try {
      const parsed = JSON.parse(raw) as Partial<SyncSettings>;
      return {
        enabled: parsed.enabled === true,
        wifiOnly: parsed.wifiOnly !== false,
        includeExisting: parsed.includeExisting === true,
      };
    } catch {
      return { ...DEFAULT_SETTINGS };
    }
  }

  saveSettings(settings: SyncSettings): void {
    this.setMeta('settings', JSON.stringify(settings));
  }

  getBaselineMs(): number | null {
    const raw = this.getMeta('baseline_ms');
    if (raw === null) return null;
    const value = Number(raw);
    return Number.isFinite(value) ? value : null;
  }

  setBaselineMs(value: number): void {
    this.setMeta('baseline_ms', String(value));
  }

  getLastSyncAt(): number | null {
    const raw = this.getMeta('last_sync_at');
    if (raw === null) return null;
    const value = Number(raw);
    return Number.isFinite(value) ? value : null;
  }

  setLastSyncAt(value: number): void {
    this.setMeta('last_sync_at', String(value));
  }

  private getMeta(key: string): string | null {
    const row = this.conn
      .prepare('SELECT value FROM meta WHERE account = ? AND key = ?')
      .get(this.account, key);
    return row && typeof row.value === 'string' ? row.value : null;
  }

  private setMeta(key: string, value: string): void {
    this.conn
      .prepare(
        'INSERT INTO meta(account, key, value) VALUES(?, ?, ?) ' +
          'ON CONFLICT(account, key) DO UPDATE SET value = excluded.value',
      )
      .run(this.account, key, value);
  }

  // ── discovery ──────────────────────────────────────────────────────────

  /**
   * Idempotent enqueue: an asset already present in ANY state is left exactly
   * as it is, so re-discovering the same unchanged library can never duplicate
   * work or resurrect completed history. Returns how many rows were added.
   */
  upsertDiscovered(batch: DiscoveredAssetInput[], nowMs: number): number {
    let added = 0;
    withTransaction(this.conn, () => {
      const stmt = this.conn.prepare(
        'INSERT OR IGNORE INTO items ' +
          '(account, asset_id, revision, filename, is_video, state, attempts, next_attempt_at, operation_key, file_id, updated_at) ' +
          'VALUES (?, ?, ?, ?, ?, ?, 0, NULL, ?, NULL, ?)',
      );
      for (const asset of batch) {
        const result = stmt.run(
          this.account,
          asset.assetId,
          asset.revision,
          asset.filename,
          asset.isVideo ? 1 : 0,
          'pending',
          asset.operationKey,
          nowMs,
        );
        added += result.changes;
      }
    });
    return added;
  }

  // ── queue operations ───────────────────────────────────────────────────

  /**
   * Atomically claim up to `limit` due items: pending/retryable rows whose
   * time has come flip to `uploading` inside one transaction. The claim IS
   * the crash boundary — a death mid-upload leaves an honest `uploading` row
   * that startup recovery requeues.
   */
  claimDue(limit: number, nowMs: number): SyncLedgerRow[] {
    const claimed: SyncLedgerRow[] = [];
    withTransaction(this.conn, () => {
      const select = this.conn.prepare(
        'SELECT * FROM items ' +
          "WHERE account = ? AND state IN ('pending', 'failed-retryable') " +
          'AND (next_attempt_at IS NULL OR next_attempt_at <= ?) ' +
          // Plain-column ordering keeps idx_items_state_due usable: sorting
          // through COALESCE would force a full sort of the pending set on
          // EVERY claim — quadratic across a large library.
          'ORDER BY next_attempt_at, updated_at LIMIT ?',
      );
      const rows = select.all(this.account, nowMs, limit) as unknown as RawItemRow[];
      if (rows.length === 0) return;
      const update = this.conn.prepare(
        "UPDATE items SET state = 'uploading', updated_at = ? WHERE account = ? AND asset_id = ?",
      );
      for (const raw of rows) {
        update.run(nowMs, this.account, raw.asset_id);
        claimed.push(toAppRow({ ...raw, state: 'uploading' }));
      }
    });
    return claimed;
  }

  markCompleted(assetId: string, fileId: string, nowMs: number): void {
    this.conn
      .prepare(
        "UPDATE items SET state = 'completed', file_id = ?, next_attempt_at = NULL, updated_at = ? " +
          'WHERE account = ? AND asset_id = ?',
      )
      .run(fileId, nowMs, this.account, assetId);
  }

  /** Bounded retry: attempts grow here; the engine decides the deadline. */
  markRetryableFailure(
    assetId: string,
    nextAttemptAt: number | null,
    nowMs: number,
  ): void {
    this.conn
      .prepare(
        "UPDATE items SET state = 'failed-retryable', attempts = attempts + 1, next_attempt_at = ?, updated_at = ? " +
          'WHERE account = ? AND asset_id = ?',
      )
      .run(nextAttemptAt, nowMs, this.account, assetId);
  }

  markPermanentFailure(assetId: string, nowMs: number): void {
    this.conn
      .prepare(
        "UPDATE items SET state = 'failed-permanent', next_attempt_at = NULL, updated_at = ? " +
          'WHERE account = ? AND asset_id = ?',
      )
      .run(nowMs, this.account, assetId);
  }

  markSkipped(assetId: string, nowMs: number): void {
    this.conn
      .prepare(
        "UPDATE items SET state = 'skipped', next_attempt_at = NULL, updated_at = ? " +
          'WHERE account = ? AND asset_id = ?',
      )
      .run(nowMs, this.account, assetId);
  }

  /** Return a cancelled/aborted item to the runnable pool without penalty. */
  releaseToPending(assetId: string, nowMs: number): void {
    this.conn
      .prepare(
        "UPDATE items SET state = 'pending', next_attempt_at = NULL, updated_at = ? " +
          "WHERE account = ? AND asset_id = ? AND state = 'uploading'",
      )
      .run(nowMs, this.account, assetId);
  }

  /**
   * Startup recovery: rows stuck in `uploading` belong to a dead process.
   * They go straight back to pending with their attempt budget preserved.
   */
  resetStaleUploadingToPending(nowMs: number): number {
    let changed = 0;
    withTransaction(this.conn, () => {
      const select = this.conn.prepare(
        "SELECT asset_id FROM items WHERE account = ? AND state = 'uploading'",
      );
      const rows = select.all(this.account) as unknown as Array<{ asset_id: string }>;
      const update = this.conn.prepare(
        "UPDATE items SET state = 'pending', next_attempt_at = NULL, updated_at = ? " +
          'WHERE account = ? AND asset_id = ?',
      );
      for (const row of rows) {
        update.run(nowMs, this.account, row.asset_id);
        changed += 1;
      }
    });
    return changed;
  }

  /** Explicit user action on failed items: retry them WITHOUT wiping history. */
  retryFailures(nowMs: number): number {
    const result = this.conn
      .prepare(
        "UPDATE items SET state = 'pending', attempts = 0, next_attempt_at = NULL, updated_at = ? " +
          "WHERE account = ? AND state IN ('failed-retryable', 'failed-permanent')",
      )
      .run(nowMs, this.account);
    return result.changes;
  }

  /** Earliest scheduled retry across pending/retryable rows, or null. */
  earliestNextAttemptAt(): number | null {
    const row = this.conn
      .prepare(
        'SELECT MIN(next_attempt_at) AS due FROM items ' +
          "WHERE account = ? AND state IN ('pending', 'failed-retryable') " +
          'AND next_attempt_at IS NOT NULL',
      )
      .get(this.account) as unknown as { due: number | null };
    return row.due === null || row.due === undefined ? null : Number(row.due);
  }

  counts(): {
    pending: number;
    uploading: number;
    completed: number;
    retryable: number;
    permanent: number;
    skipped: number;
  } {
    const row = this.conn
      .prepare(
        'SELECT ' +
          "SUM(CASE WHEN state = 'pending' THEN 1 ELSE 0 END) AS pending, " +
          "SUM(CASE WHEN state = 'uploading' THEN 1 ELSE 0 END) AS uploading, " +
          "SUM(CASE WHEN state = 'completed' THEN 1 ELSE 0 END) AS completed, " +
          "SUM(CASE WHEN state = 'failed-retryable' THEN 1 ELSE 0 END) AS retryable, " +
          "SUM(CASE WHEN state = 'failed-permanent' THEN 1 ELSE 0 END) AS permanent, " +
          "SUM(CASE WHEN state = 'skipped' THEN 1 ELSE 0 END) AS skipped " +
          'FROM items WHERE account = ?',
      )
      .get(this.account) as unknown as Record<string, number | null>;
    return {
      pending: Number(row.pending ?? 0),
      uploading: Number(row.uploading ?? 0),
      completed: Number(row.completed ?? 0),
      retryable: Number(row.retryable ?? 0),
      permanent: Number(row.permanent ?? 0),
      skipped: Number(row.skipped ?? 0),
    };
  }
}


