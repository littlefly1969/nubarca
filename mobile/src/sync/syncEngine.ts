// The ONE synchronization orchestrator.
//
// Responsibilities stay deliberately narrow: run the eligibility/network
// policy, drive bounded discovery and a bounded upload queue through the
// persistent ledger, classify failures via syncPolicy, and guard every
// completion against session-identity staleness. React components observe it;
// they never become it.
//
// Invariants worth calling out:
//   * Every durable transition goes through the ledger FIRST — crash at any
//     point resumes from committed truth, never from memory.
//   * Uploads are file/native-backed ({ uri } form parts); originals are
//     never read into the JS heap, never base64-encoded.
//   * At most config.maxConcurrentUploads uploads exist at once; there is no
//     unbounded Promise.all anywhere.
//   * A completion mutates state only while its captured identity generation
//     is still current — logout/account-switch orphans are dropped silently.
//   * The idempotency key sent on the wire is an OPERATION identity built per
//     logical sync; blob identity/dedup stays entirely server-side.

import { DEFAULT_CONFIG, type SyncConfig } from './syncConfig.ts';
import type {
  ConnectivityPort,
  EnginePhase,
  EngineSnapshot,
  MediaLibraryPort,
  SyncSettings,
  UploaderPort,
} from './syncTypes.ts';
import type { SyncLedger, SyncLedgerRow } from './syncLedger.ts';
import {
  backoffDelayMs,
  buildOperationKey,
  classifyHttpFailure,
  isUploadAllowed,
  mimeFromFilename,
} from './syncPolicy.ts';

interface InflightEntry {
  controller: AbortController;
  accountId: string;
  generation: number;
}

export interface SyncEngineDeps {
  ledger: SyncLedger;
  mediaLibrary: MediaLibraryPort;
  connectivity: ConnectivityPort;
  uploader: UploaderPort;
  /**
   * The authenticated identity this engine works for, WITH its session
   * generation. Returns null once signed out. Completions compare against
   * what they captured at start; any mismatch means "not my account's
   * reality anymore" and is dropped before touching the ledger.
   */
  identity: () => { accountId: string; generation: number } | null;
  now(): number;
  random?: () => number;
  config?: Partial<SyncConfig>;
}

export class SyncEngine {
  private readonly deps: SyncEngineDeps;
  private readonly config: SyncConfig;

  private settings: SyncSettings;
  private phase: EnginePhase = 'idle';
  private permission: 'undetermined' | 'granted' | 'limited' | 'denied' = 'undetermined';
  private authRequired = false;
  private userPaused = false;

  private stopped = false;
  private scanning = false;
  /** Set when discovery must run even if a pass just completed. */
  private discoveryRequested = true;

  private readonly inflight = new Map<string, InflightEntry>();
  private readonly waiters = new Set<() => void>();
  private readonly listeners = new Set<(snapshot: EngineSnapshot) => void>();

  constructor(deps: SyncEngineDeps) {
    this.deps = deps;
    this.config = { ...DEFAULT_CONFIG, ...deps.config };
    this.settings = deps.ledger.getSettings();
  }

  // ── lifecycle ───────────────────────────────────────────────────────────

  /**
   * Attach to a freshly opened ledger for the CURRENT account. Requeues rows
   * stuck in `uploading` from a dead process (crash recovery), refreshes the
   * permission view without ever prompting, and starts scheduling if enabled.
   */
  attach(): void {
    const recovered = this.deps.ledger.resetStaleUploadingToPending(this.now());
    if (recovered > 0) {
      // Recovered work is real pending work; make sure scheduling sees it.
      this.discoveryRequested = true;
    }
    void this.refreshPermission().then(() => {
      this.emit();
      this.startLoop();
    });
  }

  /** Full stop for teardown/logout. Active uploads are really aborted. */
  detach(): void {
    this.stopped = true;
    for (const entry of this.inflight.values()) {
      entry.controller.abort('detach');
    }
    this.interruptAll();
  }

