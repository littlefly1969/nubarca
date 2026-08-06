import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { Link, useParams } from 'react-router';
import {
  ApiError,
  getSharedAlbum,
  listSharedAlbumItems,
  withdrawSharedAlbumContribution,
  type SharedAlbumDetail,
  type SharedAlbumItem,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';
import { HlsVideoPlayer } from '../video/HlsVideoPlayer';
import { ContributeToAlbumPanel } from '../albums/ContributeToAlbumPanel';
import { AlbumDetailsEditor } from '../albums/AlbumDetailsEditor';
import { AlbumSharedContentPanel } from '../albums/AlbumSharedContentPanel';
import { computeJustifiedRows, type JustifiedLayoutItem } from '../media/layout/computeJustifiedRows';
import { MEDIA_WALL_GAP_PX, mediaWallRowParams } from '../media/layout/mediaWallGeometry';

// SHARE-ALBUM-01: the recipient's view of ONE live shared album.
//
// Deliberately a purpose-built surface rather than the owner's MediaWorkspace.
// MediaWorkspace is wired to the owner's library — filters, People, Similar
// Photos, album membership editing, exclusion, metadata. Reusing it here would
// mean disabling a dozen affordances one by one and hoping none was missed. A
// recipient gets a viewer whose only capabilities are "look" and, if the owner
// allowed it, "download": the affordances the recipient must not have do not
// exist in this component at all, so they cannot leak back in.
//
// The video PLAYER, on the other hand, is the shared one — pointed at the
// album-scoped route family. That is the part where a second implementation
// would be a genuine liability.

type Status =
  | { kind: 'loading' }
  | { kind: 'ready'; album: SharedAlbumDetail; items: SharedAlbumItem[] }
  // The share is gone: revoked, declined, the album deleted, or the owner's
  // account disabled. All indistinguishable by design, and all mean the same
  // thing to the person looking at the screen.
  | { kind: 'unavailable' }
  | { kind: 'error'; message: string };

export function SharedAlbumDetailPage() {
  const { albumId } = useParams<{ albumId: string }>();
  const { state, invalidateAuth } = useAuth();
  const { t, tn } = useI18n();
  const [status, setStatus] = useState<Status>({ kind: 'loading' });
  const [openIndex, setOpenIndex] = useState<number | null>(null);
  const abortRef = useRef<AbortController | null>(null);
  const lightboxRef = useRef<HTMLDivElement>(null);
  const wallRef = useRef<HTMLDivElement>(null);
  const [contributeOpen, setContributeOpen] = useState(false);
  const contributeButtonRef = useRef<HTMLButtonElement>(null);
  // A transient notice for state that changed under the user (role downgrade,
  // revocation, an item removed by the owner) — shown once, never as a loop.
  const [notice, setNotice] = useState<string | null>(null);
  // SHARE-ALBUM-03: curation, offered only when the server says this caller may
  // edit. Absent — not disabled — for a Viewer or Contributor, so the UI never
  // advertises a capability they do not have.
  const [editOpen, setEditOpen] = useState(false);
  const [curateOpen, setCurateOpen] = useState(false);
  const editButtonRef = useRef<HTMLButtonElement>(null);
  const curateButtonRef = useRef<HTMLButtonElement>(null);
  // `null` until the real width is known, so tiles never render at an invented
  // size and then reflow — the same rule MediaGrid follows.
  const [wallWidth, setWallWidth] = useState<number | null>(null);

  const load = useCallback(() => {
    if (!albumId) return;
    abortRef.current?.abort();
    const ctrl = new AbortController();
    abortRef.current = ctrl;
    setStatus({ kind: 'loading' });
    Promise.all([getSharedAlbum(albumId, ctrl.signal), listSharedAlbumItems(albumId, ctrl.signal)])
      .then(([album, items]) => setStatus({ kind: 'ready', album, items }))
      .catch((err) => {
        if ((err as Error).name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        if (err instanceof ApiError && err.status === 404) { setStatus({ kind: 'unavailable' }); return; }
        setStatus({ kind: 'error', message: t('sharedAlbum.loadError') });
      });
  }, [albumId, invalidateAuth, t]);

  useEffect(() => {
    load();
    return () => abortRef.current?.abort();
  }, [load]);

  const items = status.kind === 'ready' ? status.items : [];
  const open = openIndex !== null ? items[openIndex] : undefined;

  // Arrow keys and Escape while the lightbox is open. Registered on the
  // document so it works regardless of which element inside has focus.
  useEffect(() => {
    if (openIndex === null) return;
    function onKey(e: globalThis.KeyboardEvent) {
      if (e.key === 'Escape') { setOpenIndex(null); return; }
      if (e.key === 'ArrowRight') {
        setOpenIndex((i) => (i === null ? null : Math.min(i + 1, items.length - 1)));
      }
      if (e.key === 'ArrowLeft') {
        setOpenIndex((i) => (i === null ? null : Math.max(i - 1, 0)));
      }
    }
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [openIndex, items.length]);

  useEffect(() => {
    if (openIndex !== null) lightboxRef.current?.focus();
  }, [openIndex]);

  useLayoutEffect(() => {
    const node = wallRef.current;
    if (!node) return;
    const measure = () => {
      const next = Math.round(node.getBoundingClientRect().width);
      if (next > 0) {
        setWallWidth((prev) => (prev != null && Math.abs(prev - next) < 1 ? prev : next));
      }
    };
    measure();
    if (typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(measure);
    ro.observe(node);
    return () => ro.disconnect();
  }, [items.length]);

  // The SAME justified geometry as the library and album walls: every full row
  // spans the container exactly and each tile keeps its real aspect ratio. A
  // plain CSS grid was tried first and looked wrong — mixed portrait/landscape
  // media left ragged rows and holes, which is precisely what a justified
  // layout exists to prevent.
  const rows = useMemo(() => {
    if (wallWidth == null) return [];
    const layoutItems: JustifiedLayoutItem[] = items.map((item, index) => ({
      id: item.fileItemId,
      originalIndex: index,
      aspectRatio: item.width && item.height ? item.width / item.height : 1,
    }));
    const params = mediaWallRowParams(wallWidth);
    return computeJustifiedRows(layoutItems, {
      containerWidth: wallWidth,
      gap: MEDIA_WALL_GAP_PX,
      ...params,
    });
  }, [items, wallWidth]);

  async function withdraw(item: SharedAlbumItem) {
    if (!window.confirm(t('sharedAlbum.confirmWithdraw'))) return;
    try {
      await withdrawSharedAlbumContribution(albumId!, item.fileItemId);
      setOpenIndex(null);
      load();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      // Already gone — the owner removed it, or a second tab withdrew it.
      // Reload to the truth instead of reporting a failure for a no-op.
      if (err instanceof ApiError && err.status === 404) {
        setNotice(t('sharedAlbum.itemGone'));
        setOpenIndex(null);
        load();
        return;
      }
      setNotice(t('sharedAlbum.withdrawError'));
    }
  }

  if (state.status !== 'authed' || !albumId) return null;

  if (status.kind === 'loading') {
    return <div className="page-container"><p>{t('common.loading')}</p></div>;
  }

  if (status.kind === 'unavailable') {
    return (
      <div className="page-container">
        <p className="empty-state" data-testid="shared-album-unavailable" role="status">
          {t('sharedAlbum.unavailable')}
        </p>
        <Link to="/shared-albums">{t('sharedAlbum.backToShared')}</Link>
      </div>
    );
  }

  if (status.kind === 'error') {
    return (
      <div className="page-container">
        <p className="page-error" role="alert">{status.message}</p>
        <button type="button" className="row-action" onClick={load}>{t('common.retry')}</button>
        {' '}
        <Link to="/shared-albums">{t('sharedAlbum.backToShared')}</Link>
      </div>
    );
  }

  const { album } = status;

  return (
    <section className="ws-page-outer shared-album-page" data-testid="shared-album-page">
      <header className="ws-page-header album-detail-header">
        <Link to="/shared-albums" className="back-link">{t('sharedAlbum.backToShared')}</Link>
        <div className="album-detail-title-row">
          <div>
            <h1>{album.name}</h1>
            {/* "Live, and owned by somebody else" is stated, not implied. */}
            <p className="muted" data-testid="shared-album-owner">
              {t('sharedAlbum.liveOwnedBy', { owner: album.ownerDisplayName })}
            </p>
            {album.description && <p className="album-description">{album.description}</p>}
          </div>
          <div className="album-detail-header-actions">
            <span className="album-badge album-badge-shared">
              {album.role === 'editor'
                ? t('albumRole.editor')
                : album.role === 'contributor'
                  ? t('albumRole.contributor')
                  : t('albumRole.viewer')}
            </span>
            {/* Offered only to a Contributor. The server refuses a Viewer
                regardless — hiding it is UX, not the gate. */}
            {album.canEdit && (
              <>
                <button
                  type="button"
                  ref={editButtonRef}
                  className="row-action"
                  data-testid="shared-album-edit"
                  onClick={() => setEditOpen(true)}
                >
                  {t('albumEdit.open')}
                </button>
                <button
                  type="button"
                  ref={curateButtonRef}
                  className="row-action"
                  data-testid="shared-album-curate"
                  onClick={() => setCurateOpen(true)}
                >
                  {t('albumContent.tab')}
                </button>
              </>
            )}
            {(album.role === 'contributor' || album.role === 'editor') && (
              <button
                type="button"
                ref={contributeButtonRef}
                className="row-action"
                data-testid="shared-album-add"
                onClick={() => setContributeOpen(true)}
              >
                {t('sharedAlbum.addToAlbum')}
              </button>
            )}
          </div>
        </div>
        {notice && <p className="inline-error" role="status" data-testid="shared-album-notice">{notice}</p>}
      </header>

      {items.length === 0 ? (
        <p className="empty-state" data-testid="shared-album-empty">{t('sharedAlbum.empty')}</p>
      ) : (
        <>
          <p className="muted">{tn(items.length, 'albums.itemsCount')}</p>
          <div className="shared-media-wall" ref={wallRef} data-testid="shared-album-items">
            {rows.map((row) => (
              <div
                key={row.key}
                className="shared-media-row"
                style={{ height: `${row.height}px`, gap: `${MEDIA_WALL_GAP_PX}px` }}
              >
                {row.items.map((tile) => {
                  const item = items[tile.originalIndex];
                  return (
                    <button
                      key={tile.id}
                      type="button"
                      className="shared-media-tile"
                      data-testid="shared-media-tile"
                      style={{ width: `${tile.width}px`, height: `${tile.height}px` }}
                      aria-label={t('sharedAlbum.openItem', {
                        index: tile.originalIndex + 1, total: items.length,
                      })}
                      onClick={() => setOpenIndex(tile.originalIndex)}
                    >
                      <img
                        src={item.kind === 'video'
                          ? (item.posterUrl ?? item.thumbnailUrl)
                          : item.thumbnailUrl}
                        alt=""
                        loading="lazy"
                      />
                      {item.kind === 'video' && (
                        <span className="shared-media-video-badge" aria-hidden="true">▶</span>
                      )}
                      {/* Which of these are mine, so "withdraw" is never a
                          guess. Discreet: no contributor identity is shown to
                          a member — that provenance is the owner's surface. */}
                      {item.canWithdraw && (
                        <span className="shared-media-mine-badge" data-testid="shared-media-mine">
                          {t('sharedAlbum.mine')}
                        </span>
                      )}
                    </button>
                  );
                })}
              </div>
            ))}
          </div>
        </>
      )}

      {editOpen && (
        <AlbumDetailsEditor
          albumId={albumId}
          version={album.version}
          name={album.name}
          description={album.description}
          onSaved={load}
          onClose={() => setEditOpen(false)}
          returnFocusRef={editButtonRef}
        />
      )}

      {curateOpen && (
        <AlbumSharedContentPanel
          albumId={albumId}
          onClose={() => { setCurateOpen(false); load(); }}
          returnFocusRef={curateButtonRef}
        />
      )}

      {contributeOpen && (
        <ContributeToAlbumPanel
          albumId={albumId}
          presentFileIds={new Set(items.map((i) => i.fileItemId))}
          onContributed={load}
          onClose={() => setContributeOpen(false)}
          returnFocusRef={contributeButtonRef}
        />
      )}

      {open && openIndex !== null && (
        <div
          className="shared-lightbox-backdrop"
          data-testid="shared-lightbox"
          onMouseDown={(e) => { if (e.target === e.currentTarget) setOpenIndex(null); }}
        >
          <div
            ref={lightboxRef}
            className="shared-lightbox"
            role="dialog"
            aria-modal="true"
            aria-label={t('sharedAlbum.viewerLabel', { name: album.name })}
            tabIndex={-1}
          >
            <header className="shared-lightbox-head">
              <span className="muted">{openIndex + 1} / {items.length}</span>
              <div className="shared-lightbox-actions">
                {/* Rendered only when the membership permits originals. The
                    endpoint enforces the same rule — this is a courtesy. */}
                {open.canWithdraw && (
                  <button
                    type="button"
                    className="row-action"
                    data-testid="shared-withdraw"
                    aria-label={t('sharedAlbum.withdrawAria')}
                    onClick={() => void withdraw(open)}
                  >
                    {t('sharedAlbum.withdraw')}
                  </button>
                )}
                {open.downloadUrl && (
                  <a
                    className="row-action"
                    href={open.downloadUrl}
                    data-testid="shared-download"
                    // No referrer: the album-scoped URL should not travel to
                    // anything the download navigation might touch.
                    rel="noreferrer"
                  >
                    {t('common.download')}
                  </a>
                )}
                <button
                  type="button"
                  className="ws-icon-button"
                  aria-label={t('common.close')}
                  data-testid="shared-lightbox-close"
                  onClick={() => setOpenIndex(null)}
                >
                  ✕
                </button>
              </div>
            </header>

            <div className="shared-lightbox-stage">
              <button
                type="button"
                className="shared-lightbox-nav"
                aria-label={t('sharedAlbum.previous')}
                disabled={openIndex === 0}
                onClick={() => setOpenIndex(Math.max(openIndex - 1, 0))}
              >
                ‹
              </button>

              {open.kind === 'video' && open.videoUrl ? (
                <HlsVideoPlayer
                  key={open.fileItemId}
                  fileId={open.fileItemId}
                  videoUrl={open.videoUrl}
                  posterUrl={open.posterUrl ?? undefined}
                  className="shared-lightbox-video"
                />
              ) : (
                <img
                  className="shared-lightbox-image"
                  src={open.kind === 'video' ? (open.posterUrl ?? open.previewUrl) : open.previewUrl}
                  alt=""
                  data-testid="shared-lightbox-image"
                />
              )}

              <button
                type="button"
                className="shared-lightbox-nav"
                aria-label={t('sharedAlbum.next')}
                disabled={openIndex === items.length - 1}
                onClick={() => setOpenIndex(Math.min(openIndex + 1, items.length - 1))}
              >
                ›
              </button>
            </div>
          </div>
        </div>
      )}
    </section>
  );
}
