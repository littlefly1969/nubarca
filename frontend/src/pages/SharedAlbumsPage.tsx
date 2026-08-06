import { useCallback, useEffect, useRef, useState } from 'react';
import { Link } from 'react-router';
import {
  ApiError,
  acceptAlbumInvitation,
  declineAlbumInvitation,
  listAlbumInvitations,
  listSharedAlbums,
  type AlbumInvitation,
  type SharedAlbumCoverItem,
  type SharedAlbumSummary,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';
import { ReceivedCopiesPanel } from '../albums/ReceivedCopiesPanel';

// SHARE-ALBUM-01: "Shared with me" — live albums other people own and have
// shared with this user, plus the invitations they have not answered yet.
//
// This page shows OTHER PEOPLE's albums only. The user's own albums stay at
// /albums; the two are never mixed, so "who owns this" is never ambiguous.

type Status =
  | { kind: 'loading' }
  | { kind: 'ready'; albums: SharedAlbumSummary[]; invitations: AlbumInvitation[] }
  | { kind: 'error'; message: string };

function CoverMosaic({ items, name }: { items: SharedAlbumCoverItem[]; name: string }) {
  const { t } = useI18n();
  if (items.length === 0) {
    return <div className="album-cover album-cover-empty" aria-hidden="true">🖼</div>;
  }
  return (
    <div
      className={`album-cover album-cover-mosaic count-${Math.min(items.length, 4)}`}
      data-testid="shared-album-cover"
    >
      {items.slice(0, 4).map((c) => (
        <img
          key={c.fileItemId}
          src={c.thumbnailUrl}
          alt={t('albums.coverAlt', { name })}
          loading="lazy"
          onError={(e) => { (e.target as HTMLImageElement).style.visibility = 'hidden'; }}
        />
      ))}
    </div>
  );
}

export function SharedAlbumsPage() {
  const { invalidateAuth } = useAuth();
  const { t, tn, formatDate } = useI18n();
  const [status, setStatus] = useState<Status>({ kind: 'loading' });
  const [busy, setBusy] = useState<string | null>(null);
  const abortRef = useRef<AbortController | null>(null);

  const load = useCallback(() => {
    abortRef.current?.abort();
    const ctrl = new AbortController();
    abortRef.current = ctrl;
    setStatus({ kind: 'loading' });
    Promise.all([listSharedAlbums(ctrl.signal), listAlbumInvitations(ctrl.signal)])
      .then(([albums, invitations]) => setStatus({ kind: 'ready', albums, invitations }))
      .catch((err) => {
        if ((err as Error).name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        setStatus({ kind: 'error', message: t('sharedAlbums.loadError') });
      });
  }, [invalidateAuth, t]);

  useEffect(() => {
    load();
    return () => abortRef.current?.abort();
  }, [load]);

  async function respond(invitation: AlbumInvitation, accept: boolean) {
    setBusy(invitation.membershipId);
    try {
      if (accept) await acceptAlbumInvitation(invitation.membershipId);
      else await declineAlbumInvitation(invitation.membershipId);
      load();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      // 404 means the owner cancelled it while this page was open. Reloading
      // shows the current truth rather than an error about a thing that is gone.
      load();
    } finally {
      setBusy(null);
    }
  }

  return (
    <div className="page-container shared-albums-page">
      <div className="albums-page-head">
        <h2>{t('sharedAlbums.heading')}</h2>
      </div>
      <p className="muted">{t('sharedAlbums.intro')}</p>

      {status.kind === 'loading' && (
        <ul className="album-grid" data-testid="shared-albums-skeleton" aria-hidden="true">
          {[0, 1, 2, 3].map((i) => <li key={i} className="album-card album-card-skeleton" />)}
        </ul>
      )}

      {status.kind === 'error' && <p className="page-error" role="alert">{status.message}</p>}

      {status.kind === 'ready' && (
        <>
          {status.invitations.length > 0 && (
            <section aria-label={t('sharedAlbums.invitationsHeading')}>
              <h3>{t('sharedAlbums.invitationsHeading')}</h3>
              <ul className="shared-invitations" data-testid="shared-invitations">
                {status.invitations.map((invitation) => (
                  <li
                    key={invitation.membershipId}
                    className="shared-invitation"
                    data-testid="shared-invitation"
                  >
                    <div className="shared-invitation-body">
                      <p className="shared-invitation-title">
                        {t('sharedAlbums.invitedBy', {
                          owner: invitation.ownerDisplayName,
                          album: invitation.albumName,
                        })}
                      </p>
                      {invitation.albumDescription && (
                        <p className="album-card-desc">{invitation.albumDescription}</p>
                      )}
                      <p className="muted">
                        {tn(invitation.itemCount, 'albums.itemsCount')}
                        {' · '}
                        {t('sharedAlbums.invitedAt', { date: formatDate(invitation.invitedAt) })}
                        {' · '}
                        {invitation.allowOriginalDownload
                          ? t('sharedAlbums.downloadAllowed')
                          : t('sharedAlbums.downloadNotAllowed')}
                      </p>
                    </div>
                    <div className="shared-invitation-actions">
                      <button
                        type="button"
                        className="row-action-primary"
                        data-testid="invitation-accept"
                        disabled={busy === invitation.membershipId}
                        onClick={() => void respond(invitation, true)}
                      >
                        {t('sharedAlbums.accept')}
                      </button>
                      <button
                        type="button"
                        className="row-action"
                        data-testid="invitation-decline"
                        disabled={busy === invitation.membershipId}
                        onClick={() => void respond(invitation, false)}
                      >
                        {t('sharedAlbums.decline')}
                      </button>
                    </div>
                  </li>
                ))}
              </ul>
            </section>
          )}

          {/* SHARE-COPY-01. Kept as its own section, deliberately not merged
              into the invitation list above: accepting an invitation gives you a
              view of somebody else's album, accepting a copy gives you an album
              of your own that they can never revoke. Different decisions, so
              different surfaces. */}
          <ReceivedCopiesPanel />

          {status.albums.length === 0 ? (
            status.invitations.length === 0 && (
              <p className="empty-state" data-testid="shared-albums-empty">
                {t('sharedAlbums.empty')}
              </p>
            )
          ) : (
            <section aria-label={t('sharedAlbums.albumsHeading')}>
              <h3>{t('sharedAlbums.albumsHeading')}</h3>
              <ul className="album-grid" data-testid="shared-album-list">
                {status.albums.map((album) => (
                  <li key={album.albumId} className="album-card" data-testid="shared-album-card">
                    <Link
                      to={`/shared-albums/${album.albumId}`}
                      className="album-card-link"
                      aria-label={album.name}
                    >
                      <CoverMosaic items={album.coverItems} name={album.name} />
                    </Link>
                    <div className="album-card-body">
                      <div className="album-card-titlerow">
                        <Link to={`/shared-albums/${album.albumId}`} className="album-card-name">
                          {album.name}
                        </Link>
                        {/* The album belongs to somebody else — say so on the
                            card, not only on the detail page. */}
                        <span className="album-badge album-badge-shared" data-testid="shared-owner-badge">
                          {t('sharedAlbums.ownedBy', { owner: album.ownerDisplayName })}
                        </span>
                      </div>
                      {album.description && <p className="album-card-desc">{album.description}</p>}
                      <p className="album-card-counts">{tn(album.itemCount, 'albums.itemsCount')}</p>
                    </div>
                  </li>
                ))}
              </ul>
            </section>
          )}
        </>
      )}
    </div>
  );
}
