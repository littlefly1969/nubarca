// Photos tab: the whole owner photo library, newest first, cursor-paginated.
// Opens the shared viewer; long-press starts multi-select for bulk add-to-album.
import React, { useCallback, useState } from 'react';
import { Pressable, StyleSheet, View } from 'react-native';
import { Redirect, router, useFocusEffect } from 'expo-router';
import { Ionicons } from '@expo/vector-icons';
import { Screen, AppHeader, HeaderButton } from '../../src/ui/components';
import {
  EmptyState,
  ErrorState,
  LoadingState,
  PrimaryButton,
} from '../../src/ui/states';
import { MediaGrid } from '../../src/components/MediaGrid';
import { AddToAlbumSheet } from '../../src/components/AddToAlbumSheet';
import { useSession } from '../../src/session/SessionProvider';
import { useViewer } from '../../src/media/viewerContext';
import { usePagedList } from '../../src/lib/usePagedList';
import { useSelectionState } from '../../src/lib/useSelectionState';
import { listMedia } from '../../src/api/media.ts';
import type { MediaItem } from '../../src/api/media.ts';
import { colors } from '../../src/ui/tokens';
import { useI18n } from '../../src/i18n';

const PAGE_SIZE = 60;

export default function Photos(): React.JSX.Element {
  const session = useSession();
  const { t } = useI18n();
  const viewer = useViewer();
  const selectionState = useSelectionState();
  const [sheetVisible, setSheetVisible] = useState(false);

  const fetcher = useCallback(
    async (cursor: string | null, signal: AbortSignal) => {
      return await listMedia(
        {
          kind: 'image',
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

  // Refresh on each focus so changes made elsewhere are reflected; the
  // PagedList token makes this race-safe against in-flight loads.
  useFocusEffect(
    useCallback(() => {
      if (session.status === 'authed') void refresh();
      return undefined;
    }, [refresh, fetcher, session.status]),
  );

  if (session.status !== 'authed') {
    return <Redirect href="/login" />;
  }

  const openViewer = (item: MediaItem): void => {
    viewer.open(snapshot.items, item.id);
    router.push(`/media/${item.id}`);
  };

  return (
    <Screen style={styles.screen}>
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
              <Pressable
                accessibilityRole="button"
                accessibilityLabel={t('selection.select')}
                onPress={() => selectionState.begin()}
                style={({ pressed }) => [styles.iconBtn, pressed && styles.pressed]}
                hitSlop={4}
              >
                <Ionicons name="checkmark-circle-outline" size={22} color={colors.accent} />
              </Pressable>
              <Pressable
                accessibilityRole="button"
                accessibilityLabel={t('common.signOut')}
                onPress={() => {
                  void session.logout();
                }}
                style={({ pressed }) => [styles.iconBtn, pressed && styles.pressed]}
                hitSlop={4}
              >
                <Ionicons name="log-out-outline" size={22} color={colors.accent} />
              </Pressable>
            </>
          )
        }
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
          {selectionState.selecting && (
            <View style={styles.actionBar}>
              <PrimaryButton
                label={`${t('selection.addToAlbum')} (${selectionState.count})`}
                onPress={() => setSheetVisible(true)}
              />
            </View>
          )}
        </>
      )}

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

const styles = StyleSheet.create({
  screen: { backgroundColor: '#F5F7FB' },
  iconBtn: {
    width: 40,
    height: 40,
    alignItems: 'center',
    justifyContent: 'center',
  },
  pressed: { opacity: 0.7 },
  actionBar: {
    position: 'absolute',
    left: 16,
    right: 16,
    bottom: 16,
  },
});