  async refreshPermission(): Promise<void> {
    try {
      this.permission = await this.deps.mediaLibrary.getPermissions();
    } catch {
      // A platform hiccup must not flip a granted user into onboarding.
      if (this.permission === 'undetermined') this.permission = 'denied';
    }
  }

  private now(): number {
    return this.deps.now();
  }

  // ── user controls ───────────────────────────────────────────────────────

  /**
   * Enablement AFTER the user granted media access in the UI flow (the
   * engine itself NEVER prompts). Defaults to new-media-only; historical
   * ingestion requires the separate explicit includeExisting choice.
   */
  enable(settings?: Partial<SyncSettings>): void {
    this.userPaused = false;
    this.authRequired = false;
    this.settings = { ...this.settings, ...settings, enabled: true };
    this.deps.ledger.saveSettings(this.settings);
    if (this.deps.ledger.getBaselineMs() === null) {
      // New-media baseline: only assets newer than THIS moment flow until
      // includeExisting is explicitly turned on.
      this.deps.ledger.setBaselineMs(this.now());
    }
    if (settings?.includeExisting === true) {
      this.discoveryRequested = true;
    }
    this.emit();
    this.kick();
  }

  disable(): void {
    // Non-destructive by definition: ledger rows survive untouched; nothing
    // is deleted on either side; automatic work simply stops.
    this.settings = { ...this.settings, enabled: false };
    this.deps.ledger.saveSettings(this.settings);
    for (const entry of this.inflight.values()) {
      entry.controller.abort('disable');
    }
    this.phase = 'idle';
    this.emit();
    this.interruptAll();
  }

  pause(): void {
    this.userPaused = true;
    for (const entry of this.inflight.values()) {
      entry.controller.abort('pause');
    }
    this.phase = 'paused';
    this.emit();
    this.interruptAll();
  }

  resume(): void {
    this.userPaused = false;
    this.authRequired = false;
    this.emit();
    this.kick();
  }

  updateSettings(partial: Partial<SyncSettings>): void {
    this.settings = { ...this.settings, ...partial };
    this.deps.ledger.saveSettings(this.settings);
    this.emit();
    this.kick();
  }

  /** Manual "Sync now": force a reconciliation pass, then drain the queue. */
  syncNow(): void {
    this.discoveryRequested = true;
    this.kick();
  }

  /** Retry failed items without wiping completed history. */
  retryFailedItems(): void {
    this.deps.ledger.retryFailures(this.now());
    this.authRequired = false;
    this.emit();
    this.kick();
  }

  /** Called by connectivity listeners; cheap unless policy just changed. */
  notifyNetworkChanged(): void {
    this.kick();
  }

  /**
   * Foreground resume: the app became active. If sync is enabled and not
   * paused/auth-blocked, re-check permission and network policy and continue.
   * This is the GUARANTEED recovery path; background work is best-effort at
   * the OS's mercy (Android gives no reliable long-running upload guarantee).
   */
  resumeForeground(): void {
    void this.refreshPermission().then(() => {
      this.emit();
      this.kick();
    });
  }

  // ── observation ─────────────────────────────────────────────────────────

  subscribe(listener: (snapshot: EngineSnapshot) => void): () => void {
    this.listeners.add(listener);
    listener(this.snapshot());
    return () => {
      this.listeners.delete(listener);
    };
  }

  snapshot(): EngineSnapshot {
    const counts = this.deps.ledger.counts();
    return {
      settings: { ...this.settings },
      phase: this.currentPhase(),
      permission: this.permission,
      authRequired: this.authRequired,
      pendingCount: counts.pending,
      retryableCount: counts.retryable,
      permanentCount: counts.permanent,
      uploadingCount: this.inflight.size,
      completedCount: counts.completed,
      skippedCount: counts.skipped,
      lastSyncAt: this.deps.ledger.getLastSyncAt(),
    };
  }

  private currentPhase(): EnginePhase {
    if (!this.settings.enabled) return 'disabled';
    if (this.permission === 'denied' || this.permission === 'undetermined') {
      return 'permission-blocked';
    }
    if (this.userPaused || this.authRequired) return 'paused';
    if (this.scanning) return 'discovering';
    if (this.inflight.size > 0) return 'working';
    return this.phase;
  }

