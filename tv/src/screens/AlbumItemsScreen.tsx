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
import { colors, font, overlayQrSize, overscan, spacing } from '../theme';
import {
  clearTvActiveFaceSearch,
  getTvActiveFaceSearch,
  listTvAlbumItems,
  type TvAlbum,
  type TvAlbumItem,
  type TvAlbumItems,
} from '../api/tv';
import { ApiError } from '../api/client';
import { FocusableMediaTile } from '../components/FocusableMediaTile';
import { FocusableButton } from '../components/FocusableButton';
import { AuthedTilePreview } from '../components/AuthedTilePreview';
import { FaceFilterIndicator } from '../components/FaceFilterIndicator';
import { OverlayQrCorners } from '../components/OverlayQrCorners';
import { useMenuOverlay } from '../lib/useMenuOverlay';
import { sameItemIds } from '../lib/liveItems';
import { getTvMediaAspectRatio } from '../lib/mediaAspectRatio';
import { buildTvJustifiedRows, type TvJustifiedRow } from '../lib/justifiedMediaRows';
import {
  MEDIA_GRID_FOCUS_BLEED,
  MEDIA_GRID_PACKING_GAP,
  MEDIA_GRID_VISUAL_GAP,
  mediaGridTargetRowHeight,
} from '../lib/mediaGridPresentation';
import { useTvMediaGridFocus, type TvMediaFocusTargets } from '../lib/mediaGridFocus';
import { remapFocusIndexById } from '../lib/focusRemap';
import { useI18n } from '../i18n';
import { tvDebug } from '../debug';

// Live-refresh interval for an open PartyMode album's items so guest uploads
// appear on the TV without reopening the album (10-20s band).
const PARTY_ITEMS_POLL_MS = 15_000;

// Poll interval for an open party album's active "find your face" search so the
// TV switches to the matching subset promptly.
const FACE_SEARCH_POLL_MS = 6_000;

// Grid geometry. Photos/videos flow into justified rows (each row spans the
// content width; tiles keep their REAL aspect ratio — a vertical video is a
// vertical tile). The target row height is derived from the surface height so a
// 1080p-class layout shows ~3.5-4 rows and a 720p-class layout at least 3. The
// FlatList virtualizes ROWS, so only tiles near the viewport mount (and download).
const GRID_GAP = MEDIA_GRID_VISUAL_GAP;
const ROW_FOCUS_BLEED = MEDIA_GRID_FOCUS_BLEED;

interface Props {
  album: TvAlbum;
  onBack: () => void;
  onOpenItem: (
    items: TvAlbumItem[],
    index: number,
    autoPlay: boolean,
    context: {
      albumId: string;
      partyEnabled: boolean;
      partyUrl: string | null;
      partyUploadUrl: string | null;
    },
  ) => void;
  // The limited TV session was revoked/expired (401) → return to pairing.
  onSessionInvalid: () => void;
}

