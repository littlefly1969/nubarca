// Files tab: secondary read-only folder browser with breadcrumbs and paging.
// Images open the viewer; videos (MIME-detected) open the player.
import React, { useCallback, useRef, useState } from 'react';
import { FlatList, Pressable, StyleSheet, Text, View } from 'react-native';
import { Redirect, router, useFocusEffect } from 'expo-router';
import { Ionicons } from '@expo/vector-icons';
import { Screen, AppHeader } from '../../src/ui/components';
import { EmptyState, ErrorState, LoadingState } from '../../src/ui/states';
import { AuthedImage } from '../../src/components/AuthedImage';
import { useSession } from '../../src/session/SessionProvider';
import {
  getDirectoryChildren,
  type FileSummary,
  type FolderSummary,
} from '../../src/api/folders.ts';
import { smallThumbnailPath, posterPath } from '../../src/api/videos.ts';
import { radii, spacing, touch } from '../../src/ui/tokens';
import { useI18n } from '../../src/i18n';
import { themed, useColors } from '../../src/ui/theme.ts';
import { media } from '../../src/ui/palette.ts';

interface Crumb {
  id: string | null;
  name: string;
}

const PAGE_SIZE = 60;

function isVideoFile(file: FileSummary): boolean {
  return file.mimeType.startsWith('video/');
}

export default function Files(): React.JSX.Element {
  const styles = useStyles();
  const colors = useColors();
  const session = useSession();
  const { t } = useI18n();

  const [stack, setStack] = useState<Crumb[]>([{ id: null, name: t('files.breadcrumbHome') }]);
  const [folders, setFolders] = useState<FolderSummary[]>([]);
  const [files, setFiles] = useState<FileSummary[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [hasMore, setHasMore] = useState(false);
  const [loading, setLoading] = useState(true);
  const [failed, setFailed] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [reloadToken, setReloadToken] = useState(0);
  // RACE GUARD (acceptance): every request is bound to the generation of the
  // directory it was opened for. Navigating A → B bumps the generation and
  // aborts the in-flight request; a late page from A can never land under B.
  const dirGenerationRef = useRef(0);

  const current = stack[stack.length - 1];

  // Folder navigation resets the list; loadMore appends a deduped page.
  // reloadToken lets the error-state retry re-run the same load.
  useFocusEffect(
    useCallback(() => {
      if (session.status !== 'authed') return;
      let cancelled = false;
      const generation = ++dirGenerationRef.current;
      const controller = new AbortController();
      setLoading(true);
      setFailed(false);
      void (async () => {
        try {
          const res = await getDirectoryChildren(current.id, { limit: PAGE_SIZE }, controller.signal);
          if (cancelled || generation !== dirGenerationRef.current) return;
          setFolders(res.folders);
          setFiles(res.files);
          setCursor(res.nextCursor ?? null);
          setHasMore(res.hasMore ?? false);
        } catch {
          // Aborts belong to the superseding navigation, not to the user.
          if (!cancelled && !controller.signal.aborted) setFailed(true);
        } finally {
          if (!cancelled && generation === dirGenerationRef.current) setLoading(false);
        }
      })();
      return () => {
        cancelled = true;
        controller.abort();
      };
    }, [current.id, session.status, reloadToken]),
  );

  async function loadMore(): Promise<void> {
    if (!hasMore || loadingMore || cursor === null) return;
    const generation = dirGenerationRef.current;
    const dirId = current.id;
    const requestedCursor = cursor;
    const controller = new AbortController();
    setLoadingMore(true);
    try {
      const res = await getDirectoryChildren(dirId, { limit: PAGE_SIZE, cursor: requestedCursor }, controller.signal);
      // The response is only valid if the user never left this directory.
      if (generation !== dirGenerationRef.current) return;
      setFiles((prev) => {
        const known = new Set(prev.map((f) => f.id));
        return [...prev, ...res.files.filter((f) => !known.has(f.id))];
      });
      setCursor(res.nextCursor ?? null);
      setHasMore(res.hasMore ?? false);
    } catch {
      if (generation === dirGenerationRef.current) setFailed(true);
    } finally {
      if (generation === dirGenerationRef.current) setLoadingMore(false);
    }
  }

  if (session.status !== 'authed') {
    return <Redirect href="/login" />;
  }

  function openFolder(folder: FolderSummary): void {
    setStack((prev) => [...prev, { id: folder.id, name: folder.name }]);
  }

  function popTo(index: number): void {
    setStack((prev) => prev.slice(0, index + 1));
  }

  function openFile(file: FileSummary): void {
    if (isVideoFile(file)) {
      router.push({ pathname: '/media/[id]', params: { id: file.id, kind: 'video', name: file.name } });
    } else if (file.mimeType.startsWith('image/')) {
      router.push({ pathname: '/media/[id]', params: { id: file.id, kind: 'image', name: file.name } });
    }
  }

  const entries: Array<FolderSummary | FileSummary> = [...folders, ...files];

  return (
    <Screen>
      <AppHeader title={t('tabs.files')} />

      <View style={styles.breadcrumbBar}>
        <FlatList
          horizontal
          data={stack}
          keyExtractor={(crumb, i) => `${crumb.id ?? 'root'}-${i}`}
          showsHorizontalScrollIndicator={false}
          renderItem={({ item, index }) => (
            <View style={styles.crumbWrap}>
              {index > 0 && <Text style={styles.crumbSep}>›</Text>}
              <Pressable
                accessibilityRole="button"
                accessibilityLabel={item.name}
                onPress={() => index !== stack.length - 1 && popTo(index)}
              >
                <Text
                  style={index === stack.length - 1 ? styles.crumbCurrent : styles.crumb}
                  numberOfLines={1}
                >
                  {item.name}
                </Text>
              </Pressable>
            </View>
          )}
          contentContainerStyle={styles.breadcrumbContent}
        />
      </View>

      {loading ? (
        <LoadingState />
      ) : failed && entries.length === 0 ? (
        <ErrorState
          title={t('grid.errorTitle')}
          message={t('gallery.loadErrorNetwork', { what: t('gallery.whatFolder') })}
          onRetry={() => setReloadToken((v) => v + 1)}
        />
      ) : (
        <FlatList
          data={entries}
          keyExtractor={(entry) => entry.id}
          renderItem={({ item }) =>
            isFolder(item) ? (
              <Pressable
                accessibilityRole="button"
                accessibilityLabel={(item as FolderSummary).name}
                onPress={() => openFolder(item as FolderSummary)}
                style={({ pressed }) => [styles.folderRow, pressed && styles.pressed]}
              >
                <Ionicons name="folder" size={22} color={colors.accent} />
                <Text style={styles.folderName} numberOfLines={1}>
                  {(item as FolderSummary).name}
                </Text>
                <Ionicons name="chevron-forward" size={20} color={colors.textTertiary} />
              </Pressable>
            ) : (
              <FileRow file={item as FileSummary} onPress={() => openFile(item as FileSummary)} />
            )
          }
          ListEmptyComponent={<EmptyState icon="📂" title={t('gallery.folderEmpty')} />}
          onEndReached={() => {
            void loadMore();
          }}
          onEndReachedThreshold={0.5}
          ListFooterComponent={
            loadingMore ? (
              <Text style={styles.loadingMore}>{t('common.loading')}</Text>
            ) : null
          }
          contentContainerStyle={styles.listContent}
        />
      )}
    </Screen>
  );
}

function isFolder(entry: FolderSummary | FileSummary): boolean {
  return (entry as FileSummary).mimeType === undefined;
}


// One file row: thumbnail/poster + name.
function FileRow({
  file,
  onPress,
}: {
  file: FileSummary;
  onPress: () => void;
}): React.JSX.Element {
  const styles = useStyles();
  const video = isVideoFile(file);
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={file.name}
      onPress={onPress}
      style={({ pressed }) => [styles.fileRow, pressed && styles.pressed]}
    >
      <View>
        <AuthedImage
          path={video ? posterPath(file.id) : smallThumbnailPath(file.id)}
          style={styles.rowThumb}
          accessibilityLabel=""
        />
        {video && (
          <View style={styles.playBadge}>
            <Ionicons name="play" size={12} color={media.text} />
          </View>
        )}
      </View>
      <Text style={styles.fileName} numberOfLines={1} ellipsizeMode="middle">
        {file.name}
      </Text>
    </Pressable>
  );
}

