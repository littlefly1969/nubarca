// MediaGrid: the one virtualized grid every media surface uses (Photos,
// Videos, album detail, album add-picker).
//
// FlashList receives the FLAT media items. There is no row model, no tile
// arithmetic and no pixel-offset restore here: the list owns virtualization,
// layout and recycling, and this file owns what a media tile is, what happens
// when one is pressed, and the two position commands the product needs —
// coming back from the viewer, and keeping your place when the column count
// changes. Both are the same three steps: stable item ID, flat index, one
// scroll.

import React, { useCallback, useEffect, useLayoutEffect, useRef } from 'react';
import {
  RefreshControl,
  StyleSheet,
  Text,
  View,
  useWindowDimensions,
  type NativeScrollEvent,
  type NativeSyntheticEvent,
} from 'react-native';
import { FlashList, type FlashListRef, type ViewToken } from '@shopify/flash-list';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { MediaTile } from './MediaTile';
import { columnsForWidth, grid, spacing, typography } from '../ui/tokens';
import { indexOfItemId } from '../media/galleryAnchor.ts';
import { Button } from '../ui/components';
import { useI18n } from '../i18n';
import type { MediaItem } from '../api/media';
import { themed, useColors } from '../ui/theme';

export interface MediaGridProps {
  items: MediaItem[];
  onEndReached?: () => void;
  refreshing?: boolean;
  onRefresh?: () => void;
  footerPhase?: string | null;
  onLoadMoreRetry?: () => void;
  selecting?: boolean;
  selectedIds?: ReadonlySet<string>;
  onPressItem: (item: MediaItem) => void;
  onToggleSelect?: (id: string) => void;
  /**
   * Long-press outside selection mode: the screen decides how selection
   * begins (enter mode + select the pressed item) — the grid never mutates
   * selection state implicitly.
   */
  onLongPressItem?: (item: MediaItem) => void;
  /**
   * Scroll plumbing supplied by the immersive shell. The grid reports its
   * offset and leaves room for the chrome floating over it; it does not know
   * what the chrome is or when it hides.
   */
  onScroll?: (event: NativeSyntheticEvent<NativeScrollEvent>) => void;
  scrollEventThrottle?: number;
  /**
   * Bring this item into view once. Used by a gallery returning from the
   * viewer: the user swiped to something else in there, and the grid should be
   * looking at what they were last looking at.
   */
  anchorItemId?: string | null;
  /** Called once the anchor has been honoured, so it is not applied twice. */
  onAnchorConsumed?: () => void;
  contentPaddingTop?: number;
  contentPaddingBottom?: number;
}

// A tile counts as the user's context once it is meaningfully on screen, not
// when one row of pixels peeks in at the top edge.
const VIEWABILITY = { itemVisiblePercentThreshold: 60 };

export function MediaGrid({
  items,
  onEndReached,
  refreshing = false,
  onRefresh,
  footerPhase = null,
  onLoadMoreRetry,
  selecting = false,
  selectedIds,
  onPressItem,
  onToggleSelect,
  onLongPressItem,
  onScroll,
  scrollEventThrottle,
  contentPaddingTop,
  contentPaddingBottom,
  anchorItemId = null,
  onAnchorConsumed,
}: MediaGridProps): React.JSX.Element {
  const styles = useStyles();
  const colors = useColors();
  const { t } = useI18n();
  const { width } = useWindowDimensions();
  const insets = useSafeAreaInsets();
  const columns = columnsForWidth(width);

  const listRef = useRef<FlashListRef<MediaItem>>(null);
  // What the user is looking at, as an ID.
  const visibleItemIdRef = useRef<string | null>(null);
  const previousColumnsRef = useRef(columns);
  // Non-null only between detecting a column change and restoring for it.
  const pendingColumnAnchorRef = useRef<string | null>(null);

  const keyOf = useCallback((item: MediaItem) => item.id, []);

  const onViewableItemsChanged = useCallback(
    ({ viewableItems }: { viewableItems: ViewToken<MediaItem>[] }) => {
      // Mid-restore the visible window still describes the position being
      // corrected, so letting it write back would anchor us to the very jump
      // we are undoing.
      if (pendingColumnAnchorRef.current !== null) return;
      const first = viewableItems[0]?.item;
      if (first) visibleItemIdRef.current = first.id;
    },
    [],
  );

  // A COLUMN CHANGE IS THE ONLY TRIGGER. Not a page append, not a rerender, not
  // selection, not the footer, not the theme.
  //
  // This effect runs at React commit, which is before FlashList has relaid out
  // for the new column count, so it only ARMS the restore: the anchor read
  // here is still the one the user had, and scrolling now would be undone by
  // the relayout that follows.
  useLayoutEffect(() => {
    if (previousColumnsRef.current === columns) return;
    previousColumnsRef.current = columns;
    pendingColumnAnchorRef.current = visibleItemIdRef.current;
  }, [columns]);

  const scrollToItemId = useCallback(
    (id: string): boolean => {
      const index = indexOfItemId(items, id);
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
    [items],
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
  // parent's setState while another component was rendering.
  useEffect(() => {
    if (anchorItemId === null) return;
    // An anchor naming an item this page has not loaded stays armed, so the
    // page that does contain it can honour it instead of the grid jumping
    // somewhere arbitrary and calling it done.
    if (!scrollToItemId(anchorItemId)) return;
    onAnchorConsumed?.();
  }, [anchorItemId, scrollToItemId, onAnchorConsumed]);

  const renderItem = useCallback(
    ({ item }: { item: MediaItem }) => (
      <View style={styles.cell}>
        <MediaTile
          item={item}
          selected={selectedIds?.has(item.id) ?? false}
          selecting={selecting}
          onPress={() => {
            if (selecting && onToggleSelect !== undefined) onToggleSelect(item.id);
            else onPressItem(item);
          }}
          onLongPress={
            !selecting && onLongPressItem !== undefined
              ? () => onLongPressItem(item)
              : undefined
          }
        />
      </View>
    ),
    [styles, selecting, selectedIds, onPressItem, onToggleSelect, onLongPressItem],
  );

  const footer = (() => {
    if (footerPhase === 'loadingMore') {
      return (
        <View style={styles.footer}>
          <Text style={styles.footerText}>{t('common.loading')}</Text>
        </View>
      );
    }
    if (footerPhase === 'error' && onLoadMoreRetry !== undefined) {
      return (
        <View style={styles.loadMore}>
          <Button
            label={t('gallery.loadMoreError')}
            variant="secondary"
            onPress={onLoadMoreRetry}
            accessibilityLabel={t('common.retry')}
          />
        </View>
      );
    }
    return null;
  })();

  return (
    <FlashList
      ref={listRef}
      testID="media-grid"
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
      refreshControl={
        onRefresh !== undefined ? (
          <RefreshControl
            progressBackgroundColor={colors.surface}
            refreshing={refreshing}
            onRefresh={onRefresh}
            tintColor={colors.accent}
            colors={[colors.accent]}
          />
        ) : undefined
      }
      ListFooterComponent={footer}
      style={styles.list}
    />
  );
}

const useStyles = themed((colors) =>
  StyleSheet.create({
    list: { flex: 1 },
    cell: {
      flex: 1,
      paddingHorizontal: grid.gap / 2,
      paddingBottom: grid.gap,
    },
    footer: {
      paddingVertical: spacing.l,
      alignItems: 'center',
    },
    footerText: { ...typography.secondary, color: colors.textTertiary },
    loadMore: {
      marginHorizontal: spacing.l,
      marginTop: spacing.m,
      marginBottom: spacing.xl,
    },
  }),
);
