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
import { useTvFixedGridFocus } from '../lib/useTvFixedGridFocus';
import {
  buildTvGridRows,
  tvGridColumns,
  tvGridRowOf,
  tvGridTileWidth,
  type TvGridRow,
} from '../lib/tvFixedGrid';
import { MEDIA_GRID_FOCUS_BLEED, MEDIA_GRID_VISUAL_GAP } from '../lib/mediaGridPresentation';
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
// NAVIGATION. The grid is a UNIFORM fixed-column matrix with a static
// index-based focus graph (see lib/tvFixedGrid.ts). Nothing re-renders while the
// user navigates, so a held D-pad and a series of taps produce the same walk;
// that is the fix for the "fast and slow behave differently" defect, and it is
// proved by the burst tests rather than by tuning.
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
const GRID_GAP = MEDIA_GRID_VISUAL_GAP;
// Tile aspect. Uniform boxes are what make the geometric focus fallback agree
// with the explicit graph; the media inside is contained, never cropped.
const TILE_ASPECT = 16 / 10;

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
  onOpen,
  onFocusIndex,
}: {
  item: TvPersonalMediaItem;
  index: number;
  total: number;
  width: number;
  height: number;
  preferred: boolean;
  focusTargets: ReturnType<ReturnType<typeof useTvFixedGridFocus>['targetsFor']>;
  focusable: boolean;
  priority: ReturnType<typeof previewPriority>;
  onOpen: (index: number) => void;
  onFocusIndex: (index: number, id: string) => void;
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
      onFocusChange={(f) => { setFocused(f); if (f) onFocusIndex(index, item.id); }}
    >
      <MediaTilePreview
        kind={item.kind}
        path={item.cardImageUrl}
        personal
        priority={priority}
        style={{ width: '100%', height, borderRadius: 8 }}
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
  const columns = tvGridColumns(contentWidth);
  const tileWidth = tvGridTileWidth(contentWidth, columns, GRID_GAP);
  const tileHeight = Math.round(tileWidth / TILE_ASPECT);

  const items = load.items;
  const total = load.totalCount ?? items.length;
  const rows = useMemo(
    () => buildTvGridRows(items, columns, (item) => item.id),
    [items, columns],
  );
  const gridFocus = useTvFixedGridFocus(items.length, columns);

  const listRef = useRef<FlatList<TvGridRow<TvPersonalMediaItem>>>(null);
  const focusedIndexRef = useRef(-1);
  const [focusedIndex, setFocusedIndex] = useState(-1);

  const onTileFocus = useCallback((index: number, id: string) => {
    rememberFocusedTile(index, id);
    focusedIndexRef.current = index;
    // ONLY the poster-warming band reads this. Moving within a row leaves the
    // band unchanged, so the state is left alone: a re-render here would sit on
    // the key-press path, which is exactly what must stay empty. Navigation
    // itself never depends on it — the focus graph is static.
    setFocusedIndex((current) =>
      tvGridRowOf(current, columns) === tvGridRowOf(index, columns) && current >= 0
        ? current
        : index);
    tvDebug('grid nav', 'row', tvGridRowOf(index, columns), 'col', index % columns);
  }, [rememberFocusedTile, columns]);

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
    { item: row }: ListRenderItemInfo<TvGridRow<TvPersonalMediaItem>>,
  ) => (
    <View style={styles.gridRow}>
      {row.items.map((item, offset) => {
        const index = row.firstIndex + offset;
        return (
          <LibraryTile
            key={item.id}
            item={item}
            index={index}
            total={total}
            width={tileWidth}
            height={tileHeight}
            preferred={gridFocusable && restoreIndex !== null && index === restoreIndex}
            focusTargets={gridFocus.targetsFor(index)}
            focusable={gridFocusable}
            priority={previewPriority(index, focusedIndex, columns)}
            onOpen={openAt}
            onFocusIndex={onTileFocus}
          />
        );
      })}
    </View>
  ), [
    total, tileWidth, tileHeight, gridFocusable, restoreIndex,
    gridFocus, focusedIndex, columns, openAt, onTileFocus,
  ]);

  const filterCount = activeFilterCount(identity);
  const emptyLabel = filterCount > 0 ? t('gallery.noResults') : t('gallery.empty');

  return (
    <View style={[styles.container, { paddingTop: inset.y, paddingHorizontal: inset.x }]}>
      {/* Kind tabs. Always visible: they are the primary in-screen navigation,
          not a hidden menu action. */}
      <View style={styles.tabs}>
        {(['all', 'image', 'video'] as const).map((kind) => (
          <FocusableButton
            key={kind}
            label={tabLabel(kind, load, t)}
            onPress={() => setKind(kind)}
            // The active tab is NOT the focus default: focus belongs to the
            // grid, and a tab stealing it on every filter change would fight
            // the restore.
          />
        ))}
      </View>

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
          ref={listRef}
          data={rows}
          renderItem={renderRow}
          keyExtractor={(row) => row.key}
          contentContainerStyle={[styles.grid, { paddingBottom: inset.y }]}
          // `removeClippedSubviews` is deliberately NOT set: it DETACHES
          // already-rendered rows just outside the viewport, and a detached row
          // cannot be resolved as an explicit nextFocus target.
          //
          // The window is sized generously on purpose. `additionalRenderRegions`
          // is documented in the react-native-tvos README but is NOT implemented
          // in the shipped 0.85.3-3 JavaScript, so a wide window is what keeps
          // the immediate vertical neighbours of the focused row mounted during
          // an auto-repeat burst. Virtualization is NOT disabled — distant rows
          // stay unmounted, and the uniform grid's geometric fallback names the
          // same tile the link would if one is briefly unresolved.
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
                {title} · {total}
              </Text>
            </View>
          </View>
          <MenuCommandRail
            style={[styles.commandBar, { left: inset.x, right: inset.x, bottom: inset.y }]}
          >
            <FocusableButton
              label={t('gallery.backToHome')}
              onPress={onBack}
              onFocusChange={(f) => { if (f) bumpOverlay(); }}
              hasTVPreferredFocus
            />
            <FocusableButton
              label={t('filters.title')}
              onPress={() => { hideOverlay(); setPanel('filters'); }}
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
  tabs: {
    flexDirection: 'row',
    gap: spacing.sm,
    marginBottom: spacing.sm,
  },
  body: { color: colors.muted, fontSize: font.body, textAlign: 'center' },
  stateBox: { marginTop: spacing.xl, alignItems: 'center', gap: spacing.md },
  grid: { gap: GRID_GAP },
  gridRow: {
    flexDirection: 'row',
    gap: GRID_GAP,
    paddingVertical: MEDIA_GRID_FOCUS_BLEED,
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
