// Album detail: one coherent mixed photo/video surface.
// Membership removal NEVER deletes files — bulk DELETE hits
// /api/albums/{id}/items/bulk only, and the success notice says the files stay.
import React, { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, Pressable, StyleSheet } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { Redirect, router, useLocalSearchParams, useFocusEffect } from 'expo-router';
import { AppHeader, HeaderButton } from '../../src/ui/components';
import { ImmersiveGalleryShell } from '../../src/ui/ImmersiveGalleryShell';
import { OverflowMenu } from '../../src/components/OverflowMenu';
import { PartySettingsSheet } from '../../src/components/PartySettingsSheet';
import { AlbumSharingSheet } from '../../src/components/AlbumSharingSheet';
import {
  EmptyState,
  ErrorState,
  LoadingState,
} from '../../src/ui/states';
import { MediaGrid } from '../../src/components/MediaGrid';
import { NamePromptModal } from '../../src/components/NamePromptModal';
import { useSession } from '../../src/session/SessionProvider';
import { useViewer } from '../../src/media/viewerContext';
import { useReturnAnchor } from '../../src/media/useReturnAnchor';
import { ownedSlides } from '../../src/media/viewerEntries';
import { usePagedList } from '../../src/lib/usePagedList';
import { useSelectionState } from '../../src/lib/useSelectionState';
import { useReportSelectionMode } from '../../src/ui/selectionMode';
import {
  getAlbum,
  updateAlbum,
  setAlbumTvVisibility,
  deleteAlbum,
  bulkRemoveAlbumItems,
} from '../../src/api/albums.ts';
import type { AlbumDetail } from '../../src/api/albums.ts';
import type { MediaItem } from '../../src/api/media.ts';
import { useMediaFilters } from '../../src/media/useMediaFilters';
import { MediaFilterChips } from '../../src/components/MediaFilterChips';
import { MediaFilterSheet } from '../../src/components/MediaFilterSheet';
import { shouldRefreshOnFocus } from '../../src/lib/focusRefresh';
import { useI18n } from '../../src/i18n';
import { useColors } from '../../src/ui/theme';

const PAGE_SIZE = 60;

