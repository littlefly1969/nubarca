// Album add-picker: the whole library grid in multi-select mode; ONE bulk add
// on confirm, then back to the album.
import React, { useCallback } from 'react';
import { Alert, StyleSheet, View } from 'react-native';
import { Redirect, router, useLocalSearchParams, useFocusEffect } from 'expo-router';
import { Screen, AppHeader, HeaderButton } from '../../../src/ui/components';
import {
  EmptyState,
  ErrorState,
  LoadingState,
  PrimaryButton,
} from '../../../src/ui/states';
import { MediaGrid } from '../../../src/components/MediaGrid';
import { useSession } from '../../../src/session/SessionProvider';
import { usePagedList } from '../../../src/lib/usePagedList';
import { useSelectionState } from '../../../src/lib/useSelectionState';
import { bulkAddAlbumItems } from '../../../src/api/albums.ts';
import { listMedia } from '../../../src/api/media.ts';
import type { MediaItem } from '../../../src/api/media.ts';
import { spacing } from '../../../src/ui/tokens';
import { useI18n } from '../../../src/i18n';

const PAGE_SIZE = 60;

export default function AlbumAdd(): React.JSX.Element {
  const session = useSession();
  const { t } = useI18n();
  const params = useLocalSearchParams<{ id: string }>();
  const albumId = params.id;
  const selectionState = useSelectionState();

  const fetcher = useCallback(
    async (cursor: string | null, signal: AbortSignal) => {
      return await listMedia(
        { kind: 'all', sort: 'datetaken', direction: 'desc', limit: PAGE_SIZE, cursor },
        signal,
      );
    },
    [],
  );

  const { snapshot, refresh, loadMore, retryFailed } = usePagedList<MediaItem>((i) => i.id, fetcher);

  useFocusEffect(
    useCallback(() => {
      if (session.status === 'authed') void refresh();
      return undefined;
    }, [refresh, fetcher, session.status]),
  );

  if (session.status !== 'authed') {
    return <Redirect href="/login" />;
  }

  function addSelected(): void {
    void (async () => {
      try {
        const result = await bulkAddAlbumItems(albumId, [...selectionState.ids]);
        Alert.alert(
          t('selection.addToAlbum'),
          t('selection.addedNotice', {
            succeeded: result.succeeded,
            skipped: result.skipped,
          }),
        );
        router.back();
      } catch {
        Alert.alert(
          t('selection.addToAlbum'),
          t('gallery.loadErrorNetwork', { what: t('tabs.albums') }),
        );
      }
    })();
  }

  return (
    <Screen>
      <AppHeader
        title={t('albumDetail.addMedia')}
        actions={
          selectionState.selecting ? (
            <HeaderButton label={t('albums.cancel')} onPress={selectionState.cancel} />
          ) : undefined
        }
      />

      {snapshot.phase === 'loading' ? (
        <LoadingState />
      ) : snapshot.phase === 'error' && snapshot.items.length === 0 ? (
        <ErrorState
          title={t('grid.errorTitle')}
          onRetry={() => {
            void refresh();
          }}
        />
      ) : snapshot.items.length === 0 ? (
        <EmptyState title={t('grid.emptyPhotos')} hint={t('grid.emptyHint')} />
      ) : (
        <>
          <View style={styles.gridWrap}>
            <MediaGrid
              items={snapshot.items}
              selecting
              selectedIds={selectionState.ids}
              onPressItem={() => undefined}
              onToggleSelect={selectionState.toggle}
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
          </View>
          <View style={styles.actionBar}>
            <PrimaryButton
              label={`${t('selection.addToAlbum')} (${selectionState.count})`}
              disabled={selectionState.count === 0}
              onPress={addSelected}
            />
          </View>
        </>
      )}
    </Screen>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  gridWrap: { flex: 1 },
  actionBar: {
    padding: spacing.l,
  },
});
