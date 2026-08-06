import { useCallback, useEffect, useMemo, useState } from 'react';
import { useLocation, useNavigate, useParams, useSearchParams } from 'react-router';
import {
  addAestheticLabFromGallery,
  addPlateImagesFromGallery,
  ApiError,
  getFileMetadata,
  getSimilarPhotosPage,
  type FileMetadata,
  type MediaItem,
  type SimilarPhotoItem,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { smallThumbnailUrl } from '../components/files/types';
import { useI18n, type MessageKey } from '../i18n';
import { MediaViewer, type MediaViewerItem } from '../components/MediaViewer';
import { MediaGrid } from '../media/workspace/MediaGrid';
import { MediaMetadataPanel } from '../media/metadata/MediaMetadataPanel';
import { useMediaSimilarityActions } from '../media/viewer/mediaViewerActions';
import { useMediaSelection } from '../gallery/useMediaSelection';
import { MediaSelectionBar } from '../gallery/workspace/MediaSelectionBar';
import type { GalleryDestinationAction } from '../gallery/workspace/DestinationMenu';
import { moveFilesToTrash } from '../gallery/workspace/bulkTrash';
import { AlbumPickerModal } from '../gallery/AlbumPickerModal';
import { TrashConfirmation } from '../gallery/workspace/TrashConfirmation';

// Similar Photos Explorer — a dedicated, owner-private page listing all photos
// similar to a source image above a chosen similarity threshold, with keyset
// "load more" pagination.
//
// It is NOT a library filter: the ranking, the score and the threshold belong to
// this page and to the /api/files/{id}/similar endpoint. What it now SHARES with
// the Library and Albums is its presentation and interaction — the same
// MediaGrid (justified rows, tile geometry, spacing, selection affordance,
// hover/focus, placeholders, dark surfaces), the same MediaViewer, and the same
// selection/bulk-action model. The similarity percentage rides along as an
// explicit optional MediaGrid badge, so nothing was forked.
//
// The similar DTO stays lean on internals (id + name + score, no vectors, no
// blob internals) but does carry each result's DISPLAY width/height, resolved
// server-side through the same helper the library uses — so the shared wall
// reserves the identical tile here as it does there, and no surface has to
// guess a shape. Grid uses SMALL thumbnails; the source header uses the MEDIUM
// preview.

const PAGE_SIZE = 60;
const MIN_PCT = 50;
const MAX_PCT = 95;
const DEFAULT_PCT = 75;
const DEBOUNCE_MS = 400;

const PRESETS: ReadonlyArray<{ labelKey: MessageKey; pct: number }> = [
  { labelKey: 'similar.presetStrict', pct: 85 },
  { labelKey: 'similar.presetBalanced', pct: 75 },
  { labelKey: 'similar.presetBroad', pct: 65 },
];

// Medium preview for the source header (never the original full-res).
function mediumPreviewUrl(fileId: string): string {
  return `/api/files/${fileId}/preview`;
}

function clampPct(value: number): number {
  if (Number.isNaN(value)) return DEFAULT_PCT;
  return Math.min(MAX_PCT, Math.max(MIN_PCT, Math.round(value)));
}

// Read the threshold from the URL (?minSimilarity=0.75, a 0..1 fraction).
function pctFromParams(params: URLSearchParams): number {
  const raw = params.get('minSimilarity');
  if (raw === null) return DEFAULT_PCT;
  const fraction = Number.parseFloat(raw);
  return Number.isFinite(fraction) ? clampPct(fraction * 100) : DEFAULT_PCT;
}

// Project a similar-photo result onto the shared MediaItem shape the media wall
// consumes. Every result of this endpoint is a photo.
//
// `width`/`height` are DISPLAY dimensions: the server resolves the stored pair
// through ImageDisplayDimensions before sending it, so an EXIF quarter-turn
// (orientation 5–8) arrives already swapped. They are NOT the coded dimensions
// held in blob_metadata — those describe the bytes before rotation, and handing
// them to the wall reserved a landscape tile for a portrait thumbnail.
//
// This is the same resolution the Library and Album listings apply, which is
// what makes the shared justified layout give a result the identical tile in
// every surface. When the server has no extracted dimensions both are null and
// `getMediaAspectRatio` applies its square photo fallback. The remaining fields
// the lean similarity DTO does not carry stay null/0; the grid already handles
// that (no size/resolution line in the hover overlay).
function toMediaItem(item: SimilarPhotoItem): MediaItem {
  return {
    id: item.fileItemId,
    kind: 'image',
    name: item.name,
    title: null,
    displayName: item.name,
    mimeType: '',
    sizeBytes: 0,
    width: item.width,
    height: item.height,
    createdAt: '',
    updatedAt: null,
    takenAt: null,
    favorite: false,
    rating: null,
    thumbnailUrl: smallThumbnailUrl(item.fileItemId),
    occurrenceCount: 1,
    hasDuplicates: false,
    hasGps: null,
  };
}

type Phase = 'loading' | 'ready' | 'empty' | 'indexing' | 'notfound' | 'error';

export function SimilarPhotosExplorerPage() {
  const { fileId } = useParams<{ fileId: string }>();
  const [searchParams, setSearchParams] = useSearchParams();
  const navigate = useNavigate();
  const location = useLocation();
  const { invalidateAuth } = useAuth();
  const { t, tn } = useI18n();

  // `pct` drives the controls (50–95); `debouncedPct` drives the fetch.
  const [pct, setPct] = useState(() => pctFromParams(searchParams));
  const [debouncedPct, setDebouncedPct] = useState(pct);

  const [sourceName, setSourceName] = useState<string | null>(null);
  const [items, setItems] = useState<SimilarPhotoItem[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [hasMore, setHasMore] = useState(false);
  const [phase, setPhase] = useState<Phase>('loading');
  const [loadingMore, setLoadingMore] = useState(false);
  const [moreError, setMoreError] = useState(false);

  // Shared media selection + bulk-action state (identical model to the Library).
  const selection = useMediaSelection();
  const [actionBusy, setActionBusy] = useState(false);
  const [trashOpen, setTrashOpen] = useState(false);
  const [trashBusy, setTrashBusy] = useState(false);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [viewerIndex, setViewerIndex] = useState<number | null>(null);

  // Visible ids in display order — the range anchor for Shift-select.
  const orderedIds = useMemo(() => items.map((it) => it.fileItemId), [items]);
  const mediaItems = useMemo(() => items.map(toMediaItem), [items]);
  const viewerItems = useMemo<MediaViewerItem[]>(
    () => mediaItems.map((it) => ({
      id: it.id, name: it.name, displayName: it.displayName, kind: 'image',
    })),
    [mediaItems],
  );
  // The similarity percentage, as a shared-grid badge rather than a bespoke tile.
  const badges = useMemo(() => {
    const map = new Map<string, string>();
    for (const item of items) map.set(item.fileItemId, `${Math.round(item.score * 100)}%`);
    return map;
  }, [items]);

  const minSimilarity = debouncedPct / 100;

  // Where "back" should go: the location the user came from when the explorer
  // was opened from a workspace viewer, else the Library.
  //
  // Captured ONCE, on mount. The threshold effect below rewrites the URL with
  // `replace: true`, and a replace drops the route state — so reading
  // location.state lazily would silently degrade every return to the Library
  // fallback the moment the first threshold sync ran.
  const [returnTo] = useState<string>(() => {
    const from = (location.state as { from?: unknown } | null)?.from;
    return typeof from === 'string' && from.length > 0 ? from : '/media';
  });

  // Debounce the slider/input so dragging doesn't spam the API.
  useEffect(() => {
    const timer = setTimeout(() => setDebouncedPct(pct), DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [pct]);

  // Keep the threshold in the URL (shareable/bookmarkable within the app).
  useEffect(() => {
    const fraction = (debouncedPct / 100).toFixed(2);
    setSearchParams(
      (prev) => {
        const next = new URLSearchParams(prev);
        next.set('minSimilarity', fraction);
        return next;
      },
      { replace: true },
    );
  }, [debouncedPct, setSearchParams]);

  // Load the source photo's display name (also an ownership guard: 404 for a
  // foreign/missing file).
  useEffect(() => {
    if (fileId === undefined) return;
    const controller = new AbortController();
    setSourceName(null);
    getFileMetadata(fileId, controller.signal)
      .then((m) => setSourceName(m.name))
      .catch((err) => {
        if (err instanceof ApiError && err.status === 401) {
          invalidateAuth();
        } else if (err instanceof ApiError && err.status === 404) {
          setPhase('notfound');
        }
      });
    return () => controller.abort();
  }, [fileId, invalidateAuth]);

  // (Re)load the first page whenever the source or threshold changes.
  useEffect(() => {
    if (fileId === undefined) return;
    const controller = new AbortController();
    setPhase('loading');
    setItems([]);
    setCursor(null);
    setHasMore(false);
    setMoreError(false);
    setNotice(null);
    setViewerIndex(null);
    selection.clear(); // a new source/threshold invalidates any stale selection
    (async () => {
      try {
        const page = await getSimilarPhotosPage(
          fileId,
          { minSimilarity, limit: PAGE_SIZE },
          controller.signal,
        );
        if (controller.signal.aborted) return;
        if (!page.profileAvailable || !page.queryIndexed) {
          setPhase('indexing');
          return;
        }
        setItems(page.items);
        setCursor(page.nextCursor);
        setHasMore(page.hasMore);
        setPhase(page.items.length === 0 ? 'empty' : 'ready');
      } catch (err) {
        if (controller.signal.aborted) return;
        if (err instanceof ApiError && err.status === 401) {
          invalidateAuth();
          return;
        }
        if (err instanceof ApiError && err.status === 404) {
          setPhase('notfound');
          return;
        }
        setPhase('error');
      }
    })();
    return () => controller.abort();
  }, [fileId, minSimilarity, invalidateAuth, selection.clear]);

  async function loadMore() {
    if (fileId === undefined || !hasMore || cursor === null || loadingMore) return;
    setLoadingMore(true);
    setMoreError(false);
    try {
      const page = await getSimilarPhotosPage(fileId, {
        minSimilarity,
        limit: PAGE_SIZE,
        cursor,
      });
      setItems((prev) => [...prev, ...page.items]);
      setCursor(page.nextCursor);
      setHasMore(page.hasMore);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        invalidateAuth();
        return;
      }
      setMoreError(true);
    } finally {
      setLoadingMore(false);
    }
  }

  // ---- Bulk actions on the selection (same handlers as the Gallery) --------

  const runDestination = useCallback(
    async (
      execute: (ids: string[]) => Promise<{ added: unknown[]; skipped: unknown[] }>,
      noticeKey: 'aesthetics' | 'plates',
    ) => {
      const ids = [...selection.selected];
      if (ids.length === 0) return;
      setActionBusy(true);
      setNotice(null);
      try {
        const result = await execute(ids);
        if (noticeKey === 'aesthetics') {
          setNotice(
            t('aesthetics.addedFromGallery', { added: result.added.length, skipped: result.skipped.length }),
          );
        } else {
          const base = result.added.length === 1
            ? t('gallery.ws.plates.added_one', { count: 1 })
            : t('gallery.ws.plates.added_other', { count: result.added.length });
          const extra = result.skipped.length > 0
            ? t('gallery.ws.plates.skipped', { count: result.skipped.length })
            : '';
          setNotice(base + extra);
        }
        selection.clear();
      } catch (err) {
        if (err instanceof ApiError && err.status === 401) {
          invalidateAuth();
          return;
        }
        setNotice(noticeKey === 'aesthetics' ? t('aesthetics.addError') : t('gallery.ws.plates.error'));
      } finally {
        setActionBusy(false);
      }
    },
    [selection, t, invalidateAuth],
  );

  const destinations = useMemo<GalleryDestinationAction[]>(
    () => [
      {
        id: 'beauty-lab',
        label: t('gallerySel.addToAestheticsLab'),
        isAvailable: true,
        run: () => void runDestination((ids) => addAestheticLabFromGallery(ids), 'aesthetics'),
      },
      {
        id: 'plates',
        label: t('gallery.ws.destPlates'),
        isAvailable: true,
        run: () => void runDestination((ids) => addPlateImagesFromGallery(ids), 'plates'),
      },
    ],
    [t, runDestination],
  );

  async function confirmTrash() {
    const ids = [...selection.selected];
    if (ids.length === 0) {
      setTrashOpen(false);
      return;
    }
    setTrashBusy(true);
    const result = await moveFilesToTrash(ids, { onAuthError: invalidateAuth });
    const movedSet = new Set(result.moved);
    // Trashed items disappear from the results in place; failed ids stay selected
    // for retry (mirrors the workspace's confirmTrash).
    setItems((prev) => prev.filter((it) => !movedSet.has(it.fileItemId)));
    selection.selectAll(result.failed);
    setViewerIndex(null);
    const done = result.moved.length === 1
      ? t('gallery.ws.trash.done_one', { count: 1 })
      : t('gallery.ws.trash.done_other', { count: result.moved.length });
    const failedNote = result.failed.length > 0
      ? t('gallery.ws.trash.failed', { count: result.failed.length })
      : '';
    setNotice(done + failedNote);
    setTrashBusy(false);
    setTrashOpen(false);
  }

  // The SAME viewer-action resolver the library workspace uses, so a photo
  // opened here offers exactly the actions it offers anywhere else. "Explore
  // similar photos" re-roots this explorer on the opened photo — carrying the
  // chosen threshold and the original return target, and doing nothing at all
  // when the opened photo is already the anchor (no duplicate history entry on
  // itself). "Find similar in Library" leaves for the Library's own filter.
  const similarityActions = useMediaSimilarityActions({
    onNavigate: () => setViewerIndex(null),
    exploreMinSimilarity: minSimilarity,
    exploreState: { from: returnTo },
    currentExploreAnchor: fileId ?? null,
  });

  const onMetadataChanged = useCallback((changedId: string, metadata: FileMetadata) => {
    setItems((prev) => prev.map((it) => (
      it.fileItemId === changedId ? { ...it, name: metadata.effective.displayName } : it
    )));
  }, []);

  return (
    <section className="ws-page similar-explorer" aria-busy={phase === 'loading'}>
      <header className="ws-page-header similar-explorer-header">
        <button
          type="button"
          className="row-action"
          data-testid="similar-back"
          onClick={() => void navigate(returnTo)}
        >
          {t('similar.backToLibrary')}
        </button>
      </header>

      {phase === 'notfound' ? (
        <p className="muted" role="alert">
          {t('similar.notAvailable')}
        </p>
      ) : (
        <>
          {/* Source photo context — stays visible above the results. */}
          {fileId !== undefined && (
            <div className="similar-explorer-source">
              <div className="similar-explorer-source-thumb">
                <img
                  src={mediumPreviewUrl(fileId)}
                  alt={sourceName ?? t('similar.sourceAlt')}
                  draggable={false}
                />
              </div>
              <div className="similar-explorer-source-meta">
                <span className="similar-explorer-eyebrow">{t('similar.eyebrow')}</span>
                <h1 className="similar-explorer-title" title={sourceName ?? undefined}>
                  {sourceName ?? t('similar.photoFallback')}
                </h1>
              </div>
            </div>
          )}

          {/* Threshold control. This is the explorer's own ranking knob — NOT a
              library filter chip. */}
          <div className="similar-explorer-filter">
            <div className="similar-explorer-filter-row">
              <label htmlFor="minsim-slider" className="similar-explorer-filter-label">
                {t('similar.minSimilarity')}
              </label>
              <div className="similar-explorer-value">{pct}%</div>
            </div>
            <input
              id="minsim-slider"
              type="range"
              min={MIN_PCT}
              max={MAX_PCT}
              step={1}
              value={pct}
              onChange={(e) => setPct(clampPct(Number(e.target.value)))}
              className="similar-explorer-slider"
              aria-label={t('similar.minSimilarityAria')}
            />
            <div className="similar-explorer-controls">
              <div className="similar-explorer-presets" role="group" aria-label={t('similar.presetsGroup')}>
                {PRESETS.map((p) => (
                  <button
                    key={p.labelKey}
                    type="button"
                    className={
                      pct === p.pct
                        ? 'similar-explorer-preset is-active'
                        : 'similar-explorer-preset'
                    }
                    onClick={() => setPct(p.pct)}
                  >
                    {t(p.labelKey)} · {p.pct}%
                  </button>
                ))}
              </div>
              <label className="similar-explorer-number">
                <span className="visually-hidden">{t('similar.minSimilarityAria')}</span>
                <input
                  type="number"
                  min={MIN_PCT}
                  max={MAX_PCT}
                  step={1}
                  value={pct}
                  onChange={(e) => setPct(clampPct(Number(e.target.value)))}
                  aria-label={t('similar.minSimilarityNumericAria')}
                />
                <span aria-hidden="true">%</span>
              </label>
            </div>
            <p className="muted similar-explorer-help">
              {t('similar.help')}
            </p>
          </div>

          {/* Result count / status. */}
          <p className="muted similar-explorer-status" role="status">
            {phase === 'ready'
              ? tn(items.length, 'similar.countStatus', { plus: hasMore ? '+' : '', pct })
              : phase === 'loading'
                ? t('similar.finding')
                : ''}
          </p>

          {/* No explorer-specific loading block: the status line above already
              announces the wait, and the results then arrive through the shared
              MediaGrid — which reserves each tile at its real proportions and
              owns the only placeholder in the wall. A second, page-level
              skeleton of eight equal-width tiles claimed a geometry no result
              would actually have. */}

          {phase === 'error' && (
            <div className="folder-error" role="alert">
              {t('similar.loadError')}
              <button
                type="button"
                className="retry-button"
                onClick={() => setDebouncedPct((v) => v)}
              >
                {t('common.tryAgain')}
              </button>
            </div>
          )}

          {phase === 'indexing' && (
            <p className="muted">
              {t('similar.indexing')}
            </p>
          )}

          {phase === 'empty' && (
            <div className="similar-explorer-empty">
              <p className="muted">{t('similar.emptyTitle')}</p>
              <p className="muted">{t('similar.emptyHint')}</p>
            </div>
          )}

          {phase === 'ready' && (
            <>
              {/* The SAME wall the Library and Albums render. */}
              <MediaGrid
                items={mediaItems}
                orderedIds={orderedIds}
                selection={selection}
                onOpen={(index) => setViewerIndex(index)}
                badges={badges}
              />

              <div className="gallery-scroll-footer">
                {hasMore && (
                  <button
                    type="button"
                    className="row-action-primary"
                    onClick={() => void loadMore()}
                    disabled={loadingMore}
                  >
                    {loadingMore ? t('common.loading') : t('common.loadMore')}
                  </button>
                )}
                {moreError && (
                  <p className="muted" role="alert">
                    {t('similar.loadMoreError')}
                  </p>
                )}
                {!hasMore && (
                  <p className="muted gallery-scroll-end">
                    {t('similar.endOfResults')}
                  </p>
                )}
              </div>
            </>
          )}
        </>
      )}

      {viewerIndex !== null && viewerItems[viewerIndex] !== undefined && (
        <MediaViewer
          items={viewerItems}
          index={viewerIndex}
          onClose={() => setViewerIndex(null)}
          onIndexChange={setViewerIndex}
          renderDetails={({ item, metadata, metadataError, adoptMetadata }) => (
            <MediaMetadataPanel
              fileId={item.id}
              kind="image"
              initialData={metadata}
              loadError={metadataError}
              onMetadataChanged={(id, next) => { adoptMetadata(next); onMetadataChanged(id, next); }}
              // Resolved centrally: this surface adds no origin condition of its
              // own, so the drawer here is the drawer everywhere.
              {...similarityActions({ id: item.id, kind: 'image' })}
            />
          )}
        />
      )}

      <MediaSelectionBar
        count={selection.count}
        busy={actionBusy || trashBusy}
        destinations={destinations}
        onAddToAlbum={() => setPickerOpen(true)}
        onMoveToTrash={() => setTrashOpen(true)}
        onClear={selection.clear}
      />

      {notice && (
        <div className="gallery-notice" role="status">
          {notice}
        </div>
      )}

      {trashOpen && (
        <TrashConfirmation
          count={selection.count}
          busy={trashBusy}
          onConfirm={confirmTrash}
          onCancel={() => setTrashOpen(false)}
        />
      )}

      {pickerOpen && (
        <AlbumPickerModal
          fileItemIds={[...selection.selected]}
          onClose={() => setPickerOpen(false)}
        />
      )}
    </section>
  );
}
