import React, { useCallback, useEffect, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  BackHandler,
  FlatList,
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  useWindowDimensions,
  View,
} from 'react-native';
import { clearSession, ApiError } from '../api/client';
import { logoutRequest } from '../api/auth';
import {
  getDirectoryChildren,
  listImages,
  isImage,
  smallThumbnailPath,
  diagnoseThumbnail,
} from '../api/gallery';
import type { FileSummary, FolderSummary, ImageItem } from '../api/gallery';
import { getImageStats } from '../api/imageLoader';
import ImageViewer from '../components/ImageViewer';
import AuthedImage from '../components/AuthedImage';
import { useI18n } from '../i18n';

const PAGE_SIZE = 60;
const GAP = 8;
const TARGET_TILE = 168;

type Mode = 'photos' | 'files';

interface Crumb {
  id: string | null;
  name: string;
}

interface Selection {
  id: string;
  name: string;
}

// Two views:
//   * Photos (default) — ALL of the owner's images via /api/images, paginated
//     independently of folders. This is the real gallery experience; the root
//     folder is often only directories (e.g. the date organizer files photos
//     under Photos/YYYY/…), so a folder-rooted view shows no images up front.
//   * Files — a read-only folder browser (subfolders + the current folder's
//     files) with a breadcrumb trail and up/back navigation.
// Grid uses SMALL thumbnails; the viewer uses the MEDIUM preview; originals are
// never auto-loaded.
export default function GalleryScreen({
  onLogout,
}: {
  onLogout: (opts?: { expired?: boolean }) => void;
}): React.JSX.Element {
  const { t } = useI18n();
  const { width } = useWindowDimensions();
  const columns = Math.max(2, Math.floor(width / TARGET_TILE));
  const tileSize = Math.floor((width - GAP * 2 - GAP * (columns - 1)) / columns);

  const [mode, setMode] = useState<Mode>('photos');

  // Photos-mode state.
  const [photos, setPhotos] = useState<ImageItem[]>([]);
  const [photoCursor, setPhotoCursor] = useState<string | null>(null);
  const [photoHasMore, setPhotoHasMore] = useState(false);

  // Files-mode state.
  const [stack, setStack] = useState<Crumb[]>([{ id: null, name: 'Home' }]);
  const [folders, setFolders] = useState<FolderSummary[]>([]);
  const [files, setFiles] = useState<FileSummary[]>([]);
  const [fileCursor, setFileCursor] = useState<string | null>(null);
  const [fileHasMore, setFileHasMore] = useState(false);

  // Shared request state.
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [loadMoreError, setLoadMoreError] = useState(false);
  const [viewing, setViewing] = useState<Selection | null>(null);
  const [showDiag, setShowDiag] = useState(false);

  const current = stack[stack.length - 1];

  const handleExpired = useCallback(async () => {
    await clearSession();
    onLogout({ expired: true });
  }, [onLogout]);

  const describeError = (err: unknown, what: string): string =>
    err instanceof ApiError
      ? t('gallery.loadErrorHttp', { what, status: err.status })
      : t('gallery.loadErrorNetwork', { what });

  const loadPhotos = useCallback(
    async (opts?: { refresh?: boolean }) => {
      if (opts?.refresh) setRefreshing(true);
      else setLoading(true);
      setError(null);
      setLoadMoreError(false);
      try {
        const res = await listImages({ limit: PAGE_SIZE });
        setPhotos(res.items);
        setPhotoCursor(res.nextCursor);
        setPhotoHasMore(res.hasMore);
      } catch (err) {
        if (err instanceof ApiError && err.status === 401) {
          await handleExpired();
          return;
        }
        setError(describeError(err, t('gallery.whatPhotos')));
      } finally {
        if (opts?.refresh) setRefreshing(false);
        else setLoading(false);
      }
    },
    [handleExpired],
  );

  const loadFolder = useCallback(
    async (folderId: string | null, opts?: { refresh?: boolean }) => {
      if (opts?.refresh) setRefreshing(true);
      else setLoading(true);
      setError(null);
      setLoadMoreError(false);
      try {
        const res = await getDirectoryChildren(folderId, { limit: PAGE_SIZE });
        setFolders(res.folders);
        setFiles(res.files);
        setFileCursor(res.nextCursor ?? null);
        setFileHasMore(res.hasMore ?? false);
      } catch (err) {
        if (err instanceof ApiError && err.status === 401) {
          await handleExpired();
          return;
        }
        setError(describeError(err, t('gallery.whatFolder')));
      } finally {
        if (opts?.refresh) setRefreshing(false);
        else setLoading(false);
      }
    },
    [handleExpired],
  );

  // Load whenever the mode or (in files mode) the current folder changes.
  useEffect(() => {
    if (mode === 'photos') void loadPhotos();
    else void loadFolder(current.id);
  }, [mode, current.id, loadPhotos, loadFolder]);

  const loadMore = useCallback(async () => {
    if (loadingMore) return;
    setLoadMoreError(false);
    try {
      if (mode === 'photos') {
        if (!photoHasMore || photoCursor === null) return;
        setLoadingMore(true);
        const res = await listImages({ limit: PAGE_SIZE, cursor: photoCursor });
        setPhotos((prev) => [...prev, ...res.items]);
        setPhotoCursor(res.nextCursor);
        setPhotoHasMore(res.hasMore);
      } else {
        if (!fileHasMore || fileCursor === null) return;
        setLoadingMore(true);
        const res = await getDirectoryChildren(current.id, {
          limit: PAGE_SIZE,
          cursor: fileCursor,
        });
        setFiles((prev) => [...prev, ...res.files]);
        setFileCursor(res.nextCursor ?? null);
        setFileHasMore(res.hasMore ?? false);
      }
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        await handleExpired();
        return;
      }
      setLoadMoreError(true);
    } finally {
      setLoadingMore(false);
    }
  }, [
    mode,
    loadingMore,
    photoHasMore,
    photoCursor,
    fileHasMore,
    fileCursor,
    current.id,
    handleExpired,
  ]);

  const refresh = useCallback(() => {
    if (mode === 'photos') void loadPhotos({ refresh: true });
    else void loadFolder(current.id, { refresh: true });
  }, [mode, loadPhotos, loadFolder, current.id]);

  const openTile = useCallback((id: string, name: string, image: boolean) => {
    if (image) setViewing({ id, name });
  }, []);

  const openFolder = useCallback((folder: FolderSummary) => {
    setStack((prev) => [...prev, { id: folder.id, name: folder.name }]);
  }, []);

  const goBack = useCallback(() => {
    setStack((prev) => (prev.length > 1 ? prev.slice(0, -1) : prev));
  }, []);

  const jumpTo = useCallback((index: number) => {
    setStack((prev) =>
      index < prev.length - 1 ? prev.slice(0, index + 1) : prev,
    );
  }, []);

  // Android hardware back: close the viewer, else (files mode) go up a folder,
  // else let the OS handle it (exit at the root / photos view).
  useEffect(() => {
    const sub = BackHandler.addEventListener('hardwareBackPress', () => {
      if (viewing !== null) {
        setViewing(null);
        return true;
      }
      if (mode === 'files' && stack.length > 1) {
        goBack();
        return true;
      }
      return false;
    });
    return () => sub.remove();
  }, [viewing, mode, stack.length, goBack]);

  async function handleLogout(): Promise<void> {
    try {
      await logoutRequest();
    } catch {
      // Best-effort: drop the local session even if the server call fails.
    }
    await clearSession();
    onLogout();
  }

  async function runThumbnailDiagnostic(): Promise<void> {
    const firstId =
      mode === 'photos' ? photos[0]?.id : files.find(isImage)?.id;
    if (firstId === undefined) {
      Alert.alert('Thumbnail diagnostic', 'No images here to test.');
      return;
    }
    const { status, verdict } = await diagnoseThumbnail(firstId);
    const s = getImageStats();
    Alert.alert(
      'Thumbnail diagnostic',
      `HTTP status: ${status}\n` +
        `Mode: data-URI fetch (primary on Android)\n\n` +
        `Cache: ${s.cached} held, ${s.inFlight} in flight\n` +
        `Hits: ${s.hits}  Misses: ${s.misses}\n` +
        `Fetches: ${s.fetches}  Failures: ${s.failures}\n\n` +
        verdict,
    );
  }

  const renderPhoto = useCallback(
    ({ item }: { item: ImageItem }) => (
      <Tile id={item.id} name={item.name} image size={tileSize} onOpen={openTile} />
    ),
    [tileSize, openTile],
  );

  const renderFile = useCallback(
    ({ item }: { item: FileSummary }) => (
      <Tile
        id={item.id}
        name={item.name}
        image={isImage(item)}
        size={tileSize}
        onOpen={openTile}
      />
    ),
    [tileSize, openTile],
  );

  const hasImagesHere =
    mode === 'photos' ? photos.length > 0 : files.some(isImage);

  const DiagRow = showDiag && hasImagesHere ? (
    <View style={styles.diagRow}>
      <Pressable
        style={styles.diagBtn}
        onPress={() => void runThumbnailDiagnostic()}
      >
        <Text style={styles.diagText}>🔍 Thumbnail diagnostic</Text>
      </Pressable>
      <Text style={styles.modeBadge}>images: data-URI</Text>
    </View>
  ) : null;

  const FilesHeader = (
    <View>
      {folders.length > 0 && (
        <View style={styles.section}>
          {folders.map((f) => (
            <Pressable
              key={f.id}
              style={styles.folderRow}
              onPress={() => openFolder(f)}
            >
              <Text style={styles.folderIcon}>📁</Text>
              <Text style={styles.folderName} numberOfLines={1}>
                {f.name}
              </Text>
              <Text style={styles.chevron}>›</Text>
            </Pressable>
          ))}
        </View>
      )}
      {DiagRow}
    </View>
  );

  const ListFooter = loadMoreError ? (
    <Pressable style={styles.loadMore} onPress={() => void loadMore()}>
      <Text style={styles.loadMoreErrorText}>
        {t('gallery.loadMoreError')}
      </Text>
    </Pressable>
  ) : (mode === 'photos' ? photoHasMore : fileHasMore) ? (
    <Pressable
      style={styles.loadMore}
      onPress={() => void loadMore()}
      disabled={loadingMore}
    >
      {loadingMore ? (
        <ActivityIndicator color="#1a73e8" />
      ) : (
        <Text style={styles.loadMoreText}>{t('gallery.loadMore')}</Text>
      )}
    </Pressable>
  ) : null;

  const emptyText =
    mode === 'photos'
      ? t('gallery.noPhotos')
      : folders.length === 0
        ? t('gallery.folderEmpty')
        : null;
  const ListEmpty = emptyText ? (
    <View style={styles.emptyWrap}>
      <Text style={styles.emptyIcon}>{mode === 'photos' ? '🖼️' : '🗂️'}</Text>
      <Text style={styles.emptyText}>{emptyText}</Text>
      <Text style={styles.emptyHint}>{t('gallery.pullToRefresh')}</Text>
    </View>
  ) : null;

  return (
    <View style={styles.root}>
      <View style={styles.header}>
        {mode === 'files' && stack.length > 1 ? (
          <Pressable onPress={goBack} hitSlop={12} style={styles.headerBtn}>
            <Text style={styles.headerAction}>{t('common.back')}</Text>
          </Pressable>
        ) : (
          <View style={styles.headerBtn} />
        )}
        <Pressable
          onLongPress={() => setShowDiag((v) => !v)}
          delayLongPress={600}
          style={styles.wordmarkWrap}
        >
          <Text style={styles.wordmark}>NubArca</Text>
        </Pressable>
        <Pressable
          onPress={() => void handleLogout()}
          hitSlop={12}
          style={styles.headerBtn}
        >
          <Text style={[styles.headerAction, styles.headerActionEnd]}>
            {t('common.signOut')}
          </Text>
        </Pressable>
      </View>

      {/* Mode toggle: Photos (all images) vs Files (folder browser). */}
      <View style={styles.segment}>
        <Pressable
          style={[styles.segmentBtn, mode === 'photos' && styles.segmentActive]}
          onPress={() => setMode('photos')}
        >
          <Text
            style={[
              styles.segmentText,
              mode === 'photos' && styles.segmentTextActive,
            ]}
          >
            {t('gallery.photos')}
          </Text>
        </Pressable>
        <Pressable
          style={[styles.segmentBtn, mode === 'files' && styles.segmentActive]}
          onPress={() => setMode('files')}
        >
          <Text
            style={[
              styles.segmentText,
              mode === 'files' && styles.segmentTextActive,
            ]}
          >
            {t('gallery.files')}
          </Text>
        </Pressable>
      </View>

      {/* Breadcrumb — files mode only; shows location and jumps to ancestors. */}
      {mode === 'files' && (
        <View style={styles.breadcrumbBar}>
          <ScrollView
            horizontal
            showsHorizontalScrollIndicator={false}
            contentContainerStyle={styles.breadcrumbContent}
          >
            {stack.map((c, i) => {
              const isLast = i === stack.length - 1;
              return (
                <View key={`${c.id ?? 'root'}-${i}`} style={styles.crumbWrap}>
                  {i > 0 && <Text style={styles.crumbSep}>›</Text>}
                  <Pressable onPress={() => jumpTo(i)} disabled={isLast} hitSlop={6}>
                    <Text
                      style={[styles.crumb, isLast && styles.crumbCurrent]}
                      numberOfLines={1}
                    >
                      {i === 0 ? t('gallery.home') : c.name}
                    </Text>
                  </Pressable>
                </View>
              );
            })}
          </ScrollView>
        </View>
      )}

      {loading ? (
        <View style={styles.centered}>
          <ActivityIndicator size="large" color="#1a73e8" />
          <Text style={styles.centeredHint}>{t('common.loading')}</Text>
        </View>
      ) : error !== null ? (
        <View style={styles.centered}>
          <Text style={styles.errorText}>{error}</Text>
          <Pressable style={styles.retryBtn} onPress={refresh}>
            <Text style={styles.retryText}>{t('common.retry')}</Text>
          </Pressable>
        </View>
      ) : mode === 'photos' ? (
        <FlatList
          key={`photos-cols-${columns}`}
          data={photos}
          renderItem={renderPhoto}
          keyExtractor={(item) => item.id}
          numColumns={columns}
          columnWrapperStyle={{ gap: GAP }}
          contentContainerStyle={styles.listContent}
          ListHeaderComponent={DiagRow}
          ListFooterComponent={ListFooter}
          ListEmptyComponent={ListEmpty}
          refreshing={refreshing}
          onRefresh={refresh}
          initialNumToRender={columns * 4}
          maxToRenderPerBatch={columns * 4}
          windowSize={5}
          removeClippedSubviews
        />
      ) : (
        <FlatList
          key={`files-cols-${columns}`}
          data={files}
          renderItem={renderFile}
          keyExtractor={(item) => item.id}
          numColumns={columns}
          columnWrapperStyle={{ gap: GAP }}
          contentContainerStyle={styles.listContent}
          ListHeaderComponent={FilesHeader}
          ListFooterComponent={ListFooter}
          ListEmptyComponent={ListEmpty}
          refreshing={refreshing}
          onRefresh={refresh}
          initialNumToRender={columns * 4}
          maxToRenderPerBatch={columns * 4}
          windowSize={5}
          removeClippedSubviews
        />
      )}

      <ImageViewer file={viewing} onClose={() => setViewing(null)} />
    </View>
  );
}

