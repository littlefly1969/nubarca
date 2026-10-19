import { describe, expect, it, vi } from 'vitest';
import type { MediaItem } from '@nubarca/api-client';
import { MediaSelectionStore, type SelectionChange } from './mediaSelectionStore';

const item = (id: string, kind: 'image' | 'video' = 'image') => ({ id, kind, displayName: id, thumbnailUrl: `/t/${id}` }) as MediaItem;

describe('MediaSelectionStore', () => {
  it('keeps pick order, never duplicates an id and notifies once per real change', () => {
    const store = new MediaSelectionStore();
    const listener = vi.fn();
    store.subscribe(listener);
    store.add(['b', 'a', 'b']);
    store.add(['a']);
    expect(store.getSnapshot().ids).toEqual(['b', 'a']);
    expect(listener).toHaveBeenCalledTimes(1);
    expect(store.toggle('b')).toBe(false);
    expect(store.toggle('c', item('c'))).toBe(true);
    expect(store.getSnapshot().ids).toEqual(['a', 'c']);
    expect(store.getSnapshot().items.get('c')?.displayName).toBe('c');
  });

  it('remembers snapshots only for selected ids, so a pick survives leaving its wall', () => {
    const store = new MediaSelectionStore();
    store.add(['v']);
    store.remember([item('v', 'video'), item('x')]);
    expect(store.getSnapshot().items.get('v')?.kind).toBe('video');
    expect(store.getSnapshot().items.has('x')).toBe(false);
    const before = store.getSnapshot();
    store.remember([item('x')]);
    expect(store.getSnapshot()).toBe(before);
  });

  it('reports local changes to a mirror but never echoes adopted ones', () => {
    const store = new MediaSelectionStore();
    const changes: SelectionChange[] = [];
    store.onChange((c) => changes.push(c));
    store.add(['a', 'b']);
    store.remove(['a', 'zzz']);
    store.replace(['b', 'c']);
    store.clear();
    store.adopt(['s1', 's2'], [item('s1')]);
    expect(changes).toEqual([
      { kind: 'add', ids: ['a', 'b'] },
      { kind: 'remove', ids: ['a'] },
      { kind: 'add', ids: ['c'] },
      { kind: 'clear' },
    ]);
    expect(store.getSnapshot().ids).toEqual(['s1', 's2']);
    expect(store.getSnapshot().items.get('s1')).toBeDefined();
  });
});
