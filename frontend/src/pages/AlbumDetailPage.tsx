import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import {
  ApiError,
  getAlbum,
  getAlbumPartySettings,
  listAlbumMembers,
  type AlbumDetail,
  type AlbumPartyStatus,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';
import { AlbumSettingsPanel } from '../albums/AlbumSettingsPanel';
import { AlbumSharePanel } from '../albums/AlbumSharePanel';
import { AlbumSharedContentPanel } from '../albums/AlbumSharedContentPanel';
import { AlbumCopyPanel } from '../albums/AlbumCopyPanel';
import { MediaWorkspace } from '../media/workspace/MediaWorkspace';
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
  const { t } = useI18n();
  const [searchParams, setSearchParams] = useSearchParams();
  const [status, setStatus] = useState<HeaderStatus>({ kind: 'loading' });
  const [settingsOpen, setSettingsOpen] = useState(false);
  const settingsButtonRef = useRef<HTMLButtonElement>(null);
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
  const contentButtonRef = useRef<HTMLButtonElement>(null);
  const [hasMembers, setHasMembers] = useState(false);
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
  const copyButtonRef = useRef<HTMLButtonElement>(null);
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
    Promise.all([getAlbum(albumId, ctrl.signal), getAlbumPartySettings(albumId, ctrl.signal)])
      .then(([album, party]) => setStatus({ kind: 'ready', album, party }))
      .catch((err) => {
        if ((err as Error).name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        if (err instanceof ApiError && err.status === 404) { void navigate('/albums'); return; }
        setStatus({ kind: 'error', message: t('albumDetail.loadError') });
      });
    return () => ctrl.abort();
  }, [albumId, invalidateAuth, navigate, t]);

  // Whether to offer the shared-content view at all. A plain 404/401 here just
  // leaves it hidden: it is a navigation affordance, not a permission.
  const refreshMembership = useCallback(() => {
    if (!albumId) return;
    listAlbumMembers(albumId)
      .then((members) => setHasMembers(members.some(
        (m) => m.state === 'pending' || m.state === 'accepted')))
      .catch(() => setHasMembers(false));
  }, [albumId]);

  useEffect(() => { refreshMembership(); }, [refreshMembership]);

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
      <header className="ws-page-header album-detail-header">
        <Link to="/albums" className="back-link">{t('albumDetail.backToAlbums')}</Link>
        <div className="album-detail-title-row">
          <div>
            <h1>{album.name}</h1>
            {album.description && <p className="album-description">{album.description}</p>}
          </div>
          <div className="album-detail-header-actions">
            {hasMembers && (
              <button
                type="button"
                ref={contentButtonRef}
                className="row-action"
                data-testid="album-open-content"
                onClick={() => setContentOpen(true)}
              >
                {t('albumContent.tab')}
              </button>
            )}
            <button
              type="button"
              ref={shareButtonRef}
              className="row-action"
              data-testid="album-open-share"
              onClick={() => setShareOpen(true)}
            >
              {t('albumShare.openButton')}
            </button>
            <button
              type="button"
              ref={copyButtonRef}
              className="row-action"
              data-testid="album-open-copy"
              onClick={() => setCopyOpen(true)}
            >
              {t('albumCopy.openButton')}
            </button>
            <button
              type="button"
              ref={settingsButtonRef}
              className="row-action"
              data-testid="album-open-settings"
              onClick={() => setSettingsOpen(true)}
            >
              {t('mediaWs.albumSettings')}
            </button>
          </div>
        </div>
      </header>

      <MediaWorkspace
        source={source}
        identity={identity}
        onIdentityChange={onIdentityChange}
        searchPlaceholder={t('mediaWs.searchAlbum')}
      />

      {shareOpen && (
        <AlbumSharePanel
          albumId={albumId}
          albumName={album.name}
          onClose={() => { setShareOpen(false); refreshMembership(); }}
          returnFocusRef={shareButtonRef}
        />
      )}

      {copyOpen && (
        <AlbumCopyPanel
          albumId={albumId}
          albumName={album.name}
          onClose={() => setCopyOpen(false)}
          returnFocusRef={copyButtonRef}
        />
      )}

      {contentOpen && (
        <AlbumSharedContentPanel
          albumId={albumId}
          onClose={() => setContentOpen(false)}
          returnFocusRef={contentButtonRef}
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
          returnFocusRef={settingsButtonRef}
        />
      )}
    </section>
  );
}
