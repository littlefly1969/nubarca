// The one gallery list. Every media surface — Photos, Videos, owned albums,
// shared albums — virtualizes through this and nowhere else.
//
// It owns exactly three things: FlashList over the FLAT items, the responsive
// column count, and the two position commands the product needs. It owns no
// geometry: the gutter comes from ordinary padding, and the tile squares
// itself inside the column the list hands it.
//
// What a tile IS, and what happens when one is pressed, belongs to the caller.

import React, { useCallback, useEffect, useLayoutEffect, useRef } from 'react';
import {
  StyleSheet,
  View,
  useWindowDimensions,
  type NativeScrollEvent,
  type NativeSyntheticEvent,
  type ViewStyle,
} from 'react-native';
import type { RefreshControlProps } from 'react-native';
import { FlashList, type FlashListRef, type ViewToken } from '@shopify/flash-list';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { columnsForWidth, grid, spacing } from '../ui/tokens';
import { indexOfItemId } from '../media/galleryAnchor.ts';

export interface GalleryListProps<TItem> {
  items: TItem[];
  keyOf: (item: TItem) => string;
  renderTile: (item: TItem) => React.ReactNode;
  onEndReached?: () => void;
  refreshControl?: React.ReactElement<RefreshControlProps>;
  ListFooterComponent?: React.ReactElement | null;
  onScroll?: (event: NativeSyntheticEvent<NativeScrollEvent>) => void;
  scrollEventThrottle?: number;
  /** Bring this item into view once — a gallery returning from the viewer. */
  anchorItemId?: string | null;
  /** Called once the anchor has been honoured, so it is not applied twice. */
  onAnchorConsumed?: () => void;
  contentPaddingTop?: number;
  contentPaddingBottom?: number;
  style?: ViewStyle;
  testID?: string;
}

// A tile counts as the user's context once it is meaningfully on screen, not
// when one row of pixels peeks in at the top edge.
const VIEWABILITY = { itemVisiblePercentThreshold: 60 };

