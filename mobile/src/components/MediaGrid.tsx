// MediaGrid: the one virtualized grid every media surface uses (Photos,
// Videos, album detail, album add-picker). FlatList-based — virtualization is
// never traded for ScrollView + map.
//
// POSITION IS AN ITEM, NOT AN OFFSET. Rotating the phone changes the column
// count, and React Native requires a remount to change `numColumns` — which
// resets the list to the top. The grid therefore remembers the first visible
// media ID, outside the list that remounts, and puts that item back afterwards.
// An offset could not survive the change even in principle: the same pixel
// height is a different row under a different geometry.

import React, { useCallback, useEffect, useMemo, useRef } from 'react';
import {
  FlatList,
  RefreshControl,
  StyleSheet,
  Text,
  View,
  type NativeScrollEvent,
  type NativeSyntheticEvent,
  type ViewToken,
} from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { MediaTile } from './MediaTile';
import { columnsForWidth, grid, spacing, typography } from '../ui/tokens';
import { gridMetrics } from '../ui/gridMetrics.ts';
import { Button } from '../ui/components';
import { useI18n } from '../i18n';
import { useWindowDimensions } from 'react-native';
import type { MediaItem } from '../api/media';
import { anchorFromVisible, anchorIndexOf } from '../media/galleryAnchor.ts';
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
  ListHeaderComponent?: React.ComponentType<unknown> | React.ReactElement | null;
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
  ListHeaderComponent,
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
  // The canonical GALLERY gutter, not a generic spacing step: a media library
  // is a seam between pictures, and at four pixels it starts to read as a set
  // of tiles rather than as one surface.
  const gap = grid.gap;
  // One source of truth for the geometry, so the size a tile is given and the
  // space actually left between tiles cannot disagree.
  const { tileSize, sidePadding } = gridMetrics(width, insets.left + insets.right, columns, gap);

  // Stable callback identity keeps FlatList from re-mounting rows.
  const renderItem = useCallback(
    ({ item }: { item: MediaItem }) => (
      <MediaTile
        item={item}
        size={tileSize}
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
    ),
    [tileSize, selecting, selectedIds, onPressItem, onToggleSelect, onLongPressItem],
  );

  // Selection state changes must not reshuffle the whole grid.
  const keyExtractor = useCallback((item: MediaItem) => item.id, []);

  // --- position, held OUTSIDE the list that remounts ------------------------
  const listRef = useRef<FlatList<MediaItem> | null>(null);
  const visibleAnchor = useRef<string | null>(null);
  const pendingAnchor = useRef<string | null>(null);

  // Identity must be stable: React Native throws if this callback changes
  // between renders.
  const onViewableItemsChanged = useRef(
    ({ viewableItems }: { viewableItems: ViewToken[] }) => {
      visibleAnchor.current = anchorFromVisible(
        viewableItems.map((token) => (token.item as MediaItem).id),
      );
    },
  ).current;

  const restoreAnchor = useCallback(() => {
    const id = pendingAnchor.current;
    if (id === null) return;
    const index = anchorIndexOf(items, id);
    pendingAnchor.current = null;
    onAnchorConsumed?.();
    // A missing anchor is an ordinary answer: the item was deleted, or a filter
    // removed it. Stay where we are rather than jumping to the top.
    if (index === null) return;
    listRef.current?.scrollToIndex({ index, animated: false, viewPosition: 0 });
  }, [items, onAnchorConsumed]);

  // A column change remounts the FlatList. Capture what we were looking at so
  // the new one can be pointed back at it, then ask immediately — the remounted
  // list may not be measured yet, in which case `onScrollToIndexFailed` re-arms
  // and `onContentSizeChange` asks again once it is.
  const previousColumns = useRef(columns);
  useEffect(() => {
    if (previousColumns.current === columns) return;
    previousColumns.current = columns;
    pendingAnchor.current = visibleAnchor.current;
    restoreAnchor();
  }, [columns, restoreAnchor]);

  // A gallery returning from the viewer overrides the browse anchor: the item
  // the user was last looking at wins over the one they opened.
  //
  // IT MUST BE APPLIED HERE. Returning from the viewer changes no content size
  // and no column count, so nothing else would ever ask — which is exactly why
  // the first version of this silently did nothing at all.
  useEffect(() => {
    if (anchorItemId === null) return;
    pendingAnchor.current = anchorItemId;
    restoreAnchor();
  }, [anchorItemId, restoreAnchor]);

  const contentInset = useMemo(
    () => ({ bottom: insets.bottom }),
    [insets.bottom],
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
    <FlatList
      testID="media-grid"
      ref={listRef}
      data={items}
      key={columns}
      numColumns={columns}
      renderItem={renderItem}
      keyExtractor={keyExtractor}
      onEndReached={onEndReached}
      onEndReachedThreshold={0.5}
      refreshControl={
        onRefresh !== undefined ? (
          <RefreshControl progressBackgroundColor={colors.surface} refreshing={refreshing} onRefresh={onRefresh} tintColor={colors.accent} colors={[colors.accent]} />
        ) : undefined
      }
      ListHeaderComponent={ListHeaderComponent ?? null}
      ListFooterComponent={footer}
      onScroll={onScroll}
      scrollEventThrottle={scrollEventThrottle}
      onViewableItemsChanged={onViewableItemsChanged}
      onContentSizeChange={restoreAnchor}
      // The row may not be measured yet on a fresh mount; ask again once the
      // list has laid out rather than throwing.
      // The row is not laid out yet. Re-arm rather than throw; the next
      // content-size change asks again.
      onScrollToIndexFailed={(info) => {
        pendingAnchor.current = items[info.index]?.id ?? null;
      }}
      // `numColumns` lays a row out with flex-start, so the column seam has to
      // be stated explicitly — a container gap does not distribute it.
      columnWrapperStyle={columns > 1 ? { gap } : undefined}
      contentContainerStyle={[
        styles.content,
        { paddingHorizontal: insets.left + sidePadding },
        contentPaddingTop !== undefined && { paddingTop: contentPaddingTop },
        contentPaddingBottom !== undefined && { paddingBottom: contentPaddingBottom },
      ]}
      contentInset={contentInset}
      removeClippedSubviews
      windowSize={7}
      maxToRenderPerBatch={columns * 3}
      initialNumToRender={columns * 2}
      style={styles.list}
    />
  );
}

const useStyles = themed((colors) =>
  StyleSheet.create({
    list: { flex: 1 },
    content: {
      paddingTop: spacing.s,
      paddingBottom: spacing.xxl,
      gap: grid.gap,
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
