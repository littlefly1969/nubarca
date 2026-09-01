// Album detail: one coherent mixed photo/video surface.
// Membership removal NEVER deletes files — bulk DELETE hits
// /api/albums/{id}/items/bulk only, and the success notice says the files stay.
import React, { useCallback, useState } from 'react';
import { Alert } from 'react-native';
import { Redirect, router, useLocalSearchParams, useFocusEffect } from 'expo-router';
import { Screen, AppHeader, HeaderButton } from '../../src/ui/components';
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
import { ownedSlides } from '../../src/media/viewerEntries';
import { usePagedList } from '../../src/lib/usePagedList';
import { useSelectionState } from '../../src/lib/useSelectionState';
import {
  getAlbum,
  updateAlbum,
  setAlbumTvVisibility,
  deleteAlbum,
  bulkRemoveAlbumItems,
} from '../../src/api/albums.ts';
import type { AlbumDetail } from '../../src/api/albums.ts';
import { listAlbumMedia } from '../../src/api/media.ts';
import type { MediaItem } from '../../src/api/media.ts';
import { useI18n } from '../../src/i18n';

const PAGE_SIZE = 60;

export default function AlbumDetail(): React.JSX.Element {
  const session = useSession();
  const { t } = useI18n();
  const viewer = useViewer();
  const params = useLocalSearchParams<{ id: string }>();
  const albumId = params.id;
  const selectionState = useSelectionState();
  const [detail, setDetail] = useState<AlbumDetail | null>(null);
  const [detailFailed, setDetailFailed] = useState(false);
  const [renaming, setRenaming] = useState(false);
  const [partyOpen, setPartyOpen] = useState(false);
  const [sharingOpen, setSharingOpen] = useState(false);

  const fetcher = useCallback(
    async (cursor: string | null, signal: AbortSignal) => {
      return await listAlbumMedia(
        albumId,
        { kind: 'all', sort: 'datetaken', direction: 'desc', limit: PAGE_SIZE, cursor },
        signal,
      );
    },
    [albumId],
  );

  const { snapshot, refresh, loadMore, retryFailed } = usePagedList<MediaItem>((i) => i.id, fetcher);

  useFocusEffect(
    useCallback(() => {
      if (session.status !== 'authed') return;
      let cancelled = false;
      void refresh();
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
    }, [session.status, albumId, refresh, fetcher]),
  );

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
    <Screen>
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
            <>
              <HeaderButton label={t('sharing.open')} onPress={() => setSharingOpen(true)} />
              <HeaderButton label={t('party.open')} onPress={() => setPartyOpen(true)} />
              <HeaderButton
                label={t('albums.showOnTv')}
                onPress={() => {
                  // TV visibility has its OWN route, so toggling it can never
                  // carry an unintended rename along with it.
                  void (async () => {
                    if (detail === null) return;
                    try {
                      setDetail(await setAlbumTvVisibility(albumId, !detail.showOnTv));
                    } catch {
                      /* the header keeps showing the last known state */
                    }
                  })();
                }}
              />
              <HeaderButton label={t('albums.edit')} onPress={() => setRenaming(true)} />
              <HeaderButton label={t('albums.delete')} destructive onPress={confirmDelete} />
              <HeaderButton
                label={t('albumDetail.addMedia')}
                onPress={() => router.push(`/album/${albumId}/add`)}
              />
            </>
          )
        }
      />

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
          onLongPressItem={(item) => {
            selectionState.begin();
            selectionState.toggle(item.id);
          }}
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
    </Screen>
  );
}

