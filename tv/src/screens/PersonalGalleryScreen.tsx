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
  listPersonalGallery,
  setPersonalFavorite,
  type TvPersonalGalleryBulkResult,
  type TvPersonalGalleryItem,
} from '../api/personalGallery';
import {
  countActiveFilters,
  defaultSort,
  emptyFilters,
  initialLoadState,
  pageFailed,
  pageLoaded,
  remapFocusIndex,
  removeItem,
  startLoadMore,
  startNewQuery,
  updateItem,
  type GalleryFilters,
  type GalleryLoadState,
  type GallerySort,
} from '../personal/galleryQuery';
import { FocusableMediaTile } from '../components/FocusableMediaTile';
import { FocusableButton } from '../components/FocusableButton';
import { AuthedTilePreview } from '../components/AuthedTilePreview';
import { GalleryAlbumPanel } from './gallery/GalleryAlbumPanel';
import { GalleryWorkspacePanel } from './gallery/GalleryWorkspacePanel';
import { GalleryDestinationPanel, GalleryTrashConfirmPanel } from './gallery/GallerySelectionPanels';
import { PersonalViewer } from './gallery/PersonalViewer';
import { useMenuOverlay } from '../lib/useMenuOverlay';
import { useI18n } from '../i18n';
import { tvDebug } from '../debug';
import { normalizeTvMediaAspectRatio, PHOTO_FALLBACK_ASPECT_RATIO } from '../lib/mediaAspectRatio';
import { buildTvJustifiedRows, type TvJustifiedRow } from '../lib/justifiedMediaRows';
import {
  MEDIA_GRID_FOCUS_BLEED,
  MEDIA_GRID_PACKING_GAP,
  MEDIA_GRID_VISUAL_GAP,
  mediaGridTargetRowHeight,
} from '../lib/mediaGridPresentation';
import { useTvMediaGridFocus, type TvMediaFocusTargets } from '../lib/mediaGridFocus';

// The real Personal Gallery: the owner's full image gallery (same eligibility,
// filters, search, sorting and cursor paging as the authenticated web gallery —
// the backend query layer is shared) as a TV-native virtualized grid.
//
// Interaction model (consistent with the Party grid):
//  - Default: NO chrome. DPAD moves between tiles, SELECT opens the viewer
//    (or toggles selection in selection mode).
//  - MENU: overlay with the gallery title, count + active-filter summary and
//    the contextual command rail (Search & filters / Select, or bulk actions).
//    Focus jumps to the first command; closing restores the focused tile.
//  - The unified search workspace and selection dialogs are full-screen and
//    own BACK.
//  - BACK: panel → overlay → selection mode → Personal Area home (normal
//    navigation — LOCKING happens only at the Personal Area root).
//
// Query lifecycle: (filters + sort) is the query identity. Every change bumps
// a generation; page responses for an older generation are ignored (pure
// helpers in personal/galleryQuery.ts, node-tested). The unlock-grant
// re-validation stays at the App level — this screen adds NO polling.
const PAGE_SIZE = 50;
const GRID_GAP = MEDIA_GRID_VISUAL_GAP;
// Toast (album-add confirmation) lifetime.
const TOAST_MS = 4000;

type Panel = 'none' | 'workspace' | 'albums' | 'destinations' | 'trash';

interface Props {
  // BACK from the gallery root: to the Personal Area home (no lock).
  onBack: () => void;
  onGrantInvalid: (reason?: 'pinChanged') => void;
  onSessionInvalid: () => void;
}

const GalleryTile = memo(function GalleryTile({
  item,
  index,
  total,
  width,
  height,
  preferred,
  focusTargets,
  focusable,
  selected,
  selectionMode,
  onOpen,
  onFocusIndex,
}: {
  item: TvPersonalGalleryItem;
  index: number;
  total: number;
  width: number;
  height: number;
  preferred: boolean;
  focusTargets: TvMediaFocusTargets;
  focusable: boolean;
  selected: boolean;
  selectionMode: boolean;
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
      focusable={focusable}
      onSelect={() => onOpen(index)}
      onFocusChange={(f) => { setFocused(f); if (f) onFocusIndex(index); }}
    >
      <AuthedTilePreview
        path={item.thumbnailUrl}
        personal
        style={{ width: '100%', height, borderRadius: 8 }}
      />
      {item.favorite && <Text style={styles.favBadge}>♥</Text>}
      {item.occurrenceCount > 1 && (
        <Text style={styles.dupBadge}>×{item.occurrenceCount}</Text>
      )}
      {selectionMode && (
        <View style={[styles.selectBadge, selected && styles.selectBadgeOn]} pointerEvents="none">
          <Text style={styles.selectBadgeText}>{selected ? '✓' : ''}</Text>
        </View>
      )}
      {focused && !selectionMode && (
        <View style={styles.posBadge} pointerEvents="none">
          <Text style={styles.posBadgeText}>{index + 1} / {total}</Text>
        </View>
      )}
    </FocusableMediaTile>
  );
});

