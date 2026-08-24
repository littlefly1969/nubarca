import {
  useEffect,
  useLayoutEffect,
  useMemo,
  useState,
  type MouseEvent as ReactMouseEvent,
  type RefObject,
} from 'react';
import { useVirtualizer, useWindowVirtualizer } from '@tanstack/react-virtual';
import type { MediaItem, SemanticBestMatch } from '@nubarca/api-client';
import { formatSize } from '../../components/format';
import { useI18n } from '../../i18n';
import { useAppScrollViewport } from '../../components/appScroll';
import { SemanticMarkerStrip, toMarkers } from './SemanticMarkerStrip';
import { VideoPreview } from '../../video/VideoPreview';
import { getMediaAspectRatio } from './mediaAspectRatio';
import { computeJustifiedRows, type JustifiedLayoutItem } from '../layout/computeJustifiedRows';
import { MEDIA_WALL_GAP_PX } from '../layout/mediaWallGeometry';
import { useJustifiedWall } from '../layout/useJustifiedWall';
import type { MediaSelection } from '../../gallery/useMediaSelection';

// The full-width justified media wall. Photos and videos flow into justified
// rows (each row spans the container, tiles keep their REAL aspect ratio — a
// vertical video is a vertical tile), the rows are virtualized (never the whole
// library), and metadata lives in a hover/focus/selected overlay rather than a
// permanent card panel. Infinite scroll is owned by the parent (a sentinel below
// the wall) — this component only lays out and virtualizes what it is given.
//
// Virtualization has to follow whoever owns the scrolling. Inside the
// authenticated shell that is `.app-main`, and a window virtualizer there would
// read a scroll offset that never changes and mount the first rows forever. So
// the wall comes in two variants over one shared layout: one measured against the
// application scroll viewport, one against the document for the surfaces (and
// tests) that have no shell around them. The choice is made once per mount from a
// context value that never changes, so no hook is ever called conditionally.

// Render (and therefore start downloading) several rows beyond the viewport in
// each direction, so a fast scroll lands on rows whose images are already
// loading rather than momentarily-blank tiles. Virtualization still bounds the
// mounted set — this only widens the lead, it never mounts the whole library.
const MEDIA_WALL_OVERSCAN_ROWS = 6;

