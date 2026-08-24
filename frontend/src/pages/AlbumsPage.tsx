import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useSearchParams } from 'react-router';
import {
  ApiError,
  acceptAlbumInvitation,
  createAlbum,
  declineAlbumInvitation,
  deleteAlbum,
  listAlbumInvitations,
  listAlbums,
  listSharedAlbums,
  type AlbumInvitation,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';
import { AlbumCard } from '../albums/AlbumCard';
import { ReceivedCopiesPanel } from '../albums/ReceivedCopiesPanel';
import {
  ALBUM_COLLECTION_SCOPES,
  countByOwnerKind,
  ownedAlbumCard,
  parseAlbumScope,
  selectAlbumCards,
  sharedAlbumCard,
  type AlbumCardModel,
  type AlbumCollectionScope,
  type AlbumSortKey,
} from '../albums/albumCardModel';

// THE album destination. An album is an album: the user's own albums and the
// ones other people have shared with them live in one grid, one search and one
// sort, because "whose is it" is a property of an album rather than a reason to
// put it in a different part of the product.
//
// Ownership stays unmistakable — every card says whose album it is and, for a
// shared one, what this membership may do — and the two REMAIN two collections
// underneath: they come from two endpoints, they are normalised only at the
// presentation boundary, and they open different routes backed by different
// authority.
//
// Pending invitations are deliberately NOT in the grid. An invitation is not an
// album yet; it is a decision, and mixing a decision in among things you can
// open is how somebody accepts one by accident.

type Status =
  | { kind: 'loading' }
  | { kind: 'ready'; cards: AlbumCardModel[]; invitations: AlbumInvitation[] }
  | { kind: 'error'; message: string };

export function AlbumsPage() {
  const { invalidateAuth } = useAuth();
  const { t, tn, formatDate, formatNumber } = useI18n();
  const [searchParams, setSearchParams] = useSearchParams();
  const [status, setStatus] = useState<Status>({ kind: 'loading' });
  const [query, setQuery] = useState('');
  const [sortKey, setSortKey] = useState<AlbumSortKey>('recent');
  const [newName, setNewName] = useState('');
  const [newDesc, setNewDesc] = useState('');
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);
  const [busyInvitation, setBusyInvitation] = useState<string | null>(null);
  const abortRef = useRef<AbortController | null>(null);

  // The collection lives in the URL, so `/albums?scope=shared` is a real
  // address — that is what the retired "Shared with me" destination redirects
  // to, and what a bookmark of it keeps meaning.
  const scope = parseAlbumScope(searchParams.get('scope'));
  const setScope = (next: AlbumCollectionScope) => {
    const params = new URLSearchParams(searchParams);
    if (next === 'all') params.delete('scope');
    else params.set('scope', next);
    setSearchParams(params, { replace: true });
  };

  const load = useCallback(() => {
    abortRef.current?.abort();
    const ctrl = new AbortController();
    abortRef.current = ctrl;
    setStatus({ kind: 'loading' });
    Promise.all([
      listAlbums(ctrl.signal),
      listSharedAlbums(ctrl.signal),
      listAlbumInvitations(ctrl.signal),
    ])
      .then(([owned, shared, invitations]) => setStatus({
        kind: 'ready',
        cards: [...owned.map(ownedAlbumCard), ...shared.map(sharedAlbumCard)],
        invitations,
      }))
      .catch((err) => {
        if ((err as Error).name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        setStatus({ kind: 'error', message: t('albums.loadError') });
      });
  }, [invalidateAuth, t]);

  useEffect(() => {
    load();
    return () => abortRef.current?.abort();
  }, [load]);

  const handleCreate = async () => {
    const name = newName.trim();
    if (!name) { setCreateError(t('albums.nameRequired')); return; }
    setCreating(true);
    setCreateError(null);
    try {
      await createAlbum(name, newDesc.trim() || null);
      setNewName('');
      setNewDesc('');
      load();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      if (err instanceof ApiError && err.status === 409) setCreateError(t('albums.exists'));
      else if (err instanceof ApiError && err.status === 400) {
        const body = err.body as { error?: string } | null;
        setCreateError(body?.error ?? t('albums.invalidInput'));
      } else setCreateError(t('albums.createError'));
    } finally {
      setCreating(false);
    }
  };

  // Only ever reached for an album the caller owns: the card renders no delete
  // control at all for a shared one, and the backend refuses it regardless.
  const handleDelete = async (card: AlbumCardModel) => {
    if (card.ownerKind !== 'self') return;
    if (!window.confirm(t('albums.confirmDelete', { name: card.name }))) return;
    try {
      await deleteAlbum(card.id);
      load();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
    }
  };

  async function respond(invitation: AlbumInvitation, accept: boolean) {
    setBusyInvitation(invitation.membershipId);
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
      setBusyInvitation(null);
    }
  }

  const cards = status.kind === 'ready' ? status.cards : [];
  const totals = useMemo(() => countByOwnerKind(cards), [cards]);
  const visible = useMemo(
    () => selectAlbumCards({ cards, scope, query, sort: sortKey }),
    [cards, scope, query, sortKey],
  );

  const scopeLabel = (value: AlbumCollectionScope): string =>
    value === 'mine' ? t('albums.scopeMine')
      : value === 'shared' ? t('albums.scopeShared') : t('albums.scopeAll');
  const scopeCount = (value: AlbumCollectionScope): number =>
    value === 'mine' ? totals.mine : value === 'shared' ? totals.shared : totals.all;

  return (
    <div className="page-container albums-page">
      <div className="albums-page-head">
        <h2>{t('albums.heading')}</h2>
        <div className="albums-controls">
          <input
            type="search"
            className="albums-search"
            placeholder={t('albums.searchName')}
            aria-label={t('albums.searchName')}
            data-testid="albums-search"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
          <label className="albums-sort">
            <span className="visually-hidden">{t('albums.sortLabel')}</span>
            <select data-testid="albums-sort" value={sortKey} onChange={(e) => setSortKey(e.target.value as AlbumSortKey)}>
              <option value="recent">{t('albums.sortRecent')}</option>
              <option value="name">{t('albums.sortName')}</option>
              <option value="count">{t('albums.sortCount')}</option>
            </select>
          </label>
        </div>
      </div>

      {/* Which collection, as navigation rather than a hidden filter. */}
      <div className="albums-scope-tabs" role="tablist" aria-label={t('albums.scopeAria')} data-testid="albums-scope-tabs">
        {ALBUM_COLLECTION_SCOPES.map((value) => (
          <button
            key={value}
            type="button"
            role="tab"
            aria-selected={value === scope}
            className={`albums-scope-tab${value === scope ? ' is-active' : ''}`}
            data-testid={`albums-scope-${value}`}
            onClick={() => setScope(value)}
          >
            {scopeLabel(value)}
            <span className="albums-scope-count">{formatNumber(scopeCount(value))}</span>
          </button>
        ))}
      </div>

      <section className="create-form" aria-label={t('albums.createFormLabel')}>
        <input
          type="text"
          placeholder={t('albums.namePlaceholder')}
          value={newName}
          onChange={(e) => setNewName(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter') void handleCreate(); }}
          aria-label={t('albums.newNameAria')}
        />
        <input
          type="text"
          placeholder={t('albums.descPlaceholder')}
          value={newDesc}
          onChange={(e) => setNewDesc(e.target.value)}
          aria-label={t('albums.newDescAria')}
        />
        <button type="button" onClick={() => void handleCreate()} disabled={creating}>
          {creating ? t('albums.creating') : t('albums.create')}
        </button>
        {createError && <p className="inline-error" role="alert">{createError}</p>}
      </section>

      {status.kind === 'loading' && (
        <ul className="album-grid" data-testid="albums-skeleton" aria-hidden="true">
          {[0, 1, 2, 3].map((i) => <li key={i} className="album-card album-card-skeleton" />)}
        </ul>
      )}
      {status.kind === 'error' && <p className="page-error" role="alert">{status.message}</p>}

      {status.kind === 'ready' && (
        <>
          {/* An invitation is a DECISION, not an album — so it sits above the
              grid in its own compact section and never among things that open. */}
          {status.invitations.length > 0 && (
            <section className="albums-invitations" aria-label={t('sharedAlbums.invitationsHeading')}>
              <h3>
                {t('sharedAlbums.invitationsHeading')}
                {' '}
                <span className="albums-scope-count">{formatNumber(status.invitations.length)}</span>
              </h3>
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
                        disabled={busyInvitation === invitation.membershipId}
                        onClick={() => void respond(invitation, true)}
                      >
                        {t('sharedAlbums.accept')}
                      </button>
                      <button
                        type="button"
                        className="row-action"
                        data-testid="invitation-decline"
                        disabled={busyInvitation === invitation.membershipId}
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

          {/* A received COPY is its own decision too: accepting an invitation
              gives you a view of somebody else's album, accepting a copy gives
              you an album of your own that they can never revoke. Once accepted
              it is an ordinary owned album and appears in the grid below. */}
          <ReceivedCopiesPanel />

          {visible.length === 0 ? (
            <p className="empty-state" data-testid="albums-empty">
              {query.trim().length > 0
                ? t('albums.noMatch')
                : scope === 'shared' ? t('sharedAlbums.empty') : t('albums.empty')}
            </p>
          ) : (
            <ul className="album-grid" data-testid="album-list">
              {visible.map((card) => (
                <AlbumCard key={card.key} card={card} onDelete={handleDelete} />
              ))}
            </ul>
          )}
        </>
      )}
    </div>
  );
}
