// One gallery virtualization engine, for every media surface.
//
// It renders EXPLICIT ROWS in an ordinary single-column FlatList. That is the
// whole architectural point: `numColumns` turns a list into a row list behind
// your back, so its index space stops being the one you are holding, and it
// forces `key={columns}` to change the count — which destroys the list on every
// rotation and puts it back at the top.
//
// With rows as data:
//   * the list instance SURVIVES a rotation; only its data and geometry change;
//   * the geometry is declared through `getItemLayout` rather than measured;
//   * position is a pixel offset we compute, which is defined for every item at
//     every geometry — so restoring never needs `scrollToIndex`, never needs a
//     failure handler, and never becomes a retry loop driven by layout events.
//
// It owns rows, layout and position. It does not own pagination, data, filters,
// selection capabilities or viewer state; a screen hands it items and a tile
// renderer and keeps its domain.

import React, { useCallback, useEffect, useMemo, useRef } from 'react';
import {
  FlatList,
  StyleSheet,
  View,
  type NativeScrollEvent,
  type NativeSyntheticEvent,
  type RefreshControlProps,
  type StyleProp,
  type ViewStyle,
} from 'react-native';
import { buildGalleryRows, rowExtent, type GalleryRow } from '../media/galleryRows.ts';
import {
  anchorFromScroll,
  geometryChanged,
  offsetForAnchor,
  type GalleryGeometry,
  type GalleryPositionAnchor,
} from '../media/galleryPosition.ts';

export interface VirtualizedGalleryRowsProps<T> {
  items: readonly T[];
  keyOf: (item: T) => string;
  columns: number;
  tileSize: number;
  sidePadding: number;
  gap: number;
  renderTile: (item: T, size: number) => React.ReactNode;

  /** Room for the collapsible chrome floating above the first row. */
  contentPaddingTop: number;
  /** Room for the navigation floating over the last row. */
  contentPaddingBottom: number;
  /** Forwarded to the immersive shell, unchanged. */
  onScroll?: (event: NativeSyntheticEvent<NativeScrollEvent>) => void;
  scrollEventThrottle?: number;

  /**
   * Bring this item into view once, at the top of its row. Set by a gallery
   * returning from the viewer on an item the user swiped to.
   */
  anchorItemId?: string | null;
  onAnchorConsumed?: () => void;

  onEndReached?: () => void;
  onEndReachedThreshold?: number;
  refreshControl?: React.ReactElement<RefreshControlProps>;
  ListFooterComponent?: React.ComponentType<unknown> | React.ReactElement | null;
  style?: StyleProp<ViewStyle>;
  testID?: string;
}

