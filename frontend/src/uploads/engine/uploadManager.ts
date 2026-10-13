import { backoffDelay, classifyResponse, type UploadFailure } from './classify';
import { accepts, endpointsFor, targetKey, type UploadTarget } from './targets';
import { TransportError, xhrTransport, type Transport, type TransportRequest, type TransportResponse } from './transport';
import { indexedDbUploadMemory, type UploadMemory } from './uploadMemory';

// THE UPLOAD ENGINE. Every owner upload in the web app goes through this one
// queue, whatever page started it and whatever page the person is on while it
// runs.
//
// What it guarantees, in the order it matters on a real connection:
//
//   * nothing is ingested twice. Each file carries one operation key for its
//     whole life (remembered across a reload), so a retry after a lost
//     response is a replay on the server, never a second copy;
//   * a failure costs one request, not one file. Connection drops, busy or
//     restarting servers and rate limits are retried with jittered backoff,
//     and an offline browser waits for the network instead of burning its
//     attempts;
//   * a large file costs one chunk, not the whole file. Above the resumable
//     threshold the file goes in chunks the server acknowledges one by one; a
//     pause, a dropped connection or a reload resumes from the last chunk the
//     server holds;
//   * a refused file says why, in one of a small set of reasons, and stops —
//     no retry loop on a file the server will never take;
//   * the session ending stops everything at once, with nothing half-sent
//     left looking like it might still finish.

export type UploadPhase =
  | 'queued'
  | 'uploading'
  // Bytes are all sent; the server is storing and checking them.
  | 'finishing'
  // Between attempts: backing off, or offline and waiting for the network.
  | 'waiting'
  | 'paused'
  | 'done'
  | 'failed'
  | 'cancelled';

export interface UploadItem {
  readonly id: string;
  readonly batchId: string;
  readonly file: File;
  readonly name: string;
  // "Holiday/2026/IMG_1.jpg" for a folder pick or drop; "" for a plain file.
  readonly relativePath: string;
  readonly size: number;
  readonly target: UploadTarget;
  readonly phase: UploadPhase;
  readonly sentBytes: number;
  readonly failure: UploadFailure | null;
  // The server's own explanation for a refusal, when it gave one.
  readonly detail: string | null;
  readonly resultFileId: string | null;
  // The name the file actually got (a taken name gains a counter).
  readonly resultName: string | null;
  readonly resumable: boolean;
}

export interface UploadSnapshot {
  readonly items: readonly UploadItem[];
  readonly paused: boolean;
}

export interface UploadEntry {
  file: File;
  relativePath?: string;
}

export interface ResumableConfig {
  enabled: boolean;
  chunkSizeBytes: number;
  maxFileBytes: number;
}

export interface UploadManagerOptions {
  transport?: Transport;
  memory?: UploadMemory;
  // Files uploaded in parallel. Each resumable file also sends chunks in parallel.
  concurrency?: number;
  chunkConcurrency?: number;
  // Files at or above this size go in resumable chunks (when the server offers it).
  resumableThresholdBytes?: number;
  // Attempts per request before a transient failure becomes the file's failure.
  maxAttempts?: number;
  retryBaseDelayMs?: number;
  // Progress-only updates are coalesced to at most one per interval.
  notifyIntervalMs?: number;
  onAuthLost?: () => void;
  random?: () => number;
  isOnline?: () => boolean;
  waitForOnline?: (signal: AbortSignal) => Promise<void>;
}

export const RESUMABLE_THRESHOLD_BYTES = 64 * 1024 * 1024;

interface Record_ {
  id: string;
  batchId: string;
  file: File;
  relativePath: string;
  target: UploadTarget;
  phase: UploadPhase;
  sentBytes: number;
  failure: UploadFailure | null;
  detail: string | null;
  resultFileId: string | null;
  resultName: string | null;
  resumable: boolean;
  resumableId: string | null;
  abort: AbortController | null;
  interrupt: 'pause' | 'cancel' | 'auth' | null;
  snapshot: UploadItem | null;
}

// An upload stopped for good, with its reason.
class UploadStop extends Error {
  constructor(readonly failure: UploadFailure, readonly detail: string | null, readonly code: string | null) {
    super(failure);
  }
}

// An upload stopped because somebody asked (pause, cancel) or the session ended.
class Interrupted extends Error {}

