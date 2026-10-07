import { act, cleanup, renderHook } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import type { MediaItem } from '@nubarca/api-client';
import { useWallNavigation } from './useWallNavigation';
import { MEDIA_WALL_GAP_PX } from '../layout/mediaWallGeometry';

afterEach(() => { cleanup(); vi.restoreAllMocks(); });
const items = (ids: string[]) => ids.map((id) => ({ id }) as MediaItem);
const rows = (ids: string[], height = 100) => ids.map((id, originalIndex) => ({
  key: id, height, width: 100, isLastRow: originalIndex === ids.length - 1,
  items: [{ id, originalIndex, width: 100, height }],
}));

function setup(ids: string[], scrollTop: number, margin = 100) {
  const viewport = document.createElement('main');
  const node = document.createElement('div');
  viewport.append(node);
  viewport.scrollTop = scrollTop;
  vi.spyOn(viewport, 'getBoundingClientRect').mockImplementation(() => ({ top: 0 } as DOMRect));
  vi.spyOn(node, 'getBoundingClientRect').mockImplementation(() => ({ top: margin - viewport.scrollTop } as DOMRect));
  const onVisibleItem = vi.fn();
  const initial = { items: items(ids), rows: rows(ids), measured: true,
    containerRef: { current: node }, viewportRef: { current: viewport }, onVisibleItem,
    target: null as { id: string; revision: number } | null };
  const hook = renderHook((props) => useWallNavigation(props), { initialProps: initial });
  return { ...hook, viewport, initial, onVisibleItem };
}

it('preserves the visible file and intra-row offset when earlier rows are prepended', () => {
  const { rerender, viewport, initial, onVisibleItem } = setup(['a', 'b', 'c'], 110);
  rerender({ ...initial, items: items(['earlier', 'a', 'b', 'c']), rows: rows(['earlier', 'a', 'b', 'c']) });
  expect(viewport.scrollTop).toBe(110 + 100 + MEDIA_WALL_GAP_PX);
  expect(onVisibleItem).toHaveBeenLastCalledWith(expect.objectContaining({ id: 'a' }));
});

it('an explicit jump goes directly to an unmounted row and is not repeated on append', () => {
  const { rerender, viewport, initial } = setup(['a', 'b', 'c'], 110);
  const target = { id: 'c', revision: 1 };
  rerender({ ...initial, target });
  const landed = 100 + 2 * (100 + MEDIA_WALL_GAP_PX);
  expect(viewport.scrollTop).toBe(landed);
  viewport.scrollTop = landed + 20;
  // Capture normal scrolling before a continuation changes the geometry.
  act(() => { viewport.dispatchEvent(new Event('scroll')); });
  rerender({ ...initial, target, items: items(['a', 'b', 'c', 'd']), rows: rows(['a', 'b', 'c', 'd']) });
  expect(viewport.scrollTop).toBeGreaterThanOrEqual(landed);
});

it('keeps the same visible file when rotation changes row heights', () => {
  const { rerender, viewport, initial, onVisibleItem } = setup(['a', 'b', 'c'], 100 + 100 + MEDIA_WALL_GAP_PX + 20);
  rerender({ ...initial, rows: rows(['a', 'b', 'c'], 150) });
  expect(viewport.scrollTop).toBe(100 + 150 + MEDIA_WALL_GAP_PX + 20);
  expect(onVisibleItem).toHaveBeenLastCalledWith(expect.objectContaining({ id: 'b' }));
});

it('does not drag a user away from the heading when appending while the wall is below view', () => {
  const { rerender, viewport, initial } = setup(['a', 'b'], 0);
  rerender({ ...initial, items: items(['a', 'b', 'c']), rows: rows(['a', 'b', 'c']) });
  expect(viewport.scrollTop).toBe(0);
});

it('accepts the subpixel rounding of scrollTop at a destination instead of losing its anchor', () => {
  const { rerender, viewport, initial, onVisibleItem } = setup(['destination', 'later'], 100, 100.75);
  rerender({ ...initial, items: items(['earlier', 'destination', 'later']), rows: rows(['earlier', 'destination', 'later']) });
  expect(viewport.scrollTop).toBe(Math.ceil(100.75 + 100 + MEDIA_WALL_GAP_PX));
  expect(onVisibleItem).toHaveBeenLastCalledWith(expect.objectContaining({ id: 'destination' }));
});

it('keeps the chosen month when a prepended page places its first photo beside the preceding month', () => {
  const { rerender, initial, onVisibleItem } = setup(['destination', 'later'], 100);
  const newRows = rows(['preceding', 'later']);
  newRows[0].items.push({ id: 'destination', originalIndex: 1, width: 100, height: 100 });
  newRows[1].items[0].originalIndex = 2;
  rerender({ ...initial, items: items(['preceding', 'destination', 'later']), rows: newRows });
  expect(onVisibleItem).toHaveBeenLastCalledWith(expect.objectContaining({ id: 'destination' }));
});