// One justified grid tile: image-dominant, NO filename/metadata text, sized to
// the item's real aspect ratio by the layout. The focused tile shows a small
// "position / total" badge (context without a filename). Memoized so a state
// change elsewhere on the screen (polls, another tile's image completing) does
// not re-render every tile.
const ItemTile = memo(function ItemTile({
  item,
  index,
  total,
  width,
  height,
  preferred,
  focusTargets,
  onOpen,
  onFocusIndex,
}: {
  item: TvAlbumItem;
  index: number;
  total: number;
  // Outer tile box from the justified layout.
  width: number;
  height: number;
  preferred: boolean;
  focusTargets: TvMediaFocusTargets;
  onOpen: (index: number) => void;
  // Reports which tile the remote is on (index + id), so a later transition can
  // restore focus to the SAME item even after the list/rows change.
  onFocusIndex: (index: number, id: string) => void;
}) {
  const [focused, setFocused] = useState(false);
  const isVideo = item.mediaType === 'video';
  // Videos show the source-aspect poster (fallback thumbnail); photos stay on
  // the small grid thumbnail, never the medium viewer preview.
  const path = isVideo ? (item.posterUrl ?? item.thumbnailUrl) : item.thumbnailUrl;
  const fallbackPath = isVideo ? item.thumbnailUrl : null;
  return (
    <FocusableMediaTile
      accessibilityLabel={item.name}
      style={{ width }}
      hasTVPreferredFocus={preferred}
      focusTargets={focusTargets}
      onSelect={() => onOpen(index)}
      onFocusChange={(f) => { setFocused(f); if (f) onFocusIndex(index, item.id); }}
    >
      <AuthedTilePreview
        path={path}
        fallbackPath={fallbackPath}
        style={{ width: '100%', height, borderRadius: 8 }}
      />
      {isVideo && <Text style={styles.badge}>▶</Text>}
      {focused && (
        <View style={styles.posBadge} pointerEvents="none">
          <Text style={styles.posBadgeText}>{index + 1} / {total}</Text>
        </View>
      )}
    </FocusableMediaTile>
  );
});

