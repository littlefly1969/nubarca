import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ActivityIndicator,
  BackHandler,
  FlatList,
  StyleSheet,
  Text,
  View,
  useTVEventHandler,
  useWindowDimensions,
  type HWEvent,
  type ListRenderItemInfo,
} from 'react-native';
import { colors, font, overscan, spacing } from '../theme';
import { ApiError } from '../api/client';
import {
  listPersonalMedia,
  type TvPersonalMediaItem,
} from '../api/personalMedia';
import { FocusableMediaTile } from '../components/FocusableMediaTile';
import { FocusableButton } from '../components/FocusableButton';
import { MenuCommandRail } from '../components/MenuCommandRail';
import { MediaTilePreview } from '../components/MediaTilePreview';
import { LibraryFilterPanel } from './library/LibraryFilterPanel';
import { PersonalMediaViewer } from './library/PersonalMediaViewer';
import { useMenuOverlay } from '../lib/useMenuOverlay';
import { useTvGridFocusMemory } from '../lib/mediaMenuFocus';
import {
  buildTvMediaGridRows,
  tvMediaGridTargetHeight,
  TV_MEDIA_GRID_FOCUS_BLEED,
  TV_MEDIA_GRID_GAP,
  type TvMediaGridRow,
} from '../lib/tvMediaGrid';
import { useTvMediaGridFocus, type TvMediaGridTargets } from '../lib/useTvMediaGridFocus';
import {
  normalizeTvMediaAspectRatio,
  PHOTO_FALLBACK_ASPECT_RATIO,
  VIDEO_FALLBACK_ASPECT_RATIO,
} from '../lib/mediaAspectRatio';
import { previewPriority } from '../video/videoTilePreview';
import { useI18n } from '../i18n';
import { tvDebug } from '../debug';
import {
  activeFilterCount,
  emptyIdentity,
  queryFingerprint,
  withMediaKind,
  type MediaKindScope,
  type MediaSortDirection,
  type MediaSortField,
  type MediaWorkspaceFilters,
  type MediaWorkspaceIdentity,
  type MediaWorkspaceSource,
} from '../personal/mediaWorkspaceQuery';

// The Personal LIBRARY — one surface for All / Photos / Videos, over the
// owner's library or over one album.
//
// It replaces the two screens that came before it (a photo gallery and a video
// list) which shared nothing: two query models, two filter vocabularies, two
// paging implementations and two navigation engines, with the video one having
// no filters at all. Here there is ONE query identity, ONE pagination model, ONE
// focus grid and ONE viewer handoff for all three tabs, and the album source is
// the same screen with a different predicate — which is exactly the relationship
// the web has between the library and an album.
//
// NAVIGATION. Every row uses the media dimensions already present in the DTO.
// Native links are static, and an adjacent row cannot receive focus until all
// its previews have rendered or reached a terminal placeholder.
//
// INPUT OWNERSHIP is explicit and never shared:
//   GRID          → the native focus engine owns the D-pad; MENU toggles the
//                   command overlay and nothing else listens for directions.
//   PANEL/OVERLAY → the focused controls own it; the grid stops being a focus
//                   destination entirely.
//   VIEWER        → the viewer owns the whole remote (see PersonalMediaViewer).
//
// BACK precedence at this level: panel → overlay → up (library home, or the
// album shelf when browsing an album).

const PAGE_SIZE = 50;
const GRID_GAP = TV_MEDIA_GRID_GAP;
const PREVIEW_BAND = 6;

type Phase = 'loadingInitial' | 'ready' | 'loadingMore' | 'errorInitial' | 'errorMore' | 'end';
type Panel = 'none' | 'filters';

interface LoadState {
  items: TvPersonalMediaItem[];
  nextCursor: string | null;
  totalCount: number | null;
  photoCount: number;
  videoCount: number;
  phase: Phase;
  generation: number;
}

const INITIAL_LOAD: LoadState = {
  items: [],
  nextCursor: null,
  totalCount: null,
  photoCount: 0,
  videoCount: 0,
  phase: 'loadingInitial',
  generation: 0,
};