export function GalleryList<TItem>({
  items,
  keyOf,
  renderTile,
  onEndReached,
  refreshControl,
  ListFooterComponent = null,
  onScroll,
  scrollEventThrottle,
  anchorItemId = null,
  onAnchorConsumed,
  contentPaddingTop,
  contentPaddingBottom,
  style,
  testID,
}: GalleryListProps<TItem>): React.JSX.Element {
  const { width } = useWindowDimensions();
  const insets = useSafeAreaInsets();
  const columns = columnsForWidth(width);

  const listRef = useRef<FlashListRef<TItem>>(null);
  // What the user is looking at, as an ID.
  const visibleItemIdRef = useRef<string | null>(null);
  const previousColumnsRef = useRef(columns);
  // Non-null only between detecting a column change and restoring for it.
  const pendingColumnAnchorRef = useRef<string | null>(null);

  const onViewableItemsChanged = useCallback(
    ({ viewableItems }: { viewableItems: ViewToken<TItem>[] }) => {
      // Mid-restore the visible window still describes the position being
      // corrected, so letting it write back would anchor us to the very jump
      // we are undoing.
      if (pendingColumnAnchorRef.current !== null) return;
      const first = viewableItems[0]?.item;
      if (first !== null && first !== undefined) visibleItemIdRef.current = keyOf(first);
    },
    [keyOf],
  );

  // A COLUMN CHANGE IS THE ONLY TRIGGER. Not a page append, not a rerender,
  // not selection, not the footer, not the theme.
  //
  // This runs at React commit, which is before FlashList has relaid out for
  // the new column count, so it only ARMS the restore: the anchor read here is
  // still the one the user had, and scrolling now would be undone by the
  // relayout that follows.
  useLayoutEffect(() => {
    if (previousColumnsRef.current === columns) return;
    previousColumnsRef.current = columns;
    pendingColumnAnchorRef.current = visibleItemIdRef.current;
  }, [columns]);

  const scrollToItemId = useCallback(
    (id: string): boolean => {
      const index = indexOfItemId(items, keyOf, id);
      const list = listRef.current;
      if (index < 0 || list === null) return false;
      // Two calls, not a retry loop, and centred rather than pinned to the top.
      // The first cannot be exact: a deep index sits outside the rendered
      // window, so FlashList has only an estimate of its offset from average
      // item size, and that error grows with distance. The first scroll renders
      // and measures the target region; the second is then arithmetic on real
      // layout, and a third has nothing left to improve. Centring spends
      // whatever remains on empty space rather than on pushing the user's own
      // tile off screen.
      void list
        .scrollToIndex({ index, animated: false, viewPosition: 0.5 })
        .then(() => list.scrollToIndex({ index, animated: false, viewPosition: 0.5 }))
        .catch(() => undefined);
      return true;
    },
    [items, keyOf],
  );

  // Fires once FlashList has committed a layout, which is the first moment an
  // index means what we think it means. Refs only in here: the documented
  // hazard of this hook is setState.
  const onCommitLayoutEffect = useCallback(() => {
    const id = pendingColumnAnchorRef.current;
    if (id === null) return;
    const listWidth = listRef.current?.getWindowSize().width;
    if (listWidth === undefined) return;
    // A rotation produces SEVERAL layout commits, and the early ones carry the
    // new column count inside the OLD viewport — five columns laid out in a
    // portrait width, giving rows less than half their eventual height. An
    // index resolved there points somewhere else entirely. Asking
    // columnsForWidth what the list's own measured width implies is an exact
    // test for "has the list caught up", with no tolerance to tune.
    if (columnsForWidth(listWidth) !== columns) return;
    // Consumed once: a still-armed anchor would start a second scroll that
    // fights the first, since each pauses offset correction and runs its own
    // convergence steps.
    pendingColumnAnchorRef.current = null;
    scrollToItemId(id);
  }, [columns, scrollToItemId]);

  // Viewer return. In an EFFECT, never during render: this used to call the
  // parent's onAnchorConsumed while another component was rendering.
  useEffect(() => {
    if (anchorItemId === null) return;
    // An anchor naming an item this page has not loaded stays armed, so the
    // page that does contain it can honour it instead of the grid jumping
    // somewhere arbitrary and calling it done.
    if (!scrollToItemId(anchorItemId)) return;
    onAnchorConsumed?.();
  }, [anchorItemId, scrollToItemId, onAnchorConsumed]);

  const renderItem = useCallback(
    ({ item }: { item: TItem }) => <View style={styles.cell}>{renderTile(item)}</View>,
    [renderTile],
  );

  return (
    <FlashList
      ref={listRef}
      testID={testID}
      data={items}
      keyExtractor={keyOf}
      numColumns={columns}
      renderItem={renderItem}
      onViewableItemsChanged={onViewableItemsChanged}
      viewabilityConfig={VIEWABILITY}
      onCommitLayoutEffect={onCommitLayoutEffect}
      onEndReached={onEndReached}
      onScroll={onScroll}
      scrollEventThrottle={scrollEventThrottle}
      // Half a gutter on the content and half on each cell makes the outer
      // margin equal the seam between tiles, with no arithmetic per tile.
      contentContainerStyle={{
        paddingTop: contentPaddingTop ?? spacing.s,
        paddingBottom: contentPaddingBottom ?? insets.bottom + spacing.xxl,
        paddingLeft: insets.left + grid.gap / 2,
        paddingRight: insets.right + grid.gap / 2,
      }}
      refreshControl={refreshControl}
      ListFooterComponent={ListFooterComponent}
      style={style}
    />
  );
}

const styles = StyleSheet.create({
  cell: {
    flex: 1,
    paddingHorizontal: grid.gap / 2,
    paddingBottom: grid.gap,
  },
});
