// MediaGrid: the owner-media adapter over the one gallery list.
//
// It decides what a media tile IS, what happens when one is pressed, and what
// the footer says. It decides nothing about virtualization, columns, gutters
// or position — GalleryList owns those, and every media surface shares them.

import React, { useCallback } from 'react';
import {
  RefreshControl,
  StyleSheet,
  Text,
  View,
  type NativeScrollEvent,
  type NativeSyntheticEvent,
} from 'react-native';
import { MediaTile } from './MediaTile';
import { GalleryList } from './GalleryList';
import { spacing, typography } from '../ui/tokens';
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

  const keyOf = useCallback((item: MediaItem) => item.id, []);

  const renderTile = useCallback(
    (item: MediaItem) => (
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
    ),
    [selecting, selectedIds, onPressItem, onToggleSelect, onLongPressItem],
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
    <GalleryList
      testID="media-grid"
      items={items}
      keyOf={keyOf}
      renderTile={renderTile}
      onEndReached={onEndReached}
      onScroll={onScroll}
      scrollEventThrottle={scrollEventThrottle}
      anchorItemId={anchorItemId}
      onAnchorConsumed={onAnchorConsumed}
      contentPaddingTop={contentPaddingTop}
      contentPaddingBottom={contentPaddingBottom}
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