interface Props {
  source: MediaWorkspaceSource;
  // Shown in the MENU overlay title (the album name, or the library label).
  title: string;
  // BACK from the root of this screen. Normal navigation — LOCKING happens only
  // at the Personal Area root, never here.
  onBack: () => void;
  onGrantInvalid: (reason?: 'pinChanged') => void;
  onSessionInvalid: () => void;
}

const LibraryTile = memo(function LibraryTile({
  item,
  index,
  total,
  width,
  height,
  preferred,
  focusTargets,
  focusable,
  priority,
  rowKey,
  rowIndex,
  onPreviewReady,
  onOpen,
  onFocusIndex,
}: {
  item: TvPersonalMediaItem;
  index: number;
  total: number;
  width: number;
  height: number;
  preferred: boolean;
  focusTargets: TvMediaGridTargets;
  focusable: boolean;
  priority: ReturnType<typeof previewPriority>;
  rowKey: string;
  rowIndex: number;
  onPreviewReady: (rowKey: string, id: string) => void;
  onOpen: (index: number) => void;
  onFocusIndex: (index: number, id: string, rowIndex: number) => void;
}) {
  const [focused, setFocused] = useState(false);
  return (
    <FocusableMediaTile
      accessibilityLabel={item.displayName}
      style={{ width }}
      index={index}
      hasTVPreferredFocus={preferred}
      focusTargets={focusTargets}
      focusable={focusable}
      onSelect={() => onOpen(index)}
      onFocusChange={(f) => { setFocused(f); if (f) onFocusIndex(index, item.id, rowIndex); }}
    >
      <MediaTilePreview
        kind={item.kind}
        path={item.cardImageUrl}
        personal
        priority={priority}
        style={{ width: '100%', height, borderRadius: 8 }}
        onReady={() => onPreviewReady(rowKey, item.id)}
      />
      {item.kind === 'video' && (
        <View style={styles.videoBadge} pointerEvents="none">
          <Text style={styles.videoBadgeText}>
            {item.durationSeconds === null ? '▶' : `▶ ${formatDuration(item.durationSeconds)}`}
          </Text>
        </View>
      )}
      {item.favorite && <Text style={styles.favBadge}>♥</Text>}
      {item.occurrenceCount > 1 && (
        <Text style={styles.dupBadge}>×{item.occurrenceCount}</Text>
      )}
      {focused && focusable && (
        <View style={styles.posBadge} pointerEvents="none">
          <Text style={styles.posBadgeText}>{index + 1} / {total}</Text>
        </View>
      )}
    </FocusableMediaTile>
  );
});

