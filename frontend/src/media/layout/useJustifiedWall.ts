import { useCallback, useLayoutEffect, useMemo, useRef, useState, type RefObject } from 'react';
import { computeJustifiedRows, type JustifiedLayoutItem } from './computeJustifiedRows';
import { MEDIA_WALL_GAP_PX, mediaWallRowParams } from './mediaWallGeometry';

// The measure-then-justify half of a media wall, independent of who owns the
// scrolling and of what a tile contains.
//
// Extracted because NubArca now has more than one wall — the owner's library and
// albums, and a recipient's shared album — and they must agree on geometry
// exactly: every full row spans the container and every tile keeps its real
// aspect ratio, so mixed portrait and landscape media never leaves ragged rows.
// They deliberately do NOT share a tile: an owner's tile carries a display name,
// a selection control and semantic markers, none of which a recipient may be
// shown. Shared LAYOUT, separate presentation, is the line.
//
// No rows are produced until a real width is measured, so tiles never render at
// an invented size and then reflow.

export type JustifiedWallRows = ReturnType<typeof computeJustifiedRows>;

export interface JustifiedWall {
  // Attach this to the wall element. A callback ref, not a plain object one,
  // because a wall that only mounts once its items have arrived would otherwise
  // never be measured: a `[]`-dependency effect runs while the element does not
  // exist yet, and nothing tells it to look again.
  ref: (node: HTMLDivElement | null) => void;
  // The same element as a readable ref, for callers that measure against it.
  containerRef: RefObject<HTMLDivElement | null>;
  measured: boolean;
  width: number;
  rows: JustifiedWallRows;
}

export function useJustifiedWall(layoutItems: JustifiedLayoutItem[]): JustifiedWall {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const [node, setNode] = useState<HTMLDivElement | null>(null);
  const [containerWidth, setContainerWidth] = useState<number | null>(null);

  const ref = useCallback((next: HTMLDivElement | null) => {
    containerRef.current = next;
    setNode(next);
  }, []);

  useLayoutEffect(() => {
    if (!node) return;
    const measure = () => {
      const next = Math.round(node.getBoundingClientRect().width);
      // Only react to real (>= 1px) width changes; sub-pixel jitter must not
      // recompute the layout.
      if (next > 0) {
        setContainerWidth((prev) => (prev != null && Math.abs(prev - next) < 1 ? prev : next));
      }
    };
    measure();
    if (typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(measure);
    ro.observe(node);
    return () => ro.disconnect();
  }, [node]);

  const measured = containerWidth != null;
  const width = containerWidth ?? 0;
  const params = mediaWallRowParams(width || 1);

  const rows = useMemo(
    () => (measured
      ? computeJustifiedRows(layoutItems, {
        containerWidth: width,
        gap: MEDIA_WALL_GAP_PX,
        targetRowHeight: params.targetRowHeight,
        minRowHeight: params.minRowHeight,
        maxRowHeight: params.maxRowHeight,
      })
      : []),
    [measured, layoutItems, width, params.targetRowHeight, params.minRowHeight, params.maxRowHeight],
  );

  return { ref, containerRef, measured, width, rows };
}
