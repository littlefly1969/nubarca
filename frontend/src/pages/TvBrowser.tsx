import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import {
  ApiError,
  clearTvActiveFaceSearch,
  getTvActiveFaceSearch,
  listTvAlbumItems,
  listTvAlbums,
  type TvAlbum,
  type TvAlbumItem,
  type TvAlbumItems,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { VideoPreview } from '../video/VideoPreview';
import { TvViewer } from '../tv/party/TvViewer';
import { TvFaceIndicator, TvGuestHubQr } from '../tv/party/TvPartyOverlays';
import { buildTvRows, TV_GRID_GAP } from './tvGridLayout';
import { findNextTvGridItem, type TvGridDirection } from './tvGridNavigation';

type View =
  | { kind: 'albums' }
  | { kind: 'items'; album: TvAlbumItems }
  | { kind: 'viewer'; album: TvAlbumItems; index: number; playing: boolean };

// Poll interval for an OPEN party album's active "find your face" search. Short
// enough that a guest's search appears promptly on the TV; each request re-checks
// the TV session + owner + ShowOnTv on the server. Only runs while a PartyMode
// album is open.
const FACE_SEARCH_POLL_MS = 6000;

// Live-refresh interval for an OPEN party album's items (grid + slideshow). Kept
// in the 10-20s band: frequent enough that guest uploads appear quickly, light
// enough for the public party-view rate limit. Only runs while a PartyMode album
// is open — the album LIST has its own 20s poll for revocation.
const PARTY_ITEMS_POLL_MS = 15_000;

// After this idle period (no key / pointer activity) the 10-foot chrome — the
// corner QR codes and the header/viewer command bar — fades away, like the
// native TV overlay; any activity brings it straight back.
const CHROME_IDLE_MS = 10_000;

// Moves keyboard/D-pad focus between the ALBUM tiles (uniform list) by a linear
// delta. The justified item grid uses spatial navigation instead (findNextTvGridItem).
function moveGridFocus(container: HTMLElement | null, delta: number) {
  if (!container) return;
  const tiles = Array.from(container.querySelectorAll<HTMLElement>('[data-tile]'));
  if (tiles.length === 0) return;
  const current = tiles.indexOf(document.activeElement as HTMLElement);
  const next = current < 0 ? 0 : Math.min(tiles.length - 1, Math.max(0, current + delta));
  tiles[next]?.focus();
}

// True when both item lists are the same items in the same order — used to skip
// a state update (and the timer/QR churn it causes) when a poll returns nothing
// new. Server ordering is stable ascending by AddedAt, so new uploads append.
function sameItemIds(a: TvAlbumItem[], b: TvAlbumItem[]): boolean {
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i += 1) {
    if (a[i].id !== b[i].id) return false;
  }
  return true;
}

// Same view/upload party surface (drives whether the QRs are shown).
function samePartyFlags(a: TvAlbumItems, b: TvAlbumItems): boolean {
  return a.partyEnabled === b.partyEnabled
    && a.partyUrl === b.partyUrl
    && a.partyUploadUrl === b.partyUploadUrl;
}

interface TvBrowserProps {
  // Called when a TV API request returns 401 — the limited session was revoked
  // by the owner or expired. The parent returns to the pairing/revoked screen.
  onSessionInvalid?: () => void;
  // Called on BACK from the album list (the Party root): the parent returns to
  // the mode-selection page (no PIN required to come back to Party).
  onExitRoot?: () => void;
  // A new value re-reads the album list and the open album at once — the
  // display has just come back from a sleep or a hidden tab.
  refreshKey?: unknown;
}

