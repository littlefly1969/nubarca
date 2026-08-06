import { describe, expect, it } from 'vitest';
import { runUploadQueue, validateClientRelativePath, type ChunkTask } from './chunkUploader';

// Slice 93 — the chunk upload queue engine: bounded concurrency, bounded
// retry with backoff, cooperative pause/resume and cancellation.

function task(itemId: string, chunkIndex: number): ChunkTask {
  return { itemId, chunkIndex, sizeBytes: 4, getBlob: () => new Blob(['DATA']) };
}

const sleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

describe('runUploadQueue', () => {
  it('uploads every task and respects the concurrency bound', async () => {
    let inFlight = 0;
    let maxInFlight = 0;
    const uploaded: string[] = [];

    const result = await runUploadQueue({
      tasks: Array.from({ length: 8 }, (_, i) => task('item', i)),
      concurrency: 3,
      put: async (t) => {
        inFlight++;
        maxInFlight = Math.max(maxInFlight, inFlight);
        await sleep(10);
        uploaded.push(`${t.itemId}:${t.chunkIndex}`);
        inFlight--;
      },
    });

    expect(result.completed).toBe(8);
    expect(result.failed).toBe(0);
    expect(uploaded).toHaveLength(8);
    expect(maxInFlight).toBeLessThanOrEqual(3);
    expect(maxInFlight).toBeGreaterThan(1); // it actually parallelised
  });

  it('retries transient failures with backoff and succeeds', async () => {
    let attempts = 0;
    const result = await runUploadQueue({
      tasks: [task('item', 0)],
      retryBaseDelayMs: 1,
      put: async () => {
        attempts++;
        if (attempts < 3) throw new Error('transient');
      },
    });
    expect(attempts).toBe(3);
    expect(result.completed).toBe(1);
    expect(result.failed).toBe(0);
  });

  it('marks a task failed after the attempt budget and continues with others', async () => {
    const failedTasks: number[] = [];
    const result = await runUploadQueue({
      tasks: [task('item', 0), task('item', 1)],
      concurrency: 1,
      maxAttempts: 2,
      retryBaseDelayMs: 1,
      put: async (t) => {
        if (t.chunkIndex === 0) throw new Error('always fails');
      },
      onChunkFailed: (t) => failedTasks.push(t.chunkIndex),
    });
    expect(result.failed).toBe(1);
    expect(result.completed).toBe(1);
    expect(failedTasks).toEqual([0]);
  });

  it('pauses and resumes cooperatively', async () => {
    let paused = true;
    const uploaded: number[] = [];
    const run = runUploadQueue({
      tasks: [task('item', 0)],
      put: async (t) => { uploaded.push(t.chunkIndex); },
      isPaused: () => paused,
    });

    await sleep(60);
    expect(uploaded).toHaveLength(0); // nothing starts while paused

    paused = false;
    const result = await run;
    expect(uploaded).toEqual([0]);
    expect(result.completed).toBe(1);
  });

  it('stops promptly when cancelled', async () => {
    let cancelled = false;
    const uploaded: number[] = [];
    const result = await runUploadQueue({
      tasks: [task('item', 0), task('item', 1), task('item', 2)],
      concurrency: 1,
      put: async (t) => {
        uploaded.push(t.chunkIndex);
        cancelled = true; // cancel after the first chunk
      },
      isCancelled: () => cancelled,
    });
    expect(result.cancelled).toBe(true);
    expect(uploaded).toEqual([0]);
  });
});

describe('validateClientRelativePath', () => {
  it('accepts normal relative paths', () => {
    expect(validateClientRelativePath('photos/2024/img.jpg')).toBeNull();
    expect(validateClientRelativePath('file.txt')).toBeNull();
  });

  it('rejects traversal, absolute, drive-prefixed and malformed paths', () => {
    expect(validateClientRelativePath('../evil.txt')).not.toBeNull();
    expect(validateClientRelativePath('a/../b.txt')).not.toBeNull();
    expect(validateClientRelativePath('/etc/passwd')).not.toBeNull();
    expect(validateClientRelativePath('C:\\x.txt')).not.toBeNull();
    expect(validateClientRelativePath('a//b.txt')).not.toBeNull();
    expect(validateClientRelativePath('')).not.toBeNull();
  });
});
