// Videos tab: poster-first grid with play affordances and duration badges.
// Tapping a tile opens the shared media route, which becomes the native player.
import React, { useCallback, useEffect, useRef, useState } from 'react';
import { Redirect, useFocusEffect } from 'expo-router';
import { Pressable, StyleSheet } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { Screen, AppHeader } from '../../src/ui/components';
import { EmptyState, ErrorState, LoadingState } from '../../src/ui/states';
import { MediaGrid } from '../../src/components/MediaGrid';
import { ownedSlides } from '../../src/media/viewerEntries';
import { useSession } from '../../src/session/SessionProvider';
import { useViewer } from '../../src/media/viewerContext';
import { usePagedList } from '../../src/lib/usePagedList';
import { shouldRefreshOnFocus } from '../../src/lib/focusRefresh';
import type { MediaItem } from '../../src/api/media.ts';
import { useMediaFilters } from '../../src/media/useMediaFilters';
import { MediaFilterChips } from '../../src/components/MediaFilterChips';
import { MediaFilterSheet } from '../../src/components/MediaFilterSheet';
import { colors } from '../../src/ui/tokens';
import { router } from 'expo-router';
import { useI18n } from '../../src/i18n';

const PAGE_SIZE = 60;

export default function Videos(): React.JSX.Element {
  const session = useSession();
  const { t } = useI18n();
  const viewer = useViewer();

  const [filtersOpen, setFiltersOpen] = useState(false);
  const filters = useMediaFilters('video', PAGE_SIZE);
  const { snapshot, refresh, loadMore, retryFailed } =
    usePagedList<MediaItem>((i) => i.id, filters.fetchPage);

  // Refresh on focus only when there is nothing to lose: a refresh replaces
  // the accumulator with page one, so doing it on every return from the viewer
  // discarded every page the reader had scrolled through. See lib/focusRefresh.
  const itemCountRef = useRef(0);
  itemCountRef.current = snapshot.items.length;
  useFocusEffect(
    useCallback(() => {
      if (session.status !== 'authed') return undefined;
      if (shouldRefreshOnFocus({ itemCount: itemCountRef.current, stale: false })) {
        void refresh();
      }
      return undefined;
    }, [refresh, session.status]),
  );

  // §19: a committed filter change is a new query generation, so the cursor is
  // dropped and the accumulator cleared instead of paging an old query into a
  // new one.
  const generation = filters.generation;
  const firstGeneration = useRef(generation);
  useEffect(() => {
    if (generation === firstGeneration.current) return;
    firstGeneration.current = generation;
    void refresh();
  }, [generation, refresh]);

  if (session.status !== 'authed') {
    return <Redirect href="/login" />;
  }

  const openPlayer = (item: MediaItem): void => {
    viewer.open(ownedSlides(snapshot.items), item.id);
    router.push(`/media/${item.id}`);
  };

  return (
    <Screen>
      <AppHeader
        title={t('tabs.videos')}
        actions={
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t('filters.open')}
            onPress={() => setFiltersOpen(true)}
            style={({ pressed }) => [styles.iconBtn, pressed && styles.pressed]}
            hitSlop={4}
          >
            <Ionicons
              name={filters.chips.length > 0 ? 'funnel' : 'funnel-outline'}
              size={20}
              color={colors.accent}
            />
          </Pressable>
        }
      />

      <MediaFilterChips
        chips={filters.chips}
        people={filters.people}
        onRemove={filters.removeChip}
        onClearAll={filters.clearAll}
      />

      {snapshot.phase === 'loading' ? (
        <LoadingState />
      ) : snapshot.phase === 'error' && snapshot.items.length === 0 ? (
        <ErrorState
          title={t('grid.errorTitle')}
          message={t('gallery.loadErrorNetwork', { what: t('gallery.whatVideos') })}
          onRetry={() => {
            void refresh();
          }}
        />
      ) : snapshot.items.length === 0 ? (
        <EmptyState title={t('grid.emptyVideos')} hint={t('grid.emptyHint')} />
      ) : (
        <MediaGrid
          items={snapshot.items}
          onPressItem={openPlayer}
          refreshing={snapshot.phase === 'refreshing'}
          onRefresh={() => {
            void refresh();
          }}
          onEndReached={
            snapshot.hasMore
              ? () => {
                  void loadMore();
                }
              : undefined
          }
          footerPhase={
            snapshot.phase === 'loadingMore'
              ? 'loadingMore'
              : snapshot.phase === 'error'
                ? 'error'
                : null
          }
          onLoadMoreRetry={() => {
            void retryFailed();
          }}
        />
      )}

      <MediaFilterSheet
        visible={filtersOpen}
        identity={filters.identity}
        onApply={(next, sort, direction) => {
          filters.apply(next, sort, direction);
          setFiltersOpen(false);
        }}
        onClose={() => setFiltersOpen(false)}
      />
    </Screen>
  );
}

const styles = StyleSheet.create({
  iconBtn: { width: 40, height: 40, alignItems: 'center', justifyContent: 'center' },
  pressed: { opacity: 0.7 },
});