export function PersonalLibraryScreen({
  source, title, onBack, onGrantInvalid, onSessionInvalid,
}: Props) {
  const { t, lang } = useI18n();
  const { width, height } = useWindowDimensions();
  const inset = overscan(width, height);

  const [identity, setIdentity] = useState<MediaWorkspaceIdentity>(() => emptyIdentity(source));
  const [load, setLoad] = useState<LoadState>(INITIAL_LOAD);
  const [panel, setPanel] = useState<Panel>('none');
  const [viewerIndex, setViewerIndex] = useState<number | null>(null);

  const {
    visible: overlayVisible,
    visibleRef: overlayVisibleRef,
    toggle: toggleOverlay,
    hide: hideOverlay,
    bump: bumpOverlay,
  } = useMenuOverlay();

  const generationRef = useRef(0);
  const loadRef = useRef(load);
  loadRef.current = load;
  const identityRef = useRef(identity);
  identityRef.current = identity;
  const panelRef = useRef(panel);
  panelRef.current = panel;
  const viewerOpenRef = useRef(false);
  viewerOpenRef.current = viewerIndex !== null;

  const gridInteractive = panel === 'none' && viewerIndex === null;
  const menuOwnsFocus = overlayVisible && gridInteractive;
  const gridFocusable = gridInteractive && !overlayVisible;

  const {
    restoreIndex, onTileFocused: rememberFocusedTile, restoreTo, read: readGridFocus,
  } = useTvGridFocusMemory(menuOwnsFocus, load.items);

  // Shared auth-failure mapping: 401 → pairing teardown; 403 pin_changed →
  // mode selector with the notice; any other 403 → plain lock.
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

  // The query identity's FINGERPRINT is the dependency, not the object: an
  // identity rebuilt with equal values must not restart the query.
  const fingerprint = queryFingerprint(identity);

  useEffect(() => {
    generationRef.current += 1;
    const gen = generationRef.current;
    const previousItems = loadRef.current.items;
    const previousFocus = readGridFocus();
    setLoad({ ...INITIAL_LOAD, generation: gen });

    let cancelled = false;
    listPersonalMedia(identityRef.current, null, PAGE_SIZE)
      .then((page) => {
        if (cancelled || generationRef.current !== gen) return;
        setLoad({
          items: page.items,
          nextCursor: page.nextCursor,
          totalCount: page.totalCount,
          photoCount: page.photoCount,
          videoCount: page.videoCount,
          phase: page.hasMore ? 'ready' : 'end',
          generation: gen,
        });
        // Keep the user on the same ITEM across a filter change when it
        // survived, otherwise the nearest position — never a jump to the top
        // that loses their place for no reason.
        const next = remapFocus(previousItems, previousFocus.id, previousFocus.index, page.items);
        restoreTo(next, page.items[next]?.id ?? null);
      })
      .catch((err: unknown) => {
        if (cancelled || generationRef.current !== gen) return;
        if (handleAuthError(err)) return;
        setLoad((s) => ({ ...s, phase: 'errorInitial' }));
      });
    return () => { cancelled = true; };
  }, [fingerprint, handleAuthError, restoreTo, readGridFocus]);

  const loadMore = useCallback(() => {
    const current = loadRef.current;
    if (current.nextCursor === null) return;
    if (current.phase !== 'ready' && current.phase !== 'errorMore') return;
    const gen = generationRef.current;
    setLoad((s) => ({ ...s, phase: 'loadingMore' }));
    listPersonalMedia(identityRef.current, current.nextCursor, PAGE_SIZE)
      .then((page) => {
        setLoad((s) => (s.generation !== gen ? s : {
          ...s,
          items: [...s.items, ...page.items],
          nextCursor: page.nextCursor,
          totalCount: page.totalCount,
          phase: page.hasMore ? 'ready' : 'end',
        }));
      })
      .catch((err: unknown) => {
        if (handleAuthError(err)) return;
        setLoad((s) => (s.generation !== gen ? s : { ...s, phase: 'errorMore' }));
      });
  }, [handleAuthError]);

  // MENU toggles the overlay only while the plain grid is up (panels and the
  // viewer own their own MENU behaviour). This is the ONLY global key listener
  // on this screen and it never touches a direction — the native focus engine
  // owns those, with no competitor.
  const onTVEvent = useCallback((evt: HWEvent) => {
    if (!evt || evt.eventKeyAction === 0) return;
    if (panelRef.current !== 'none' || viewerOpenRef.current) return;
    if (evt.eventType === 'menu') toggleOverlay();
    else bumpOverlay();
  }, [toggleOverlay, bumpOverlay]);
  useTVEventHandler(onTVEvent);

  // BACK precedence at the library root (panels/viewer register their own
  // handlers and win first, because handlers are LIFO): overlay → up.
  useEffect(() => {
    const onBackPress = () => {
      if (panelRef.current !== 'none' || viewerOpenRef.current) return false;
      if (overlayVisibleRef.current) {
        hideOverlay();
        return true;
      }
      onBack();
      return true;
    };
    const sub = BackHandler.addEventListener('hardwareBackPress', onBackPress);
    return () => sub.remove();
  }, [onBack, hideOverlay, overlayVisibleRef]);

  // ---------------------------------------------------------------- layout

  const contentWidth = Math.max(1, width - 2 * inset.x);

  const items = load.items;
  const total = load.totalCount ?? items.length;
  const rows = useMemo(
    () => buildTvMediaGridRows({
      items,
      contentWidth,
      targetRowHeight: tvMediaGridTargetHeight(height - 2 * inset.y),
      getAspectRatio: (item) => normalizeTvMediaAspectRatio(
        item.width,
        item.height,
        item.kind === 'video' ? VIDEO_FALLBACK_ASPECT_RATIO : PHOTO_FALLBACK_ASPECT_RATIO,
      ),
      getId: (item) => item.id,
    }),
    [items, contentWidth, height, inset.y],
  );
  const gridFocus = useTvMediaGridFocus(rows);

  const [focusedIndex, setFocusedIndex] = useState(-1);
  const focusedRowRef = useRef(-1);

  const onTileFocus = useCallback((index: number, id: string, rowIndex: number) => {
    rememberFocusedTile(index, id);
    if (focusedRowRef.current !== rowIndex) {
      focusedRowRef.current = rowIndex;
      setFocusedIndex(index);
    }
    tvDebug('grid nav', 'row', rowIndex);
  }, [rememberFocusedTile]);

  const openAt = useCallback((index: number) => {
    hideOverlay();
    setViewerIndex(index);
  }, [hideOverlay]);

  const closeViewer = useCallback((currentIndex: number) => {
    const list = loadRef.current.items;
    const clamped = Math.max(0, Math.min(currentIndex, list.length - 1));
    restoreTo(clamped, list[clamped]?.id ?? null);
    setViewerIndex(null);
  }, [restoreTo]);

  const applyFilters = useCallback((
    filters: MediaWorkspaceFilters,
    sort: MediaSortField,
    direction: MediaSortDirection,
  ) => {
    setPanel('none');
    setIdentity((current) => ({ ...current, filters, sort, direction }));
  }, []);

  const setKind = useCallback((kind: MediaKindScope) => {
    hideOverlay();
    setIdentity((current) => withMediaKind(current, kind));
  }, [hideOverlay]);

  const renderRow = useCallback((
    { item: row, index: rowIndex }: ListRenderItemInfo<TvMediaGridRow<TvPersonalMediaItem>>,
  ) => {
    const rowReady = gridFocus.isRowReady(row.key);
    return (
      <View style={styles.gridRow}>
        {row.tiles.map((tile) => {
          const { item, originalIndex: index, width, height: tileHeight } = tile;
          return (
            <LibraryTile
              key={item.id}
              item={item}
              index={index}
              total={total}
              width={width}
              height={tileHeight}
              preferred={gridFocusable && rowReady && restoreIndex !== null && index === restoreIndex}
              focusTargets={gridFocus.targetsFor(item.id)}
              focusable={gridFocusable && rowReady}
              priority={previewPriority(index, focusedIndex, PREVIEW_BAND)}
              rowKey={row.key}
              rowIndex={rowIndex}
              onPreviewReady={gridFocus.onPreviewReady}
              onOpen={openAt}
              onFocusIndex={onTileFocus}
            />
          );
        })}
      </View>
    );
  }, [total, gridFocusable, restoreIndex, gridFocus, focusedIndex, openAt, onTileFocus]);

  const filterCount = activeFilterCount(identity);
  const emptyLabel = filterCount > 0 ? t('gallery.noResults') : t('gallery.empty');

  return (
    <View style={[styles.container, { paddingTop: inset.y, paddingHorizontal: inset.x }]}>
      {load.phase === 'loadingInitial' ? (
        <View style={styles.stateBox}>
          <ActivityIndicator size="large" color={colors.accent} />
          <Text style={styles.body}>{t('gallery.loading')}</Text>
        </View>
      ) : load.phase === 'errorInitial' ? (
        <View style={styles.stateBox}>
          <Text style={styles.body}>{t('gallery.loadError')}</Text>
          <FocusableButton
            label={t('common.tryAgain')}
            onPress={() => setIdentity((current) => ({ ...current }))}
            hasTVPreferredFocus={gridInteractive}
          />
          <FocusableButton label={t('gallery.backToHome')} onPress={onBack} />
        </View>
      ) : items.length === 0 ? (
        <View style={styles.stateBox}>
          <Text style={styles.body}>{emptyLabel}</Text>
          {filterCount > 0 && (
            <FocusableButton
              label={t('gallery.clearFilters')}
              onPress={() => setIdentity(emptyIdentity(source))}
              hasTVPreferredFocus={gridInteractive}
            />
          )}
          <FocusableButton
            label={t('gallery.backToHome')}
            onPress={onBack}
            hasTVPreferredFocus={gridInteractive && filterCount === 0}
          />
        </View>
      ) : (
        <FlatList
          data={rows}
          renderItem={renderRow}
          keyExtractor={(row) => row.key}
          contentContainerStyle={[styles.grid, { paddingBottom: inset.y }]}
          // Keep nearby rows mounted for progressive preview loading. Distant
          // rows remain virtualized, and the focus barrier never targets them.
          initialNumToRender={8}
          maxToRenderPerBatch={4}
          windowSize={11}
          onEndReachedThreshold={2}
          onEndReached={loadMore}
        />
      )}

      {load.phase === 'loadingMore' && items.length > 0 && (
        <View style={[styles.statusPill, { bottom: inset.y }]} pointerEvents="none">
          <ActivityIndicator color={colors.muted} />
          <Text style={styles.statusText}>{t('media.loading')}</Text>
        </View>
      )}
      {load.phase === 'errorMore' && gridInteractive && (
        <View style={[styles.statusPill, { bottom: inset.y }]}>
          <Text style={styles.statusText}>{t('gallery.loadMoreError')}</Text>
          <FocusableButton label={t('common.tryAgain')} onPress={loadMore} />
        </View>
      )}

      {!overlayVisible && gridInteractive && filterCount > 0 && (
        <View style={[styles.appliedStatus, { top: inset.y }]} pointerEvents="none">
          <Text style={styles.appliedStatusText}>
            {lang === 'it'
              ? `${total} elementi · ${filterCount} filtri`
              : `${total} items · ${filterCount} filters`}
          </Text>
        </View>
      )}

      {overlayVisible && gridInteractive && (
        <>
          <View style={[styles.titleRow, { top: inset.y }]} pointerEvents="none">
            <View style={styles.titlePill}>
              <Text style={styles.titleText} numberOfLines={1}>
                {title} · {tabLabel(identity.mediaKind, load, t)} · {total}
              </Text>
            </View>
          </View>
          <MenuCommandRail
            style={[styles.commandBar, { left: inset.x, right: inset.x, bottom: inset.y }]}
          >
            {(['all', 'image', 'video'] as const).map((kind) => (
              <FocusableButton
                key={kind}
                label={tabLabel(kind, load, t)}
                onPress={() => setKind(kind)}
                onFocusChange={(f) => { if (f) bumpOverlay(); }}
                hasTVPreferredFocus={kind === identity.mediaKind}
              />
            ))}
            <FocusableButton
              label={t('filters.title')}
              onPress={() => { hideOverlay(); setPanel('filters'); }}
              onFocusChange={(f) => { if (f) bumpOverlay(); }}
            />
            <FocusableButton
              label={t('gallery.backToHome')}
              onPress={onBack}
              onFocusChange={(f) => { if (f) bumpOverlay(); }}
            />
          </MenuCommandRail>
        </>
      )}

      {panel === 'filters' && (
        <LibraryFilterPanel
          applied={identity}
          resultCount={total}
          onApply={applyFilters}
          onCancel={() => setPanel('none')}
        />
      )}

      {viewerIndex !== null && items.length > 0 && (
        <PersonalMediaViewer
          items={items}
          startIndex={Math.min(viewerIndex, items.length - 1)}
          totalCount={total}
          hasMore={load.nextCursor !== null}
          onNeedMore={loadMore}
          onClose={closeViewer}
        />
      )}
    </View>
  );
}

