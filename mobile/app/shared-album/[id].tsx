// /shared-album/[id]: the RECIPIENT's view of one album shared with them.
//
// AUTHORITY RULE (acceptance-critical): thumbnails, previews, posters, video
// and downloads use EXCLUSIVELY the server-provided album-scoped URLs carried
// by SharedAlbumItem. Nothing here ever constructs /api/files/{fileId}/… for
// shared media — that family is owner-only, and hand-building one would be a
// privacy hole rather than a shortcut.
//
// v1 surface: detail header, Tutto/Foto/Video filters, cursor pagination,
// photo viewer, video playback, download when item.downloadUrl != null and
// the membership grants it, withdrawal of OWN contributions whenever
// item.canWithdraw === true (even after a role downgrade to Viewer).

import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  Pressable,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import * as FileSystem from 'expo-file-system/legacy';
import * as Sharing from 'expo-sharing';
import { Redirect, router, useFocusEffect, useLocalSearchParams } from 'expo-router';
import { Ionicons } from '@expo/vector-icons';
import { AppHeader, IconButton } from '../../src/ui/components';
import { ImmersiveGalleryShell } from '../../src/ui/ImmersiveGalleryShell';
import { AccountButton } from '../../src/ui/AccountButton';
import { iconSizes } from '../../src/ui/tokens';
import { EmptyState, ErrorState, LoadingState } from '../../src/ui/states';
import { AuthedImage } from '../../src/components/AuthedImage';
import { useSession } from '../../src/session/SessionProvider';
import { useViewer } from '../../src/media/viewerContext';
import { useReturnAnchor } from '../../src/media/useReturnAnchor';
import { GalleryList } from '../../src/components/GalleryList';
import { sharedSlides } from '../../src/media/viewerEntries';
import { authenticatedSource } from '../../src/media/imageSource';
import { usePagedList } from '../../src/lib/usePagedList';
import { getAlbumExperienceCapabilities } from '../../src/albums/albumCapabilities';
import {
  getSharedAlbum,
  listSharedAlbumItems,
  withdrawSharedAlbumContribution,
  type SharedAlbumDetail,
  type SharedAlbumItem,
} from '../../src/api/sharedAlbums.ts';
import {
  makeSharedDownloadOperationId,
  runSharedAlbumOriginalDownload,
  type SharedDownloadIo,
} from '../../src/media/sharedDownload.ts';
import { radii, spacing, touch } from '../../src/ui/tokens';
import { useI18n } from '../../src/i18n';
import { themed, useColors } from '../../src/ui/theme';
import { media } from '../../src/ui/palette.ts';

const PAGE_SIZE = 60;
type Kind = 'all' | 'image' | 'video';

// The real expo binding of the shared-download seam: the SAME legacy
// filesystem API and session-cookie headers the route used before, now
// handed to runSharedAlbumOriginalDownload so its lifecycle stays testable.
const sharedDownloadIo: SharedDownloadIo = {
  cacheDirectory: FileSystem.cacheDirectory ?? '',
  makeOperationId: makeSharedDownloadOperationId,
  makeDirectoryAsync: (path, options) => FileSystem.makeDirectoryAsync(path, options),
  downloadAsync: async (uri, targetUri, options) => {
    const res = await FileSystem.downloadAsync(uri, targetUri, options);
    return {
      status: res.status,
      headers: res.headers as Record<string, string | string[] | undefined>,
    };
  },
  moveAsync: ({ from, to }) => FileSystem.moveAsync({ from, to }),
  deleteAsync: (path, options) => FileSystem.deleteAsync(path, options),
  shareAsync: (uri, options) => Sharing.shareAsync(uri, options),
};

