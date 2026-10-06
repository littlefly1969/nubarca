import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { uploadToAlbumShareWithProgress } from '@nubarca/api-client';
import { MockUploadXhr } from '../test-utils/MockUploadXhr';

const file = new File(['photo'], 'photo.jpg', { type: 'image/jpeg' });

beforeEach(() => {
  MockUploadXhr.sent = [];
  vi.stubGlobal('XMLHttpRequest', MockUploadXhr);
});
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

describe('album share upload confirmation', () => {
  it('sends one file with credentials, reports transfer, and waits for a valid response', async () => {
    const progress = vi.fn();
    const resolved = vi.fn();
    const promise = uploadToAlbumShareWithProgress('a/b', file, progress).then(resolved);
    const xhr = MockUploadXhr.sent[0];
    expect(xhr.url).toBe('/api/album-share/a%2Fb/upload');
    expect(xhr.withCredentials).toBe(true);
    expect(xhr.body?.get('file')).toMatchObject({ name: file.name, size: file.size, type: file.type });
    xhr.progress(50, 100);
    expect(progress).toHaveBeenLastCalledWith(0.5);
    xhr.upload.onload?.();
    expect(progress).toHaveBeenLastCalledWith(1);
    await Promise.resolve();
    expect(resolved).not.toHaveBeenCalled();
    xhr.finish();
    await promise;
    expect(resolved).toHaveBeenCalledWith({ accepted: 1, rejected: 0, stopped: null });
  });

  it.each(['<html>proxy</html>', '', '{}', 'null',
    '{"accepted":1}', '{"accepted":2,"rejected":0,"stopped":null}',
    '{"accepted":0,"rejected":0,"stopped":null}',
    '{"accepted":-1,"rejected":0,"stopped":null}',
  ])('never calls an invalid response a success: %s', async (body) => {
    const promise = uploadToAlbumShareWithProgress('token', file);
    const rejection = expect(promise).rejects.toThrow('invalid upload report');
    MockUploadXhr.sent[0].finish(body);
    await rejection;
  });

  it('accepts a ceiling report without claiming that the file arrived', async () => {
    const promise = uploadToAlbumShareWithProgress('token', file);
    const report = { accepted: 0, rejected: 0, stopped: 'upload_limit_reached' };
    MockUploadXhr.sent[0].finish(report);
    await expect(promise).resolves.toEqual(report);
  });

  it('preserves refusal status and code', async () => {
    const promise = uploadToAlbumShareWithProgress('token', file);
    const rejection = expect(promise).rejects.toMatchObject({ status: 409, code: 'uploads_disabled' });
    MockUploadXhr.sent[0].finish({ error: 'uploads_disabled' }, 409);
    await rejection;
  });

  it('does not start an already cancelled request', async () => {
    const controller = new AbortController();
    controller.abort();
    await expect(uploadToAlbumShareWithProgress('token', file, undefined, controller.signal))
      .rejects.toMatchObject({ name: 'AbortError' });
    expect(MockUploadXhr.sent).toHaveLength(0);
  });

  it('aborts an active request and removes its abort listener when settled', async () => {
    const controller = new AbortController();
    const remove = vi.spyOn(controller.signal, 'removeEventListener');
    const promise = uploadToAlbumShareWithProgress('token', file, undefined, controller.signal);
    const rejection = expect(promise).rejects.toMatchObject({ name: 'AbortError' });
    controller.abort();
    await rejection;
    expect(MockUploadXhr.sent[0].aborted).toBe(true);
    expect(remove).toHaveBeenCalledWith('abort', expect.any(Function));
  });

  it('allows a slow large upload while bytes move, then expires inactivity', async () => {
    vi.useFakeTimers();
    const promise = uploadToAlbumShareWithProgress('token', file);
    const rejection = expect(promise).rejects.toMatchObject({ name: 'TimeoutError' });
    const xhr = MockUploadXhr.sent[0];
    for (let i = 1; i <= 4; i++) {
      await vi.advanceTimersByTimeAsync(119_000);
      xhr.progress(i, 10);
      expect(xhr.aborted).toBe(false);
    }
    await vi.advanceTimersByTimeAsync(120_000);
    await rejection;
    expect(xhr.aborted).toBe(true);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('bounds server processing after transfer even without a computable length', async () => {
    vi.useFakeTimers();
    const promise = uploadToAlbumShareWithProgress('token', file);
    const rejection = expect(promise).rejects.toMatchObject({ name: 'TimeoutError' });
    const xhr = MockUploadXhr.sent[0];
    xhr.progress(0, 0, false);
    xhr.upload.onload?.();
    await vi.advanceTimersByTimeAsync(299_999);
    expect(xhr.aborted).toBe(false);
    await vi.advanceTimersByTimeAsync(1);
    await rejection;
    expect(xhr.aborted).toBe(true);
  });

  it('clears the watchdog and signal handler after a successful request', async () => {
    vi.useFakeTimers();
    const controller = new AbortController();
    const progress = vi.fn();
    const promise = uploadToAlbumShareWithProgress('token', file, progress, controller.signal);
    const xhr = MockUploadXhr.sent[0];
    xhr.finish();
    await promise;
    expect(vi.getTimerCount()).toBe(0);
    controller.abort();
    expect(xhr.aborted).toBe(false);
    xhr.progress(1, 2);
    expect(progress).not.toHaveBeenCalled();
  });
});
