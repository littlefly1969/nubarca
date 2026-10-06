import { useEffect, useLayoutEffect, useRef, type RefObject } from 'react';
import type { MediaItem } from '@nubarca/api-client';
import type { computeJustifiedRows } from '../layout/computeJustifiedRows';
import { MEDIA_WALL_GAP_PX } from '../layout/mediaWallGeometry';

interface Options {
  items: MediaItem[];
  rows: ReturnType<typeof computeJustifiedRows>;
  measured: boolean;
  containerRef: RefObject<HTMLDivElement | null>;
  viewportRef: RefObject<HTMLElement | null> | null;
  target?: { id: string; revision: number } | null;
  onVisibleItem?(item: MediaItem): void;
}

// Keep the same visible row when earlier pages are prepended or width changes.
// An explicit jump overrides that anchor. Neither depends on DOM tile mounts:
// those are virtualized and may not exist yet at the destination.
export function useWallNavigation(options: Options) {
  const live = useRef(options);
  live.current = options;
  const anchor = useRef<{ id: string; offset: number } | null>(null);
  const lastTarget = useRef<string | null>(null);
  const previousRows = useRef(options.rows);

  function geometry() {
    const node = live.current.containerRef.current;
    const viewport = live.current.viewportRef?.current;
    const scroll = viewport?.scrollTop ?? window.scrollY;
    const origin = viewport?.getBoundingClientRect().top ?? 0;
    const margin = node ? node.getBoundingClientRect().top - origin + scroll : 0;
    const chrome = node?.closest('.ws-page')?.querySelector<HTMLElement>('.ws-sticky-chrome');
    const cover = chrome && getComputedStyle(chrome).position === 'sticky' ? chrome.getBoundingClientRect().height + 8 : 0;
    return { viewport, scroll, margin, cover };
  }

  function capture() {
    const { rows, items, measured, onVisibleItem } = live.current;
    if (!measured || rows.length === 0) return;
    const { scroll, margin, cover } = geometry();
    const relative = scroll + cover - margin;
    let start = 0;
    const row = rows.find((candidate) => {
      const end = start + candidate.height + MEDIA_WALL_GAP_PX;
      if (end > relative) return true;
      start = end; return false;
    }) ?? rows.at(-1)!;
    // Repacking an earlier page can put the destination beside the tail of
    // the preceding month. Keep announcing the chosen, still-visible tile
    // until its row leaves the viewport, including after rotation.
    const tile = row.items.find((candidate) => candidate.id === anchor.current?.id) ?? row.items[0];
    if (!tile) return;
    anchor.current = relative >= -1 ? { id: tile.id, offset: Math.max(0, relative - start) } : null;
    const item = items[tile.originalIndex];
    if (item) onVisibleItem?.(item);
  }

  useLayoutEffect(() => {
    if (!options.measured) return;
    const targetKey = options.target ? `${options.target.id}:${options.target.revision}` : null;
    if (targetKey === null) lastTarget.current = null;
    const jumping = targetKey !== null && targetKey !== lastTarget.current;
    const held = jumping ? { id: options.target!.id, offset: 0 } : anchor.current;
    if (held && (jumping || previousRows.current !== options.rows)) {
      let start = 0;
      const found = options.rows.some((row) => {
        if (row.items.some((tile) => tile.id === held.id)) return true;
        start += row.height + MEDIA_WALL_GAP_PX; return false;
      });
      if (found) {
        const { viewport, margin, cover } = geometry();
        const top = Math.max(0, Math.ceil(margin + start + held.offset - cover));
        if (viewport) viewport.scrollTop = top;
        else window.scrollTo({ top, behavior: 'instant' });
        anchor.current = held;
        lastTarget.current = targetKey;
      }
    }
    previousRows.current = options.rows;
    capture();
  }, [options.rows, options.measured, options.target]);

  useEffect(() => {
    const target = options.viewportRef?.current ?? window;
    let frame: number | null = null;
    const onScroll = () => {
      if (frame === null) frame = requestAnimationFrame(() => { frame = null; capture(); });
    };
    target.addEventListener('scroll', onScroll, { passive: true });
    return () => {
      target.removeEventListener('scroll', onScroll);
      if (frame !== null) cancelAnimationFrame(frame);
    };
  }, [options.viewportRef]);
}