export default function AlbumDetail(): React.JSX.Element {
  const colors = useColors();
  const session = useSession();
  const { t } = useI18n();
  const viewer = useViewer();
  const returnAnchor = useReturnAnchor();
  const params = useLocalSearchParams<{ id: string }>();
  const albumId = params.id;
  const selectionState = useSelectionState();
  // The bottom navigation steps aside while this is on.
  useReportSelectionMode(selectionState.selecting);
  const [detail, setDetail] = useState<AlbumDetail | null>(null);
  const [detailFailed, setDetailFailed] = useState(false);
  const [renaming, setRenaming] = useState(false);
  const [partyOpen, setPartyOpen] = useState(false);
  const [sharingOpen, setSharingOpen] = useState(false);
  const [filtersOpen, setFiltersOpen] = useState(false);

  // The SAME filter model the library tabs use, told it is browsing an album.
  // The shared model already knows what that means: album membership stops
  // being offered (every item is a member) and a visual search is confined to
  // this album rather than answered from the whole library.
  const filters = useMediaFilters('all', PAGE_SIZE, { kind: 'album', albumId });
  const { snapshot, refresh, loadMore, retryFailed } =
    usePagedList<MediaItem>((i) => i.id, filters.fetchPage);

  const itemCountRef = useRef(0);
  itemCountRef.current = snapshot.items.length;
  useFocusEffect(
    useCallback(() => {
      if (session.status !== 'authed') return;
      let cancelled = false;
      // Same rule as the library tabs: a refresh replaces the accumulator with
      // page one, so it is for a list that has nothing to lose.
      if (shouldRefreshOnFocus({ itemCount: itemCountRef.current, stale: false })) {
        void refresh();
      }
      void getAlbum(albumId).then(
        (d) => {
          if (!cancelled) setDetail(d);
        },
        () => {
          if (!cancelled) setDetailFailed(true);
        },
      );
      return () => {
        cancelled = true;
      };
    }, [session.status, albumId, refresh]),
  );

  // A committed filter change is a new query generation: cursor dropped,
  // accumulator cleared, selection released because its items may be gone.
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

  function removeSelected(): void {
    void (async () => {
      try {
        const result = await bulkRemoveAlbumItems(albumId, [...selectionState.ids]);
        selectionState.clear();
        await refresh();
        Alert.alert(
          detail?.name ?? '',
          t('albumDetail.removedNotice', {
            count: result.succeeded + result.skipped,
          }),
        );
      } catch {
        Alert.alert(
          detail?.name ?? '',
          t('gallery.loadErrorNetwork', { what: t('tabs.albums') }),
        );
      }
    })();
  }

  function confirmDelete(): void {
    const name = detail?.name ?? '';
    Alert.alert(t('albums.deleteConfirmTitle'), t('albums.deleteConfirmBody', { name }), [
      { text: t('albums.cancel'), style: 'cancel' },
      {
        text: t('albums.delete'),
        style: 'destructive',
        onPress: () => {
          void (async () => {
            // Deletes the ALBUM ONLY — membership rows. Files stay.
            try {
              await deleteAlbum(albumId);
              router.back();
            } catch {
              Alert.alert(
                name,
                t('gallery.loadErrorNetwork', { what: t('albums.delete') }),
              );
            }
          })();
        },
      },
    ]);
  }

  const title = detail?.name ?? '';

  return (
    // A pushed route, so nothing floats at the bottom — but the media gallery
    // behaves exactly as it does in the library (NUBARCA-UX-01 §9).
    <ImmersiveGalleryShell
      topChrome={
        <>
          <AppHeader
                title={title}
            actions={
              selectionState.selecting ? (
                <>
                  <HeaderButton
                    label={t('albumDetail.removeSelected', { count: selectionState.count })}
                    destructive
                    onPress={removeSelected}
                  />
                  <HeaderButton
                    label={t('albumDetail.cancelSelection')}
                    onPress={selectionState.cancel}
                  />
                </>
              ) : (
                /* TWO primary actions, everything else in the overflow. Six text
                   buttons ran off the edge of a phone, which is how the Party
                   screen — and the whole message-moderation surface behind it —
                   became unreachable while being fully implemented. */
                <>
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
                  <HeaderButton
                    label={t('albumDetail.addMedia')}
                    onPress={() => router.push(`/album/${albumId}/add`)}
                  />
                  <OverflowMenu
                    actions={[
                      {
                        id: 'party',
                        label: t('party.open'),
                        icon: 'sparkles-outline',
                        onPress: () => setPartyOpen(true),
                      },
                      {
                        id: 'sharing',
                        label: t('sharing.open'),
                        icon: 'person-add-outline',
                        onPress: () => setSharingOpen(true),
                      },
                      {
                        id: 'tv',
                        label: t('albums.showOnTv'),
                        icon: 'tv-outline',
                        onPress: () => {
                          // TV visibility has its OWN route, so toggling it can
                          // never carry an unintended rename along with it.
                          void (async () => {
                            if (detail === null) return;
                            try {
                              setDetail(await setAlbumTvVisibility(albumId, !detail.showOnTv));
                            } catch {
                              /* the header keeps showing the last known state */
                            }
                          })();
                        },
                      },
                      {
                        id: 'edit',
                        label: t('albums.edit'),
                        icon: 'create-outline',
                        onPress: () => setRenaming(true),
                      },
                      {
                        id: 'delete',
                        label: t('albums.delete'),
                        icon: 'trash-outline',
                        destructive: true,
                        onPress: confirmDelete,
                      },
                    ]}
                  />
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
        </>
      }
    >
      {(scroll) => (
        <>

        {snapshot.phase === 'loading' && detail === null ? (
          <LoadingState />
        ) : snapshot.phase === 'error' && snapshot.items.length === 0 ? (
          <ErrorState
            title={t('grid.errorTitle')}
            message={
              detailFailed
                ? t('gallery.loadErrorNetwork', { what: t('tabs.albums') })
                : null
            }
            onRetry={() => {
              void refresh();
            }}
          />
        ) : snapshot.items.length === 0 ? (
          <EmptyState icon="🖼" title={t('albumDetail.empty')} hint={t('albumDetail.emptyHint')} />
        ) : (
          <MediaGrid
            items={snapshot.items}
            selecting={selectionState.selecting}
            selectedIds={selectionState.ids}
            onPressItem={(item) => {
              viewer.open(ownedSlides(snapshot.items), item.id);
              router.push(`/media/${item.id}`);
            }}
            onToggleSelect={selectionState.toggle}
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
            anchorItemId={returnAnchor.itemId}
            onAnchorConsumed={returnAnchor.consume}
          onScroll={scroll.onScroll}
            scrollEventThrottle={scroll.scrollEventThrottle}
            contentPaddingTop={scroll.contentPaddingTop}
            contentPaddingBottom={scroll.contentPaddingBottom}
          />
        )}

        <AlbumSharingSheet
          albumId={albumId}
          visible={sharingOpen}
          onClose={() => setSharingOpen(false)}
        />

        <PartySettingsSheet
          albumId={albumId}
          visible={partyOpen}
          onClose={() => setPartyOpen(false)}
        />

        <MediaFilterSheet
          visible={filtersOpen}
          identity={filters.identity}
          onApply={(next, sort, direction) => {
            filters.apply(next, sort, direction);
            setFiltersOpen(false);
          }}
          onClose={() => setFiltersOpen(false)}
        />

        <NamePromptModal
          visible={renaming}
          title={t('albums.rename')}
          initialName={title}
          initialDescription={detail?.description ?? ''}
          withDescription
          onCancel={() => setRenaming(false)}
          onSubmit={async (name, description) => {
            const updated = await updateAlbum(albumId, name, description);
            setDetail(updated);
            setRenaming(false);
            await refresh();
          }}
        />
        </>
      )}
    </ImmersiveGalleryShell>
  );
}


const styles = StyleSheet.create({
  iconBtn: { width: 40, height: 40, alignItems: 'center', justifyContent: 'center' },
  pressed: { opacity: 0.7 },
});
