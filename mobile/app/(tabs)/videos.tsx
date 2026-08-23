// Videos tab: poster-first grid with play affordances and duration badges.
// Tapping a tile opens the shared media route, which becomes the native player.
import React, { useCallback } from 'react';
import { Redirect, useFocusEffect } from 'expo-router';
import { Screen, AppHeader } from '../../src/ui/components';
import { EmptyState, ErrorState, LoadingState } from '../../src/ui/states';
import { MediaGrid } from '../../src/components/MediaGrid';
import { useSession } from '../../src/session/SessionProvider';
import { useViewer } from '../../src/media/viewerContext';
import { usePagedList } from '../../src/lib/usePagedList';
import { listMedia } from '../../src/api/media.ts';
import type { MediaItem } from '../../src/api/media.ts';
import { router } from 'expo-router';
import { useI18n } from '../../src/i18n';

const PAGE_SIZE = 60;

export default function Videos(): React.JSX.Element {
  const session = useSession();
  const { t } = useI18n();
  const viewer = useViewer();

  const fetcher = useCallback(
    async (cursor: string | null, signal: AbortSignal) => {
      return await listMedia(
        {
          kind: 'video',
          sort: 'datetaken',
          direction: 'desc',
          limit: PAGE_SIZE,
          cursor,
        },
        signal,
      );
    },
    [],
  );

  const { snapshot, refresh, loadMore } = usePagedList<MediaItem>((i) => i.id, fetcher);

  useFocusEffect(
    useCallback(() => {
      if (session.status === 'authed') void refresh();
      return undefined;
    }, [refresh, fetcher, session.status]),
  );

  if (session.status !== 'authed') {
    return <Redirect href="/login" />;
  }

  const openPlayer = (item: MediaItem): void => {
    viewer.open(snapshot.items, item.id);
    router.push(`/media/${item.id}`);
  };

  return (
    <Screen>
      <AppHeader title={t('tabs.videos')} />

      {snapshot.phase === 'loading' ? (
        <LoadingState />
      ) : snapshot.phase === 'error' && snapshot.items.length === 0 ? (
        <ErrorState
          title={t('grid.errorTitle')}
          message={t('gallery.loadErrorNetwork', { what: t('gallery.whatFolder') })}
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
            void loadMore();
          }}
        />
      )}
    </Screen>
  );
}
