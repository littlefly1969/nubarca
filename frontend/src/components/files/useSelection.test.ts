import { describe, expect, it } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import { useSelection } from './useSelection';
import type { Entry } from './types';
import type { FileSummary } from '@nubarca/api-client';

function fileEntry(id: string): Entry {
  return {
    kind: 'file',
    id,
    file: { id, name: id, mimeType: 'text/plain', sizeBytes: 1, createdAt: '2026-01-01T00:00:00Z' } as FileSummary,
  };
}

const entries: Entry[] = ['a', 'b', 'c', 'd'].map(fileEntry);

describe('useSelection', () => {
  it('toggles individual keys', () => {
    const { result } = renderHook(() => useSelection());
    act(() => result.current.toggle('file:a'));
    expect(result.current.count).toBe(1);
    expect(result.current.isSelected('file:a')).toBe(true);
    act(() => result.current.toggle('file:a'));
    expect(result.current.count).toBe(0);
  });

  it('range-selects from the anchor (shift)', () => {
    const { result } = renderHook(() => useSelection());
    act(() => result.current.toggle('file:a')); // anchor = a
    act(() => result.current.selectRange(entries, 'file:c'));
    expect([...result.current.selected].sort()).toEqual(['file:a', 'file:b', 'file:c']);
  });

  it('selectOnly replaces the selection and resets the anchor', () => {
    const { result } = renderHook(() => useSelection());
    act(() => result.current.toggle('file:a'));
    act(() => result.current.toggle('file:b'));
    act(() => result.current.selectOnly('file:d'));
    expect([...result.current.selected]).toEqual(['file:d']);
  });

  it('clear empties the selection', () => {
    const { result } = renderHook(() => useSelection());
    act(() => result.current.toggle('file:a'));
    act(() => result.current.clear());
    expect(result.current.count).toBe(0);
  });

  it('retainExisting drops keys no longer present', () => {
    const { result } = renderHook(() => useSelection());
    act(() => result.current.toggle('file:a'));
    act(() => result.current.toggle('file:b'));
    act(() => result.current.retainExisting([fileEntry('a')]));
    expect([...result.current.selected]).toEqual(['file:a']);
  });
});
