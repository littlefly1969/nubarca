import { afterEach, expect, it, vi } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import { useMoveToPersonal } from './useMoveToPersonal';
import { installFetchMock, jsonResponse } from '../../test-utils';

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

it('open() captures an immutable snapshot and close() hides the dialog', () => {
  const { result } = renderHook(() =>
    useMoveToPersonal({ onFullSuccess: vi.fn(), onPartialSuccess: vi.fn() }));

  expect(result.current.isOpen).toBe(false);
  act(() => result.current.open(['a', 'b']));
  expect(result.current.isOpen).toBe(true);
  expect(result.current.ids).toEqual(['a', 'b']);

  act(() => result.current.close());
  expect(result.current.isOpen).toBe(false);
});

it('execute() sends fileIds + an explicit empty folderIds, and reports full success', async () => {
  const calls: unknown[] = [];
  installFetchMock({
    'POST /api/private-vault/move-in': (req) => {
      calls.push(JSON.parse(req.body ?? '{}'));
      return jsonResponse({ movedFiles: 2, movedFolders: 0 });
    },
  });
  const onFullSuccess = vi.fn();
  const onPartialSuccess = vi.fn();
  const { result } = renderHook(() => useMoveToPersonal({ onFullSuccess, onPartialSuccess }));

  act(() => result.current.open(['a', 'b']));
  await act(async () => {
    await result.current.execute('tok-1');
  });

  expect(calls).toEqual([{ fileIds: ['a', 'b'], folderIds: [] }]);
  expect(onFullSuccess).toHaveBeenCalledWith(['a', 'b']);
  expect(onPartialSuccess).not.toHaveBeenCalled();
});

it('reports a partial success when the moved count does not match the request', async () => {
  installFetchMock({
    'POST /api/private-vault/move-in': () => jsonResponse({ movedFiles: 1, movedFolders: 0 }),
  });
  const onFullSuccess = vi.fn();
  const onPartialSuccess = vi.fn();
  const { result } = renderHook(() => useMoveToPersonal({ onFullSuccess, onPartialSuccess }));

  act(() => result.current.open(['a', 'b']));
  await act(async () => {
    await result.current.execute('tok-1');
  });

  expect(onFullSuccess).not.toHaveBeenCalled();
  expect(onPartialSuccess).toHaveBeenCalledWith({ moved: 1, total: 2 });
});