const useStyles = themed((colors) =>
  StyleSheet.create({
    breadcrumbBar: {
      borderBottomWidth: StyleSheet.hairlineWidth,
      borderBottomColor: colors.separator,
      backgroundColor: colors.surface,
    },
    breadcrumbContent: {
      paddingHorizontal: spacing.l,
      paddingBottom: spacing.s,
      alignItems: 'center',
    },
    crumbWrap: { flexDirection: 'row', alignItems: 'center' },
    crumbSep: { color: colors.textTertiary, marginHorizontal: 6 },
    crumb: { color: colors.accent, fontSize: 13, maxWidth: 160 },
    crumbCurrent: {
      color: colors.textPrimary,
      fontWeight: '700',
      fontSize: 13,
      maxWidth: 180,
    },
    listContent: { paddingBottom: spacing.xl },
    loadingMore: {
      textAlign: 'center',
      paddingVertical: spacing.m,
      color: colors.textTertiary,
    },
    folderRow: {
      flexDirection: 'row',
      alignItems: 'center',
      gap: spacing.m,
      paddingVertical: spacing.m,
      paddingHorizontal: spacing.l,
      minHeight: touch.minSize,
    },
    folderName: { flex: 1, fontSize: 15, color: colors.textPrimary },
    fileRow: {
      flexDirection: 'row',
      alignItems: 'center',
      gap: spacing.m,
      paddingVertical: spacing.s,
      paddingHorizontal: spacing.l,
      minHeight: touch.minSize,
    },
    rowThumb: {
      width: 52,
      height: 40,
      borderRadius: radii.s,
      backgroundColor: colors.tilePlaceholder,
    },
    playBadge: {
      position: 'absolute',
      right: 2,
      bottom: 2,
      width: 18,
      height: 18,
      borderRadius: 9,
      backgroundColor: media.scrim,
      alignItems: 'center',
      justifyContent: 'center',
    },
    fileName: { flex: 1, fontSize: 14, color: colors.textPrimary },
    pressed: { opacity: 0.7 },
  }),
);
