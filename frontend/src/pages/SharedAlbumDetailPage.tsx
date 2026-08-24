import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import {
  ApiError,
  getSharedAlbum,
  type SharedAlbumDetail,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';
import { sharedAlbumAddContext } from '../albums/sharedAlbumAddContext';
import { AlbumDetailsEditor } from '../albums/AlbumDetailsEditor';
import { AlbumSharedContentPanel } from '../albums/AlbumSharedContentPanel';
import { SharedAlbumBrowser } from '../albums/SharedAlbumBrowser';
import { getAlbumExperienceCapabilities } from '../albums/albumCapabilities';

// The recipient's view of ONE live shared album.
//
// This page owns the album's IDENTITY — who owns it, what this membership may
// do, and the curation panels an Editor gets. The media itself is browsed by
// SharedAlbumBrowser, which is the same browsing language the owner's album
// uses: All / Photos / Videos, one justified wall, one full-screen viewer, one
// Play. What separates the two is not the components, it is the authority: every
// media URL here is album-scoped and was built by the server, and every action
// this membership may not perform is absent from the tree rather than disabled.
//
// Media SELECTION is deliberately not here. There used to be a shared-album-only
// photo grid; it was a second, worse media picker with no tabs, no search, no
// filters and no videos. A Contributor now goes to the ordinary Media Library,
// selects there exactly as they always do, and the common destination picker
// opens with this album already chosen.

type Status =
  | { kind: 'loading' }
  | { kind: 'ready'; album: SharedAlbumDetail }
  // The share is gone: revoked, declined, the album deleted, or the owner's
  // account disabled. All indistinguishable by design, and all mean the same
  // thing to the person looking at the screen.
  | { kind: 'unavailable' }
  | { kind: 'error'; message: string };

export function SharedAlbumDetailPage() {
  const { albumId } = useParams<{ albumId: string }>();
  const { state, invalidateAuth } = useAuth();
  const { t } = useI18n();
  const navigate = useNavigate();
  const [status, setStatus] = useState<Status>({ kind: 'loading' });
  const abortRef = useRef<AbortController | null>(null);
  // A transient notice for state that changed under the user (a role downgrade,
  // a revocation, an item removed by the owner) — shown once, never as a loop.
  const [notice, setNotice] = useState<string | null>(null);
  // Curation, offered only when the SERVER says this caller may edit. Absent —
  // not disabled — otherwise, so the UI never advertises a capability the person
  // does not have.
  const [editOpen, setEditOpen] = useState(false);
  const [curateOpen, setCurateOpen] = useState(false);
  const editButtonRef = useRef<HTMLButtonElement>(null);
  const curateButtonRef = useRef<HTMLButtonElement>(null);

  const load = useCallback(() => {
    if (!albumId) return;
    abortRef.current?.abort();
    const ctrl = new AbortController();
    abortRef.current = ctrl;
    setStatus({ kind: 'loading' });
    getSharedAlbum(albumId, ctrl.signal)
      .then((album) => setStatus({ kind: 'ready', album }))
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

  const album = status.kind === 'ready' ? status.album : null;

  // What this membership may do, in one place. The server's `canEdit` decides
  // curation — never the role label, which is why an Editor whose canEdit came
  // back false gets no editing controls.
  const capabilities = useMemo(
    () => getAlbumExperienceCapabilities({
      ownership: 'member',
      role: album?.role ?? null,
      canEdit: album?.canEdit ?? false,
      allowOriginalDownload: album?.allowOriginalDownload ?? false,
    }),
    [album?.role, album?.canEdit, album?.allowOriginalDownload],
  );

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
        <Link to="/albums?scope=shared">{t('sharedAlbum.backToShared')}</Link>
      </div>
    );
  }

  if (status.kind === 'error') {
    return (
      <div className="page-container">
        <p className="page-error" role="alert">{status.message}</p>
        <button type="button" className="row-action" onClick={load}>{t('common.retry')}</button>
        {' '}
        <Link to="/albums?scope=shared">{t('sharedAlbum.backToShared')}</Link>
      </div>
    );
  }

  const current = status.album;

  return (
    <section className="ws-page-outer shared-album-page" data-testid="shared-album-page">
      <header className="ws-page-header album-detail-header">
        <Link to="/albums?scope=shared" className="back-link">{t('sharedAlbum.backToShared')}</Link>
        <div className="album-detail-title-row">
          <div>
            <h1>{current.name}</h1>
            {/* "Live, and owned by somebody else" is stated, not implied. */}
            <p className="muted" data-testid="shared-album-owner">
              {t('sharedAlbum.liveOwnedBy', { owner: current.ownerDisplayName })}
            </p>
            {current.description && <p className="album-description">{current.description}</p>}
          </div>
          <div className="album-detail-header-actions">
            <span className="album-badge album-badge-shared">
              {current.role === 'editor'
                ? t('albumRole.editor')
                : current.role === 'contributor'
                  ? t('albumRole.contributor')
                  : t('albumRole.viewer')}
            </span>
            {capabilities.editAlbumDetails && (
              <button
                type="button"
                ref={editButtonRef}
                className="row-action"
                data-testid="shared-album-edit"
                onClick={() => setEditOpen(true)}
              >
                {t('albumEdit.open')}
              </button>
            )}
            {capabilities.curateContent && (
              <button
                type="button"
                ref={curateButtonRef}
                className="row-action"
                data-testid="shared-album-curate"
                onClick={() => setCurateOpen(true)}
              >
                {t('albumContent.tab')}
              </button>
            )}
            {/* It does not open a picker: it hands the Library a transient "I am
                filling this album" context and goes there, because there is
                exactly one place in NubArca where media is chosen. */}
            {capabilities.contribute && (
              <button
                type="button"
                className="row-action"
                data-testid="shared-album-add"
                onClick={() => navigate('/media', {
                  state: { sharedAlbumAdd: sharedAlbumAddContext(albumId, current.name) },
                })}
              >
                {t('sharedAlbum.addFromLibrary')}
              </button>
            )}
          </div>
        </div>
        {notice && <p className="inline-error" role="status" data-testid="shared-album-notice">{notice}</p>}
      </header>

      <SharedAlbumBrowser
        albumId={albumId}
        albumName={current.name}
        capabilities={capabilities}
        onItemsChanged={load}
        onNotice={setNotice}
      />

      {editOpen && (
        <AlbumDetailsEditor
          albumId={albumId}
          version={current.version}
          name={current.name}
          description={current.description}
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
    </section>
  );
}