export function PersonalGalleryScreen({ onBack, onGrantInvalid, onSessionInvalid }: Props) {
  const { t, tn, lang } = useI18n();
  const { width, height } = useWindowDimensions();
  const inset = overscan(width, height);

  // Query identity + accumulator.
  const [filters, setFilters] = useState<GalleryFilters>(emptyFilters);
  const [sort, setSort] = useState<GallerySort>(defaultSort);
  const [load, setLoad] = useState<GalleryLoadState<TvPersonalGalleryItem>>(initialLoadState);
  const generationRef = useRef(0);
  const loadRef = useRef(load);
  loadRef.current = load;
  const filtersRef = useRef(filters);
  filtersRef.current = filters;
  const sortRef = useRef(sort);
  sortRef.current = sort;

  // UI state.
  const [panel, setPanel] = useState<Panel>('none');
  const [selection, setSelection] = useState<string[] | null>(null);
  const [viewerIndex, setViewerIndex] = useState<number | null>(null);
  const [toast, setToast] = useState<string | null>(null);
  const {
    visible: overlayVisible,
    visibleRef: overlayVisibleRef,
    toggle: toggleOverlay,
    hide: hideOverlay,
    bump: bumpOverlay,
  } = useMenuOverlay();

  const panelRef = useRef(panel);
  panelRef.current = panel;
  const viewerOpenRef = useRef(false);
  viewerOpenRef.current = viewerIndex !== null;
  const selectionRef = useRef(selection);
  selectionRef.current = selection;

  // Focus bookkeeping (same pattern as the Party grid).
  const lastFocusedIndexRef = useRef(0);
  const [restoreIndex, setRestoreIndex] = useState<number | null>(0);
  const onTileFocus = useCallback((index: number) => {
    lastFocusedIndexRef.current = index;
    // One-shot only: a permanently preferred tile can steal focus when
    // FlatList remounts a clipped row during D-pad scrolling.
    setRestoreIndex((current) => (current === null ? current : null));
  }, []);

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

  // Initial page for the current (filters + sort) identity. Any change starts a
  // new generation; responses of older generations are ignored by the reducer.
  // Selection is dropped (ids may no longer be in the result set) and focus is
  // remapped by item id once the fresh page arrives.
  useEffect(() => {
    generationRef.current += 1;
    const gen = generationRef.current;
    const previousItems = loadRef.current.items;
    const previousFocus = lastFocusedIndexRef.current;
    setSelection((cur) => (cur !== null ? [] : null));
    setLoad(startNewQuery(gen));

    let cancelled = false;
    listPersonalGallery(filters, sort, PAGE_SIZE, null)
      .then((page) => {
        if (cancelled) return;
        setLoad((s) => pageLoaded(s, gen, page, false));
        // Generic, non-technical notice when a semantic query cannot fully run.
        if (page.semanticActive === true && page.semanticStatus === 'unavailable') {
          setToast(lang === 'it' ? 'Il motore di ricerca locale non è disponibile.' : 'The local search engine is unavailable.');
        } else if (page.semanticActive === true && page.semanticStatus === 'indexing') {
          setToast(lang === 'it'
            ? 'Le foto filtrate non sono ancora disponibili per la ricerca semantica.'
            : 'The filtered photos are not yet available for semantic search.');
        }
        const next = remapFocusIndex(previousItems, previousFocus, page.items);
        lastFocusedIndexRef.current = next;
        setRestoreIndex(next);
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        if (handleAuthError(err)) return;
        setLoad((s) => pageFailed(s, gen, false));
      });
    return () => {
      cancelled = true;
    };
  }, [filters, sort, handleAuthError]);

  // Next cursor page (grid end-reached, viewer near-end, or the explicit retry).
  const loadMore = useCallback(() => {
    const current = loadRef.current;
    const started = startLoadMore(current);
    if (started === current) return; // not in a loadable phase / no cursor
    const gen = generationRef.current;
    const cursor = current.nextCursor;
    setLoad(started);
    listPersonalGallery(filtersRef.current, sortRef.current, PAGE_SIZE, cursor)
      .then((page) => setLoad((s) => pageLoaded(s, gen, page, true)))
      .catch((err: unknown) => {
        if (handleAuthError(err)) return;
        setLoad((s) => pageFailed(s, gen, true));
      });
  }, [handleAuthError]);

  // Favorite mutation (viewer entry point): server-confirmed, then reconciled
  // into the accumulator so the grid badge + viewer button agree. When the
  // favorite FILTER is active and this toggle drops the item OUT of the current
  // result set, remove it and decrement the authoritative total instead of
  // updating it in place — so the counter never goes stale and the viewer moves
  // predictably to the next valid item (or back to the grid when none remain).
  const toggleFavorite = useCallback(async (id: string, favorite: boolean) => {
    const result = await setPersonalFavorite(id, favorite);
    const favFilter = filtersRef.current.favorite;
    setLoad((s) =>
      favFilter !== null && result.favorite !== favFilter
        ? removeItem(s, id)
        : updateItem(s, id, (it) => ({ ...it, favorite: result.favorite })));
  }, []);

  // MENU toggles the overlay only while the plain grid is up (panels and the
  // viewer own their own MENU behavior).
  const onTVEvent = useCallback((evt: HWEvent) => {
    if (!evt || evt.eventKeyAction === 0) return;
    if (panelRef.current !== 'none' || viewerOpenRef.current) return;
    tvDebug('remote', evt.eventType, 'personal-gallery overlay', overlayVisibleRef.current);
    if (evt.eventType === 'menu') toggleOverlay();
    else bumpOverlay();
  }, [toggleOverlay, bumpOverlay, overlayVisibleRef]);
  useTVEventHandler(onTVEvent);

  // BACK precedence at the gallery root (panels/viewer register their own
  // handlers and win first): overlay → selection mode → Personal Area home.
  useEffect(() => {
    const onBackPress = () => {
      if (panelRef.current !== 'none' || viewerOpenRef.current) return false;
      if (overlayVisibleRef.current) {
        hideOverlay();
        return true;
      }
      if (selectionRef.current !== null) {
        setSelection(null);
        return true;
      }
      onBack();
      return true;
    };
    const sub = BackHandler.addEventListener('hardwareBackPress', onBackPress);
    return () => sub.remove();
  }, [onBack, hideOverlay, overlayVisibleRef]);

  // Overlay closed → restore focus to the last-focused tile.
  const prevOverlayVisibleRef = useRef(false);
  useEffect(() => {
    if (prevOverlayVisibleRef.current && !overlayVisible) {
      const max = loadRef.current.items.length - 1;
      setRestoreIndex(Math.max(0, Math.min(lastFocusedIndexRef.current, max)));
    }
    prevOverlayVisibleRef.current = overlayVisible;
  }, [overlayVisible]);

  // Toast auto-dismiss.
  useEffect(() => {
    if (toast === null) return;
    const timer = setTimeout(() => setToast(null), TOAST_MS);
    return () => clearTimeout(timer);
  }, [toast]);

  const openAt = useCallback((index: number) => {
    if (selectionRef.current !== null) {
      const id = loadRef.current.items[index]?.id;
      if (!id) return;
      setSelection((cur) => {
        if (cur === null) return cur;
        return cur.includes(id) ? cur.filter((x) => x !== id) : [...cur, id];
      });
      return;
    }
    hideOverlay();
    setViewerIndex(index);
  }, [hideOverlay]);

  const closeViewer = useCallback((currentIndex: number) => {
    const max = loadRef.current.items.length - 1;
    const clamped = Math.max(0, Math.min(currentIndex, max));
    lastFocusedIndexRef.current = clamped;
    setRestoreIndex(clamped);
    setViewerIndex(null);
  }, []);

  const applyWorkspace = useCallback((nextFilters: GalleryFilters, nextSort: GallerySort) => {
    setPanel('none');
    setSort(nextSort);
    setFilters(nextFilters);
  }, []);

  const finishDestination = useCallback((result: TvPersonalGalleryBulkResult, label: string) => {
    const failed = result.failures.map((item) => item.itemId);
    setSelection(failed.length > 0 ? failed : null);
    setPanel('none');
    setToast(lang === 'it'
      ? `${label}: ${result.succeeded} aggiunte, ${result.skipped} non aggiunte.`
      : `${label}: ${result.succeeded} added, ${result.skipped} not added.`);
  }, [lang]);

  const finishTrash = useCallback((result: TvPersonalGalleryBulkResult) => {
    const removed = new Set(result.succeededItemIds);
    setLoad((current) => {
      let next = current;
      for (const id of removed) next = removeItem(next, id);
      return next;
    });
    const failed = result.failures.map((item) => item.itemId);
    setSelection(failed.length > 0 ? failed : null);
    setPanel('none');
    const remainingCount = loadRef.current.items.filter((item) => !removed.has(item.id)).length;
    const nextFocus = Math.max(0, Math.min(lastFocusedIndexRef.current, remainingCount - 1));
    lastFocusedIndexRef.current = nextFocus;
    setRestoreIndex(nextFocus);
    setToast(lang === 'it'
      ? `${result.succeeded} spostate nel Cestino, ${result.skipped} non spostate.`
      : `${result.succeeded} moved to Trash, ${result.skipped} not moved.`);
  }, [lang]);

  const items = load.items;
  const loadedCount = items.length;
  // Display total is the server-authoritative count; fall back to the loaded
  // count only until the first page of a query lands (totalCount still null).
  const total = load.totalCount ?? loadedCount;
  const activeFilterCount = countActiveFilters(filters);
  const gridInteractive = panel === 'none' && viewerIndex === null;

  const contentWidth = Math.max(1, width - 2 * inset.x);
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
        PHOTO_FALLBACK_ASPECT_RATIO,
      ),
      getId: (item) => item.id,
    }),
    [items, contentWidth, targetRowHeight],
  );
  const focusForItem = useTvMediaGridFocus(rows, GRID_GAP);

  const selectionSet = selection === null ? null : new Set(selection);
  const renderRow = useCallback(({ item: row }: ListRenderItemInfo<TvJustifiedRow<TvPersonalGalleryItem>>) => (
    <View style={styles.gridRow}>
      {row.tiles.map((tile) => (
        <GalleryTile
          key={tile.item.id}
          item={tile.item}
          index={tile.originalIndex}
          total={total}
          width={tile.width}
          height={tile.height}
          preferred={
            gridInteractive
            && !overlayVisible
            && restoreIndex !== null
            && tile.originalIndex === restoreIndex
          }
          focusTargets={focusForItem(tile.item.id)}
          focusable={gridInteractive}
          selected={selectionSet?.has(tile.item.id) ?? false}
          selectionMode={selection !== null}
          onOpen={openAt}
          onFocusIndex={onTileFocus}
        />
      ))}
    </View>
    // selectionSet derives from selection — listing selection is enough.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  ), [
    total,
    gridInteractive,
    overlayVisible,
    restoreIndex,
    selection,
    focusForItem,
    openAt,
    onTileFocus,
  ]);

  const countLabel = load.phase === 'end'
    ? tn(total, 'gallery.countLoaded')
    : t('gallery.countMore', { count: String(total) });
  const appliedPills = buildAppliedPills(filters, lang);

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
            onPress={() => setFilters((f) => ({ ...f }))}
            hasTVPreferredFocus={gridInteractive}
          />
          <FocusableButton label={t('gallery.backToHome')} onPress={onBack} />
        </View>
      ) : total === 0 ? (
        <View style={styles.stateBox}>
          <Text style={styles.body}>
            {activeFilterCount > 0 ? t('gallery.noResults') : t('gallery.empty')}
          </Text>
          {activeFilterCount > 0 && (
            <FocusableButton
              label={t('gallery.clearFilters')}
              onPress={() => setFilters(emptyFilters)}
              hasTVPreferredFocus={gridInteractive}
            />
          )}
          <FocusableButton
            label={t('gallery.backToHome')}
            onPress={onBack}
            hasTVPreferredFocus={gridInteractive && activeFilterCount === 0}
          />
        </View>
      ) : (
        <FlatList
          data={rows}
          renderItem={renderRow}
          keyExtractor={(row) => row.key}
          contentContainerStyle={[styles.grid, { paddingBottom: inset.y }]}
          initialNumToRender={6}
          maxToRenderPerBatch={4}
          windowSize={7}
          removeClippedSubviews
          onEndReachedThreshold={2}
          onEndReached={loadMore}
        />
      )}

      {/* Load-more / error strip (non-focusable except the retry). */}
      {load.phase === 'loadingMore' && total > 0 && (
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

      {!overlayVisible && gridInteractive && activeFilterCount > 0 && (
        <View style={[styles.appliedStatus, { top: inset.y }]} pointerEvents="none">
          <Text style={styles.appliedStatusText}>
            {lang === 'it'
              ? `${total} foto · ${activeFilterCount} filtri`
              : `${total} photos · ${activeFilterCount} filters`}
          </Text>
        </View>
      )}

      {!overlayVisible && gridInteractive && selection !== null && (
        <View style={[styles.selectionStatus, { top: inset.y }]} pointerEvents="none">
          <Text style={styles.selectionStatusText}>{tn(selection.length, 'gallery.selectedCount')}</Text>
        </View>
      )}

      {toast !== null && (
        <View style={[styles.toast, { top: inset.y }]} pointerEvents="none">
          <Text style={styles.toastText} numberOfLines={2}>{toast}</Text>
        </View>
      )}

      {/* MENU overlay (grid only). */}
      {overlayVisible && gridInteractive && (
        <>
          <View style={[styles.titleRow, { top: inset.y }]} pointerEvents="none">
            <View style={styles.titlePill}>
              <Text style={styles.titleText} numberOfLines={1}>
                {t('gallery.title')} · {countLabel}
              </Text>
              {appliedPills.length > 0 && (
                <View style={styles.filterPillRow}>
                  {appliedPills.slice(0, 3).map((pill) => (
                    <Text key={pill} style={styles.filterPill} numberOfLines={1}>{pill}</Text>
                  ))}
                  {appliedPills.length > 3 && (
                    <Text style={styles.filterPill}>+{appliedPills.length - 3}</Text>
                  )}
                </View>
              )}
            </View>
          </View>
          <View style={[styles.commandBar, { left: inset.x, right: inset.x, bottom: inset.y }]}>
            {selection === null ? (
              <>
                <FocusableButton
                  label={t('gallery.backToHome')}
                  onPress={onBack}
                  onFocusChange={(f) => { if (f) bumpOverlay(); }}
                  hasTVPreferredFocus
                />
                <FocusableButton
                  label={lang === 'it' ? 'Ricerca e filtri' : 'Search and filters'}
                  onPress={() => { hideOverlay(); setPanel('workspace'); }}
                  onFocusChange={(f) => { if (f) bumpOverlay(); }}
                />
                <FocusableButton
                  label={t('gallery.select')}
                  onPress={() => { setSelection([]); hideOverlay(); }}
                  onFocusChange={(f) => { if (f) bumpOverlay(); }}
                />
              </>
            ) : (
              <>
                <FocusableButton
                  label={lang === 'it' ? 'Album' : 'Album'}
                  onPress={() => { hideOverlay(); setPanel('albums'); }}
                  disabled={selection.length === 0}
                  onFocusChange={(f) => { if (f) bumpOverlay(); }}
                  hasTVPreferredFocus
                />
                <FocusableButton
                  label={lang === 'it' ? 'Aggiungi a' : 'Add to'}
                  onPress={() => { hideOverlay(); setPanel('destinations'); }}
                  disabled={selection.length === 0}
                  onFocusChange={(f) => { if (f) bumpOverlay(); }}
                />
                <FocusableButton
                  label={lang === 'it' ? 'Cestino' : 'Trash'}
                  onPress={() => { hideOverlay(); setPanel('trash'); }}
                  disabled={selection.length === 0}
                  onFocusChange={(f) => { if (f) bumpOverlay(); }}
                />
                <FocusableButton
                  label={lang === 'it' ? 'Azzera selezione' : 'Clear selection'}
                  onPress={() => { setSelection(null); hideOverlay(); }}
                  onFocusChange={(f) => { if (f) bumpOverlay(); }}
                />
              </>
            )}
          </View>
        </>
      )}

      {panel === 'workspace' && (
        <GalleryWorkspacePanel
          appliedFilters={filters}
          appliedSort={sort}
          resultCount={total}
          onApply={applyWorkspace}
          onCancel={() => setPanel('none')}
          onAuthError={handleAuthError}
        />
      )}
      {panel === 'albums' && selection !== null && (
        <GalleryAlbumPanel
          fileItemIds={selection}
          onDone={(result, albumName) => {
            setPanel('none');
            setSelection(result.skipped > 0 ? selection : null);
            setToast(t('gallery.albumAdded', {
              added: String(result.succeeded),
              skipped: String(result.skipped),
              album: albumName,
            }));
          }}
          onCancel={() => setPanel('none')}
          onAuthError={handleAuthError}
        />
      )}
      {panel === 'destinations' && selection !== null && (
        <GalleryDestinationPanel
          fileItemIds={selection}
          onDone={finishDestination}
          onCancel={() => setPanel('none')}
          onAuthError={handleAuthError}
        />
      )}
      {panel === 'trash' && selection !== null && (
        <GalleryTrashConfirmPanel
          fileItemIds={selection}
          onDone={(result) => finishTrash(result)}
          onCancel={() => setPanel('none')}
          onAuthError={handleAuthError}
        />
      )}

      {viewerIndex !== null && items.length > 0 && (
        <PersonalViewer
          items={items}
          startIndex={Math.min(viewerIndex, items.length - 1)}
          totalCount={total}
          hasMore={load.nextCursor !== null}
          loadingMore={load.phase === 'loadingMore'}
          onNeedMore={loadMore}
          onClose={closeViewer}
          onToggleFavorite={toggleFavorite}
          onAuthError={handleAuthError}
        />
      )}
    </View>
  );
}