// Keep the user on the same ITEM across a query change when it survived,
// otherwise the nearest position. Pure and local — it needs no history.
function remapFocus(
  previous: readonly TvPersonalMediaItem[],
  previousId: string | null,
  previousIndex: number,
  next: readonly TvPersonalMediaItem[],
): number {
  if (next.length === 0) return 0;
  if (previousId !== null) {
    const found = next.findIndex((item) => item.id === previousId);
    if (found >= 0) return found;
  }
  void previous;
  return Math.max(0, Math.min(previousIndex, next.length - 1));
}

function tabLabel(
  kind: MediaKindScope,
  load: LoadState,
  t: (key: 'library.tabAll' | 'library.tabPhotos' | 'library.tabVideos') => string,
): string {
  if (kind === 'image') return `${t('library.tabPhotos')} ${load.photoCount || ''}`.trim();
  if (kind === 'video') return `${t('library.tabVideos')} ${load.videoCount || ''}`.trim();
  return t('library.tabAll');
}

function formatDuration(seconds: number): string {
  const total = Math.max(0, Math.round(seconds));
  const minutes = Math.floor(total / 60);
  const rest = total % 60;
  return `${minutes}:${rest.toString().padStart(2, '0')}`;
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.bg },
  body: { color: colors.muted, fontSize: font.body, textAlign: 'center' },
  stateBox: { marginTop: spacing.xl, alignItems: 'center', gap: spacing.md },
  grid: { gap: GRID_GAP },
  gridRow: {
    flexDirection: 'row',
    gap: GRID_GAP,
    paddingVertical: TV_MEDIA_GRID_FOCUS_BLEED,
    overflow: 'visible',
  },
  videoBadge: {
    position: 'absolute',
    bottom: spacing.xs,
    left: spacing.xs,
    paddingHorizontal: spacing.xs,
    paddingVertical: 1,
    borderRadius: 6,
    backgroundColor: 'rgba(0,0,0,0.78)',
  },
  videoBadgeText: { color: colors.text, fontSize: font.caption, fontWeight: '700' },
  favBadge: {
    position: 'absolute',
    top: spacing.xs,
    left: spacing.xs,
    color: '#ff6b8a',
    fontSize: font.body,
    textShadowColor: 'rgba(0,0,0,0.9)',
    textShadowRadius: 4,
  },
  dupBadge: {
    position: 'absolute',
    top: spacing.xs,
    right: spacing.xs,
    color: colors.text,
    fontSize: font.caption,
    fontWeight: '800',
    textShadowColor: 'rgba(0,0,0,0.9)',
    textShadowRadius: 4,
  },
  posBadge: {
    position: 'absolute',
    bottom: spacing.xs,
    alignSelf: 'center',
    paddingHorizontal: spacing.sm,
    paddingVertical: 2,
    borderRadius: 999,
    backgroundColor: 'rgba(0,0,0,0.78)',
  },
  posBadgeText: { color: colors.text, fontSize: 15, fontWeight: '700' },
  titleRow: { position: 'absolute', left: 0, right: 0, alignItems: 'center' },
  titlePill: {
    maxWidth: '80%',
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.xs,
    borderRadius: 12,
    backgroundColor: 'rgba(0,0,0,0.78)',
  },
  titleText: { color: colors.text, fontSize: font.body, fontWeight: '700' },
  commandBar: {
    position: 'absolute',
    flexDirection: 'row',
    flexWrap: 'wrap',
    alignItems: 'center',
    gap: spacing.sm,
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.sm,
    borderRadius: 14,
    backgroundColor: 'rgba(0,0,0,0.78)',
  },
  statusPill: {
    position: 'absolute',
    alignSelf: 'center',
    flexDirection: 'row',
    alignItems: 'center',
    gap: spacing.sm,
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.xs,
    borderRadius: 999,
    backgroundColor: 'rgba(0,0,0,0.78)',
  },
  statusText: { color: colors.muted, fontSize: font.caption },
  appliedStatus: {
    position: 'absolute',
    alignSelf: 'center',
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.xs,
    borderRadius: 999,
    backgroundColor: 'rgba(0,0,0,0.78)',
  },
  appliedStatusText: { color: colors.text, fontSize: font.caption, fontWeight: '700' },
});
