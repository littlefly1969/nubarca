import { describe, expect, it, vi } from 'vitest';
import { UploadManager, type UploadSnapshot } from './uploadManager';
import { inMemoryUploadMemory } from './uploadMemory';
import { TransportError, type TransportRequest, type TransportResponse } from './transport';
import type { UploadTarget } from './targets';

const ROOT: UploadTarget = { kind: 'folder', folderId: null, label: 'Home' };
const TRIPS: UploadTarget = { kind: 'folder', folderId: 'f-1', label: 'Trips' };

type Handler = (request: TransportRequest) => TransportResponse | Promise<TransportResponse>;

function ok(body: unknown, status = 201): TransportResponse {
  return { status, body, retryAfterMs: null };
}

function fakeTransport(handler: Handler) {
  const calls: TransportRequest[] = [];
  const transport = vi.fn(async (request: TransportRequest) => {
    calls.push(request);
    if (request.signal?.aborted) throw new TransportError('aborted');
    return handler(request);
  });
  return { transport, calls };
}

function file(name: string, size: number, type = 'image/jpeg'): File {
  return new File([new Uint8Array(size)], name, { type, lastModified: 1_700_000_000_000 });
}

function manager(handler: Handler, extra: Partial<ConstructorParameters<typeof UploadManager>[0]> = {}) {
  const { transport, calls } = fakeTransport(handler);
  const memory = inMemoryUploadMemory();
  const engine = new UploadManager({
    transport,
    memory,
    retryBaseDelayMs: 1,
    notifyIntervalMs: 0,
    random: () => 0,
    isOnline: () => true,
    ...extra,
  });
  return { engine, calls, memory, transport };
}

async function until(engine: UploadManager, predicate: (s: UploadSnapshot) => boolean, ms = 2000) {
  const start = Date.now();
  while (!predicate(engine.getSnapshot())) {
    if (Date.now() - start > ms) throw new Error(`timed out: ${JSON.stringify(engine.getSnapshot().items.map((i) => [i.name, i.phase, i.failure]))}`);
    await new Promise((r) => setTimeout(r, 5));
  }
}

const allFinished = (s: UploadSnapshot) => s.items.length > 0 && s.items.every((i) => ['done', 'failed', 'cancelled'].includes(i.phase));