function buildAppliedPills(filters: GalleryFilters, lang: 'it' | 'en'): string[] {
  const pills: string[] = [];
  if (filters.semanticQuery.trim()) pills.push(filters.semanticQuery.trim());
  if (filters.q) pills.push(filters.q);
  const people = Object.keys(filters.people).length;
  if (people > 0) pills.push(lang === 'it' ? `${people} persone` : `${people} people`);
  if (filters.dateFrom || filters.dateTo) pills.push(`${filters.dateFrom || '…'} → ${filters.dateTo || '…'}`);
  if (filters.favorite !== null) pills.push(filters.favorite
    ? (lang === 'it' ? 'Preferite' : 'Favorites')
    : (lang === 'it' ? 'Non preferite' : 'Not favorites'));
  if (filters.minRating !== null) pills.push(`★ ${filters.minRating}+`);
  if (filters.hasGps !== null) pills.push(filters.hasGps ? 'GPS ✓' : 'GPS —');
  if (filters.collapseDuplicates) pills.push(lang === 'it' ? 'Senza duplicati' : 'No duplicates');
  return pills;
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.bg },
  body: { color: colors.muted, fontSize: font.body, textAlign: 'center' },
  stateBox: { marginTop: spacing.xl, alignItems: 'center', gap: spacing.md },
  grid: { gap: GRID_GAP },
  gridRow: {
    flexDirection: 'row',
    gap: GRID_GAP,
    paddingVertical: MEDIA_GRID_FOCUS_BLEED,
    overflow: 'visible',
  },
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
  selectBadge: {
    position: 'absolute',
    top: spacing.xs,
    right: spacing.xs,
    width: 34,
    height: 34,
    borderRadius: 17,
    borderWidth: 2,
    borderColor: '#ffffff',
    backgroundColor: 'rgba(0,0,0,0.55)',
    alignItems: 'center',
    justifyContent: 'center',
  },
  selectBadgeOn: { backgroundColor: colors.accent, borderColor: '#ffffff' },
  selectBadgeText: { color: '#ffffff', fontSize: 20, fontWeight: '900' },
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
  filterPillRow: { flexDirection: 'row', justifyContent: 'center', gap: spacing.xs, marginTop: spacing.xs },
  filterPill: {
    maxWidth: 260,
    color: colors.text,
    fontSize: font.caption,
    paddingHorizontal: spacing.sm,
    paddingVertical: 2,
    borderRadius: 999,
    backgroundColor: 'rgba(37,99,235,0.72)',
  },
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
  selectionStatus: {
    position: 'absolute',
    left: spacing.md,
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.xs,
    borderRadius: 999,
    backgroundColor: 'rgba(37,99,235,0.88)',
  },
  selectionStatusText: { color: '#ffffff', fontSize: font.caption, fontWeight: '900' },
  toast: {
    position: 'absolute',
    alignSelf: 'center',
    maxWidth: '70%',
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.xs,
    borderRadius: 12,
    backgroundColor: 'rgba(0,0,0,0.85)',
  },
  toastText: { color: colors.text, fontSize: font.body },
});
