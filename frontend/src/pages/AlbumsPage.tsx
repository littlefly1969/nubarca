import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link } from 'react-router';
import {
  ApiError,
  createAlbum,
  deleteAlbum,
  listAlbums,
  type AlbumCoverItem,
  type AlbumSummary,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';

type Status =
  | { kind: 'loading' }
  | { kind: 'ready'; albums: AlbumSummary[] }
  | { kind: 'error'; message: string };

type SortKey = 'updated' | 'name' | 'count';

// Slice 5: modernized album list — cover mosaics + per-kind counts (from the
// enriched AlbumSummary), name search and sort. Albums stay mixed; no new
// derivatives are generated (the cover reuses existing thumbnails/posters).

function CoverMosaic({ items, name }: { items: AlbumCoverItem[]; name: string }) {
  const { t } = useI18n();
  if (items.length === 0) {
    return <div className="album-cover album-cover-empty" aria-hidden="true">🖼</div>;
  }
  return (
    <div className={`album-cover album-cover-mosaic count-${Math.min(items.length, 4)}`} data-testid="album-cover">
      {items.slice(0, 4).map((c) => (
        <img key={c.fileItemId} src={c.thumbnailUrl} alt={t('albums.coverAlt', { name })} loading="lazy" onError={(e) => { (e.target as HTMLImageElement).style.visibility = 'hidden'; }} />
      ))}
    </div>
  );
}

export function AlbumsPage() {
  const { invalidateAuth } = useAuth();
  const { t, tn, formatDate } = useI18n();
  const [status, setStatus] = useState<Status>({ kind: 'loading' });
  const [query, setQuery] = useState('');
  const [sortKey, setSortKey] = useState<SortKey>('updated');
  const [newName, setNewName] = useState('');
  const [newDesc, setNewDesc] = useState('');
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);
  const abortRef = useRef<AbortController | null>(null);

  const load = useCallback(() => {
    abortRef.current?.abort();
    const ctrl = new AbortController();
    abortRef.current = ctrl;
    setStatus({ kind: 'loading' });
    listAlbums(ctrl.signal)
      .then((albums) => setStatus({ kind: 'ready', albums }))
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

  const handleDelete = async (album: AlbumSummary) => {
    if (!window.confirm(t('albums.confirmDelete', { name: album.name }))) return;
    try {
      await deleteAlbum(album.id);
      load();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
    }
  };

  const visible = useMemo(() => {
    if (status.kind !== 'ready') return [];
    const needle = query.trim().toLowerCase();
    const filtered = needle.length > 0
      ? status.albums.filter((a) => a.name.toLowerCase().includes(needle))
      : status.albums;
    const sorted = [...filtered];
    sorted.sort((a, b) => {
      if (sortKey === 'name') return a.name.localeCompare(b.name);
      if (sortKey === 'count') return (b.photoCount + b.videoCount) - (a.photoCount + a.videoCount);
      return b.updatedAt.localeCompare(a.updatedAt);
    });
    return sorted;
  }, [status, query, sortKey]);

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
            <select data-testid="albums-sort" value={sortKey} onChange={(e) => setSortKey(e.target.value as SortKey)}>
              <option value="updated">{t('albums.sortUpdated')}</option>
              <option value="name">{t('albums.sortName')}</option>
              <option value="count">{t('albums.sortCount')}</option>
            </select>
          </label>
        </div>
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
        visible.length === 0 ? (
          <p className="empty-state" data-testid="albums-empty">
            {query.trim().length > 0 ? t('albums.noMatch') : t('albums.empty')}
          </p>
        ) : (
          <ul className="album-grid" data-testid="album-list">
            {visible.map((album) => {
              const total = album.photoCount + album.videoCount;
              return (
                <li key={album.id} className="album-card" data-testid="album-card">
                  <Link to={`/albums/${album.id}`} className="album-card-link" aria-label={album.name}>
                    <CoverMosaic items={album.coverItems} name={album.name} />
                  </Link>
                  <div className="album-card-body">
                    <div className="album-card-titlerow">
                      <Link to={`/albums/${album.id}`} className="album-card-name">{album.name}</Link>
                      {album.showOnTv && <span className="album-badge album-badge-tv" data-testid="album-tv-badge">{t('albums.tvBadge')}</span>}
                    </div>
                    {album.description && <p className="album-card-desc">{album.description}</p>}
                    <p className="album-card-counts">
                      <span>{tn(total, 'albums.itemsCount')}</span>
                      {album.photoCount > 0 && <span> · {t('albums.photoCount', { count: album.photoCount })}</span>}
                      {album.videoCount > 0 && <span> · {t('albums.videoCount', { count: album.videoCount })}</span>}
                      {album.excludedCount > 0 && <span> · {t('albums.excludedCount', { count: album.excludedCount })}</span>}
                    </p>
                    <p className="album-card-updated muted">{t('albums.updatedAt', { date: formatDate(album.updatedAt) })}</p>
                  </div>
                  <button
                    type="button"
                    className="btn-danger album-card-delete"
                    onClick={() => void handleDelete(album)}
                    aria-label={t('albums.deleteLabel', { name: album.name })}
                    data-testid="album-delete-btn"
                  >
                    {t('common.delete')}
                  </button>
                </li>
              );
            })}
          </ul>
        )
      )}
    </div>
  );
}
