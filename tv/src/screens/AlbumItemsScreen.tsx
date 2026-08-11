import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ActivityIndicator,
  BackHandler,
  FlatList,
  StyleSheet,
  Text,
  TVFocusGuideView,
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
import { MenuCommandRail } from '../components/MenuCommandRail';
import { MediaTilePreview } from '../components/MediaTilePreview';
import { FaceFilterIndicator } from '../components/FaceFilterIndicator';
import { OverlayQrCorners } from '../components/OverlayQrCorners';
import { useMenuOverlay } from '../lib/useMenuOverlay';
import { sameItemIds } from '../lib/liveItems';
import {
  buildTvMediaGridRows,
  tvMediaGridTargetHeight,
  TV_MEDIA_GRID_BATCH_ROWS,
  TV_MEDIA_GRID_FOCUS_BLEED,
  TV_MEDIA_GRID_GAP,
  TV_MEDIA_GRID_INITIAL_ROWS,
  TV_MEDIA_GRID_WINDOW_SIZE,
  type TvMediaGridRow,
} from '../lib/tvMediaGrid';
import { getTvMediaAspectRatio } from '../lib/mediaAspectRatio';
import { useTvGridFocusMemory } from '../lib/mediaMenuFocus';
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
const GRID_GAP = TV_MEDIA_GRID_GAP;
const ROW_FOCUS_BLEED = TV_MEDIA_GRID_FOCUS_BLEED;

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
  focusable,
  onOpen,
  onFocusIndex,
}: {
  item: TvAlbumItem;
  index: number;
  total: number;
  width: number;
  height: number;
  preferred: boolean;
  // False while the MENU command rail owns focus: the grid must not stay a
  // focus destination underneath it.
  focusable: boolean;
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
      focusable={focusable}
      onSelect={() => onOpen(index)}
      onFocusChange={(f) => { setFocused(f); if (f) onFocusIndex(index, item.id); }}
    >
      <MediaTilePreview
        kind={isVideo ? 'video' : 'image'}
        path={path}
        fallbackPath={fallbackPath}
        style={{ width: '100%', height, borderRadius: 8 }}
      />
      {isVideo && <Text style={styles.badge}>▶</Text>}
      {focused && focusable && (
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
//    The bar is a MODAL focus scope: focus moves to the first command, LEFT and
//    RIGHT move between commands, SELECT activates one, and no direction can
//    leave the bar — grid navigation is suspended while it is open. MENU hides
//    it again; hardware BACK hides it first, then returns to the album list.
//    Closing the overlay restores focus to the EXACT previously focused tile.
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

  // Focus OWNERSHIP. While the overlay is up the command rail owns focus and
  // the grid stops being a focus destination entirely; the exact tile the user
  // was on is remembered by id (with the index as the fallback) and restored
  // when the overlay closes for ANY reason — MENU again, BACK, the idle
  // auto-hide, or a command. `restoreIndex` is a ONE-SHOT request: leaving it
  // set on a clipped row is what used to let Android request focus again when
  // that row remounted.
  // The three callbacks are identity-stable; only `restoreIndex` re-renders.
  const {
    restoreIndex, onTileFocused: rememberFocusedTile, restoreTo, read: readGridFocus,
  } = useTvGridFocusMemory(overlayVisible, displayItems);
  // The single expression that says whether the grid is a focus destination at
  // all. A tile may only ASK for focus when it could accept it.
  const gridFocusable = !overlayVisible;

  const contentWidth = Math.max(1, width - 2 * inset.x);
  const total = displayItems.length;
  const rows = useMemo(
    () => buildTvMediaGridRows({
      items: displayItems,
      contentWidth,
      targetRowHeight: tvMediaGridTargetHeight(height - 2 * inset.y),
      getAspectRatio: getTvMediaAspectRatio,
      getId: (item) => item.id,
    }),
    [displayItems, contentWidth, height, inset.y],
  );
  const onTileFocus = useCallback((index: number, id: string) => {
    rememberFocusedTile(index, id);
  }, [rememberFocusedTile]);

  // Face-filter transitions preserve the user's position: keep focus on the
  // focused photo when it is still shown in the new display list, otherwise
  // focus the first (matching) photo.
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
    const nextIndex = remapFocusIndexById(prevItems, readGridFocus().index, nextItems);
    restoreTo(nextIndex, nextItems[nextIndex]?.id ?? null);
  }, [faceFilter, restoreTo, readGridFocus]);

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

  const renderRow = useCallback(({
    item: row,
  }: ListRenderItemInfo<TvMediaGridRow<TvAlbumItem>>) => {
    return (
      <TVFocusGuideView
        style={styles.row}
        scrollSnapAlign="start"
        trapFocusLeft
        trapFocusRight
      >
        {row.tiles.map((tile) => {
          const { item, originalIndex: index, width, height: tileHeight } = tile;
          return (
            <ItemTile
              key={item.id}
              item={item}
              index={index}
              total={total}
              width={width}
              height={tileHeight}
              // No tile holds preferred focus while the overlay owns it.
              preferred={gridFocusable && restoreIndex !== null && index === restoreIndex}
              focusable={gridFocusable}
              onOpen={openAt}
              onFocusIndex={onTileFocus}
            />
          );
        })}
      </TVFocusGuideView>
    );
  }, [openAt, total, gridFocusable, restoreIndex, onTileFocus]);

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
          // Geometry is known before thumbnails decode. Native TV focus owns
          // navigation, while the bounded window keeps the next rows mounted.
          initialNumToRender={TV_MEDIA_GRID_INITIAL_ROWS}
          maxToRenderPerBatch={TV_MEDIA_GRID_BATCH_ROWS}
          windowSize={TV_MEDIA_GRID_WINDOW_SIZE}
          removeClippedSubviews={false}
          snapToAlignment="item"
          scrollAnimationEnabled={false}
        />
      )}

      {/* MENU overlay: QR corners + centered album title on top, compact command
          bar at the bottom. Absolute-positioned — it never reflows or shrinks the
          grid. The bar is a real focus SCOPE (MenuCommandRail): the first
          command takes focus, the four traps keep every direction inside it, and
          the grid below is switched non-focusable, so nothing there — including a
          row the FlatList mounts meanwhile — can take focus back. Closing the
          overlay (MENU/BACK/auto-hide/command) restores the exact tile. */}
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

          <MenuCommandRail
            style={[styles.commandBar, { left: inset.x, right: inset.x, bottom: inset.y }]}
          >
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
          </MenuCommandRail>
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