export function VirtualizedGalleryRows<T>({
  items,
  keyOf,
  columns,
  tileSize,
  sidePadding,
  gap,
  renderTile,
  contentPaddingTop,
  contentPaddingBottom,
  onScroll,
  scrollEventThrottle = 16,
  anchorItemId = null,
  onAnchorConsumed,
  onEndReached,
  onEndReachedThreshold = 0.5,
  refreshControl,
  ListFooterComponent,
  style,
  testID,
}: VirtualizedGalleryRowsProps<T>): React.JSX.Element {
  const listRef = useRef<FlatList<GalleryRow<T>> | null>(null);

  const rows = useMemo(() => buildGalleryRows(items, columns), [items, columns]);
  const extent = rowExtent(tileSize, gap);
  const geometry = useMemo<GalleryGeometry>(
    () => ({ columns, rowExtent: extent, topPadding: contentPaddingTop }),
    [columns, extent, contentPaddingTop],
  );

  // Position identity, for anchorFromScroll/offsetForAnchor. Recomputed only
  // when the items change, never per scroll frame.
  const identified = useMemo(() => items.map((item) => ({ id: keyOf(item) })), [items, keyOf]);

  // --- position, in refs: scrolling must never re-render a gallery ----------
  const browseAnchor = useRef<GalleryPositionAnchor | null>(null);
  const activeGeometry = useRef<GalleryGeometry | null>(null);
  // While a geometry restore is in flight the incoming scroll events belong to
  // the OLD position being replayed, not to the user. Capturing them would
  // overwrite the anchor with wherever the new list happened to start — which
  // is how the previous design "restored" the first photo.
  const restoring = useRef(false);

  const handleScroll = useCallback(
    (event: NativeSyntheticEvent<NativeScrollEvent>) => {
      onScroll?.(event);
      if (restoring.current) return;
      const anchor = anchorFromScroll({
        y: event.nativeEvent.contentOffset.y,
        geometry,
        items: identified,
      });
      if (anchor !== null) browseAnchor.current = anchor;
    },
    [onScroll, geometry, identified],
  );

  // A geometry change is applied in ONE scroll, from the anchor captured under
  // the previous geometry. No measurement, no retry, no remount.
  //
  // IN AN EFFECT, NOT IN RENDER. The first version did this in the render body
  // and called `onAnchorConsumed` there — a parent setState during another
  // component's render, which React forbids: it re-enters, and the churn
  // starves the very virtualization batches that fill the screen. Scroll
  // commands and parent notifications belong after the commit that laid the
  // new rows out.
  useEffect(() => {
    if (!geometryChanged(activeGeometry.current, geometry)) {
      if (activeGeometry.current === null) activeGeometry.current = geometry;
      return;
    }
    const anchor = browseAnchor.current;
    activeGeometry.current = geometry;
    if (anchor === null) return;
    const offset = offsetForAnchor({ anchor, geometry, items: identified });
    if (offset === null) return;
    restoring.current = true;
    listRef.current?.scrollToOffset({ offset, animated: false });
    // The scroll event this produces belongs to the replay, not to the user.
    const settle = requestAnimationFrame(() => {
      restoring.current = false;
    });
    return () => cancelAnimationFrame(settle);
  }, [geometry, identified]);

  // A viewer return: bring the item the user was last looking at to the top of
  // the viewport. Row progress 0, because this is a jump to a thing rather than
  // a continuation of a scroll.
  useEffect(() => {
    if (anchorItemId === null) return;
    const offset = offsetForAnchor({
      anchor: { itemId: anchorItemId, rowProgress: 0 },
      geometry,
      items: identified,
    });
    // A missing item asks for no movement, and is still consumed.
    if (offset !== null) {
      restoring.current = true;
      listRef.current?.scrollToOffset({ offset, animated: false });
      requestAnimationFrame(() => {
        restoring.current = false;
      });
    }
    onAnchorConsumed?.();
  }, [anchorItemId, geometry, identified, onAnchorConsumed]);

  const renderRow = useCallback(
    ({ item: row }: { item: GalleryRow<T> }) => (
      <View style={[styles.row, { marginBottom: gap, gap }]}>
        {row.items.map((item) => (
          <React.Fragment key={keyOf(item)}>{renderTile(item, tileSize)}</React.Fragment>
        ))}
      </View>
    ),
    [gap, keyOf, renderTile, tileSize],
  );

  // Declared, not measured — and declared from the same origin the content
  // actually starts at. The first version omitted the top padding, so every
  // frame it reported was one chrome-height too high: the window RN chose did
  // not contain the rows on screen, which is what left tiles blank.
  //
  // This is only sound because the list has no header component: an unmeasured
  // header would shift the content without shifting these numbers, and the
  // engine deliberately does not accept one.
  const getItemLayout = useCallback(
    (_data: ArrayLike<GalleryRow<T>> | null | undefined, index: number) => ({
      length: extent,
      offset: contentPaddingTop + extent * index,
      index,
    }),
    [extent, contentPaddingTop],
  );

  const keyExtractor = useCallback(
    (row: GalleryRow<T>) => `row-${row.firstItemIndex}`,
    [],
  );

  return (
    <FlatList
      testID={testID}
      ref={listRef}
      data={rows}
      renderItem={renderRow}
      keyExtractor={keyExtractor}
      getItemLayout={getItemLayout}
      onScroll={handleScroll}
      scrollEventThrottle={scrollEventThrottle}
      onEndReached={onEndReached}
      onEndReachedThreshold={onEndReachedThreshold}
      refreshControl={refreshControl}
      ListFooterComponent={ListFooterComponent ?? null}
      contentContainerStyle={{
        paddingTop: contentPaddingTop,
        paddingBottom: contentPaddingBottom,
        paddingHorizontal: sidePadding,
      }}
      // NO removeClippedSubviews. Each item is a row VIEW containing tiles, and
      // clipping a container on Android is a well-known way to end up with
      // empty cells that never come back.
      //
      // The batch numbers are rows now, not items: a row is roughly a third of
      // the viewport width tall, so a screen holds about six of them and these
      // fill one screen plus margin per batch.
      windowSize={11}
      maxToRenderPerBatch={8}
      initialNumToRender={8}
      style={style}
    />
  );
}

const styles = StyleSheet.create({
  row: { flexDirection: 'row' },
});
