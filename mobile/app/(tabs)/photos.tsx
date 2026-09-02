// Photos tab: the whole owner photo library, newest first, cursor-paginated.
// Opens the shared viewer; long-press starts multi-select for bulk add-to-album.
import React, { useCallback, useEffect, useRef, useState } from 'react';
import { Alert } from 'react-native';
import { Redirect, router, useFocusEffect } from 'expo-router';
import { Ionicons } from '@expo/vector-icons';
import { Screen, AppHeader, HeaderButton, IconButton } from '../../src/ui/components';
import { EmptyState, ErrorState, LoadingState } from '../../src/ui/states';
import { MediaGrid } from '../../src/components/MediaGrid';
import { AddToAlbumSheet } from '../../src/components/AddToAlbumSheet';
import { ownedSlides } from '../../src/media/viewerEntries';
import { useSession } from '../../src/session/SessionProvider';
import { useViewer } from '../../src/media/viewerContext';
import { usePagedList } from '../../src/lib/usePagedList';
import { shouldRefreshOnFocus } from '../../src/lib/focusRefresh';
import { useSelectionState } from '../../src/lib/useSelectionState';
import type { MediaItem } from '../../src/api/media.ts';
import { useMediaFilters } from '../../src/media/useMediaFilters';
import { MediaFilterChips } from '../../src/components/MediaFilterChips';
import { MediaFilterSheet } from '../../src/components/MediaFilterSheet';
import { MediaSelectionBar } from '../../src/components/MediaSelectionBar';
import { getMediaSelectionCapabilities } from '@nubarca/contracts';
import { applyToSelection, moveToTrash, restoreFromTrash } from '../../src/api/mediaLifecycle';
import { useI18n } from '../../src/i18n';
import { useColors } from '../../src/ui/theme';
import { iconSizes } from '../../src/ui/tokens';

const PAGE_SIZE = 60;

export default function Photos(): React.JSX.Element {
  const colors = useColors();
  const session = useSession();
  const { t } = useI18n();
  const viewer = useViewer();
  const selectionState = useSelectionState();
  const [sheetVisible, setSheetVisible] = useState(false);
  const [filtersOpen, setFiltersOpen] = useState(false);

  const filters = useMediaFilters('image', PAGE_SIZE);
  const { snapshot, refresh, loadMore, retryFailed } =
    usePagedList<MediaItem>((i) => i.id, filters.fetchPage);

  // Refresh on each focus so changes made elsewhere are reflected; the
  // PagedList token makes this race-safe against in-flight loads.
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

  // §19: a committed filter change is a NEW query generation — the cursor is
  // dropped, the accumulator cleared, the in-flight request abandoned, and any
  // selection released because the selected items may no longer be results.
  const generation = filters.generation;
  const firstGeneration = useRef(generation);
  useEffect(() => {
    if (generation === firstGeneration.current) return;
    firstGeneration.current = generation;
    selectionState.cancel();
    void refresh();
  }, [generation, refresh, selectionState]);

  if (session.status !== 'authed') {
    return <Redirect href="/login" />;
  }

  // §38: what the selection may do is a DOMAIN answer, asked of the shared
  // capability matrix — the same one the browser asks — not a device check.
  const selectedItems = snapshot.items.filter((i) => selectionState.ids.has(i.id));
  const capabilities = getMediaSelectionCapabilities({
    items: selectedItems,
    source: 'library',
    scope: filters.identity.libraryScope,
  });

  const runLifecycle = async (
    action: (id: string, signal?: AbortSignal) => Promise<void>,
  ): Promise<void> => {
    const ids = [...selectionState.ids];
    const result = await applyToSelection(ids, action);
    // Report what actually happened: a partial result must not read as success.
    if (result.failed > 0) {
      Alert.alert(
        t('selection.failed'),
        t('selection.partial', { ok: String(result.succeeded), n: String(result.requested) }),
      );
    }
    selectionState.cancel();
    void refresh();
  };

  const openViewer = (item: MediaItem): void => {
    viewer.open(ownedSlides(snapshot.items), item.id);
    router.push(`/media/${item.id}`);
  };

  return (
    <Screen>
      <AppHeader
        title={t('tabs.photos')}
        actions={
          selectionState.selecting ? (
            <>
              <HeaderButton
                label={t('selection.addToAlbum')}
                onPress={() => setSheetVisible(true)}
              />
              <HeaderButton label={t('albumDetail.cancelSelection')} onPress={selectionState.cancel} />
            </>
          ) : (
            <>
              {/* Shell only: the same three actions, invoking the same
                  callbacks, expressed with the shared control instead of three
                  hand-rolled Pressables. `selected` on the filter button is a
                  semantic state, not a second icon style invented here. */}
              <IconButton
                accessibilityLabel={t('filters.open')}
                onPress={() => setFiltersOpen(true)}
                selected={filters.chips.length > 0}
              >
                <Ionicons
                  name={filters.chips.length > 0 ? 'funnel' : 'funnel-outline'}
                  size={iconSizes.m}
                  color={colors.accent}
                />
              </IconButton>
              <IconButton
                accessibilityLabel={t('selection.select')}
                onPress={() => selectionState.begin()}
              >
                <Ionicons
                  name="checkmark-circle-outline"
                  size={iconSizes.m}
                  color={colors.accent}
                />
              </IconButton>
              <IconButton
                accessibilityLabel={t('settings.open')}
                onPress={() => router.push('/settings')}
              >
                <Ionicons name="settings-outline" size={iconSizes.m} color={colors.accent} />
              </IconButton>
            </>
          )
        }
      />

      <MediaFilterChips
        chips={filters.chips}
        people={filters.people}
        inert={filters.inert}
        onRemove={filters.removeChip}
        onClearAll={filters.clearAll}
      />

      {snapshot.phase === 'loading' ? (
        <LoadingState />
      ) : snapshot.phase === 'error' && snapshot.items.length === 0 ? (
        <ErrorState
          title={t('grid.errorTitle')}
          message={t('gallery.loadErrorNetwork', { what: t('gallery.whatPhotos') })}
          onRetry={() => {
            void refresh();
          }}
        />
      ) : snapshot.items.length === 0 ? (
        <EmptyState title={t('grid.emptyPhotos')} hint={t('grid.emptyHint')} />
      ) : (
        <>
          <MediaGrid
            items={snapshot.items}
            selecting={selectionState.selecting}
            selectedIds={selectionState.ids}
            onPressItem={openViewer}
            onToggleSelect={selectionState.toggle}
            // ONE transition: enter the mode and keep the item. Doing it in
            // two steps is what made the first long-pressed photo not stick.
            onLongPressItem={(item) => selectionState.beginWith(item.id)}
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
          {selectionState.selecting && (
            <MediaSelectionBar
              selecting={selectionState.selecting}
              count={selectionState.count}
              capabilities={capabilities}
              onAddToAlbum={() => setSheetVisible(true)}
              onTrash={() => { void runLifecycle(moveToTrash); }}
              onRestore={() => { void runLifecycle(restoreFromTrash); }}
              onCancel={selectionState.cancel}
            />
          )}
        </>
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

      <AddToAlbumSheet
        visible={sheetVisible}
        onClose={() => setSheetVisible(false)}
        onCompleted={(result) => {
          if (result === null || result.succeeded > 0) selectionState.clear();
        }}
        fileItemIds={[...selectionState.ids]}
      />
    </Screen>
  );
}

