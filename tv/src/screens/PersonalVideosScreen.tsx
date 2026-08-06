import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ActivityIndicator,
  BackHandler,
  FlatList,
  Pressable,
  StyleSheet,
  Text,
  View,
  useTVEventHandler,
  useWindowDimensions,
  type HWEvent,
  type ListRenderItemInfo,
} from 'react-native';
import { ApiError } from '../api/client';
import {
  listPersonalVideos,
  type TvPersonalVideoItem,
} from '../api/personalVideos';
import { AuthedTilePreview } from '../components/AuthedTilePreview';
import { FocusableButton } from '../components/FocusableButton';
import { FocusableMediaTile } from '../components/FocusableMediaTile';
import { TvVideoPlayer, type TvVideoControls } from '../components/TvVideoPlayer';
import { useI18n } from '../i18n';
import { useScreenAwake } from '../lib/useScreenAwake';
import { colors, font, overscan, spacing } from '../theme';
import { normalizeTvMediaAspectRatio, VIDEO_FALLBACK_ASPECT_RATIO } from '../lib/mediaAspectRatio';
import { buildTvJustifiedRows, type TvJustifiedRow } from '../lib/justifiedMediaRows';
import {
  MEDIA_GRID_FOCUS_BLEED,
  MEDIA_GRID_PACKING_GAP,
  MEDIA_GRID_VISUAL_GAP,
  mediaGridTargetRowHeight,
} from '../lib/mediaGridPresentation';
import { useTvMediaGridFocus, type TvMediaFocusTargets } from '../lib/mediaGridFocus';

const PAGE_SIZE = 60;
const GRID_GAP = MEDIA_GRID_VISUAL_GAP;

interface Props {
  onBack: () => void;
  onGrantInvalid: (reason?: 'pinChanged') => void;
  onSessionInvalid: () => void;
}

function formatDuration(seconds: number | null): string {
  if (seconds === null || !Number.isFinite(seconds)) return '';
  const value = Math.max(0, Math.round(seconds));
  const hours = Math.floor(value / 3600);
  const minutes = Math.floor((value % 3600) / 60);
  const rest = value % 60;
  return hours > 0
    ? `${hours}:${String(minutes).padStart(2, '0')}:${String(rest).padStart(2, '0')}`
    : `${minutes}:${String(rest).padStart(2, '0')}`;
}

const VideoTile = memo(function VideoTile({
  item, index, total, width, height, preferred, focusTargets, onOpen, onFocusIndex,
}: {
  item: TvPersonalVideoItem;
  index: number;
  total: number;
  width: number;
  height: number;
  preferred: boolean;
  focusTargets: TvMediaFocusTargets;
  onOpen: (index: number) => void;
  onFocusIndex: (index: number) => void;
}) {
  const [focused, setFocused] = useState(false);

  return (
    <FocusableMediaTile
      accessibilityLabel={item.name}
      style={{ width }}
      hasTVPreferredFocus={preferred}
      focusTargets={focusTargets}
      onSelect={() => onOpen(index)}
      onFocusChange={(value) => {
        setFocused(value);
        if (value) onFocusIndex(index);
      }}
    >
      <View style={{ width: '100%', height, borderRadius: 8, overflow: 'hidden' }}>
        <AuthedTilePreview
          path={item.posterUrl}
          personal
          style={{ width: '100%', height: '100%' }}
        />
        <View style={styles.videoBadge}><Text style={styles.videoBadgeText}>▶</Text></View>
        {item.durationSeconds !== null && (
          <View style={styles.durationBadge}>
            <Text style={styles.durationText}>{formatDuration(item.durationSeconds)}</Text>
          </View>
        )}
        {focused && (
          <View style={styles.positionBadge}>
            <Text style={styles.positionText}>{index + 1} / {total}</Text>
          </View>
        )}
      </View>
    </FocusableMediaTile>
  );
});

