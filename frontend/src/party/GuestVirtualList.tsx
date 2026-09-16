import { useEffect, useRef, type ReactNode, type RefObject } from 'react';
import { useVirtualizer, useWindowVirtualizer } from '@tanstack/react-virtual';
import { guestDirectoryItemKey, type GuestDirectoryItem } from '@nubarca/api-client';
import { useAppScrollMargin, useAppScrollViewport } from '../components/appScroll';

// THE GUEST LIST, BOUNDED.
//
// The server already refuses to send more than a page at a time; this is the
// other half of the same promise — the browser keeps only what is on the screen.
// A host scrolling a party of a thousand groups accumulates a thousand pages of
// state in memory, but never a thousand cards in the DOM: the mounted set is
// what fits the viewport plus a small lead in each direction, whatever the
// scroll position, at any viewport size.
//
// It scrolls WITH THE PAGE. There is no inner scrollbar and no fixed height: the
// list reserves the height its items would occupy and positions the visible few
// inside it, so the shell's own scrolling is the only scrolling there is — the
// toolbar, the counts and the detail pane keep behaving exactly as they did.
//
// Heights are MEASURED, not assumed. A card of two people and a card of twenty,
// a card before the party and the same card at the door, are different heights,
// and an estimate is only the placeholder used before a card has been laid out
// once. That is why this uses @tanstack/react-virtual, which is already how the
// media wall and the album content list are bounded, rather than a private
// implementation of the same arithmetic.

/** Before a card has been measured. Close to a real one, so the bar barely moves. */
const CARD_ESTIMATE_PX = 168;

/** The list's own gap, added to each measurement rather than left to CSS. */
const CARD_GAP_PX = 10;

/**
 * Cards kept mounted beyond the viewport, in each direction. Six is a fast
 * flick's worth of lead — enough that scrolling lands on cards that are already
 * there, small enough that the mounted set stays far below the point where the
 * DOM itself becomes the slow part.
 */
const OVERSCAN_CARDS = 6;

/**
 * How close to the end the visible range comes before the next page is asked
 * for. Ten cards ahead is roughly one flick of warning, so the list keeps
 * moving instead of stopping at a spinner.
 */
const NEAR_END_CARDS = 10;

// What this list needs from a virtualizer, whichever scroll model produced it —
// the same structural shape the media wall uses, so neither surface has to name
// TanStack's generics.
interface ListVirtualizer {
  getTotalSize(): number;
  getVirtualItems(): ReadonlyArray<{ index: number; key: string | number | bigint; start: number }>;
  options: { scrollMargin: number };
  measureElement(node: Element | null): void;
  scrollToOffset(offset: number, options?: { align?: 'start' }): void;
}

export interface GuestVirtualListProps {
  items: GuestDirectoryItem[];
  /** The list's identity: a new search or filter makes a NEW list, from the top. */
  resetKey: string;
  hasMore: boolean;
  loadingMore: boolean;
  /** The visible range has come within sight of the end. */
  onNearEnd(): void;
  renderItem(item: GuestDirectoryItem): ReactNode;
}

export function GuestVirtualList(props: GuestVirtualListProps) {
  const viewportRef = useAppScrollViewport();
  return viewportRef
    ? <ViewportScrolledList {...props} viewportRef={viewportRef} />
    : <DocumentScrolledList {...props} />;
}

/** Inside the authenticated shell: `.app-main` is what scrolls. */
function ViewportScrolledList({
  viewportRef, ...props
}: GuestVirtualListProps & { viewportRef: RefObject<HTMLElement | null> }) {
  const containerRef = useRef<HTMLUListElement>(null);
  const scrollMargin = useAppScrollMargin(containerRef, viewportRef);
  const virtualizer = useVirtualizer({
    count: props.items.length,
    getScrollElement: () => viewportRef.current,
    estimateSize: () => CARD_ESTIMATE_PX + CARD_GAP_PX,
    measureElement: measureCard,
    getItemKey: (index) => keyOf(props.items, index),
    overscan: OVERSCAN_CARDS,
    scrollMargin,
  });
  return <Rows {...props} containerRef={containerRef} virtualizer={virtualizer} />;
}

/** No shell around it — the document scrolls (unit tests, any future surface). */
function DocumentScrolledList(props: GuestVirtualListProps) {
  const containerRef = useRef<HTMLUListElement>(null);
  const scrollMargin = useAppScrollMargin(containerRef, null);
  const virtualizer = useWindowVirtualizer({
    count: props.items.length,
    estimateSize: () => CARD_ESTIMATE_PX + CARD_GAP_PX,
    measureElement: measureCard,
    getItemKey: (index) => keyOf(props.items, index),
    overscan: OVERSCAN_CARDS,
    scrollMargin,
  });
  return <Rows {...props} containerRef={containerRef} virtualizer={virtualizer} />;
}

/**
 * A card's real height, plus the gap that follows it.
 *
 * The gap belongs to the measurement rather than to CSS because the rows are
 * positioned, not flowed: `gap` would have nothing to apply to. Falling back to
 * the estimate when an element reports no height keeps a list that has not been
 * laid out — a test renderer, a card inside something collapsed — showing its
 * cards at their estimated size instead of stacking them all at zero.
 */
function measureCard(element: Element): number {
  const measured = Math.round(element.getBoundingClientRect().height);
  return (measured > 0 ? measured : CARD_ESTIMATE_PX) + CARD_GAP_PX;
}

function keyOf(items: GuestDirectoryItem[], index: number): string {
  const item = items[index];
  return item ? guestDirectoryItemKey(item) : `missing:${index}`;
}

function Rows({
  items, resetKey, hasMore, loadingMore, onNearEnd, renderItem, containerRef, virtualizer,
}: GuestVirtualListProps & {
  containerRef: RefObject<HTMLUListElement | null>;
  virtualizer: ListVirtualizer;
}) {
  const visible = virtualizer.getVirtualItems();
  const last = visible.length > 0 ? visible[visible.length - 1] : undefined;
  const lastIndex = last?.index;
  const { scrollMargin } = virtualizer.options;

  // A new search or filter is a different list. Its first page is already on
  // the screen by the time this runs, so the host would otherwise be left at
  // the scroll position of a list that no longer exists — looking at the middle
  // of results they never scrolled through. Not on the first render: arriving
  // at the console must not move the page.
  const known = useRef(resetKey);
  useEffect(() => {
    if (known.current === resetKey) return;
    known.current = resetKey;
    virtualizer.scrollToOffset(scrollMargin, { align: 'start' });
  }, [resetKey, scrollMargin, virtualizer]);

  // Paging follows the VISIBLE RANGE, which is the only thing that knows where
  // the host actually is once the DOM holds a fraction of the list. The button
  // below stays for anyone who does not scroll — a keyboard, a screen reader, a
  // page whose last request failed.
  useEffect(() => {
    if (!hasMore || loadingMore || lastIndex === undefined) return;
    if (lastIndex >= items.length - NEAR_END_CARDS) onNearEnd();
  }, [lastIndex, items.length, hasMore, loadingMore, onNearEnd]);

  return (
    <ul
      className="guest-list guest-list--virtual" data-testid="guest-list" ref={containerRef}
      style={{ height: `${virtualizer.getTotalSize()}px` }}
    >
      {visible.map((row) => {
        const item = items[row.index];
        if (item === undefined) return null;
        return (
          <li
            key={String(row.key)} className="guest-row" data-index={row.index}
            ref={virtualizer.measureElement}
            style={{ transform: `translateY(${row.start - scrollMargin}px)` }}
          >
            {renderItem(item)}
          </li>
        );
      })}
    </ul>
  );
}