export function TvBrowser({ onSessionInvalid, onExitRoot, refreshKey }: TvBrowserProps) {
  const { t, tn } = useI18n();
  const [albums, setAlbums] = useState<TvAlbum[] | null>(null);
  const [view, setView] = useState<View>({ kind: 'albums' });
  const [error, setError] = useState<string | null>(null);
  const gridRef = useRef<HTMLDivElement>(null);
  const [focusedVideoId, setFocusedVideoId] = useState<string | null>(null);
  // Real measured width of the item grid; null until the ResizeObserver reports
  // it, so justified rows are never laid out against an invented width (no
  // first-paint reflow). A resize recomputes the rows only — it never refetches.
  const [containerWidth, setContainerWidth] = useState<number | null>(null);

  const reloadAlbums = useCallback(async (signal?: AbortSignal) => {
    try {
      const list = await listTvAlbums(signal);
      setAlbums(list);
      setError(null);
      // If the album currently being browsed was disabled by the owner, drop
      // back to the album list on the next refresh (live revocation).
      setView((v) => (v.kind !== 'albums' && !list.some((a) => a.id === v.album.id)
        ? { kind: 'albums' }
        : v));
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      if (err instanceof ApiError && err.status === 401) {
        onSessionInvalid?.();
        return;
      }
      setError(t('tv.loadAlbumsError'));
    }
  }, [onSessionInvalid, t]);

  // Initial load + periodic refresh so a disabled album disappears without a
  // full reload of the TV.
  useEffect(() => {
    const ctrl = new AbortController();
    void reloadAlbums(ctrl.signal);
    const timer = window.setInterval(() => void reloadAlbums(), 20_000);
    return () => {
      ctrl.abort();
      window.clearInterval(timer);
    };
  }, [reloadAlbums]);

  // Latest view kept in a ref so the poll interval below does not re-subscribe
  // on every slideshow index change (which would reset the timer each tick).
  const viewRef = useRef(view);
  useEffect(() => { viewRef.current = view; }, [view]);

  // Live-refresh the OPEN party album's items so guest uploads appear on the TV
  // without leaving/reopening the album. Merges the server list (stable append
  // order) into the current view: in the grid it just adopts the fresh list; in
  // the slideshow it keeps the CURRENT item stable by id (so playback is not
  // reset) and appends new items to the end. Every request re-checks the TV
  // session + party/ShowOnTv on the server; a 404 (album/party revoked) drops
  // safely back to the album list, a 401 invalidates the session.
  const refreshOpenAlbumItems = useCallback(async () => {
    const v = viewRef.current;
    if (v.kind === 'albums') return;
    const albumId = v.album.id;
    try {
      const detail = await listTvAlbumItems(albumId);
      setView((cur) => {
        if (cur.kind === 'albums' || cur.album.id !== albumId) return cur;
        if (sameItemIds(cur.album.items, detail.items) && samePartyFlags(cur.album, detail)) {
          return cur; // nothing new — avoid needless re-render/timer churn
        }
        // The viewer keeps its own live list; the grid adopts the fresh one.
        return cur.kind === 'items' ? { kind: 'items', album: detail } : { ...cur, album: detail };
      });
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onSessionInvalid?.();
        return;
      }
      if (err instanceof ApiError && err.status === 404) {
        // Album no longer served (party/ShowOnTv revoked) → safe state.
        setView((cur) => (cur.kind !== 'albums' && cur.album.id === albumId
          ? { kind: 'albums' }
          : cur));
        void reloadAlbums();
        return;
      }
      // Transient error: keep showing what we already have.
    }
  }, [onSessionInvalid, reloadAlbums]);

  // A resume re-reads the list and the open grid now rather than at the next tick.
  const firstRefresh = useRef(true);
  useEffect(() => {
    if (firstRefresh.current) {
      firstRefresh.current = false;
      return;
    }
    void reloadAlbums();
    if (viewRef.current.kind === 'items') void refreshOpenAlbumItems();
  }, [refreshKey, reloadAlbums, refreshOpenAlbumItems]);

  const openAlbumId = view.kind !== 'albums' ? view.album.id : null;
  // The GRID polls; an open viewer polls for itself (the same feeds, the same
  // rules), so the two never ask for the same thing at once.
  const partyOpen = view.kind === 'items' && view.album.partyEnabled;
  useEffect(() => {
    if (!openAlbumId || !partyOpen) return;
    const timer = window.setInterval(() => void refreshOpenAlbumItems(), PARTY_ITEMS_POLL_MS);
    return () => window.clearInterval(timer);
  }, [openAlbumId, partyOpen, refreshOpenAlbumItems]);

  // Active party face filter. A guest's search reaches this TV only after the
  // explicit "Show these photos on TV" activation on the party page; the poll
  // below then narrows the OPEN album's grid + slideshow to the matching
  // subset (face-filter mode) until BACK / "show all" deletes it or a newer
  // activation replaces it.
  const [faceSearch, setFaceSearch] = useState<{
    searchId: string;
    faceThumbnailUrl: string | null;
    items: TvAlbumItem[];
  } | null>(null);
  const faceSearchRef = useRef(faceSearch);
  useEffect(() => { faceSearchRef.current = faceSearch; }, [faceSearch]);

  // A face search is per-open-album; leaving the album (or party turning off)
  // drops any active filter so we never show a stale one over another album.
  useEffect(() => {
    if (!openAlbumId || !partyOpen) setFaceSearch(null);
  }, [openAlbumId, partyOpen]);

  const pollFaceSearch = useCallback(async (albumId: string) => {
    try {
      const active = await getTvActiveFaceSearch(albumId);
      if (active.active && active.searchId && active.items.length > 0) {
        setFaceSearch((cur) => {
          if (cur && cur.searchId === active.searchId
            && cur.faceThumbnailUrl === active.faceThumbnailUrl
            && sameItemIds(cur.items, active.items)) {
            return cur; // unchanged — avoid re-render/timer churn
          }
          return {
            searchId: active.searchId!,
            faceThumbnailUrl: active.faceThumbnailUrl,
            items: active.items,
          };
        });
      } else {
        setFaceSearch((cur) => (cur ? null : cur));
      }
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onSessionInvalid?.();
        return;
      }
      // Transient / 404 (album no longer served) → keep what we have; the album
      // poll handles hard revocation.
    }
  }, [onSessionInvalid]);

  useEffect(() => {
    if (!openAlbumId || !partyOpen) return;
    void pollFaceSearch(openAlbumId);
    const timer = window.setInterval(() => void pollFaceSearch(openAlbumId), FACE_SEARCH_POLL_MS);
    return () => window.clearInterval(timer);
  }, [openAlbumId, partyOpen, pollFaceSearch]);

  // BACK / "show all photos" in face-filter mode: delete THIS search server-side
  // (session + stored face crop; row-scoped, so a newer activation is never
  // touched by a stale exit) and restore the full album locally.
  const exitFaceSearch = useCallback(() => {
    const albumId = openAlbumId;
    const current = faceSearchRef.current;
    setFaceSearch(null);
    if (albumId) {
      void clearTvActiveFaceSearch(albumId, current?.searchId).catch(() => { /* best effort */ });
    }
  }, [openAlbumId]);

  // Preserve position across face-filter transitions:
  //   * viewer/slideshow — keep the CURRENT photo when it belongs to the new
  //     display list, else land on the first matching photo (activation) or
  //     keep the same photo in the full album (restore);
  //   * grid — keep focus on the focused tile when it is still shown, else
  //     focus the first tile.
  const prevFaceRef = useRef(faceSearch);
  useEffect(() => {
    const prev = prevFaceRef.current;
    prevFaceRef.current = faceSearch;
    if (prev === faceSearch || (prev === null && faceSearch === null)) return;

    // Grid focus (best-effort, after the filtered grid re-renders).
    const focusedId = (document.activeElement as HTMLElement | null)?.dataset?.itemId;
    window.requestAnimationFrame(() => {
      const container = gridRef.current;
      if (!container || viewRef.current.kind !== 'items') return;
      const target = (focusedId
        && container.querySelector<HTMLElement>(`[data-item-id="${CSS.escape(focusedId)}"]`))
        || container.querySelector<HTMLElement>('[data-tile]');
      target?.focus();
    });
  }, [faceSearch]);

  const openAlbum = useCallback(async (album: TvAlbum) => {
    try {
      const detail = await listTvAlbumItems(album.id);
      setView({ kind: 'items', album: detail });
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onSessionInvalid?.();
        return;
      }
      if (err instanceof ApiError && err.status === 404) {
        // Disabled between listing and opening — refresh and stay on the list.
        void reloadAlbums();
        return;
      }
      setError(t('tv.openAlbumError'));
    }
  }, [reloadAlbums, onSessionInvalid, t]);

  const backToAlbums = useCallback(() => setView({ kind: 'albums' }), []);

  const onGridKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLDivElement>) => {
      const isArrow = e.key === 'ArrowRight' || e.key === 'ArrowLeft'
        || e.key === 'ArrowUp' || e.key === 'ArrowDown';
      if (isArrow) {
        e.preventDefault();
        if (view.kind === 'items') {
          // Justified grid → spatial navigation over the computed rows.
          const currentId = (document.activeElement as HTMLElement | null)?.dataset?.itemId;
          const container = gridRef.current;
          const dir: TvGridDirection = e.key === 'ArrowRight' ? 'right'
            : e.key === 'ArrowLeft' ? 'left'
              : e.key === 'ArrowDown' ? 'down' : 'up';
          const nextId = currentId
            ? findNextTvGridItem(rowsRef.current, TV_GRID_GAP, currentId, dir)
            : null;
          if (nextId && container) {
            container.querySelector<HTMLElement>(`[data-item-id="${CSS.escape(nextId)}"]`)?.focus();
          } else if (!currentId && container) {
            container.querySelector<HTMLElement>('[data-tile]')?.focus();
          }
        } else {
          // Album list → uniform linear roving focus.
          moveGridFocus(gridRef.current, (e.key === 'ArrowRight' || e.key === 'ArrowDown') ? 1 : -1);
        }
      } else if (e.key === 'Backspace' && view.kind === 'items') {
        e.preventDefault();
        // BACK in face-filter mode exits the filter (and deletes the search);
        // the NEXT press follows the existing behavior (back to the albums).
        if (faceSearch) exitFaceSearch();
        else backToAlbums();
      } else if (e.key === 'Backspace' && view.kind === 'albums') {
        // BACK from the Party root returns to the mode selector (no PIN).
        e.preventDefault();
        onExitRoot?.();
      }
    },
    [view.kind, faceSearch, exitFaceSearch, backToAlbums, onExitRoot],
  );

  // Idle auto-hide of the 10-foot chrome (corner QRs + header/command bar) while
  // an album is open: visible on entry and on any activity, fading out after
  // CHROME_IDLE_MS. On the album list it is always shown.
  const [chromeVisible, setChromeVisible] = useState(true);
  const albumOpen = view.kind !== 'albums';
  useEffect(() => {
    if (!albumOpen) {
      setChromeVisible(true);
      return;
    }
    let timer = 0;
    const show = () => {
      setChromeVisible(true);
      window.clearTimeout(timer);
      timer = window.setTimeout(() => setChromeVisible(false), CHROME_IDLE_MS);
    };
    show();
    const opts: AddEventListenerOptions = { passive: true };
    window.addEventListener('mousemove', show, opts);
    window.addEventListener('pointerdown', show, opts);
    window.addEventListener('keydown', show);
    return () => {
      window.clearTimeout(timer);
      window.removeEventListener('mousemove', show);
      window.removeEventListener('pointerdown', show);
      window.removeEventListener('keydown', show);
    };
  }, [albumOpen, view.kind]);
  const chromeClass = `tv-chrome ${chromeVisible ? '' : 'tv-chrome-hidden'}`.trim();

  // While face-filter mode is active the open album's grid AND slideshow show
  // only the matching subset (rank order); otherwise the full album.
  const displayItems: TvAlbumItem[] = view.kind === 'albums'
    ? []
    : (faceSearch ? faceSearch.items : view.album.items);

  // Justified rows for the item grid (only when a real width is known). Reuses
  // the SAME layout + aspect-ratio primitives as the web gallery and the native
  // TV app, so a vertical video is a vertical tile and nothing is cropped.
  const showItemsGrid = view.kind === 'items' && displayItems.length > 0;
  const rows = useMemo(
    () => (containerWidth != null ? buildTvRows(displayItems, containerWidth) : []),
    [displayItems, containerWidth],
  );
  const rowsRef = useRef(rows);
  rowsRef.current = rows;

  // Measure the grid width (never an invented fallback). A resize recomputes the
  // rows only — no refetch, no album change, no viewer/poll reset.
  useLayoutEffect(() => {
    if (!showItemsGrid) {
      setContainerWidth(null);
      return;
    }
    const node = gridRef.current;
    if (!node) return;
    const measure = () => {
      const next = Math.round(node.getBoundingClientRect().width);
      if (next > 0) {
        setContainerWidth((prev) => (prev != null && Math.abs(prev - next) < 1 ? prev : next));
      }
    };
    measure();
    if (typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(measure);
    ro.observe(node);
    return () => ro.disconnect();
  }, [showItemsGrid]);

  // Reserve the vertical scrollbar for the whole TV experience. /tv scrolls the
  // window, so when the page grows past the viewport the appearing scrollbar
  // would steal ~15px of width → the ResizeObserver would report a narrower grid
  // → every row's tiles recompute smaller → all lower rows shift ("the grid
  // rearranges by several rows, photos change size, blurred backdrop peeks at the
  // edges"), and it can oscillate. A permanently reserved gutter keeps the width
  // constant, so the justified layout is computed once and stays put.
  useEffect(() => {
    const el = document.documentElement;
    el.classList.add('tv-scroll-stable');
    return () => el.classList.remove('tv-scroll-stable');
  }, []);



  if (error) {
    return (
      <div className="tv-browser">
        <p role="alert">{error}</p>
      </div>
    );
  }

  if (albums === null) {
    return (
      <div className="tv-browser">
        <p>{t('tv.loadingAlbums')}</p>
      </div>
    );
  }

  if (view.kind === 'viewer') {
    const { album } = view;
    return (
      <TvViewer
        key={`${album.id}:${view.index}`}
        items={displayItems}
        startIndex={Math.min(view.index, Math.max(0, displayItems.length - 1))}
        autoPlay={view.playing}
        albumId={album.id}
        albumName={album.name}
        partyEnabled={album.partyEnabled}
        partyUrl={album.partyEnabled ? album.partyUrl : null}
        partySlideshow={album.partySlideshow ?? null}
        onClose={() => {
          setView((v) => (v.kind === 'viewer' ? { kind: 'items', album: v.album } : v));
          void refreshOpenAlbumItems();
        }}
        onSessionInvalid={onSessionInvalid}
        initialOverlay
        refreshKey={refreshKey}
      />
    );
  }


  if (view.kind === 'items') {
    const { album } = view;
    const firstTileId = displayItems[0]?.id;
    return (
      <div className="tv-browser">
        <div className={`tv-browser-header ${chromeClass}`}>
          <button type="button" onClick={faceSearch ? exitFaceSearch : backToAlbums}>
            {faceSearch ? t('tv.faceShowAll') : t('tv.backToAlbums')}
          </button>
          <h2>{album.name}</h2>
          {displayItems.length > 0 && (
            <button
              type="button"
              className="tv-slideshow-start"
              onClick={() => setView({ kind: 'viewer', album, index: 0, playing: true })}
            >
              {t('tv.slideshow')}
            </button>
          )}
        </div>
        {faceSearch && (
          <TvFaceIndicator
            faceThumbnailUrl={faceSearch.faceThumbnailUrl}
            albumName={album.name}
            count={faceSearch.items.length}
            onShowAll={exitFaceSearch}
          />
        )}
        <TvGuestHubQr partyUrl={album.partyEnabled ? album.partyUrl : null} hidden={!chromeVisible} />
        {displayItems.length === 0 ? (
          <p className="tv-empty">{t('tv.emptyAlbum')}</p>
        ) : (
          // Justified proportional grid (same layout as the web gallery + native
          // TV app). No rows are laid out until the width is measured — a stable
          // skeleton shows meanwhile so nothing reflows on first paint.
          <div className="tv-jgrid" ref={gridRef} onKeyDown={onGridKeyDown}>
            {containerWidth == null ? (
              <div className="tv-jgrid-skeleton" data-testid="tv-grid-skeleton" aria-hidden="true" />
            ) : rows.map((row) => (
              <div key={row.key} className="tv-jrow" style={{ height: row.height, gap: `${TV_GRID_GAP}px` }}>
                {row.items.map((tile) => {
                  const item = displayItems[tile.originalIndex];
                  const isVideo = item.mediaType === 'video';
                  return (
                    <button
                      key={item.id}
                      type="button"
                      data-tile
                      data-item-id={item.id}
                      aria-label={item.name}
                      tabIndex={item.id === firstTileId ? 0 : -1}
                      className="tv-jtile"
                      style={{ width: `${tile.width}px`, height: `${tile.height}px` }}
                      onFocus={() => setFocusedVideoId(isVideo ? item.id : null)}
                      onBlur={() => setFocusedVideoId((current) => current === item.id ? null : current)}
                      onMouseEnter={() => { if (isVideo) setFocusedVideoId(item.id); }}
                      onMouseLeave={() => setFocusedVideoId((current) => current === item.id ? null : current)}
                      onClick={() => setView({ kind: 'viewer', album, index: tile.originalIndex, playing: false })}
                    >
                      {isVideo && item.posterUrl ? (
                        <span className="tv-jtile-frame">
                          <VideoPreview
                            className="tv-jtile-video"
                            posterUrl={item.posterUrl}
                            previewStripUrl={item.previewStripUrl}
                            active={focusedVideoId === item.id}
                            fit="contain"
                          />
                        </span>
                      ) : (
                        <span className="tv-jtile-frame">
                          <img className="tv-jtile-backdrop" src={item.thumbnailUrl} alt="" aria-hidden="true" loading="lazy" />
                          <img className="tv-jtile-fg" src={item.thumbnailUrl} alt="" loading="lazy" />
                        </span>
                      )}
                      {isVideo && <span className="tv-jtile-badge" aria-hidden="true">▶</span>}
                    </button>
                  );
                })}
              </div>
            ))}
          </div>
        )}
      </div>
    );
  }

  // Album list. The BACK/exit key handler lives on the CONTAINER so it also
  // works in the empty state (where no grid exists).
  return (
    <div className="tv-browser" tabIndex={-1} onKeyDown={onGridKeyDown}>
      <div className="tv-browser-header">
        <h2>{t('tv.yourAlbums')}</h2>
      </div>
      {albums.length === 0 ? (
        <p className="tv-empty" data-testid="tv-albums-empty">
          {t('tv.noAlbums')}
        </p>
      ) : (
        <div className="tv-grid" ref={gridRef}>
          {albums.map((album, i) => (
            <button
              key={album.id}
              type="button"
              data-tile
              tabIndex={i === 0 ? 0 : -1}
              className="tv-tile tv-tile-album"
              onClick={() => void openAlbum(album)}
            >
              {album.coverThumbnailUrl ? (
                <img className="tv-tile-thumb" src={album.coverThumbnailUrl} alt="" loading="lazy" />
              ) : (
                <span className="tv-tile-thumb tv-tile-empty" aria-hidden="true" />
              )}
              <span className="tv-tile-name">{album.name}</span>
              <span className="tv-tile-count">{tn(album.itemCount, 'tv.itemCount')}</span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