export function PersonalVideosScreen({ onBack, onGrantInvalid, onSessionInvalid }: Props) {
  const { t } = useI18n();
  const { width, height } = useWindowDimensions();
  const inset = overscan(width, height);
  const [items, setItems] = useState<TvPersonalVideoItem[]>([]);
  const [totalCount, setTotalCount] = useState(0);
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [hasMore, setHasMore] = useState(false);
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [failed, setFailed] = useState(false);
  const [viewerIndex, setViewerIndex] = useState<number | null>(null);
  const [restoreIndex, setRestoreIndex] = useState<number | null>(0);
  const lastFocusedIndex = useRef(0);
  const controlsRef = useRef<TvVideoControls | null>(null);
  const itemsRef = useRef(items);
  itemsRef.current = items;
  const viewerIndexRef = useRef(viewerIndex);
  viewerIndexRef.current = viewerIndex;
  useScreenAwake(viewerIndex !== null);

  const onTileFocus = useCallback((index: number) => {
    lastFocusedIndex.current = index;
    setRestoreIndex((current) => (current === null ? current : null));
  }, []);

  const handleAuthError = useCallback((err: unknown): boolean => {
    if (err instanceof ApiError && err.status === 401) {
      onSessionInvalid();
      return true;
    }
    if (err instanceof ApiError && err.status === 403) {
      const body = err.body as { error?: string } | null;
      onGrantInvalid(body?.error === 'pin_changed' ? 'pinChanged' : undefined);
      return true;
    }
    return false;
  }, [onGrantInvalid, onSessionInvalid]);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    listPersonalVideos(PAGE_SIZE, null)
      .then((page) => {
        if (cancelled) return;
        setItems(page.items);
        setTotalCount(page.totalCount);
        setNextCursor(page.nextCursor);
        setHasMore(page.hasMore);
        setFailed(false);
      })
      .catch((err: unknown) => {
        if (!cancelled && !handleAuthError(err)) setFailed(true);
      })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [handleAuthError]);

  const loadMore = useCallback(() => {
    if (!hasMore || !nextCursor || loadingMore) return;
    setLoadingMore(true);
    listPersonalVideos(PAGE_SIZE, nextCursor)
      .then((page) => {
        setItems((current) => [...current, ...page.items]);
        setTotalCount(page.totalCount);
        setNextCursor(page.nextCursor);
        setHasMore(page.hasMore);
      })
      .catch((err: unknown) => { if (!handleAuthError(err)) setFailed(true); })
      .finally(() => setLoadingMore(false));
  }, [hasMore, nextCursor, loadingMore, handleAuthError]);

  const closeViewer = useCallback(() => {
    const current = viewerIndexRef.current ?? 0;
    setRestoreIndex(current);
    lastFocusedIndex.current = current;
    setViewerIndex(null);
  }, []);

  const moveViewer = useCallback((delta: number) => {
    setViewerIndex((current) => {
      if (current === null || itemsRef.current.length === 0) return current;
      const next = Math.max(0, Math.min(itemsRef.current.length - 1, current + delta));
      if (hasMore && next >= itemsRef.current.length - 3) loadMore();
      return next;
    });
  }, [hasMore, loadMore]);

  useTVEventHandler(useCallback((event: HWEvent) => {
    if (viewerIndexRef.current === null || !event || event.eventKeyAction === 0) return;
    switch (event.eventType) {
      case 'select': case 'playPause': controlsRef.current?.togglePlay(); break;
      case 'left': case 'longLeft': controlsRef.current?.seekBy(-10); break;
      case 'right': case 'longRight': controlsRef.current?.seekBy(10); break;
      case 'up': moveViewer(-1); break;
      case 'down': moveViewer(1); break;
    }
  }, [moveViewer]));

  useEffect(() => {
    const handler = () => {
      if (viewerIndexRef.current !== null) closeViewer();
      else onBack();
      return true;
    };
    const sub = BackHandler.addEventListener('hardwareBackPress', handler);
    return () => sub.remove();
  }, [closeViewer, onBack]);

  const contentWidth = Math.max(1, width - inset.x * 2);
  const targetRowHeight = mediaGridTargetRowHeight(height);
  const rows = useMemo(
    () => buildTvJustifiedRows({
      items,
      contentWidth,
      targetRowHeight,
      gap: GRID_GAP,
      packingGap: MEDIA_GRID_PACKING_GAP,
      getAspectRatio: (item) => normalizeTvMediaAspectRatio(
        item.width,
        item.height,
        VIDEO_FALLBACK_ASPECT_RATIO,
      ),
      getId: (item) => item.id,
    }),
    [items, contentWidth, targetRowHeight],
  );
  const focusForItem = useTvMediaGridFocus(rows, GRID_GAP);

  if (viewerIndex !== null) {
    const item = items[Math.min(viewerIndex, items.length - 1)];
    return item ? (
      <View style={styles.viewer}>
        <TvVideoPlayer
          key={item.id}
          videoPath={item.videoUrl}
          posterPath={item.posterUrl}
          controlsRef={controlsRef}
          personal
          onEnded={() => moveViewer(1)}
        />
        <Pressable focusable hasTVPreferredFocus style={styles.viewerCapture} />
        <View style={[styles.viewerTitle, { top: inset.y }]} pointerEvents="none">
          <Text style={styles.viewerTitleText} numberOfLines={1}>{item.name}</Text>
          <Text style={styles.viewerCounter}>{viewerIndex + 1} / {totalCount}</Text>
        </View>
        <View style={[styles.viewerHint, { bottom: inset.y }]} pointerEvents="none">
          <Text style={styles.viewerHintText}>{t('viewer.videoNavHint')}</Text>
        </View>
      </View>
    ) : null;
  }

  return (
    <View style={[styles.container, { paddingTop: inset.y, paddingHorizontal: inset.x }]}>
      <View style={styles.header}>
        <Text style={styles.title}>{t('personal.videos')}</Text>
        <Text style={styles.total}>{totalCount}</Text>
      </View>
      {loading ? (
        <ActivityIndicator size="large" color={colors.accent} style={styles.state} />
      ) : failed && items.length === 0 ? (
        <View style={styles.state}>
          <Text style={styles.stateText}>{t('videos.loadError')}</Text>
          <FocusableButton label={t('videos.back')} onPress={onBack} hasTVPreferredFocus />
        </View>
      ) : items.length === 0 ? (
        <View style={styles.state}>
          <Text style={styles.stateText}>{t('videos.empty')}</Text>
          <FocusableButton label={t('videos.back')} onPress={onBack} hasTVPreferredFocus />
        </View>
      ) : (
        <FlatList
          data={rows}
          keyExtractor={(row) => row.key}
          contentContainerStyle={[styles.grid, { paddingBottom: inset.y }]}
          renderItem={({ item: row }: ListRenderItemInfo<TvJustifiedRow<TvPersonalVideoItem>>) => (
            <View style={styles.row}>
              {row.tiles.map((tile) => (
                <VideoTile
                  key={tile.item.id}
                  item={tile.item}
                  index={tile.originalIndex}
                  total={totalCount}
                  width={tile.width}
                  height={tile.height}
                  preferred={
                    restoreIndex !== null
                    && tile.originalIndex === restoreIndex
                  }
                  focusTargets={focusForItem(tile.item.id)}
                  onOpen={setViewerIndex}
                  onFocusIndex={onTileFocus}
                />
              ))}
            </View>
          )}
          onEndReached={loadMore}
          onEndReachedThreshold={0.8}
          initialNumToRender={6}
          maxToRenderPerBatch={4}
          windowSize={7}
          ListFooterComponent={loadingMore
            ? <ActivityIndicator color={colors.muted} style={styles.footer} />
            : null}
        />
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.bg },
  header: { flexDirection: 'row', alignItems: 'baseline', gap: spacing.sm, marginBottom: spacing.md },
  title: { color: colors.text, fontSize: font.heading, fontWeight: '800' },
  total: { color: colors.muted, fontSize: font.body },
  row: {
    flexDirection: 'row',
    gap: GRID_GAP,
    paddingVertical: MEDIA_GRID_FOCUS_BLEED,
    overflow: 'visible',
  },
  grid: { gap: GRID_GAP, paddingBottom: spacing.xl },
  state: { flex: 1, alignItems: 'center', justifyContent: 'center', gap: spacing.md },
  stateText: { color: colors.muted, fontSize: font.body },
  footer: { margin: spacing.lg },
  videoBadge: { position: 'absolute', top: 8, left: 8, width: 34, height: 34, borderRadius: 17, alignItems: 'center', justifyContent: 'center', backgroundColor: 'rgba(0,0,0,.72)' },
  videoBadgeText: { color: '#fff', fontSize: 18 },
  durationBadge: { position: 'absolute', right: 8, bottom: 8, paddingHorizontal: 7, paddingVertical: 3, borderRadius: 6, backgroundColor: 'rgba(0,0,0,.78)' },
  durationText: { color: '#fff', fontSize: 14, fontWeight: '700' },
  positionBadge: { position: 'absolute', left: 8, bottom: 8, paddingHorizontal: 7, paddingVertical: 3, borderRadius: 6, backgroundColor: 'rgba(0,0,0,.78)' },
  positionText: { color: '#fff', fontSize: 14, fontWeight: '700' },
  viewer: { flex: 1, backgroundColor: '#05070b' },
  viewerCapture: { position: 'absolute', top: 0, right: 0, bottom: 0, left: 0 },
  viewerTitle: { position: 'absolute', left: '12%', right: '12%', alignItems: 'center' },
  viewerTitleText: { color: '#fff', fontSize: font.heading, fontWeight: '800', backgroundColor: 'rgba(0,0,0,.72)', paddingHorizontal: 18, paddingVertical: 7, borderRadius: 12 },
  viewerCounter: { color: '#fff', fontSize: 16, marginTop: 6, backgroundColor: 'rgba(0,0,0,.65)', paddingHorizontal: 10, paddingVertical: 3, borderRadius: 8 },
  viewerHint: { position: 'absolute', left: 0, right: 0, alignItems: 'center' },
  viewerHintText: { color: '#fff', fontSize: 16, backgroundColor: 'rgba(0,0,0,.72)', paddingHorizontal: 14, paddingVertical: 6, borderRadius: 10 },
});
