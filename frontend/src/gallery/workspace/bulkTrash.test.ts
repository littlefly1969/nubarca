import { describe, expect, it, vi } from 'vitest';
import { moveFilesToTrash } from './bulkTrash';

describe('moveFilesToTrash', () => {
  it('reports moved and failed independently (one failure never aborts the batch)', async () => {
    const deleteOne = vi.fn(async (id: string) => {
      if (id === 'b') throw Object.assign(new Error('boom'), { status: 500 });
    });
    const result = await moveFilesToTrash(['a', 'b', 'c'], { concurrency: 2, deleteOne });
    expect(result.moved.sort()).toEqual(['a', 'c']);
    expect(result.failed).toEqual(['b']);
    expect(deleteOne).toHaveBeenCalledTimes(3);
  });

  it('respects bounded concurrency (never more than N in flight)', async () => {
    let inFlight = 0;
    let peak = 0;
    const deleteOne = vi.fn(async () => {
      inFlight += 1;
      peak = Math.max(peak, inFlight);
      await new Promise((r) => setTimeout(r, 5));
      inFlight -= 1;
    });
    await moveFilesToTrash(['a', 'b', 'c', 'd', 'e'], { concurrency: 2, deleteOne });
    expect(peak).toBeLessThanOrEqual(2);
  });

  it('stops issuing deletes and marks the rest failed on a 401', async () => {
    const onAuthError = vi.fn();
    const deleteOne = vi.fn(async (id: string) => {
      if (id === 'a') throw Object.assign(new Error('unauth'), { status: 401 });
    });
    const result = await moveFilesToTrash(['a', 'b', 'c'], { concurrency: 1, deleteOne, onAuthError });
    expect(onAuthError).toHaveBeenCalled();
    expect(result.moved).toEqual([]);
    // 'a' failed with 401; 'b' and 'c' never issued → all counted as failed.
    expect(result.failed.sort()).toEqual(['a', 'b', 'c']);
    expect(deleteOne).toHaveBeenCalledTimes(1);
  });

  it('handles the empty case', async () => {
    const deleteOne = vi.fn();
    const result = await moveFilesToTrash([], { deleteOne });
    expect(result).toEqual({ moved: [], failed: [] });
    expect(deleteOne).not.toHaveBeenCalled();
  });
});