// Lists the media in one allowlisted album as a dense, image-only grid that
// starts near the top of the screen.
//
// Interaction model (consistent with the slideshow):
//  - Default: NO chrome — no header, no QR, no menu. The grid owns the screen;
//    SELECT on the focused tile opens the photo/slideshow.
//  - MENU shows the overlay: party QR top-left, album title top-center, party
//    upload QR top-right, and a compact command bar (Albums / Slideshow) at the
//    bottom — all inside the overscan safe area, auto-hidden after ~6s idle.
//    Focus jumps EXPLICITLY to the first bar command (LEFT/RIGHT move between
//    commands, SELECT activates, UP returns into the grid). MENU hides it
//    again; hardware BACK hides it first, then returns to the album list.
//    Closing the overlay restores focus to the previously focused tile.
//
// A 404 (album disabled between listing and opening) routes back to the album
// list. When the album is in PartyMode, the item list is polled so guest
// uploads appear live.
export function AlbumItemsScreen({ album, onBack, onOpenItem, onSessionInvalid }: Props) {
  const { t } = useI18n();
  const { width, height } = useWindowDimensions();
  const inset = overscan(width, height);
  const qrSize = overlayQrSize(height);
  const [detail, setDetail] = useState<TvAlbumItems | null>(null);
  const [error, setError] = useState(false);
  // Face-filter mode: non-null when a guest EXPLICITLY activated a face search
  // for this TV ("Show these photos on TV"). Carries the search id (needed to
  // delete it on BACK), the indicator crop URL, and the matching subset the
  // grid/slideshow is filtered to. No names/scores/identity data.
  const [faceFilter, setFaceFilter] = useState<{
    searchId: string;
    faceThumbnailUrl: string | null;
    items: TvAlbumItem[];
  } | null>(null);
  const faceFilterRef = useRef(faceFilter);
  useEffect(() => { faceFilterRef.current = faceFilter; }, [faceFilter]);
  const {
    visible: overlayVisible,
    visibleRef: overlayVisibleRef,
    toggle: toggleOverlay,
    hide: hideOverlay,
    bump: bumpOverlay,
  } = useMenuOverlay();

  // MENU is the ONLY command that shows/hides the overlay (KEYCODE_MENU → the
  // 'menu' eventType). All other remote activity (D-pad focus moves, SELECT on
  // tiles) is owned by the native focus engine; while the overlay is up it just
  // re-arms the auto-hide window. Fire TV dispatches on key-up only, so ignore
  // explicit key-downs (see ReactAndroidHWInputDeviceHelper).
  const onTVEvent = useCallback((evt: HWEvent) => {
    if (!evt || evt.eventKeyAction === 0) return;
    tvDebug('remote', evt.eventType, 'grid-overlay', overlayVisibleRef.current);
    if (evt.eventType === 'menu') toggleOverlay();
    else bumpOverlay();
  }, [toggleOverlay, bumpOverlay, overlayVisibleRef]);
  useTVEventHandler(onTVEvent);

  // BACK / "show all photos" while face-filter mode is active: delete THIS
  // search server-side (session + stored face crop; id-scoped and idempotent, so
  // a concurrent phone-side cancel or a newer activation is never disturbed) and
  // restore the full album grid locally.
  const exitFaceFilter = useCallback(() => {
    const current = faceFilterRef.current;
    setFaceFilter(null);
    void clearTvActiveFaceSearch(album.id, current?.searchId).catch(() => { /* best effort */ });
  }, [album.id]);

  // Hardware Back: first press hides the overlay; then exits face-filter mode
  // (restoring the full album); only then back to the albums.
  useEffect(() => {
    const onBackPress = () => {
      if (overlayVisibleRef.current) {
        hideOverlay();
        return true;
      }
      if (faceFilterRef.current) {
        exitFaceFilter();
        return true;
      }
      onBack();
      return true;
    };
    const sub = BackHandler.addEventListener('hardwareBackPress', onBackPress);
    return () => sub.remove();
  }, [onBack, hideOverlay, overlayVisibleRef, exitFaceFilter]);

  // Focus moving onto overlay controls also counts as activity.
  const bumpOnFocus = useCallback((focused: boolean) => {
    if (focused) bumpOverlay();
  }, [bumpOverlay]);

  // Explicit overlay focus mode. The remote must never have to spatially hunt
  // for the bottom command bar:
  //  - lastFocusedIndexRef tracks the tile the user was on (updated by every
  //    tile focus, including while the overlay is up — e.g. after pressing UP
  //    from the bar back into the grid);
  //  - when the overlay OPENS, the first bar command takes focus via its
  //    mount-time hasTVPreferredFocus (and no tile keeps a preferred flag);
  //  - when the overlay CLOSES (MENU, BACK, auto-hide or a command), the
  //    previously focused tile's hasTVPreferredFocus flips false→true, which
  //    natively calls requestFocus() on the mounted view (verified in
  //    ReactViewManager.setTVPreferredFocus — it acts on every change to true).
  // The remote's position is tracked by INDEX (for the current display list) and
  // by ITEM ID (stable across list/row rebuilds), so focus can be restored to
  // the same photo after a face-filter swap / live append / width change.
  const lastFocusedIndexRef = useRef(0);
  const lastFocusedIdRef = useRef<string | null>(null);
  const [restoreIndex, setRestoreIndex] = useState<number | null>(0);
  const onTileFocus = useCallback((index: number, id: string) => {
    lastFocusedIndexRef.current = index;
    lastFocusedIdRef.current = id;
    // hasTVPreferredFocus is a one-shot restoration request. Leaving it true on
    // a clipped row lets Android request focus again when that row remounts.
    setRestoreIndex((current) => (current === null ? current : null));
  }, []);

  useEffect(() => {
    let cancelled = false;
    const t0 = Date.now();
    listTvAlbumItems(album.id)
      .then((d) => {
        tvDebug('album-open items', d.items.length, 'ms', Date.now() - t0);
        if (!cancelled) setDetail(d);
      })
      .catch((err) => {
        if (cancelled) return;
        if (err instanceof ApiError && err.status === 404) {
          onBack();
          return;
        }
        if (err instanceof ApiError && err.status === 401) {
          onSessionInvalid();
          return;
        }
        setError(true);
      });
    return () => {
      cancelled = true;
    };
  }, [album.id, onBack, onSessionInvalid]);

  // Live refresh of the grid while a PartyMode album is open. Adopts the fresh
  // server list (stable append order); a 404 (party/ShowOnTv revoked) drops back
  // to the album list, a 401 invalidates the session. Only re-subscribes when
  // the party flag flips (not on every fetch).
  const partyOpen = detail?.partyEnabled ?? false;
  useEffect(() => {
    if (!partyOpen) return;
    const timer = setInterval(() => {
      listTvAlbumItems(album.id)
        .then((d) => {
          setDetail((prev) => (prev && sameItemIds(prev.items, d.items)
            && prev.partyEnabled === d.partyEnabled
            && prev.partyUrl === d.partyUrl
            && prev.partyUploadUrl === d.partyUploadUrl
            ? prev
            : d));
        })
        .catch((err) => {
          if (err instanceof ApiError && err.status === 404) { onBack(); return; }
          if (err instanceof ApiError && err.status === 401) { onSessionInvalid(); }
          // transient: keep the current grid
        });
    }, PARTY_ITEMS_POLL_MS);
    return () => clearInterval(timer);
  }, [partyOpen, album.id, onBack, onSessionInvalid]);

  // Poll the album's active party face filter. Only an EXPLICITLY activated
  // search ever arrives here; the newest server-accepted activation replaces the
  // previous one (server-side ordering). When it clears/expires/is deleted, the
  // grid returns to the full album. Only while a PartyMode album is open.
  useEffect(() => {
    if (!partyOpen) {
      setFaceFilter(null);
      return;
    }
    let cancelled = false;
    const poll = () => {
      getTvActiveFaceSearch(album.id)
        .then((active) => {
          if (cancelled) return;
          setFaceFilter((prev) => {
            if (active.active && active.searchId && active.items.length > 0) {
              return prev && prev.searchId === active.searchId
                && prev.faceThumbnailUrl === active.faceThumbnailUrl
                && sameItemIds(prev.items, active.items)
                ? prev
                : {
                  searchId: active.searchId,
                  faceThumbnailUrl: active.faceThumbnailUrl,
                  items: active.items,
                };
            }
            return prev ? null : prev;
          });
        })
        .catch((err) => {
          if (err instanceof ApiError && err.status === 401) { onSessionInvalid(); }
          // transient / 404: keep current state
        });
    };
    poll();
    const timer = setInterval(poll, FACE_SEARCH_POLL_MS);
    return () => { cancelled = true; clearInterval(timer); };
  }, [partyOpen, album.id, onSessionInvalid]);

  // The grid + slideshow are filtered to the matching subset while face-filter
  // mode is active, and restored to the full album when it exits.
  const displayItems = faceFilter?.items ?? detail?.items ?? [];

  // Latest list/context in refs so openAt stays IDENTITY-STABLE: renderItem and
  // every memoized tile survive poll re-renders without re-rendering.
  const displayItemsRef = useRef(displayItems);
  displayItemsRef.current = displayItems;
  const detailRef = useRef(detail);
  detailRef.current = detail;

  // Face-filter transitions preserve the user's position: keep focus on the
  // focused photo when it is still shown in the new display list, otherwise
  // focus the first (matching) photo. Changing restoreIndex flips that tile's
  // hasTVPreferredFocus false→true, which natively pulls focus to it.
  const prevFaceFilterRef = useRef(faceFilter);
  useEffect(() => {
    const prev = prevFaceFilterRef.current;
    prevFaceFilterRef.current = faceFilter;
    if (prev === faceFilter) return;
    const full = detailRef.current?.items ?? [];
    const prevItems = prev?.items ?? full;
    const nextItems = faceFilter?.items ?? full;
    // Keep the user on the same photo when it survives the transition, else the
    // first (matching) photo.
    const nextIndex = remapFocusIndexById(prevItems, lastFocusedIndexRef.current, nextItems);
    lastFocusedIndexRef.current = nextIndex;
    lastFocusedIdRef.current = nextItems[nextIndex]?.id ?? null;
    setRestoreIndex(nextIndex);
  }, [faceFilter]);

  // Overlay just closed → point the restore flag at the last-focused tile
  // (clamped in case the list shrank meanwhile, e.g. a face filter kicked in).
  const prevOverlayVisibleRef = useRef(false);
  useEffect(() => {
    if (prevOverlayVisibleRef.current && !overlayVisible) {
      // Prefer the id (the list may have changed while the overlay was up, e.g.
      // a face filter kicked in); fall back to the clamped last index.
      const items = displayItemsRef.current;
      const byId = lastFocusedIdRef.current
        ? items.findIndex((it) => it.id === lastFocusedIdRef.current)
        : -1;
      const idx = byId >= 0 ? byId : Math.min(lastFocusedIndexRef.current, items.length - 1);
      setRestoreIndex(Math.max(0, idx));
    }
    prevOverlayVisibleRef.current = overlayVisible;
  }, [overlayVisible]);

  const openAt = useCallback((index: number, autoPlay = false) => {
    const d = detailRef.current;
    if (!d) return;
    onOpenItem(displayItemsRef.current, index, autoPlay, {
      albumId: album.id,
      partyEnabled: d.partyEnabled,
      partyUrl: d.partyUrl,
      partyUploadUrl: d.partyUploadUrl,
    });
  }, [album.id, onOpenItem]);

  // Justified rows from the real aspect ratios. `useWindowDimensions()` gives the
  // real surface geometry from first paint (no invented width), and the target
  // row height scales with the surface so ~3.5-4 rows show on 1080p and ≥3 on
  // 720p, clamped to a prudent band.
  const contentWidth = Math.max(1, width - 2 * inset.x);
  const targetRowHeight = mediaGridTargetRowHeight(height);
  const total = displayItems.length;
  const rows = useMemo(
    () => buildTvJustifiedRows({
      items: displayItems,
      contentWidth,
      targetRowHeight,
      gap: GRID_GAP,
      packingGap: MEDIA_GRID_PACKING_GAP,
      getAspectRatio: getTvMediaAspectRatio,
      getId: (it) => it.id,
    }),
    [displayItems, contentWidth, targetRowHeight],
  );
  const focusForItem = useTvMediaGridFocus(rows, GRID_GAP);

  const renderRow = useCallback(({ item: row }: ListRenderItemInfo<TvJustifiedRow<TvAlbumItem>>) => (
    <View style={styles.row}>
      {row.tiles.map((tile) => (
        <ItemTile
          key={tile.item.id}
          item={tile.item}
          index={tile.originalIndex}
          total={total}
          width={tile.width}
          height={tile.height}
          // No tile holds the preferred flag while the overlay is up (the bar's
          // first command takes it); on close the restore tile flips false→true,
          // pulling focus back to where the user was.
          preferred={
            !overlayVisible
            && restoreIndex !== null
            && tile.originalIndex === restoreIndex
          }
          focusTargets={focusForItem(tile.item.id)}
          onOpen={openAt}
          onFocusIndex={onTileFocus}
        />
      ))}
    </View>
  ), [openAt, total, overlayVisible, restoreIndex, focusForItem, onTileFocus]);

  return (
    <View style={[styles.container, { paddingTop: inset.y, paddingHorizontal: inset.x }]}>
      {error ? (
        <View style={styles.stateBox}>
          <Text style={styles.body}>{t('items.openError')}</Text>
          <FocusableButton label={t('items.backToAlbums')} onPress={onBack} hasTVPreferredFocus />
        </View>
      ) : detail === null ? (
        <ActivityIndicator size="large" color={colors.accent} style={styles.stateBox} />
      ) : displayItems.length === 0 ? (
        <View style={styles.stateBox}>
          <Text style={styles.body}>{t('items.empty')}</Text>
          <FocusableButton label={t('items.backToAlbums')} onPress={onBack} hasTVPreferredFocus />
        </View>
      ) : (
        <FlatList
          data={rows}
          renderItem={renderRow}
          keyExtractor={(row) => row.key}
          contentContainerStyle={[styles.grid, { paddingBottom: inset.y }]}
          // Virtualization: mount (and download media for) only ROWS near the
          // viewport, progressively — never the whole album at once. Tuned for
          // rows (~3.5-4 visible) rather than the old per-column count.
          initialNumToRender={6}
          maxToRenderPerBatch={4}
          windowSize={7}
          removeClippedSubviews
        />
      )}

      {/* MENU overlay: QR corners + centered album title on top, compact command
          bar at the bottom. Absolute-positioned — it never reflows or shrinks the
          grid. EXPLICIT focus mode: the first bar command takes focus on mount
          (no spatial hunting past the last row), LEFT/RIGHT move between the
          commands, SELECT activates one, UP returns into the grid; closing the
          overlay (MENU/BACK/auto-hide) restores focus to the previous tile. */}
      {overlayVisible && (
        <>
          {detail?.partyEnabled && (
            <OverlayQrCorners
              partyUrl={detail.partyUrl}
              partyUploadUrl={detail.partyUploadUrl}
              insetX={inset.x}
              insetY={inset.y}
              qrSize={qrSize}
            />
          )}

          <View style={[styles.titleRow, { top: inset.y }]} pointerEvents="none">
            <View style={styles.titlePill}>
              <Text style={styles.titleText} numberOfLines={1}>{album.name}</Text>
            </View>
          </View>

          {/* Face-filter indicator (same shared component as the slideshow):
              visible only while face-filter mode is active AND the overlay is
              up. Non-focusable; below the title row so nothing existing moves. */}
          {faceFilter !== null && (
            <View style={[styles.faceIndicatorRow, { top: inset.y + 56 }]} pointerEvents="none">
              <FaceFilterIndicator
                faceThumbnailUrl={faceFilter.faceThumbnailUrl}
                albumName={album.name}
              />
            </View>
          )}

          <View style={[styles.commandBar, { left: inset.x, right: inset.x, bottom: inset.y }]}>
            <FocusableButton
              label={t('items.backToAlbums')}
              onPress={onBack}
              onFocusChange={bumpOnFocus}
              hasTVPreferredFocus
            />
            {detail !== null && displayItems.length > 0 && (
              <FocusableButton
                label={t('items.slideshow')}
                onPress={() => openAt(0, true)}
                onFocusChange={bumpOnFocus}
              />
            )}
            {faceFilter !== null && (
              <FocusableButton label={t('items.faceShowAll')} onPress={exitFaceFilter} onFocusChange={bumpOnFocus} />
            )}
          </View>
        </>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.bg },
  body: { color: colors.muted, fontSize: font.body },
  stateBox: { marginTop: spacing.xl, alignItems: 'center', gap: spacing.md },
  grid: { gap: GRID_GAP },
  // A justified row: tiles laid left→right at their computed widths, with a
  // little vertical bleed so the focused tile's scale is not clipped.
  row: {
    flexDirection: 'row',
    gap: GRID_GAP,
    paddingVertical: ROW_FOCUS_BLEED,
    overflow: 'visible',
  },
  badge: {
    position: 'absolute',
    top: spacing.xs,
    right: spacing.xs,
    color: colors.text,
    fontSize: font.body,
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
  faceIndicatorRow: { position: 'absolute', left: 0, right: 0, alignItems: 'center' },
  titleRow: { position: 'absolute', left: 0, right: 0, alignItems: 'center' },
  titlePill: {
    maxWidth: '46%',
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.xs,
    borderRadius: 12,
    backgroundColor: 'rgba(0,0,0,0.78)',
  },
  titleText: { color: colors.text, fontSize: font.heading, fontWeight: '700' },
  commandBar: {
    position: 'absolute',
    flexDirection: 'row',
    alignItems: 'center',
    gap: spacing.sm,
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.sm,
    borderRadius: 14,
    backgroundColor: 'rgba(0,0,0,0.78)',
  },
});
