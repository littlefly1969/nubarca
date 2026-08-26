// Shared-album contribution picker: the caller's OWN library in multi-select
// mode; ONE bulk contribution on confirm. Linking only — no copy, no ownership
// transfer, counts-only result (never which ids were skipped).
import React, { useCallback } from 'react';
import { Alert, StyleSheet, View } from 'react-native';
import { Redirect, router, useFocusEffect, useLocalSearchParams } from 'expo-router';
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
import { bulkContributeToSharedAlbum } from '../../../src/api/sharedAlbums.ts';
import { listMedia } from '../../../src/api/media.ts';
import type { MediaItem } from '../../../src/api/media.ts';
import { spacing } from '../../../src/ui/tokens';
import { useI18n } from '../../../src/i18n';

const PAGE_SIZE = 60;

export default function SharedAlbumAdd(): React.JSX.Element {
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

  const { snapshot, refresh, loadMore, retryFailed } = usePagedList<MediaItem>(
    (i) => i.id,
    fetcher,
  );

  useFocusEffect(
    useCallback(() => {
      if (session.status === 'authed') void refresh();
      return undefined;
    }, [refresh, fetcher, session.status]),
  );

  if (session.status !== 'authed') {
    return <Redirect href="/login" />;
  }

  function contributeSelected(): void {
    void (async () => {
      try {
        const result = await bulkContributeToSharedAlbum(albumId, [...selectionState.ids]);
        Alert.alert(
          t('shared.contribute'),
          t('selection.addedNotice', {
            succeeded: result.succeeded,
            skipped: result.skipped,
          }),
        );
        router.back();
      } catch {
        Alert.alert(
          t('shared.contribute'),
          t('gallery.loadErrorNetwork', { what: t('shared.contribute') }),
        );
      }
    })();
  }

  return (
    <Screen>
      <AppHeader
        title={t('shared.contribute')}
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
            void retryFailed();
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
              label={`${t('shared.contribute')} (${selectionState.count})`}
              disabled={selectionState.count === 0}
              onPress={contributeSelected}
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
