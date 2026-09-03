// MediaGrid: the one virtualized grid every media surface uses (Photos,
// Videos, album detail, album add-picker). FlatList-based — virtualization is
// never traded for ScrollView + map.
//
// It is a thin adapter over VirtualizedGalleryRows, which owns rows, geometry
// and position for every media surface. This file decides what a media tile is
// and what happens when one is pressed; it does not decide where the gallery is
// looking, and it no longer contains a second copy of that algorithm.

import React, { useCallback } from 'react';
import {
  RefreshControl,
  StyleSheet,
  Text,
  View,
  type NativeScrollEvent,
  type NativeSyntheticEvent,
} from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { MediaTile } from './MediaTile';
import { columnsForWidth, grid, spacing, typography } from '../ui/tokens';
import { gridMetrics } from '../ui/gridMetrics.ts';
import { VirtualizedGalleryRows } from './VirtualizedGalleryRows';
import { Button } from '../ui/components';
import { useI18n } from '../i18n';
import { useWindowDimensions } from 'react-native';
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
  // The canonical GALLERY gutter, not a generic spacing step: a media library
  // is a seam between pictures, and at four pixels it starts to read as a set
  // of tiles rather than as one surface.
  const gap = grid.gap;
  // One source of truth for the geometry, so the size a tile is given and the
  // space actually left between tiles cannot disagree.
  const { tileSize, sidePadding } = gridMetrics(width, insets.left + insets.right, columns, gap);

  // What a media tile IS. Where the gallery is looking is not this file's
  // business any more.
  const renderTile = useCallback(
    (item: MediaItem, size: number) => (
      <MediaTile
        item={item}
        size={size}
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
    [selecting, selectedIds, onPressItem, onToggleSelect, onLongPressItem],
  );

  const keyOf = useCallback((item: MediaItem) => item.id, []);

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
    <VirtualizedGalleryRows
      testID="media-grid"
      items={items}
      keyOf={keyOf}
      columns={columns}
      tileSize={tileSize}
      sidePadding={insets.left + sidePadding}
      gap={gap}
      renderTile={renderTile}
      contentPaddingTop={contentPaddingTop ?? spacing.s}
      contentPaddingBottom={contentPaddingBottom ?? insets.bottom + spacing.xxl}
      onScroll={onScroll}
      scrollEventThrottle={scrollEventThrottle}
      anchorItemId={anchorItemId}
      onAnchorConsumed={onAnchorConsumed}
      onEndReached={onEndReached}
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
