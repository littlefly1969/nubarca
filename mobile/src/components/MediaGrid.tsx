// MediaGrid: the one virtualized grid every media surface uses (Photos,
// Videos, album detail, album add-picker). FlatList-based — virtualization is
// never traded for ScrollView + map.

import React, { useCallback, useMemo } from 'react';
import { FlatList, RefreshControl, StyleSheet, Text, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { MediaTile } from './MediaTile';
import { columnsForWidth, grid, spacing, typography } from '../ui/tokens';
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
  ListHeaderComponent?: React.ComponentType<unknown> | React.ReactElement | null;
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
  const tileSize = Math.floor((width - insets.left - insets.right - gap * (columns + 1)) / columns);

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
      contentContainerStyle={[
        styles.content,
        { paddingHorizontal: insets.left + gap },
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