export default function SharedAlbum(): React.JSX.Element {
  const styles = useStyles();
  const colors = useColors();
  const session = useSession();
  const { t } = useI18n();
  const params = useLocalSearchParams<{ id: string }>();
  const albumId = params.id;
  const [detail, setDetail] = useState<SharedAlbumDetail | null>(null);
  const [detailFailed, setDetailFailed] = useState(false);
  const [kind, setKind] = useState<Kind>('all');
  const [busyItem, setBusyItem] = useState<string | null>(null);
  const viewer = useViewer();
  const galleryScope = `shared-album:${albumId}`;
  // Stable: an inline arrow here rebuilds the gallery's position identity on
  // every render, and with it the scroll handler the list is holding.
  const keyOfItem = useCallback((item: SharedAlbumItem) => item.albumItemId, []);
  const returnAnchor = useReturnAnchor(galleryScope);

  const capabilities = useMemo(
    () =>
      getAlbumExperienceCapabilities({
        ownership: 'member',
        role: detail?.role ?? null,
        canEdit: detail?.canEdit ?? false,
        allowOriginalDownload: detail?.allowOriginalDownload ?? false,
      }),
    [detail],
  );

  const fetcher = useCallback(
    async (cursor: string | null, signal: AbortSignal) => {
      const page = await listSharedAlbumItems(albumId, { kind, cursor, limit: PAGE_SIZE }, signal);
      // The shared contract carries nextCursor WITHOUT a hasMore flag; null
      // means "that was the last page".
      return {
        items: page.items,
        nextCursor: page.nextCursor,
        hasMore: page.nextCursor !== null,
      };
    },
    [albumId, kind],
  );

  const { snapshot, refresh, loadMore, retryFailed } = usePagedList<SharedAlbumItem>(
    (i) => i.albumItemId,
    fetcher,
  );

  useFocusEffect(
    useCallback(() => {
      if (session.status !== 'authed') return;
      let cancelled = false;
      void refresh();
      void getSharedAlbum(albumId).then(
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
    }, [session.status, albumId, kind, refresh]),
  );

  useEffect(() => {
    // A kind switch restarts pagination for THAT slice; the hook's immediate
    // sync shows the loading phase right away.
    void refresh();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [kind]);

  if (session.status !== 'authed') {
    return <Redirect href="/login" />;
  }

  function openItem(item: SharedAlbumItem): void {
    // Slides carry the SERVER-PROVIDED album-scoped URLs as-is.
    viewer.open(sharedSlides(snapshot.items), item.albumItemId, galleryScope);
    router.push(`/media/${item.albumItemId}`);
  }

  function withdrawItem(item: SharedAlbumItem): void {
    Alert.alert(
      t('shared.withdrawTitle'),
      t('shared.withdrawConfirmBody'),
      [
        { text: t('albums.cancel'), style: 'cancel' },
        {
          text: t('shared.withdrawAction'),
          style: 'destructive',
          onPress: () => {
            setBusyItem(item.albumItemId);
            void (async () => {
              try {
                await withdrawSharedAlbumContribution(albumId, item.fileItemId);
                await refresh();
                Alert.alert(t('shared.withdrawDone'));
              } catch {
                Alert.alert(t('shared.withdrawFailed'));
              } finally {
                setBusyItem(null);
              }
            })();
          },
        },
      ],
    );
  }

  async function downloadItem(item: SharedAlbumItem): Promise<void> {
    if (item.downloadUrl === null) return; // contract: no URL, no control
    const src = authenticatedSource(item.downloadUrl);
    if (!src) return;
    setBusyItem(item.albumItemId);
    try {
      // FILE-NATIVE download (acceptance blocker): the ORIGINAL bytes go
      // straight to disk via expo's downloader with the session cookie —
      // they NEVER pass through the JS heap or a base64 expansion, so even a
      // multi-hundred-MB original cannot OOM the app.
      //
      // The orchestrator owns the whole lifecycle inside ONE unique
      // per-operation cache directory and deletes it on EVERY exit path
      // (share done/dismissed, share failure, download/move failure), so a
      // private original never outlives the operation on this device.
      await runSharedAlbumOriginalDownload(sharedDownloadIo, {
        source: src,
        kindFallbackExtension: item.kind === 'video' ? 'mp4' : 'jpg',
        dialogTitle: t('shared.download'),
      });
    } catch {
      Alert.alert(t('shared.downloadFailed'));
    } finally {
      setBusyItem(null);
    }
  }

  function itemActions(item: SharedAlbumItem): void {
    const buttons: Array<{ text: string; style?: 'cancel' | 'destructive' | 'default'; onPress?: () => void }> = [
      { text: t('albums.cancel'), style: 'cancel' },
    ];
    if (capabilities.download && item.downloadUrl !== null) {
      buttons.push({ text: t('shared.download'), onPress: () => void downloadItem(item) });
    }
    if (item.canWithdraw && capabilities.withdrawOwnContribution) {
      buttons.push({ text: t('shared.withdrawAction'), style: 'destructive', onPress: () => withdrawItem(item) });
    }
    if (buttons.length === 1) return;
    Alert.alert(t('shared.itemActions'), undefined, buttons);
  }


  const infoLine = detail
    ? `${t('shared.badgeShared')} ${detail.ownerDisplayName} · ${
        detail.role === 'viewer'
          ? t('shared.roleViewer')
          : detail.role === 'contributor'
            ? t('shared.roleContributor')
            : t('shared.roleEditor')
      } · ${t('shared.itemsCount', { count: snapshot.items.length ? detail.itemCount : 0 })}`
    : '';

  return (
    <ImmersiveGalleryShell
      topChrome={
        <>
          <AppHeader
            title={detail?.name ?? ''}
            actions={
              <>
                {capabilities.contribute && (
                  <IconButton
                    accessibilityLabel={t('shared.contribute')}
                    onPress={() => router.push(`/shared-album/${albumId}/add`)}
                  >
                    <Ionicons name="add-circle-outline" size={iconSizes.l} color={colors.accent} />
                  </IconButton>
                )}
                <AccountButton />
              </>
            }
          />

          <Text style={styles.infoLine} numberOfLines={1}>
            {infoLine}
          </Text>

          {/* Tutto / Foto / Video — collapsible chrome, like every other
              gallery's filter row. */}
          <View style={styles.filters}>
        {(['all', 'image', 'video'] as const).map((k) => (
          <Pressable
            key={k}
            accessibilityRole="button"
            accessibilityState={{ selected: kind === k }}
            onPress={() => setKind(k)}
            style={({ pressed }) => [
              styles.chip,
              pressed && styles.pressed,
              kind === k && styles.chipOn,
            ]}
          >
            <Text style={[styles.chipText, kind === k && styles.chipTextOn]}>
              {k === 'all' ? t('albums.filterAll') : k === 'image' ? t('tabs.photos') : t('tabs.videos')}
            </Text>
          </Pressable>
        ))}
          </View>
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
            message={detailFailed ? t('gallery.loadErrorNetwork', { what: t('tabs.albums') }) : undefined}
            onRetry={() => {
              void retryFailed();
            }}
          />
        ) : snapshot.items.length === 0 ? (
          <EmptyState icon="🖼" title={t('albumDetail.empty')} hint={t('albumDetail.emptyHint')} />
        ) : (
          <GalleryList
            items={snapshot.items}
            keyOf={keyOfItem}
            contentPaddingTop={scroll.contentPaddingTop}
            contentPaddingBottom={scroll.contentPaddingBottom}
            onScroll={scroll.onScroll}
            scrollEventThrottle={scroll.scrollEventThrottle}
            anchorItemId={returnAnchor.itemId}
            onAnchorConsumed={returnAnchor.consume}
            onEndReached={
              snapshot.hasMore
                ? () => {
                    void loadMore();
                  }
                : undefined
            }
            ListFooterComponent={
              snapshot.phase === 'loadingMore' ? (
                <ActivityIndicator color={colors.accent} style={styles.footerSpinner} />
              ) : null
            }
            renderTile={(item) => {
              const busy = busyItem === item.albumItemId;
              return (
                <Pressable
                  accessibilityRole="button"
                  accessibilityLabel={item.kind === 'video' ? t('tabs.videos') : t('gallery.photos')}
                  onPress={() => openItem(item)}
                  onLongPress={() => itemActions(item)}
                  style={({ pressed }) => [styles.tile, pressed && styles.pressed]}
                >
                  <AuthedImage
                    path={item.thumbnailUrl /* SERVER-PROVIDED, album-scoped */}
                    style={styles.tileImg}
                    accessibilityLabel=""
                  />
                  {item.kind === 'video' && (
                    <View style={styles.playBadge} pointerEvents="none">
                      <Ionicons name="play" size={14} color={media.text} />
                    </View>
                  )}
                  {busy && (
                    <View style={styles.busyOverlay} pointerEvents="none">
                      <ActivityIndicator color={media.text} />
                    </View>
                  )}
                  {item.canWithdraw && !busy && (
                    <View style={styles.withdrawDot} pointerEvents="none">
                      <Text style={styles.withdrawDotText}>✎</Text>
                    </View>
                  )}
                </Pressable>
              );
            }}
          />
        )}
        </>
      )}
    </ImmersiveGalleryShell>
  );
}