function newId(prefix: string): string {
  const uuid = typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
    ? crypto.randomUUID()
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}-${Math.random().toString(36).slice(2)}`;
  return `${prefix}-${uuid}`;
}

function relativePathOf(entry: UploadEntry): string {
  const explicit = entry.relativePath;
  if (explicit !== undefined) return explicit;
  const fromPicker = (entry.file as File & { webkitRelativePath?: string }).webkitRelativePath;
  return typeof fromPicker === 'string' ? fromPicker : '';
}

function defaultWaitForOnline(signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (typeof window === 'undefined' || navigator.onLine !== false) {
      resolve();
      return;
    }
    const done = () => {
      window.removeEventListener('online', done);
      signal.removeEventListener('abort', abort);
      resolve();
    };
    const abort = () => {
      window.removeEventListener('online', done);
      reject(new Interrupted());
    };
    window.addEventListener('online', done);
    signal.addEventListener('abort', abort, { once: true });
  });
}

const FINISHED: ReadonlySet<UploadPhase> = new Set(['done', 'failed', 'cancelled']);

export function isFinished(phase: UploadPhase): boolean {
  return FINISHED.has(phase);
}

export class UploadManager {
  private readonly transport: Transport;
  private readonly memory: UploadMemory;
  private readonly concurrency: number;
  private readonly chunkConcurrency: number;
  private readonly threshold: number;
  private readonly maxAttempts: number;
  private readonly baseDelay: number;
  private readonly notifyInterval: number;
  private readonly random: () => number;
  private readonly isOnline: () => boolean;
  private readonly waitOnline: (signal: AbortSignal) => Promise<void>;
  onAuthLost: (() => void) | undefined;

  private records: Record_[] = [];
  private running = 0;
  private paused = false;
  private halted = false;
  private snapshot: UploadSnapshot = { items: [], paused: false };
  private readonly listeners = new Set<() => void>();
  private readonly completionListeners = new Set<(item: UploadItem) => void>();
  private notifyTimer: ReturnType<typeof setTimeout> | null = null;
  private configPromise: Promise<ResumableConfig | null> | null = null;

  constructor(options: UploadManagerOptions = {}) {
    this.transport = options.transport ?? xhrTransport;
    this.memory = options.memory ?? indexedDbUploadMemory;
    this.concurrency = Math.max(1, options.concurrency ?? 3);
    this.chunkConcurrency = Math.max(1, options.chunkConcurrency ?? 3);
    this.threshold = options.resumableThresholdBytes ?? RESUMABLE_THRESHOLD_BYTES;
    this.maxAttempts = Math.max(1, options.maxAttempts ?? 6);
    this.baseDelay = options.retryBaseDelayMs ?? 1000;
    this.notifyInterval = options.notifyIntervalMs ?? 120;
    this.onAuthLost = options.onAuthLost;
    this.random = options.random ?? Math.random;
    this.isOnline = options.isOnline ?? (() => typeof navigator === 'undefined' || navigator.onLine !== false);
    this.waitOnline = options.waitForOnline ?? defaultWaitForOnline;
  }

  // ---- observation ---------------------------------------------------------

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  getSnapshot = (): UploadSnapshot => this.snapshot;

  /** Called once for every file that lands, with its final state. */
  onCompleted(listener: (item: UploadItem) => void): () => void {
    this.completionListeners.add(listener);
    return () => this.completionListeners.delete(listener);
  }

  // ---- commands ------------------------------------------------------------

  /** Queues files for one destination. Returns the batch id. */
  enqueue(entries: readonly UploadEntry[], target: UploadTarget): string {
    const batchId = newId('batch');
    if (entries.length === 0) return batchId;
    // A new pick after the session ended is a new start.
    this.halted = false;
    for (const entry of entries) {
      this.records.push({
        id: newId('upload'),
        batchId,
        file: entry.file,
        relativePath: relativePathOf(entry),
        target,
        phase: 'queued',
        sentBytes: 0,
        failure: null,
        detail: null,
        resultFileId: null,
        resultName: null,
        resumable: false,
        resumableId: null,
        abort: null,
        interrupt: null,
        snapshot: null,
      });
    }
    this.notify(true);
    this.pump();
    return batchId;
  }

  pause(): void {
    if (this.paused) return;
    this.paused = true;
    for (const record of this.records) {
      if (record.abort && !isFinished(record.phase)) {
        record.interrupt = 'pause';
        record.abort.abort();
      }
    }
    this.notify(true);
  }

  resume(): void {
    if (!this.paused) return;
    this.paused = false;
    for (const record of this.records) {
      if (record.phase === 'paused') record.phase = 'queued';
    }
    this.notify(true);
    this.pump();
  }

  cancel(id: string): void {
    const record = this.records.find((r) => r.id === id);
    if (!record || isFinished(record.phase)) return;
    this.cancelRecord(record);
    this.notify(true);
  }

  cancelAll(): void {
    for (const record of this.records) {
      if (!isFinished(record.phase)) this.cancelRecord(record);
    }
    this.notify(true);
  }

  retry(id: string): void {
    const record = this.records.find((r) => r.id === id);
    if (!record || (record.phase !== 'failed' && record.phase !== 'cancelled')) return;
    this.requeue(record);
    this.halted = false;
    this.notify(true);
    this.pump();
  }

  retryFailed(): void {
    let any = false;
    for (const record of this.records) {
      if (record.phase === 'failed') {
        this.requeue(record);
        any = true;
      }
    }
    if (!any) return;
    this.halted = false;
    this.notify(true);
    this.pump();
  }

  /** Forgets finished rows (done and cancelled); failures stay until retried or dismissed. */
  clearFinished(): void {
    this.records = this.records.filter((r) => r.phase !== 'done' && r.phase !== 'cancelled');
    this.notify(true);
  }

  /** Forgets one finished row. */
  dismiss(id: string): void {
    this.records = this.records.filter((r) => r.id !== id || !isFinished(r.phase));
    this.notify(true);
  }

  /** Stops everything; used when the page that owns the engine goes away. */
  dispose(): void {
    for (const record of this.records) {
      if (record.abort) {
        record.interrupt = 'cancel';
        record.abort.abort();
      }
    }
    if (this.notifyTimer !== null) clearTimeout(this.notifyTimer);
    this.listeners.clear();
    this.completionListeners.clear();
  }

  // ---- scheduling ----------------------------------------------------------

  private requeue(record: Record_) {
    record.phase = 'queued';
    record.failure = null;
    record.detail = null;
    record.sentBytes = 0;
    record.interrupt = null;
  }

  private cancelRecord(record: Record_) {
    if (record.abort) {
      record.interrupt = 'cancel';
      record.abort.abort();
    } else {
      record.phase = 'cancelled';
      void this.forget(record, true);
    }
  }

  private pump() {
    while (!this.paused && !this.halted && this.running < this.concurrency) {
      const next = this.records.find((r) => r.phase === 'queued');
      if (!next) return;
      this.running++;
      next.phase = 'uploading';
      next.interrupt = null;
      next.abort = new AbortController();
      this.notify(true);
      void this.run(next).finally(() => {
        this.running--;
        next.abort = null;
        this.notify(true);
        this.pump();
      });
    }
  }

  private async run(record: Record_): Promise<void> {
    try {
      const result = await this.upload(record);
      record.phase = 'done';
      record.resultFileId = result.id;
      record.resultName = result.name;
      record.sentBytes = record.file.size;
      await this.forget(record, false);
      const item = this.toItem(record);
      for (const listener of this.completionListeners) {
        try { listener(item); } catch { /* a listener never breaks the queue */ }
      }
    } catch (error) {
      if (error instanceof Interrupted || (error instanceof TransportError && error.kind === 'aborted')) {
        switch (record.interrupt) {
          case 'pause':
            record.phase = 'paused';
            break;
          case 'auth':
            record.phase = 'failed';
            record.failure = 'auth';
            break;
          default:
            record.phase = 'cancelled';
            await this.forget(record, true);
            break;
        }
      } else if (error instanceof UploadStop) {
        record.phase = 'failed';
        record.failure = error.failure;
        record.detail = error.detail;
        if (error.failure === 'auth') this.haltForAuth();
      } else {
        record.phase = 'failed';
        record.failure = 'unknown';
      }
    }
  }

  private haltForAuth() {
    if (this.halted) return;
    this.halted = true;
    for (const record of this.records) {
      if (record.phase === 'queued' || record.phase === 'paused') {
        record.phase = 'failed';
        record.failure = 'auth';
      } else if (record.abort && !isFinished(record.phase)) {
        record.interrupt = 'auth';
        record.abort.abort();
      }
    }
    this.notify(true);
    this.onAuthLost?.();
  }

  // The memory key: the same file, picked again for the same place.
  private memoryKey(record: Record_): string {
    const file = record.file;
    return [targetKey(record.target), record.relativePath || file.name, file.size, file.lastModified].join('\u0000');
  }

  private async forget(record: Record_, discardServerUpload: boolean): Promise<void> {
    try { await this.memory.delete(this.memoryKey(record)); } catch { /* fail soft */ }
    if (discardServerUpload && record.resumableId) {
      const id = record.resumableId;
      record.resumableId = null;
      // Best effort: the server's sweeper reclaims what this misses.
      this.transport({ method: 'DELETE', url: `/api/uploads/resumable/${encodeURIComponent(id)}` })
        .catch(() => { /* ignore */ });
    }
  }

  // ---- one file ------------------------------------------------------------

  private async upload(record: Record_): Promise<{ id: string; name: string }> {
    // Refused here, before a byte moves: a 2 GB document must not cross the
    // network only for the album to say no.
    if (!accepts(record.target, record.file)) throw new UploadStop('not_media', null, null);
    const key = this.memoryKey(record);
    let remembered = null;
    try { remembered = await this.memory.get(key); } catch { /* fail soft */ }
    const operationKey = remembered?.operationKey ?? newId('web');
    if (!remembered) {
      try { await this.memory.set(key, { operationKey }); } catch { /* fail soft */ }
    }

    const config = record.file.size >= this.threshold ? await this.resumableConfig() : null;
    if (config && config.maxFileBytes > 0 && record.file.size > config.maxFileBytes) {
      throw new UploadStop('too_large', null, null);
    }
    if (config?.enabled) {
      record.resumable = true;
      return this.uploadResumable(record, key, operationKey, remembered?.resumableId ?? null);
    }
    record.resumable = false;
    return this.uploadMultipart(record, operationKey);
  }

  private async uploadMultipart(record: Record_, operationKey: string): Promise<{ id: string; name: string }> {
    const endpoints = endpointsFor(record.target);
    const file = record.file;
    const response = await this.send(record, () => {
      record.sentBytes = 0;
      const form = new FormData();
      form.append('file', file, file.name);
      for (const [name, value] of Object.entries(endpoints.multipartFields(record.relativePath))) {
        form.append(name, value);
      }
      return {
        method: 'POST',
        url: endpoints.multipartUrl,
        body: form,
        headers: { 'Idempotency-Key': operationKey },
        onProgress: (loaded) => {
          // `loaded` counts the multipart envelope too; never show more than the file.
          record.sentBytes = Math.min(loaded, file.size);
          if (record.sentBytes >= file.size) record.phase = 'finishing';
          this.notify(false);
        },
      };
    });
    return this.fileSummary(response);
  }

  private async uploadResumable(
    record: Record_,
    memoryKey: string,
    operationKey: string,
    rememberedId: string | null,
  ): Promise<{ id: string; name: string }> {
    const endpoints = endpointsFor(record.target);
    const file = record.file;
    let uploadId = record.resumableId ?? rememberedId;

    // A few rounds: an upload that expired or lost its bytes on the server is
    // restarted, and a completion that finds a chunk missing sends it again.
    for (let round = 0; round < 3; round++) {
      let state: ResumableState | null = null;
      if (uploadId) {
        const existing = await this.send(record, () => ({
          method: 'GET', url: `/api/uploads/resumable/${encodeURIComponent(uploadId!)}`,
        }), [404]);
        const parsed = existing.status === 200 ? parseState(existing.body) : null;
        state = parsed && parsed.sizeBytes === file.size ? parsed : null;
        if (!state) uploadId = null;
      }
      if (!state) {
        const created = await this.send(record, () => ({
          method: 'POST',
          url: '/api/uploads/resumable',
          body: JSON.stringify({ fileName: file.name, sizeBytes: file.size, contentType: file.type || null }),
          headers: { 'content-type': 'application/json' },
        }));
        state = parseState(created.body);
        if (!state) throw new UploadStop('server', null, null);
        uploadId = state.uploadId;
        try { await this.memory.set(memoryKey, { operationKey, resumableId: uploadId }); } catch { /* fail soft */ }
      }
      record.resumableId = uploadId;

      const lost = await this.sendChunks(record, state);
      if (lost) {
        uploadId = null;
        record.resumableId = null;
        continue;
      }

      record.phase = 'finishing';
      this.notify(true);
      const completed = await this.send(record, () => ({
        method: 'POST',
        url: `/api/uploads/resumable/${encodeURIComponent(state!.uploadId)}/complete`,
        body: JSON.stringify(endpoints.completeBody(record.relativePath)),
        headers: { 'content-type': 'application/json' },
      }), [404, 409]);
      if (completed.status === 404) {
        uploadId = null;
        record.resumableId = null;
        continue;
      }
      if (completed.status === 409) {
        const code = codeOf(completed.body);
        if (code === 'upload_incomplete') continue;
        if (code === 'upload_lost') {
          uploadId = null;
          record.resumableId = null;
          continue;
        }
        const verdict = classifyResponse(completed);
        if (verdict.kind === 'fail') throw new UploadStop(verdict.failure, verdict.detail, verdict.code);
      }
      record.resumableId = null;
      return this.fileSummary(completed);
    }
    throw new UploadStop('server', null, null);
  }

  // Sends every chunk the server does not hold yet. True when the server lost
  // the upload meanwhile (expired or swept) and it must start over.
  private async sendChunks(record: Record_, state: ResumableState): Promise<boolean> {
    const file = record.file;
    const chunkSize = state.chunkSizeBytes;
    const received = new Set(state.receivedChunks);
    const lengthOf = (index: number) => Math.min(chunkSize, file.size - index * chunkSize);
    let confirmed = 0;
    for (const index of received) confirmed += lengthOf(index);
    const inFlight = new Map<number, number>();
    const refresh = () => {
      let moving = 0;
      for (const bytes of inFlight.values()) moving += bytes;
      record.sentBytes = Math.min(file.size, confirmed + moving);
      this.notify(false);
    };
    refresh();

    const missing: number[] = [];
    for (let index = 0; index < state.expectedChunkCount; index++) {
      if (!received.has(index)) missing.push(index);
    }
    let next = 0;
    let lost = false;
    // The first failure stops every worker from taking another chunk; it is
    // rethrown once the chunks already in flight have settled.
    let failure: unknown = null;
    const worker = async () => {
      while (!lost && failure === null) {
        const index = missing[next++];
        if (index === undefined) return;
        const start = index * chunkSize;
        let response: TransportResponse;
        try {
          response = await this.send(record, () => {
          inFlight.set(index, 0);
            return {
              method: 'PUT',
              url: `/api/uploads/resumable/${encodeURIComponent(state.uploadId)}/chunks/${index}`,
              body: file.slice(start, start + lengthOf(index)),
              headers: { 'content-type': 'application/octet-stream' },
              onProgress: (loaded) => {
                inFlight.set(index, Math.min(loaded, lengthOf(index)));
                refresh();
              },
            };
          }, [404, 409]);
        } catch (error) {
          inFlight.delete(index);
          failure ??= error;
          return;
        }
        inFlight.delete(index);
        if (response.status === 404 || response.status === 409) {
          // 409 on a chunk: the upload completed or lost its bytes elsewhere.
          lost = true;
          return;
        }
        confirmed += lengthOf(index);
        refresh();
      }
    };
    const workers = Array.from({ length: Math.min(this.chunkConcurrency, Math.max(1, missing.length)) }, worker);
    await Promise.all(workers);
    if (failure !== null) throw failure;
    return lost;
  }

  private fileSummary(response: TransportResponse): { id: string; name: string } {
    const body = response.body as { id?: unknown; name?: unknown } | null;
    if (!body || typeof body.id !== 'string' || typeof body.name !== 'string') {
      // A proxy page or a truncated body is not a confirmation.
      throw new UploadStop('server', null, null);
    }
    return { id: body.id, name: body.name };
  }

  private resumableConfig(): Promise<ResumableConfig | null> {
    if (!this.configPromise) {
      this.configPromise = this.transport({ method: 'GET', url: '/api/uploads/resumable/config' })
        .then((response) => {
          const body = response.body as Partial<ResumableConfig> | null;
          if (response.status !== 200 || !body || typeof body.enabled !== 'boolean') {
            this.configPromise = null;
            return null;
          }
          return {
            enabled: body.enabled,
            chunkSizeBytes: Number(body.chunkSizeBytes) || 0,
            maxFileBytes: Number(body.maxFileBytes) || 0,
          };
        })
        .catch(() => {
          // Unknown is "not offered" for this file; ask again next time.
          this.configPromise = null;
          return null;
        });
    }
    return this.configPromise;
  }

  // One request with the retry policy. Statuses in `passThrough` are returned
  // to the caller instead of being classified.
  private async send(
    record: Record_,
    build: () => TransportRequest,
    passThrough: readonly number[] = [],
  ): Promise<TransportResponse> {
    for (let attempt = 1; ; attempt++) {
      const signal = record.abort?.signal;
      if (!signal || signal.aborted) throw new Interrupted();

      let response: TransportResponse | null = null;
      let retryAfter: number | null = null;
      let failure: 'network' | 'server' = 'network';
      try {
        response = await this.transport({ ...build(), signal });
      } catch (error) {
        if (error instanceof TransportError && error.kind === 'aborted') throw new Interrupted();
        response = null;
      }

      if (response) {
        if (passThrough.includes(response.status)) return response;
        const verdict = classifyResponse(response);
        if (verdict.kind === 'ok') return response;
        if (verdict.kind === 'fail') throw new UploadStop(verdict.failure, verdict.detail, verdict.code);
        retryAfter = verdict.afterMs;
        failure = verdict.failure;
      }

      // Offline: wait for the network without spending an attempt.
      if (!this.isOnline()) {
        record.phase = 'waiting';
        this.notify(true);
        await this.waitOnline(signal);
        record.phase = 'uploading';
        this.notify(true);
        attempt--;
        continue;
      }
      if (attempt >= this.maxAttempts) throw new UploadStop(failure, null, null);

      record.phase = 'waiting';
      this.notify(true);
      await this.sleep(retryAfter ?? backoffDelay(attempt, this.baseDelay, this.random), signal);
      record.phase = 'uploading';
      this.notify(true);
    }
  }

  private sleep(ms: number, signal: AbortSignal): Promise<void> {
    return new Promise((resolve, reject) => {
      if (signal.aborted) {
        reject(new Interrupted());
        return;
      }
      const timer = setTimeout(() => {
        signal.removeEventListener('abort', onAbort);
        resolve();
      }, ms);
      const onAbort = () => {
        clearTimeout(timer);
        reject(new Interrupted());
      };
      signal.addEventListener('abort', onAbort, { once: true });
    });
  }

  // ---- snapshot ------------------------------------------------------------

  private toItem(record: Record_): UploadItem {
    const previous = record.snapshot;
    if (previous
      && previous.phase === record.phase
      && previous.sentBytes === record.sentBytes
      && previous.failure === record.failure
      && previous.detail === record.detail
      && previous.resultFileId === record.resultFileId
      && previous.resumable === record.resumable) {
      return previous;
    }
    const item: UploadItem = {
      id: record.id,
      batchId: record.batchId,
      file: record.file,
      name: record.file.name,
      relativePath: record.relativePath,
      size: record.file.size,
      target: record.target,
      phase: record.phase,
      sentBytes: record.sentBytes,
      failure: record.failure,
      detail: record.detail,
      resultFileId: record.resultFileId,
      resultName: record.resultName,
      resumable: record.resumable,
    };
    record.snapshot = item;
    return item;
  }

  private notify(immediate: boolean) {
    if (!immediate) {
      if (this.notifyTimer !== null) return;
      this.notifyTimer = setTimeout(() => {
        this.notifyTimer = null;
        this.publish();
      }, this.notifyInterval);
      return;
    }
    if (this.notifyTimer !== null) {
      clearTimeout(this.notifyTimer);
      this.notifyTimer = null;
    }
    this.publish();
  }

  private publish() {
    this.snapshot = { items: this.records.map((r) => this.toItem(r)), paused: this.paused };
    for (const listener of this.listeners) listener();
  }
}

interface ResumableState {
  uploadId: string;
  sizeBytes: number;
  chunkSizeBytes: number;
  expectedChunkCount: number;
  receivedChunks: number[];
}

function parseState(body: unknown): ResumableState | null {
  if (typeof body !== 'object' || body === null) return null;
  const b = body as Record<string, unknown>;
  if (typeof b.uploadId !== 'string' || typeof b.sizeBytes !== 'number'
    || typeof b.chunkSizeBytes !== 'number' || b.chunkSizeBytes <= 0
    || typeof b.expectedChunkCount !== 'number' || !Array.isArray(b.receivedChunks)) {
    return null;
  }
  return {
    uploadId: b.uploadId,
    sizeBytes: b.sizeBytes,
    chunkSizeBytes: b.chunkSizeBytes,
    expectedChunkCount: b.expectedChunkCount,
    receivedChunks: b.receivedChunks.filter((n): n is number => Number.isInteger(n)),
  };
}

function codeOf(body: unknown): string | null {
  if (typeof body !== 'object' || body === null) return null;
  const code = (body as Record<string, unknown>).code;
  return typeof code === 'string' ? code : null;
}