  private emit(): void {
    const snapshot = this.snapshot();
    for (const listener of [...this.listeners]) listener(snapshot);
  }

  private emitScheduled = false;

  /**
   * Coalesced emit for HIGH-FREQUENCY transitions (per-item completions,
   * discovery progress). Computing a snapshot scans ledger aggregates, so
   * emitting per item would be quadratic across a large library; observers
   * need freshness, not every intermediate value. User-facing controls keep
   * using immediate emit().
   */
  private scheduleEmit(): void {
    if (this.emitScheduled) return;
    this.emitScheduled = true;
    setTimeout(() => {
      this.emitScheduled = false;
      if (!this.stopped) this.emit();
    }, 25);
  }

  // ── wake/interrupt plumbing ─────────────────────────────────────────────

  /** Wake whatever the loop is waiting on. Cheap; safe to call often. */
  private kick(): void {
    this.interruptAll();
  }

  private interruptAll(): void {
    for (const waiter of [...this.waiters]) waiter();
  }

  private async interruptibleSleep(ms: number): Promise<void> {
    if (this.stopped) return;
    // Indefinite waits register ONLY a wake waiter: setTimeout coerces
    // Infinity down to ~1ms, which would turn "idle" into a hot loop and
    // pin the event loop open forever.
    if (ms === Number.POSITIVE_INFINITY) {
      await new Promise<void>((resolve) => {
        const done = () => {
          this.waiters.delete(done);
          resolve();
        };
        this.waiters.add(done);
      });
      return;
    }
    await new Promise<void>((resolve) => {
      const done = () => {
        clearTimeout(timer);
        this.waiters.delete(done);
        resolve();
      };
      const timer = setTimeout(done, Math.max(1, ms));
      void timer;
    });
  }

  // ── scheduler loop ──────────────────────────────────────────────────────

  private loopActive = false;

  /**
   * Idempotently ensure the single scheduling loop is running. All user
   * controls funnel through here; the loop itself is one sequential worker
   * plus the (bounded) per-item upload promises it spawns.
   */
  private startLoop(): void {
    if (this.loopActive || this.stopped) return;
    this.loopActive = true;
    void this.runLoop().finally(() => {
      this.loopActive = false;
    });
  }

  private identityIsCurrent(accountId: string, generation: number): boolean {
    if (this.stopped) return false;
    const current = this.deps.identity();
    return (
      current !== null &&
      current.accountId === accountId &&
      current.generation === generation
    );
  }

  private async runLoop(): Promise<void> {
    while (!this.stopped) {
      if (!this.settings.enabled || this.userPaused || this.authRequired) {
        this.phase = 'idle';
        this.emit();
        await this.interruptibleSleep(Number.POSITIVE_INFINITY);
        continue;
      }
      // Permission can be revoked at any moment in OS settings.
      await this.refreshPermission();
      if (this.permission === 'denied' || this.permission === 'undetermined') {
        this.phase = 'idle';
        this.emit();
        // No busy loop, no re-prompting: wait for an explicit user action or
        // a foreground resume to re-check.
        await this.interruptibleSleep(Number.POSITIVE_INFINITY);
        continue;
      }

      const network = await this.deps.connectivity.getNetworkState();
      const countsNow = this.deps.ledger.counts();
      const hasRunnableWork =
        countsNow.pending + countsNow.retryable > 0 || countsNow.uploading > 0;
      if (!isUploadAllowed(this.settings.wifiOnly, network.kind)) {
        this.phase = hasRunnableWork ? 'waiting-network' : 'idle';
        this.emit();
        // Lazy poll only; policy blocks work either way, never a busy loop.
        await this.interruptibleSleep(
          hasRunnableWork ? this.config.networkPollMs : Number.POSITIVE_INFINITY,
        );
        continue;
      }

      if (this.discoveryRequested && this.inflight.size === 0) {
        await this.runDiscoveryPass();
        continue;
      }

      const freeSlots = this.config.maxConcurrentUploads - this.inflight.size;
      if (freeSlots > 0) {
        const claimed = this.deps.ledger.claimDue(freeSlots, this.now());
        if (claimed.length > 0) {
          this.phase = 'working';
          for (const row of claimed) void this.uploadItem(row);
          // Yield to the macrotask queue once per scheduling cycle so a
          // fully-microtask pipeline (fake or real fast transports) can never
          // monopolize the event loop while a large queue drains.
          await new Promise((resolve) => setImmediate(resolve));
          continue;
        }
      }

      if (this.inflight.size > 0) {
        this.phase = 'working';
        this.emit();
        await this.waitForInflightChange();
        continue;
      }

      // Nothing runnable right now. If work exists but is backing off, wake
      // when the earliest retry comes due (capped by the lazy poll); else
      // idle until kicked.
      const earliestDue = this.deps.ledger.earliestNextAttemptAt();
      if (earliestDue !== null) {
        this.phase = 'waiting-network';
        this.emit();
        await this.interruptibleSleep(
          Math.max(0, Math.min(earliestDue - this.now(), this.config.networkPollMs)),
        );
        continue;
      }

      this.phase = 'idle';
      this.emit();
      await this.interruptibleSleep(Number.POSITIVE_INFINITY);
    }
  }

