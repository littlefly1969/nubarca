// Deterministic fakes for sync-engine tests. The uploader models the REAL
// server contract under test: an Idempotency-Key-scoped commit map where a
// retried key REPLAYS its result instead of committing twice.

import { DatabaseSync } from 'node:sqlite';
import type { SqlConnection, SqlRow, SqlStatement, SqlValue } from './sqlPort.ts';
import { ensureLedgerSchema, SyncLedger } from './syncLedger.ts';
import { SyncEngine } from './syncEngine.ts';
import { DEFAULT_CONFIG } from './syncConfig.ts';
import type {
  AssetPage,
  ConnectivityPort,
  MediaLibraryPort,
  NetworkState,
  PagedAsset,
  PermissionState,
  UploadRequest,
  UploadedFile,
} from './syncTypes.ts';

export function nodeConnection(db: DatabaseSync): SqlConnection {
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

export class FakeMediaLibrary implements MediaLibraryPort {
  permission: PermissionState = 'granted';
  assets: PagedAsset[] = [];
  /** Assets the OS no longer exposes (deleted / limited-access revoked). */
  missing = new Set<string>();

  async getPermissions(): Promise<PermissionState> {
    return this.permission;
  }

  async requestPermissions(): Promise<PermissionState> {
    this.permission = 'granted';
    return this.permission;
  }

  async getPage(cursor: string | null, pageSize: number): Promise<AssetPage> {
    const start = cursor === null ? 0 : Number(cursor);
    const slice = this.assets.slice(start, start + pageSize);
    const next = start + slice.length;
    return {
      assets: slice,
      hasNextPage: next < this.assets.length,
      endCursor: String(next),
    };
  }

  async getLocalInfo(assetId: string): Promise<{ uri: string } | null> {
    if (this.missing.has(assetId)) return null;
    return { uri: `file:///media/${assetId}` };
  }
}

export interface ServerRequest {
  operationKey: string;
  localUri: string;
  filename: string;
}

/**
 * Models the server-side ingestion semantics the mobile feature relies on:
 *   * first POST for a key COMMITS and answers with its FileSummary;
 *   * a repeat of the SAME key REPLAYS the stored result (no second commit);
 *   * scripted faults simulate lost responses and status failures.
 */
export class FakeServerUploader {
  readonly commitsByKey = new Map<string, UploadedFile>();
  readonly requests: ServerRequest[] = [];
  /**
   * Request-index → one-shot fault thrown on THAT attempt, AFTER the durable
   * commit (models "server accepted, response lost"). Retries create new
   * indices and proceed normally.
   */
  readonly armedFaults = new Map<number, () => never>();
  /** Request indexes whose NEXT attempt parks mid-flight until released. */
  readonly holdRequestsSet = new Set<number>();
  private holdWaiters = new Map<number, Array<() => void>>();
  maxConcurrent = 0;
  current = 0;

  armFaultOnRequest(index: number, thrower: () => never): void {
    this.armedFaults.set(index, thrower);
  }

  holdRequest(index: number): void {
    this.holdRequestsSet.add(index);
  }

  releaseHeld(index: number): void {
    const waiters = this.holdWaiters.get(index) ?? [];
    this.holdWaiters.delete(index);
    for (const waiter of waiters) waiter();
  }

  /** True while the held upload is parked inside the fake server. */
  isHeld(index: number): boolean {
    return this.holdWaiters.has(index);
  }

  async upload(request: UploadRequest): Promise<UploadedFile> {
    this.current += 1;
    this.maxConcurrent = Math.max(this.maxConcurrent, this.current);
    try {
      const index = this.requests.length;
      this.requests.push({
        operationKey: request.operationKey,
        localUri: request.localUri,
        filename: request.filename,
      });

      // Cancellation must be observable: an aborted signal rejects like RN.
      if (request.signal.aborted) {
        throw Object.assign(new Error('aborted'), { name: 'AbortError' });
      }

      if (this.holdRequestsSet.has(index)) {
        this.holdRequestsSet.delete(index);
        await new Promise<void>((resolve) => {
          const waiters = this.holdWaiters.get(index) ?? [];
          waiters.push(resolve);
          this.holdWaiters.set(index, waiters);
        });
      }

      const fault = this.armedFaults.get(index);
      if (fault) {
        this.armedFaults.delete(index);
      }

      const existing = this.commitsByKey.get(request.operationKey);
      if (existing) {
        // IDEMPOTENT REPLAY — never commit twice.
        if (fault) fault();
        return existing;
      }
      const file: UploadedFile = {
        id: `file-${request.operationKey}`,
        name: request.filename,
        mimeType: 'application/octet-stream',
        sizeBytes: 123,
        createdAt: new Date(0).toISOString(),
        width: 100,
        height: 100,
      };
      // Durable commit happens HERE; an armed fault thrown past this point
      // models exactly "server accepted, response was lost".
      this.commitsByKey.set(request.operationKey, file);
      if (fault) fault();
      return file;
    } finally {
      this.current -= 1;
    }
  }
}

export function makeHarness(options?: { totalAssets?: number; accountId?: string }) {
  const accountId = options?.accountId ?? 'acct-A';
  const conn = nodeConnection(new DatabaseSync(':memory:'));
  ensureLedgerSchema(conn);
  const ledger = new SyncLedger(conn, accountId);

  const mediaLibrary = new FakeMediaLibrary();
  const totalAssets = options?.totalAssets ?? 6;
  mediaLibrary.assets = Array.from({ length: totalAssets }, (_, i) => ({
    id: `asset-${String(i).padStart(5, '0')}`,
    mediaType: i % 3 === 2 ? ('video' as const) : ('photo' as const),
    filename: `IMG_${String(i).padStart(5, '0')}.${i % 3 === 2 ? 'mp4' : 'jpg'}`,
    modificationTime: 1_700_000_000_000 + i,
  }));

  let network: NetworkState = { kind: 'wifi' };
  const listeners = new Set<(state: NetworkState) => void>();
  const connectivity: ConnectivityPort = {
    async getNetworkState() {
      return network;
    },
    onNetworkChange(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
  };

  const uploader = new FakeServerUploader();
  const clock = { value: 1_000_000 };
  let identityState: { accountId: string; generation: number } | null = {
    accountId,
    generation: 1,
  };

  // Deterministic, grammar-safe OPERATION identities in generation order —
  // tests can assert "the SAME id was reused across retries/restarts" without
  // knowing values upfront.
  let opCounter = 0;
  const operationIds: string[] = [];
  const newOperationId = (): string => {
    const id = `op-${String(++opCounter).padStart(6, '0')}`;
    operationIds.push(id);
    return id;
  };

  const engine = new SyncEngine({
    ledger,
    mediaLibrary,
    connectivity,
    uploader: (request) => uploader.upload(request),
    identity: () => identityState,
    now: () => clock.value,
    random: () => 0.5,
    newOperationId,
    config: {
      ...DEFAULT_CONFIG,
      networkPollMs: 5,
      // Fast, deterministic retries in tests only.
      retryBaseDelayMs: 10,
      retryMaxDelayMs: 40,
    },
  });

  return {
    conn,
    ledger,
    mediaLibrary,
    uploader,
    clock,
    engine,
    newOperationId,
    /** Operation ids in generation order (discovery order). */
    operationIds,
    setNetwork(next: NetworkState) {
      network = next;
      for (const listener of listeners) listener(next);
    },
    signOut() {
      identityState = null;
    },
    signInAs(nextAccount: string) {
      identityState = { accountId: nextAccount, generation: 2 };
    },
  };
}

export async function until(
  predicate: () => boolean,
  message = 'condition not met',
  timeoutMs = 4000,
): Promise<void> {
  const started = Date.now();
  while (!predicate()) {
    if (Date.now() - started > timeoutMs) throw new Error(`timeout: ${message}`);
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
}