describe('UploadManager — single request', () => {
  it('posts each file to its folder with rename policy, relative path and an operation key', async () => {
    const { engine, calls } = manager((req) => {
      const form = req.body as FormData;
      return ok({ id: `id-${(form.get('file') as File).name}`, name: (form.get('file') as File).name });
    });
    const done: string[] = [];
    engine.onCompleted((item) => done.push(item.resultFileId!));

    engine.enqueue([{ file: file('a.jpg', 10) }, { file: file('b.jpg', 10), relativePath: 'Trip/b.jpg' }], TRIPS);
    await until(engine, allFinished);

    expect(engine.getSnapshot().items.map((i) => i.phase)).toEqual(['done', 'done']);
    expect(done.sort()).toEqual(['id-a.jpg', 'id-b.jpg']);
    const posts = calls.filter((c) => c.method === 'POST');
    expect(posts.every((c) => c.url === '/api/folders/f-1/files')).toBe(true);
    const second = posts.find((c) => ((c.body as FormData).get('file') as File).name === 'b.jpg')!;
    expect((second.body as FormData).get('relativePath')).toBe('Trip/b.jpg');
    expect((second.body as FormData).get('conflict')).toBe('rename');
    expect(second.headers?.['Idempotency-Key']).toMatch(/^web-/);
  });

  it('sends root uploads to /api/files', async () => {
    const { engine, calls } = manager(() => ok({ id: 'x', name: 'a.jpg' }));
    engine.enqueue([{ file: file('a.jpg', 5) }], ROOT);
    await until(engine, allFinished);
    expect(calls[0].url).toBe('/api/files');
  });

  it('retries transient failures with the SAME operation key and then succeeds', async () => {
    let n = 0;
    const { engine, calls } = manager(() => {
      n++;
      if (n === 1) throw new TransportError('network');
      if (n === 2) return ok({ error: 'busy' }, 503);
      if (n === 3) return ok({ code: 'upload_in_progress' }, 409);
      return ok({ id: 'f', name: 'a.jpg' });
    });
    engine.enqueue([{ file: file('a.jpg', 5) }], ROOT);
    // "upload_in_progress" waits the server's fixed 2 s before asking again.
    await until(engine, allFinished, 6000);

    expect(engine.getSnapshot().items[0].phase).toBe('done');
    const keys = new Set(calls.map((c) => c.headers?.['Idempotency-Key']));
    expect(keys.size).toBe(1);
    expect(calls).toHaveLength(4);
  }, 10_000);

  it('gives up after the attempt budget with a network failure', async () => {
    const { engine, calls } = manager(() => { throw new TransportError('network'); }, { maxAttempts: 3 });
    engine.enqueue([{ file: file('a.jpg', 5) }], ROOT);
    await until(engine, allFinished);
    expect(engine.getSnapshot().items[0]).toMatchObject({ phase: 'failed', failure: 'network' });
    expect(calls).toHaveLength(3);
  });

  it('does not retry a refusal and reports its reason', async () => {
    const { engine, calls } = manager(() => ok({ error: 'Upload would exceed your storage quota.', code: 'quota_exceeded' }, 413));
    engine.enqueue([{ file: file('a.jpg', 5) }], ROOT);
    await until(engine, allFinished);
    expect(engine.getSnapshot().items[0]).toMatchObject({ phase: 'failed', failure: 'quota' });
    expect(calls).toHaveLength(1);
  });

  it('stops every file when the session ends and says so once', async () => {
    const onAuthLost = vi.fn();
    const { engine } = manager(() => ok(null, 401), { concurrency: 1, onAuthLost });
    engine.enqueue([{ file: file('a.jpg', 5) }, { file: file('b.jpg', 5) }, { file: file('c.jpg', 5) }], ROOT);
    await until(engine, allFinished);
    expect(engine.getSnapshot().items.every((i) => i.failure === 'auth')).toBe(true);
    expect(onAuthLost).toHaveBeenCalledTimes(1);
  });

  it('waits offline without spending attempts', async () => {
    let online = false;
    let release: () => void = () => {};
    let n = 0;
    const { engine } = manager(() => {
      n++;
      if (n === 1) throw new TransportError('network');
      return ok({ id: 'f', name: 'a.jpg' });
    }, {
      maxAttempts: 1,
      isOnline: () => online,
      waitForOnline: () => new Promise<void>((resolve) => { release = () => { online = true; resolve(); }; }),
    });
    engine.enqueue([{ file: file('a.jpg', 5) }], ROOT);
    await until(engine, (s) => s.items[0]?.phase === 'waiting');
    release();
    await until(engine, allFinished);
    expect(engine.getSnapshot().items[0].phase).toBe('done');
  });

  it('cancels a queued file, retries a failed one, and clears finished rows', async () => {
    let fail = true;
    const { engine } = manager(() => (fail ? ok({ error: 'nope' }, 400) : ok({ id: 'f', name: 'a.jpg' })), { concurrency: 1 });
    engine.pause();
    engine.enqueue([{ file: file('a.jpg', 5) }, { file: file('b.jpg', 5) }], ROOT);
    const [a, b] = engine.getSnapshot().items;
    engine.cancel(b.id);
    engine.resume();
    await until(engine, allFinished);
    expect(engine.getSnapshot().items.map((i) => i.phase)).toEqual(['failed', 'cancelled']);
    expect(engine.getSnapshot().items[0].failure).toBe('rejected');
    expect(engine.getSnapshot().items[0].detail).toBe('nope');

    fail = false;
    engine.retry(a.id);
    await until(engine, (s) => s.items[0].phase === 'done');
    engine.clearFinished();
    expect(engine.getSnapshot().items).toHaveLength(0);
  });
});