  private waitForInflightChange(): Promise<void> {
    return new Promise<void>((resolve) => {
      const done = () => {
        this.waiters.delete(done);
        resolve();
      };
      this.waiters.add(done);
    });
  }

  // ── discovery ───────────────────────────────────────────────────────────

  /**
   * One full reconciliation pass over the platform library: paginated,
   * memory-bounded (only ever `discoveryPageSize` asset descriptors alive),
   * idempotent (the ledger dedups), restart-safe (a new pass starts from the
   * top with zero harm). New-media-only mode filters by the enablement
   * baseline instead of trusting any fragile platform cursor.
   */
  async runDiscoveryPass(): Promise<{ pagesFetched: number; addedCount: number }> {
    const accountId = this.deps.identity()?.accountId ?? '';
    const includeExisting = this.settings.includeExisting;
    const baselineMs = includeExisting
      ? 0
      : (this.deps.ledger.getBaselineMs() ?? this.now());

    this.scanning = true;
    this.discoveryRequested = false;
    this.phase = 'discovering';
    this.emit();

    let cursor: string | null = null;
    let pagesFetched = 0;
    let addedCount = 0;

    try {
      while (!this.stopped && !this.discoveryRequested) {
        if (this.deps.identity() === null) break;
        const page = await this.deps.mediaLibrary.getPage(cursor, this.config.discoveryPageSize);
        pagesFetched += 1;

        const nowMs = this.now();
        const eligible = page.assets.filter(
          (asset) => includeExisting || asset.modificationTime > baselineMs,
        );

        // Chunked ledger writes keep every transaction bounded — never one
        // giant rewrite of the whole queue per tiny change.
        for (let i = 0; i < eligible.length; i += this.config.discoveryBatchSize) {
          const chunk = eligible.slice(i, i + this.config.discoveryBatchSize);
          addedCount += this.deps.ledger.upsertDiscovered(
            chunk.map((asset) => ({
              assetId: asset.id,
              revision: asset.modificationTime,
              filename: asset.filename,
              isVideo: asset.mediaType === 'video',
              operationKey: buildOperationKey(accountId, asset.id, asset.modificationTime),
            })),
            nowMs,
          );
        }
        this.scheduleEmit();

        cursor = page.endCursor;
        if (!page.hasNextPage) break;
      }
      return { pagesFetched, addedCount };
    } finally {
      this.scanning = false;
      this.emit();
      // Newly discovered items may be immediately runnable.
      this.kick();
    }
  }

  // ── upload execution ────────────────────────────────────────────────────

