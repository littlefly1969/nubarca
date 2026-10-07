import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import {
  ApiError,
  getAlbum,
  getAlbumPartySettings,
  type AlbumDetail,
  type AlbumPartyStatus,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { usePermissions } from '../auth/usePermissions';
import { PERMISSIONS } from '../auth/permissions';
import { useI18n } from '../i18n';
import { AlbumSettingsPanel } from '../albums/AlbumSettingsPanel';
import { AlbumSharePanel } from '../albums/AlbumSharePanel';
import { AlbumSharedContentPanel } from '../albums/AlbumSharedContentPanel';
import { AlbumCopyPanel } from '../albums/AlbumCopyPanel';
import { MediaWorkspace } from '../media/workspace/MediaWorkspace';
import { WorkspaceMenu } from '../media/workspace/WorkspaceMenu';
import { Icon } from '../components/icons/Icon';
import {
  filtersToUrlParams,
  identityFromUrlParams,
  type MediaWorkspaceIdentity,
  type MediaWorkspaceSource,
} from '../media/workspace/mediaWorkspaceQuery';

// Slice 5: the album detail is now a MediaWorkspace (source=album) — the same
// Tutti/Foto/Video + In libreria/Esclusi + filters/grid/viewer/selection the
// library uses — with the album's rename/description/TV/Party/delete controls
// relocated into AlbumSettingsPanel. Albums stay mixed (no photo/video split).

type HeaderStatus =
  | { kind: 'loading' }
  | { kind: 'ready'; album: AlbumDetail; party: AlbumPartyStatus | null }
  | { kind: 'error'; message: string };

export function AlbumDetailPage() {
  const { albumId } = useParams<{ albumId: string }>();
  const navigate = useNavigate();
  const { state, invalidateAuth } = useAuth();
  const canParty = usePermissions().has(PERMISSIONS.partyAccess);
  const { t } = useI18n();
  const [searchParams, setSearchParams] = useSearchParams();
  const [status, setStatus] = useState<HeaderStatus>({ kind: 'loading' });
  const [settingsOpen, setSettingsOpen] = useState(false);
  const overflowButtonRef = useRef<HTMLButtonElement>(null);
  // SHARE-ALBUM-01: sharing is its OWN entry point, not a row buried in
  // Settings next to Show-on-TV and Party. Those grant public/device
  // visibility; this grants a named person an authenticated, revocable
  // membership, and conflating the three is how a user shares the wrong way.
  const [shareOpen, setShareOpen] = useState(false);
  const shareButtonRef = useRef<HTMLButtonElement>(null);
  // SHARE-ALBUM-02: the live album's full content — the owner's own items plus
  // collaborator contributions. Offered only once the album actually has
  // members, so an unshared album keeps exactly the surface it had before and
  // there are never two near-identical views of the same album on screen.
  const [contentOpen, setContentOpen] = useState(false);
  // SHARE-COPY-01: "Send a copy" is its OWN entry point, next to but distinct
  // from "Share". Sharing grants revocable access to media that stays yours;
  // sending a copy gives away an independent album you can never take back.
  // Presenting them as two settings of one control is how somebody gives away
  // an album they only meant to show.
  //
  // This page is the OWNER's album view — a collaborator is routed to
  // SharedAlbumDetailPage instead — so the button is owner-only by construction,
  // and the backend answers any other caller with a 404 regardless.
  const [copyOpen, setCopyOpen] = useState(false);
  const abortRef = useRef<AbortController | null>(null);

  const source = useMemo<MediaWorkspaceSource>(
    () => ({ kind: 'album', albumId: albumId ?? '' }),
    [albumId],
  );

  // `identity` is owned in state (source of truth), seeded ONCE from the URL.
  // Only the shareable subset is mirrored back to the URL, so session-only
  // filters (visual/GPS/dates/favorite/rating/collapse — kept out of the URL)
  // survive an Apply instead of being wiped by a URL round-trip.
  const initialParamsRef = useRef(searchParams);
  const [identity, setIdentity] = useState<MediaWorkspaceIdentity>(
    () => identityFromUrlParams(source, initialParamsRef.current),
  );

  useEffect(() => {
    if (!albumId) return;
    abortRef.current?.abort();
    const ctrl = new AbortController();
    abortRef.current = ctrl;
    setStatus({ kind: 'loading' });
    // Party settings are only ASKED for by a caller who may have them. Without
    // `party.access` the server answers 403 — correctly — and requesting it
    // anyway would turn a permission the user simply does not hold into a
    // failure to load their own album.
    Promise.all([
      getAlbum(albumId, ctrl.signal),
      canParty ? getAlbumPartySettings(albumId, ctrl.signal) : Promise.resolve(null),
    ])
      .then(([album, party]) => setStatus({ kind: 'ready', album, party }))
      .catch((err) => {
        if ((err as Error).name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        if (err instanceof ApiError && err.status === 404) { void navigate('/albums'); return; }
        setStatus({ kind: 'error', message: t('albumDetail.loadError') });
      });
    return () => ctrl.abort();
  }, [albumId, canParty, invalidateAuth, navigate, t]);

  const onIdentityChange = useCallback((next: MediaWorkspaceIdentity) => {
    setIdentity(next);
    setSearchParams(filtersToUrlParams(next), { replace: true });
  }, [setSearchParams]);

  if (state.status !== 'authed' || !albumId) return null;
  if (status.kind === 'loading') return <div className="page-container"><p>{t('common.loading')}</p></div>;
  if (status.kind === 'error') {
    return (
      <div className="page-container">
        <p className="page-error" role="alert">{status.message}</p>
        <Link to="/albums">{t('albumDetail.backToAlbums')}</Link>
      </div>
    );
  }

  const { album, party } = status;

  return (
    <section className="ws-page-outer" data-testid="album-detail-page">
      <header className="ws-page-header album-detail-header album-owner-context">
        <div className="album-detail-title-row">
          <Link to="/albums" className="album-context-back" aria-label={t('albumDetail.backToAlbums')}>
            <Icon name="chevron-left" />
          </Link>
          <h1 title={album.name}>{album.name}</h1>
          <div className="album-detail-header-actions">
            <button
              type="button"
              ref={shareButtonRef}
              className="row-action"
              data-testid="album-open-share"
              onClick={() => setShareOpen(true)}
            >
              {t('albumShare.openButton')}
            </button>
            <WorkspaceMenu label={t('mediaWs.albumActions')} testId="album-more-actions" buttonRef={overflowButtonRef}
              actions={[
                { id: 'content', label: t('albumContent.tab'), icon: 'edit', testId: 'album-open-content', onSelect: () => setContentOpen(true) },
                { id: 'copy', label: t('albumCopy.openButton'), icon: 'shares', testId: 'album-open-copy', onSelect: () => setCopyOpen(true) },
                { id: 'print', label: t('ownerPrint.action'), icon: 'print', testId: 'album-open-print', href: `/print?album=${encodeURIComponent(album.id)}` },
                { id: 'settings', label: t('mediaWs.albumSettings'), icon: 'info', testId: 'album-open-settings', onSelect: () => setSettingsOpen(true) },
              ]} />
          </div>
        </div>
        {album.description && <p className="album-description">{album.description}</p>}
      </header>

      {/* ONE heavy media surface at a time. While the content manager is open
          the album's wall is UNMOUNTED, not hidden: a hidden workspace would
          keep its pages, its decoded images, its observers and its handlers
          alive underneath a dialog that covers it. The identity (tab, filters)
          is owned by this page, so closing the manager returns to the same view
          — re-read, which is also what shows the curator's new order. */}
      {!contentOpen && (
        <MediaWorkspace
          source={source}
          identity={identity}
          onIdentityChange={onIdentityChange}
          searchPlaceholder={t('mediaWs.searchAlbum')}
          showPhotoPresentation
        />
      )}

      {shareOpen && (
        <AlbumSharePanel
          albumId={albumId}
          albumName={album.name}
          onClose={() => setShareOpen(false)}
          returnFocusRef={shareButtonRef}
        />
      )}

      {copyOpen && (
        <AlbumCopyPanel
          albumId={albumId}
          albumName={album.name}
          onClose={() => setCopyOpen(false)}
          returnFocusRef={overflowButtonRef}
        />
      )}

      {contentOpen && (
        <AlbumSharedContentPanel
          albumId={albumId}
          onClose={() => setContentOpen(false)}
          returnFocusRef={overflowButtonRef}
        />
      )}

      {settingsOpen && (
        <AlbumSettingsPanel
          albumId={albumId}
          album={album}
          party={party}
          onAlbumUpdated={(updated) => setStatus({ kind: 'ready', album: updated, party })}
          onPartyUpdated={(updatedParty) => setStatus({ kind: 'ready', album, party: updatedParty })}
          onDeleted={() => navigate('/albums')}
          onClose={() => setSettingsOpen(false)}
          returnFocusRef={overflowButtonRef}
        />
      )}
    </section>
  );
}