const useStyles = themed((colors) =>
  StyleSheet.create({
    iconBtn: {
      width: 40,
      height: 40,
      alignItems: 'center',
      justifyContent: 'center',
    },
    pressed: { opacity: 0.7 },
    infoLine: {
      paddingHorizontal: spacing.l,
      paddingBottom: spacing.s,
      fontSize: 12,
      color: colors.textSecondary,
    },
    filters: {
      flexDirection: 'row',
      gap: spacing.s,
      paddingHorizontal: spacing.l,
      paddingBottom: spacing.s,
    },
    chip: {
      borderRadius: radii.m,
      borderWidth: StyleSheet.hairlineWidth,
      borderColor: colors.separator,
      paddingHorizontal: spacing.m,
      minHeight: touch.minSize - 12,
      alignItems: 'center',
      justifyContent: 'center',
      backgroundColor: colors.surface,
    },
    chipOn: { backgroundColor: colors.accentStrong, borderColor: colors.accent },
    chipText: { fontSize: 13, color: colors.textSecondary },
    chipTextOn: { color: colors.textOnAccent, fontWeight: '600' },
    listContent: {
      paddingHorizontal: spacing.l,
      paddingTop: spacing.s,
      paddingBottom: spacing.xl,
    },
    tile: {
      margin: spacing.xs / 2,
      aspectRatio: 1,
      borderRadius: radii.s,
      overflow: 'hidden',
      backgroundColor: colors.tilePlaceholder,
    },
    tileImg: { width: '100%', height: '100%' },
    playBadge: {
      position: 'absolute',
      right: spacing.xs,
      bottom: spacing.xs,
      width: 22,
      height: 22,
      borderRadius: 11,
      backgroundColor: media.scrimStrong,
      alignItems: 'center',
      justifyContent: 'center',
    },
    busyOverlay: {
      ...StyleSheet.absoluteFillObject,
      backgroundColor: media.chrome,
      alignItems: 'center',
      justifyContent: 'center',
    },
    withdrawDot: {
      position: 'absolute',
      left: spacing.xs,
      top: spacing.xs,
      width: 18,
      height: 18,
      borderRadius: 9,
      backgroundColor: colors.accentStrong,
      alignItems: 'center',
      justifyContent: 'center',
    },
    withdrawDotText: { color: media.text, fontSize: 11 },
    footerSpinner: { paddingVertical: spacing.m },
  }),
);