describe('UploadManager — resumable', () => {
  const CHUNK = 4;

  function resumableServer(options: { failChunkOnce?: number; lostOnFirstGet?: boolean } = {}) {
    const uploads = new Map<string, { size: number; chunks: Set<number> }>();
    let seq = 0;
    let failedOnce = false;
    const state = (id: string) => {
      const u = uploads.get(id)!;
      return {
        uploadId: id, status: 'uploading', sizeBytes: u.size, chunkSizeBytes: CHUNK,
        expectedChunkCount: Math.ceil(u.size / CHUNK), receivedChunks: [...u.chunks].sort(), expiresAt: '',
      };
    };
    const handler: Handler = (req) => {
      if (req.url === '/api/uploads/resumable/config') {
        return ok({ enabled: true, chunkSizeBytes: CHUNK, maxFileBytes: 1_000_000 }, 200);
      }
      if (req.method === 'POST' && req.url === '/api/uploads/resumable') {
        const body = JSON.parse(req.body as string) as { sizeBytes: number };
        const id = `u${++seq}`;
        uploads.set(id, { size: body.sizeBytes, chunks: new Set() });
        return ok(state(id));
      }
      const chunk = /\/api\/uploads\/resumable\/(u\d+)\/chunks\/(\d+)$/.exec(req.url);
      if (chunk) {
        const index = Number(chunk[2]);
        if (index === options.failChunkOnce && !failedOnce) {
          failedOnce = true;
          throw new TransportError('network');
        }
        uploads.get(chunk[1])!.chunks.add(index);
        req.onProgress?.((req.body as Blob).size);
        return ok({}, 200);
      }
      const complete = /\/api\/uploads\/resumable\/(u\d+)\/complete$/.exec(req.url);
      if (complete) {
        const u = state(complete[1]);
        if (u.receivedChunks.length < u.expectedChunkCount) return ok({ code: 'upload_incomplete' }, 409);
        return ok({ id: `file-${complete[1]}`, name: 'big.mov' });
      }
      const get = /\/api\/uploads\/resumable\/(u\d+)$/.exec(req.url);
      if (get && req.method === 'GET') {
        if (options.lostOnFirstGet || !uploads.has(get[1])) return ok(null, 404);
        return ok(state(get[1]), 200);
      }
      if (req.method === 'DELETE') return ok(null, 204);
      throw new Error(`unexpected ${req.method} ${req.url}`);
    };
    return { handler, uploads };
  }

  it('sends a large file in chunks, retries a failed chunk, and completes into the target', async () => {
    const server = resumableServer({ failChunkOnce: 1 });
    const { engine, calls } = manager(server.handler, { resumableThresholdBytes: 8 });
    engine.enqueue([{ file: file('big.mov', 10, 'video/quicktime'), relativePath: 'Clips/big.mov' }], TRIPS);
    await until(engine, allFinished);

    const item = engine.getSnapshot().items[0];
    expect(item).toMatchObject({ phase: 'done', resumable: true, resultFileId: 'file-u1', sentBytes: 10 });
    const chunkPuts = calls.filter((c) => c.method === 'PUT');
    expect(chunkPuts.map((c) => (c.body as Blob).size).sort()).toEqual([2, 4, 4, 4]);
    const complete = calls.find((c) => c.url.endsWith('/complete'))!;
    expect(JSON.parse(complete.body as string)).toEqual({ parentFolderId: 'f-1', conflict: 'rename', relativePath: 'Clips/big.mov' });
  });

  it('resumes a remembered upload from the chunks the server already holds', async () => {
    const server = resumableServer();
    const memory = inMemoryUploadMemory();
    const big = file('big.mov', 12, 'video/quicktime');
    const common = { memory, resumableThresholdBytes: 8, chunkConcurrency: 1, retryBaseDelayMs: 1, notifyIntervalMs: 0, isOnline: () => true };

    // The first page: the second chunk hangs until the person pauses.
    let puts = 0;
    const first = new UploadManager({
      ...common,
      transport: async (req) => {
        if (req.method === 'PUT' && ++puts === 2) {
          await new Promise((_, reject) => req.signal!.addEventListener('abort', () => reject(new TransportError('aborted'))));
        }
        return server.handler(req);
      },
    });
    first.enqueue([{ file: big }], TRIPS);
    await until(first, () => puts >= 2);
    first.pause();
    await until(first, (s) => s.items[0]?.phase === 'paused');
    first.dispose();
    const sentBefore = [...server.uploads.get('u1')!.chunks];
    expect(sentBefore).toEqual([0]);

    // A reload: a fresh engine, the same file picked again for the same place.
    const seen: TransportRequest[] = [];
    const second = new UploadManager({
      ...common,
      transport: async (req) => { seen.push(req); return server.handler(req); },
    });
    second.enqueue([{ file: big }], TRIPS);
    await until(second, allFinished);

    expect(second.getSnapshot().items[0]).toMatchObject({ phase: 'done', resultFileId: 'file-u1' });
    const resent = seen.filter((c) => c.method === 'PUT').map((c) => Number(c.url.split('/').pop()));
    expect(resent.sort()).toEqual([1, 2]);
    expect(seen.some((c) => c.method === 'POST' && c.url === '/api/uploads/resumable')).toBe(false);
  });

  it('starts over when the server no longer knows the remembered upload', async () => {
    const server = resumableServer({ lostOnFirstGet: true });
    const { engine, memory } = manager(server.handler, { resumableThresholdBytes: 8 });
    const big = file('big.mov', 9, 'video/quicktime');
    const key = ['folder:f-1', 'big.mov', 9, big.lastModified].join('\u0000');
    await memory.set(key, { operationKey: 'web-old', resumableId: 'u999' });
    engine.enqueue([{ file: big }], TRIPS);
    await until(engine, allFinished);
    expect(engine.getSnapshot().items[0].phase).toBe('done');
    // Finished uploads leave nothing behind.
    expect(await memory.get(key)).toBeNull();
  });

  it('refuses a file above the server limit without sending it', async () => {
    const { engine, calls } = manager((req) => {
      if (req.url === '/api/uploads/resumable/config') return ok({ enabled: true, chunkSizeBytes: 4, maxFileBytes: 9 }, 200);
      throw new Error('should not send');
    }, { resumableThresholdBytes: 8 });
    engine.enqueue([{ file: file('huge.mov', 20) }], ROOT);
    await until(engine, allFinished);
    expect(engine.getSnapshot().items[0]).toMatchObject({ phase: 'failed', failure: 'too_large' });
    expect(calls).toHaveLength(1);
  });

  it('falls back to one request when the server does not offer resumable uploads', async () => {
    const { engine, calls } = manager((req) => {
      if (req.url === '/api/uploads/resumable/config') return ok({ enabled: false, chunkSizeBytes: 0, maxFileBytes: 0 }, 200);
      return ok({ id: 'f', name: 'big.mov' });
    }, { resumableThresholdBytes: 8 });
    engine.enqueue([{ file: file('big.mov', 20) }], ROOT);
    await until(engine, allFinished);
    expect(engine.getSnapshot().items[0]).toMatchObject({ phase: 'done', resumable: false });
    expect(calls.map((c) => c.url)).toEqual(['/api/uploads/resumable/config', '/api/files']);
  });
});