// One grid cell. Memoized so unaffected tiles don't re-render. Images render the
// SMALL thumbnail via AuthedImage (which owns its own loading / retryable-error
// states); non-image files render a glyph.
const Tile = React.memo(function Tile({
  id,
  name,
  image,
  size,
  onOpen,
}: {
  id: string;
  name: string;
  image: boolean;
  size: number;
  onOpen: (id: string, name: string, image: boolean) => void;
}): React.JSX.Element {
  return (
    <Pressable
      style={[styles.tile, { width: size, height: size }]}
      onPress={() => onOpen(id, name, image)}
    >
      {image ? (
        <AuthedImage
          style={styles.thumb}
          path={smallThumbnailPath(id)}
          resizeMode="cover"
        />
      ) : (
        <View style={styles.glyphTile}>
          <Text style={styles.glyph}>📄</Text>
          <Text style={styles.tileName} numberOfLines={2}>
            {name}
          </Text>
        </View>
      )}
    </Pressable>
  );
});

const styles = StyleSheet.create({
  root: { flex: 1, backgroundColor: '#fff' },
  header: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingTop: 48,
    paddingBottom: 10,
    paddingHorizontal: 12,
    backgroundColor: '#fafafa',
  },
  headerBtn: { minWidth: 64, paddingVertical: 4 },
  wordmarkWrap: { flex: 1, alignItems: 'center' },
  wordmark: { fontSize: 15, fontWeight: '700', color: '#1a73e8' },
  headerAction: { color: '#1a73e8', fontSize: 15, fontWeight: '600' },
  headerActionEnd: { textAlign: 'right' },
  segment: {
    flexDirection: 'row',
    backgroundColor: '#fafafa',
    paddingHorizontal: 12,
    paddingBottom: 8,
    gap: 8,
  },
  segmentBtn: {
    flex: 1,
    paddingVertical: 8,
    borderRadius: 8,
    backgroundColor: '#eef0f3',
    alignItems: 'center',
  },
  segmentActive: { backgroundColor: '#1a73e8' },
  segmentText: { fontSize: 14, fontWeight: '600', color: '#555' },
  segmentTextActive: { color: '#fff' },
  breadcrumbBar: {
    borderBottomWidth: 1,
    borderBottomColor: '#eee',
    backgroundColor: '#fafafa',
  },
  breadcrumbContent: {
    alignItems: 'center',
    paddingHorizontal: 12,
    paddingBottom: 10,
  },
  crumbWrap: { flexDirection: 'row', alignItems: 'center' },
  crumbSep: { color: '#bbb', fontSize: 14, marginHorizontal: 6 },
  crumb: { color: '#1a73e8', fontSize: 13, maxWidth: 160 },
  crumbCurrent: { color: '#333', fontWeight: '700' },
  centered: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    padding: 24,
  },
  centeredHint: { marginTop: 12, color: '#888', fontSize: 14 },
  listContent: { padding: GAP, gap: GAP, paddingBottom: 32 },
  section: { marginBottom: 4 },
  folderRow: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingVertical: 16,
    paddingHorizontal: 8,
    borderBottomWidth: 1,
    borderBottomColor: '#f0f0f0',
  },
  folderIcon: { fontSize: 22, marginRight: 12 },
  folderName: { flex: 1, fontSize: 16, color: '#222' },
  chevron: { fontSize: 22, color: '#bbb', marginLeft: 8 },
  diagRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    marginVertical: 8,
  },
  diagBtn: {
    paddingVertical: 8,
    paddingHorizontal: 12,
    borderRadius: 8,
    backgroundColor: '#eef3fd',
  },
  diagText: { color: '#1a73e8', fontSize: 13, fontWeight: '600' },
  modeBadge: { color: '#888', fontSize: 12, fontWeight: '600' },
  tile: {},
  thumb: {
    width: '100%',
    height: '100%',
    borderRadius: 8,
    backgroundColor: '#eee',
  },
  glyphTile: {
    width: '100%',
    height: '100%',
    borderRadius: 8,
    backgroundColor: '#f2f2f2',
    alignItems: 'center',
    justifyContent: 'center',
    padding: 6,
  },
  glyph: { fontSize: 30 },
  tileName: { fontSize: 11, color: '#666', marginTop: 4, textAlign: 'center' },
  loadMore: {
    marginTop: 16,
    alignSelf: 'center',
    paddingVertical: 12,
    paddingHorizontal: 28,
    borderRadius: 8,
    borderWidth: 1,
    borderColor: '#1a73e8',
  },
  loadMoreText: { color: '#1a73e8', fontWeight: '600' },
  loadMoreErrorText: { color: '#a33', fontWeight: '600' },
  emptyWrap: { alignItems: 'center', paddingTop: 64, paddingHorizontal: 24 },
  emptyIcon: { fontSize: 40, marginBottom: 12 },
  emptyText: { color: '#666', fontSize: 16, fontWeight: '600' },
  emptyHint: { color: '#aaa', fontSize: 13, marginTop: 6 },
  errorText: {
    color: '#a33',
    fontSize: 15,
    textAlign: 'center',
    marginBottom: 12,
  },
  retryBtn: {
    paddingVertical: 10,
    paddingHorizontal: 24,
    borderRadius: 8,
    backgroundColor: '#1a73e8',
  },
  retryText: { color: '#fff', fontWeight: '600' },
});
