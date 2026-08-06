// Slice 93 — browser-side chunk upload queue. Pure orchestration: slices are
// produced lazily by the caller (Blob.slice keeps memory flat — no file is
// ever fully read into memory here), uploads run with bounded concurrency,
// transient failures retry with bounded exponential backoff, and the whole
// queue can be paused/resumed/cancelled cooperatively.

export interface ChunkTask {
  itemId: string;
  chunkIndex: number;
  sizeBytes: number;
  // Lazy slice factory: called right before the PUT so a paused/queued task
  // holds no Blob reference until it actually uploads.
  getBlob: () => Blob;
}

export interface UploadQueueOptions {
  tasks: ChunkTask[];
  put: (task: ChunkTask, blob: Blob) => Promise<unknown>;
  // Conservative defaults for small servers: 3 parallel chunk requests.
  concurrency?: number;
  maxAttempts?: number;
  retryBaseDelayMs?: number;
  onChunkDone?: (task: ChunkTask) => void;
  onChunkFailed?: (task: ChunkTask) => void;
  isPaused?: () => boolean;
  isCancelled?: () => boolean;
}

export interface UploadQueueResult {
  completed: number;
  failed: number;
  cancelled: boolean;
}

const sleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

export async function runUploadQueue(options: UploadQueueOptions): Promise<UploadQueueResult> {
  const {
    tasks,
    put,
    concurrency = 3,
    maxAttempts = 3,
    retryBaseDelayMs = 500,
    onChunkDone,
    onChunkFailed,
    isPaused = () => false,
    isCancelled = () => false,
  } = options;

  let next = 0;
  let completed = 0;
  let failed = 0;
  let cancelled = false;

  async function worker(): Promise<void> {
    while (true) {
      if (isCancelled()) {
        cancelled = true;
        return;
      }
      while (isPaused()) {
        if (isCancelled()) {
          cancelled = true;
          return;
        }
        await sleep(150);
      }
      const index = next++;
      if (index >= tasks.length) return;
      const task = tasks[index];

      let done = false;
      for (let attempt = 1; attempt <= maxAttempts && !done; attempt++) {
        if (isCancelled()) {
          cancelled = true;
          return;
        }
        try {
          await put(task, task.getBlob());
          done = true;
        } catch {
          if (attempt < maxAttempts) {
            // Bounded exponential backoff for transient failures.
            await sleep(retryBaseDelayMs * 2 ** (attempt - 1));
          }
        }
      }
      if (done) {
        completed++;
        onChunkDone?.(task);
      } else {
        failed++;
        onChunkFailed?.(task);
      }
    }
  }

  const workers = Array.from(
    { length: Math.max(1, Math.min(concurrency, tasks.length)) },
    () => worker(),
  );
  await Promise.all(workers);
  return { completed, failed, cancelled };
}

// Client-side mirror of the server's manifest path policy, used for the
// preflight summary (the server re-validates everything).
export function validateClientRelativePath(path: string): string | null {
  if (path.length === 0) return 'empty path';
  if (path.length > 1024) return 'path too long';
  const normalized = path.replace(/\\/g, '/');
  if (normalized.startsWith('/')) return 'absolute path';
  if (normalized.includes(':')) return 'drive-prefixed path';
  for (const segment of normalized.split('/')) {
    if (segment.length === 0) return 'empty path segment';
    if (segment === '.' || segment === '..') return 'path traversal';
    if (segment.length > 255) return 'segment too long';
  }
  return null;
}