  private async uploadItem(item: SyncLedgerRow): Promise<void> {
    const identity = this.deps.identity();
    if (identity === null) {
      this.deps.ledger.releaseToPending(item.assetId, this.now());
      return;
    }
    const controller = new AbortController();
    const entry: InflightEntry = {
      controller,
      accountId: identity.accountId,
      generation: identity.generation,
    };
    this.inflight.set(item.assetId, entry);
    this.scheduleEmit();

    try {
      // The OS may have dropped the asset (deleted photo, revoked limited
      // access). That is NOT a failure — record it as skipped and move on.
      const local = await this.deps.mediaLibrary.getLocalInfo(item.assetId);
      if (!local) {
        this.deps.ledger.markSkipped(item.assetId, this.now());
        return;
      }

      const file = await this.deps.uploader({
        localUri: local.uri,
        filename: item.filename ?? item.assetId,
        mimeType: mimeFromFilename(item.filename),
        operationKey: item.operationKey,
        timeoutMs: this.config.uploadTimeoutMs,
        signal: controller.signal,
      });

      // STALE-COMPLETION GUARD: between claim and completion the user may
      // have logged out or switched accounts. Only a completion whose owner
      // AND session generation are both still current may touch state.
      if (!this.identityIsCurrent(entry.accountId, entry.generation)) {
        return;
      }
      this.deps.ledger.markCompleted(item.assetId, file.id, this.now());
      this.deps.ledger.setLastSyncAt(this.now());
    } catch (err) {
      this.handleUploadFailure(item, entry, err);
    } finally {
      this.inflight.delete(item.assetId);
      this.scheduleEmit();
      this.kick();
    }
  }

  /**
   * Centralized retry classification (see syncPolicy for the taxonomy).
   * One bad item never stops unrelated queue items: everything here touches
   * exactly its own row.
   */
  private handleUploadFailure(
    item: SyncLedgerRow,
    entry: InflightEntry,
    err: unknown,
  ): void {
    const nowMs = this.now();

    // Deliberate cancellation by OUR OWN controls (pause/disable/logout):
    // no penalty, straight back to pending.
    if (entry.controller.signal.aborted) {
      this.deps.ledger.releaseToPending(item.assetId, nowMs);
      return;
    }

    const status = (err as { status?: number } | null)?.status;
    if (typeof status === 'number') {
      const verdict = classifyHttpFailure(status);
      if (verdict.cls === 'auth') {
        // Session died mid-flight. Auth recovery owns this; park the item so
        // a post-relogin attach finds it immediately. Never spin on 401.
        this.authRequired = true;
        this.deps.ledger.releaseToPending(item.assetId, nowMs);
        this.emit();
        return;
      }
      if (verdict.cls === 'permanent-status') {
        this.deps.ledger.markPermanentFailure(item.assetId, nowMs);
        return;
      }
      // retryable-status → bounded backoff below; 429/503 honor Retry-After.
      const retryAfterAtMs =
        status === 429 || status === 503
          ? ((err as { retryAfterAtMs?: number | null }).retryAfterAtMs ?? null)
          : null;
      this.scheduleRetry(item, retryAfterAtMs, nowMs);
      return;
    }

    // No status: transport-level failure (network drop / timeout). Both mean
    // "try again later" — the server may or may not have committed, which is
    // EXACTLY what the idempotency key exists for.
    this.scheduleRetry(item, null, nowMs);
  }

  private scheduleRetry(
    item: SyncLedgerRow,
    explicitDeadlineMs: number | null,
    nowMs: number,
  ): void {
    const nextAttemptNumber = item.attempts + 1;
    if (nextAttemptNumber >= this.config.maxAttemptsPerItem && explicitDeadlineMs === null) {
      // Budget exhausted with no server-provided guidance: stop spinning.
      this.deps.ledger.markPermanentFailure(item.assetId, nowMs);
      return;
    }
    const backoff = backoffDelayMs(
      nextAttemptNumber,
      { baseMs: this.config.retryBaseDelayMs, maxMs: this.config.retryMaxDelayMs },
      this.deps.random ?? Math.random,
    );
    const computedAt = nowMs + backoff;
    const deadline =
      explicitDeadlineMs !== null ? Math.max(explicitDeadlineMs, computedAt) : computedAt;
    this.deps.ledger.markRetryableFailure(item.assetId, deadline, nowMs);
  }
}