function formatDuration(totalSeconds: number): string {
  const s = Math.max(0, Math.round(totalSeconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  const pad = (n: number) => n.toString().padStart(2, '0');
  return h > 0 ? `${h}:${pad(m)}:${pad(sec)}` : `${m}:${pad(sec)}`;
}

// VSEM-03: the representative timestamp (whole ms) of a video's best semantic
// match, by media id. Absent unless a unified semantic search is active.
export type SemanticTimestamps = ReadonlyMap<string, number | null>;

// SEARCH-SEM-01: the COMPLETE temporal evidence of one semantic video result.
// Supersedes passing a lone best timestamp — the backend always returned the
// additional matches and the grid was discarding them. Present only for videos
// returned by a semantic search: the library, albums, shared albums and People
// walls pass nothing and are byte-identical to before.
export interface SemanticTileMatches {
  bestMatch: SemanticBestMatch;
  additionalMatches: SemanticBestMatch[];
}
export type SemanticMatches = ReadonlyMap<string, SemanticTileMatches>;

// Optional short overlay label per media id — the Similar Photos Explorer uses
// it for the similarity percentage. Ids with no entry render no badge, so the
// library and album walls are byte-identical to before.
export type MediaTileBadges = ReadonlyMap<string, string>;

interface GridProps {
  items: MediaItem[];
  orderedIds: string[];
  selection: MediaSelection;
  // `atMs` is supplied only when a semantic marker was activated.
  onOpen(index: number, atMs?: number): void;
  semanticTimestamps?: SemanticTimestamps;
  semanticMatches?: SemanticMatches;
  badges?: MediaTileBadges;
}

// What the wall needs from a virtualizer, whichever scroll model produced it.
interface WallVirtualizer {
  getTotalSize(): number;
  getVirtualItems(): ReadonlyArray<{ index: number; start: number }>;
  options: { scrollMargin: number };
  measure(): void;
}

type WallRows = ReturnType<typeof computeJustifiedRows>;

/**
 * The justified layout for a wall of MediaItems.
 *
 * The measuring and the row maths live in useJustifiedWall, which the shared
 * album wall uses too — the two walls must produce the same geometry, and one
 * copy of it is how that stays true. What is left here is the one thing that is
 * specific to this wall: how a MediaItem's aspect ratio is read.
 */
function useWallLayout(items: MediaItem[]): {
  ref: (node: HTMLDivElement | null) => void;
  containerRef: RefObject<HTMLDivElement | null>;
  measured: boolean;
  rows: WallRows;
} {
  const layoutItems = useMemo<JustifiedLayoutItem[]>(
    () => items.map((item, index) => ({
      id: item.id,
      originalIndex: index,
      aspectRatio: getMediaAspectRatio(item),
    })),
    [items],
  );

  const { ref, containerRef, measured, rows } = useJustifiedWall(layoutItems);
  return { ref, containerRef, measured, rows };
}

/**
 * Where the wall begins inside the application scroll viewport's content.
 *
 * A virtualizer counts scrolling from its scroll element's origin, while the wall
 * starts below the page heading and the sticky workspace chrome. Measured rather
 * than expressed as a constant, so no layout number is written down twice. The
 * observers cover the cases that actually move it: the viewport resizing, and the
 * wall's own height changing — which is what happens when a filter chip row or a
 * query notice appears above it, because that only ever accompanies a refetch.
 */
function useWallScrollMargin(
  containerRef: RefObject<HTMLDivElement | null>,
  viewportRef: RefObject<HTMLElement | null>,
): number {
  const [scrollMargin, setScrollMargin] = useState(0);

  useLayoutEffect(() => {
    const node = containerRef.current;
    const viewport = viewportRef.current;
    if (!node || !viewport) return;
    const measure = () => {
      const next = Math.round(
        node.getBoundingClientRect().top
        - viewport.getBoundingClientRect().top
        + viewport.scrollTop,
      );
      setScrollMargin((prev) => (Math.abs(prev - next) < 1 ? prev : next));
    };
    measure();
    if (typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(measure);
    ro.observe(viewport);
    ro.observe(node);
    return () => ro.disconnect();
  }, [containerRef, viewportRef]);

  return scrollMargin;
}

/** One virtual element per justified row, sized exactly — no measurement pass. */
function rowSizer(rows: WallRows) {
  return (index: number) => {
    const row = rows[index];
    if (row === undefined) return 0;
    return row.height + (row.isLastRow ? 0 : MEDIA_WALL_GAP_PX);
  };
}

export function MediaGrid(props: GridProps) {
  const viewportRef = useAppScrollViewport();
  return viewportRef
    ? <ViewportScrolledWall {...props} viewportRef={viewportRef} />
    : <DocumentScrolledWall {...props} />;
}

/** The wall inside the authenticated shell: `.app-main` owns the scrolling. */
function ViewportScrolledWall({
  viewportRef, ...props
}: GridProps & { viewportRef: RefObject<HTMLElement | null> }) {
  const { ref, containerRef, measured, rows } = useWallLayout(props.items);
  const scrollMargin = useWallScrollMargin(containerRef, viewportRef);
  const rowVirtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => viewportRef.current,
    estimateSize: rowSizer(rows),
    overscan: MEDIA_WALL_OVERSCAN_ROWS,
    scrollMargin,
  });
  return (
    <Wall
      {...props}
      wallRef={ref}
      measured={measured}
      rows={rows}
      virtualizer={rowVirtualizer}
    />
  );
}

/** The wall with no shell around it (public surfaces, unit tests). */
function DocumentScrolledWall(props: GridProps) {
  const { ref, containerRef, measured, rows } = useWallLayout(props.items);
  const rowVirtualizer = useWindowVirtualizer({
    count: rows.length,
    estimateSize: rowSizer(rows),
    overscan: MEDIA_WALL_OVERSCAN_ROWS,
    scrollMargin: containerRef.current?.offsetTop ?? 0,
  });
  return (
    <Wall
      {...props}
      wallRef={ref}
      measured={measured}
      rows={rows}
      virtualizer={rowVirtualizer}
    />
  );
}

function Wall({
  items, orderedIds, selection, onOpen, semanticTimestamps, semanticMatches, badges,
  wallRef, measured, rows, virtualizer,
}: GridProps & {
  wallRef: (node: HTMLDivElement | null) => void;
  measured: boolean;
  rows: WallRows;
  virtualizer: WallVirtualizer;
}) {
  const { t } = useI18n();

  // Row geometry changes on resize / new pages without changing the count, so
  // the virtualizer's cached sizes must be recomputed explicitly.
  useEffect(() => {
    virtualizer.measure();
  }, [rows, virtualizer]);

  return (
    <div
      ref={wallRef}
      aria-label={t('mediaWs.gridAria')}
      role="list"
      data-testid="media-grid"
      className="media-wall"
      aria-busy={measured ? undefined : true}
      style={{ position: 'relative', width: '100%', height: measured ? `${virtualizer.getTotalSize()}px` : undefined }}
    >
      {!measured && (
        <div className="media-wall__skeleton" data-testid="media-grid-skeleton" aria-hidden="true">
          {Array.from({ length: 8 }, (_v, i) => (
            <span key={i} className="media-wall__skeleton-tile" />
          ))}
        </div>
      )}
      {measured && virtualizer.getVirtualItems().map((vRow) => {
        const row = rows[vRow.index];
        return (
          <div
            key={row.key}
            data-index={vRow.index}
            className="media-wall__row"
            style={{
              position: 'absolute',
              top: 0,
              left: 0,
              width: '100%',
              height: `${row.height}px`,
              transform: `translateY(${vRow.start - virtualizer.options.scrollMargin}px)`,
              display: 'flex',
              gap: `${MEDIA_WALL_GAP_PX}px`,
            }}
          >
            {row.items.map((tile) => (
              <MediaTile
                key={tile.id}
                item={items[tile.originalIndex]}
                index={tile.originalIndex}
                width={tile.width}
                height={tile.height}
                orderedIds={orderedIds}
                selection={selection}
                onOpen={(atMs) => (atMs === undefined
                  // Ordinary opens must be indistinguishable from before —
                  // passing an explicit `undefined` would change the observed
                  // call shape for every non-semantic caller.
                  ? onOpen(tile.originalIndex)
                  : onOpen(tile.originalIndex, atMs))}
                semanticMs={semanticTimestamps?.get(tile.id) ?? null}
                semanticMatches={semanticMatches?.get(tile.id) ?? null}
                badge={badges?.get(tile.id) ?? null}
              />
            ))}
          </div>
        );
      })}
    </div>
  );
}

interface TileProps {
  item: MediaItem;
  index: number;
  width: number;
  height: number;
  orderedIds: string[];
  selection: MediaSelection;
  onOpen(atMs?: number): void;
  // VSEM-03: representative timestamp of this video's best semantic match.
  semanticMs?: number | null;
  // SEARCH-SEM-01: every matching moment of this video, for the marker strip.
  semanticMatches?: SemanticTileMatches | null;
  // Short overlay label (e.g. "92%"). Null renders nothing.
  badge?: string | null;
}

export function MediaTile({
  item, index, width, height, orderedIds, selection, onOpen, semanticMs = null,
  semanticMatches = null, badge = null,
}: TileProps) {
  const { t } = useI18n();
  const [thumbFailed, setThumbFailed] = useState(false);
  const [hovered, setHovered] = useState(false);
  const selected = selection.isSelected(item.id);
  const isVideo = item.kind === 'video';
  // Chronological, de-duplicated. Empty for photos and for any tile the caller
  // passed no semantic evidence for, which is what keeps ordinary walls clean.
  const markers = useMemo(
    () => (isVideo && semanticMatches
      ? toMarkers(semanticMatches.bestMatch, semanticMatches.additionalMatches)
      : []),
    [isVideo, semanticMatches],
  );

  function onOpenClick(e: ReactMouseEvent<HTMLButtonElement>) {
    const result = selection.handleTileClick(item.id, index, orderedIds, {
      ctrlOrMeta: e.ctrlKey || e.metaKey,
      shift: e.shiftKey,
    });
    if (result === 'open') onOpen();
  }

  function onSelectClick(e: ReactMouseEvent<HTMLButtonElement>) {
    // Separate sibling button (never nested) — but stop the click from also
    // reaching any ancestor handler so toggling never opens the viewer.
    e.stopPropagation();
    selection.toggleViaControl(item.id, index, orderedIds, e.shiftKey);
  }

  const resolution = item.width != null && item.height != null
    ? `${item.width}×${item.height}`
    : null;
  // A non-positive size means "not carried by this source" (the similarity DTO
  // is leaner than MediaItem), not "an empty file" — rendering it as "0 B" would
  // state something false about the original.
  const size = item.sizeBytes > 0 ? formatSize(item.sizeBytes) : null;
  const details = [resolution, size].filter(Boolean).join(' · ');

  return (
    <div
      className="media-tile"
      role="listitem"
      data-selected={selected}
      data-kind={item.kind}
      style={{ width: `${width}px`, height: `${height}px` }}
    >
      <button
        type="button"
        className="media-tile__open"
        data-testid="media-open"
        onClick={onOpenClick}
        onMouseEnter={() => setHovered(true)}
        onMouseLeave={() => setHovered(false)}
        onFocus={() => setHovered(true)}
        onBlur={() => setHovered(false)}
        aria-label={t('mediaWs.previewAria', { name: item.displayName })}
      >
        {isVideo ? (
          // The tile is already the video's real aspect ratio, so 'cover' fills
          // it edge to edge with nothing cropped that 'contain' would have shown
          // — and where the two do diverge (a ratio clamped by
          // getMediaAspectRatio, or the fraction of a pixel a justified row's
          // last tile absorbs) it trims that sliver instead of filling it with a
          // blurred stand-in.
          <VideoPreview
            posterUrl={item.posterUrl ?? item.thumbnailUrl}
            previewStripUrl={item.previewStripUrl}
            active={hovered}
            fit="cover"
            className="media-tile__media"
          />
        ) : thumbFailed ? (
          <span className="media-tile__media media-tile__placeholder" aria-hidden="true">🖼</span>
        ) : (
          // Photo: ONE thumbnail layer, filling the tile. There is deliberately
          // no second blurred copy behind it — the tile is reserved from the
          // item's DISPLAY dimensions (EXIF-rotation applied server-side), so a
          // backdrop had nothing legitimate left to fill and only ever surfaced
          // as blurred bands whenever the reserved ratio was wrong.
          <span className="media-tile__frame">
            <img
              src={item.thumbnailUrl}
              alt=""
              className="media-tile__media"
              loading="lazy"
              decoding="async"
              onError={() => setThumbFailed(true)}
            />
          </span>
        )}

        {isVideo && (
          <>
            <span className="media-tile__video-badge" data-testid="media-video-badge" aria-hidden="true">
              {t('mediaWs.videoBadge')}
            </span>
            {item.durationSeconds != null && (
              <span className="media-tile__video-duration" data-testid="media-video-duration">
                {formatDuration(item.durationSeconds)}
              </span>
            )}
            {/* VSEM-03: where in the video the match is. Unobtrusive, and it
                shows a TIME, never a score or any model detail. */}
            {/* SEARCH-SEM-01: every matching moment, not just the badge's
                best one. Videos returned by a semantic search only. */}
            {isVideo && markers.length > 0 && (
              <SemanticMarkerStrip
                markers={markers}
                durationSeconds={item.durationSeconds}
                formatOffset={formatDuration}
                onSeek={(ms) => onOpen(ms)}
              />
            )}
            {semanticMs != null && (
              <span
                className="media-tile__semantic-time"
                data-testid="media-semantic-time"
                title={t('mediaWs.semanticMatchAt', { time: formatDuration(semanticMs / 1000) })}
              >
                ▸ {formatDuration(semanticMs / 1000)}
              </span>
            )}
          </>
        )}

        {badge !== null && (
          // Always visible (not hover-gated): it is the ranking information the
          // Similar Photos Explorer exists to convey.
          <span className="media-tile__badge" data-testid="media-tile-badge">
            {badge}
          </span>
        )}

        <span className="media-tile__overlay" aria-hidden="true">
          <span className="media-tile__metadata">
            <strong className="media-tile__name">
              {item.displayName}
              {item.hasDuplicates && (
                <span className="media-tile__dup" data-testid="duplicate-badge" title={String(item.occurrenceCount)}>
                  ×{item.occurrenceCount}
                </span>
              )}
            </strong>
            {details && <span className="media-tile__details">{details}</span>}
          </span>
        </span>
      </button>

      <button
        type="button"
        className="media-tile__select"
        data-testid="media-select-control"
        aria-pressed={selected}
        aria-label={t(selected ? 'mediaWs.deselectAria' : 'mediaWs.selectAria', { name: item.displayName })}
        onClick={onSelectClick}
      >
        <span aria-hidden="true">{selected ? '✓' : ''}</span>
      </button>
    </div>
  );
}