describe('UploadManager — album destination', () => {
  const ALBUM: UploadTarget = { kind: 'album', albumId: 'alb-1', label: 'Estate' };

  it('posts photos to the album route without folder fields', async () => {
    const { engine, calls } = manager(() => ok({ id: 'f', name: 'a.jpg' }));
    engine.enqueue([{ file: file('a.jpg', 5) }], ALBUM);
    await until(engine, allFinished);
    expect(engine.getSnapshot().items[0].phase).toBe('done');
    expect(calls[0].url).toBe('/api/albums/alb-1/uploads');
    const form = calls[0].body as FormData;
    expect(form.get('conflict')).toBeNull();
    expect(form.get('relativePath')).toBeNull();
  });

  it('refuses what is not a photo or video without sending a byte', async () => {
    const { engine, calls } = manager(() => ok({ id: 'f', name: 'x' }));
    engine.enqueue([{ file: file('notes.txt', 5, 'text/plain') }, { file: file('IMG_1.HEIC', 5, '') }], ALBUM);
    await until(engine, allFinished);
    const [notes, heic] = engine.getSnapshot().items;
    expect(notes).toMatchObject({ phase: 'failed', failure: 'not_media' });
    expect(heic.phase).toBe('done');
    expect(calls).toHaveLength(1);
  });

  it('says plainly when the album may not be added to', async () => {
    const { engine } = manager(() => ok({}, 403));
    engine.enqueue([{ file: file('a.jpg', 5) }], ALBUM);
    await until(engine, allFinished);
    expect(engine.getSnapshot().items[0]).toMatchObject({ phase: 'failed', failure: 'not_permitted' });
  });

  it('completes a large album upload with the album, not a folder', async () => {
    const config = { enabled: true, chunkSizeBytes: 4, maxFileBytes: 1000 };
    const { engine, calls } = manager((req) => {
      if (req.url === '/api/uploads/resumable/config') return ok(config, 200);
      if (req.method === 'POST' && req.url === '/api/uploads/resumable') {
        return ok({ uploadId: 'up-1', status: 'uploading', sizeBytes: 10, chunkSizeBytes: 4, expectedChunkCount: 3, receivedChunks: [], expiresAt: '' });
      }
      if (req.method === 'PUT') return ok({}, 200);
      return ok({ id: 'f', name: 'clip.mp4' });
    }, { resumableThresholdBytes: 8 });
    engine.enqueue([{ file: file('clip.mp4', 10, 'video/mp4') }], ALBUM);
    await until(engine, allFinished);
    const complete = calls.find((c) => c.url === '/api/uploads/resumable/up-1/complete')!;
    expect(JSON.parse(complete.body as string)).toEqual({ albumId: 'alb-1' });
  });
});
