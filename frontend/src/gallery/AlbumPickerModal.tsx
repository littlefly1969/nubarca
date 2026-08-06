import { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import {
  ApiError,
  bulkAddAlbumItems,
  createAlbum,
  listAlbums,
  type AlbumSummary,
  type BulkAlbumItemsResult,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';

// Accessible album picker for the bulk "Add to album" action. Choose an
// existing album or create a new one, then add the selected files. Duplicate
// additions are handled quietly by the backend (skipped, not an error).
export interface AlbumPickerModalProps {
  fileItemIds: string[];
  onClose(): void;
  onAdded?(result: BulkAlbumItemsResult, albumName: string): void;
}

type LoadState =
  | { kind: 'loading' }
  | { kind: 'ready'; albums: AlbumSummary[] }
  | { kind: 'error' };

export function AlbumPickerModal({ fileItemIds, onClose, onAdded }: AlbumPickerModalProps) {
  const { t, tn } = useI18n();
  const { invalidateAuth } = useAuth();
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const [selectedAlbumId, setSelectedAlbumId] = useState('');
  const [newName, setNewName] = useState('');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const dialogRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const ctrl = new AbortController();
    listAlbums(ctrl.signal)
      .then((albums) => {
        setState({ kind: 'ready', albums });
        if (albums.length > 0) setSelectedAlbumId(albums[0].id);
      })
      .catch((err) => {
        if ((err as Error).name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        setState({ kind: 'error' });
      });
    return () => ctrl.abort();
  }, [invalidateAuth]);

  // Escape closes; focus the dialog on mount for keyboard users.
  //
  // The listener runs in the CAPTURE phase and stops the event dead. This modal
  // can be opened on top of another Escape-closable surface — the media viewer
  // opens it from its details drawer — and both listen on `window`. Without
  // consuming the event, a single Escape dismissed the picker AND the viewer
  // behind it, so a user adding a photo to an album lost their place entirely.
  // As the topmost modal, this one owns Escape.
  useEffect(() => {
    dialogRef.current?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return;
      e.stopImmediatePropagation();
      onClose();
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [onClose]);

  async function addTo(albumId: string, albumName: string) {
    setBusy(true);
    setError(null);
    setMessage(null);
    try {
      const result = await bulkAddAlbumItems(albumId, fileItemIds);
      const parts = [t('albumPicker.successAdded', { added: result.succeeded, album: albumName })];
      if (result.skipped > 0) {
        parts.push(t('albumPicker.successSkipped', { skipped: result.skipped }));
      }
      setMessage(parts.join(' '));
      onAdded?.(result, albumName);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setError(t('albumPicker.error'));
    } finally {
      setBusy(false);
    }
  }

  async function onAddExisting() {
    if (state.kind !== 'ready' || !selectedAlbumId) return;
    const album = state.albums.find((a) => a.id === selectedAlbumId);
    await addTo(selectedAlbumId, album?.name ?? '');
  }

  async function onCreateAndAdd() {
    const name = newName.trim();
    if (name.length === 0) return;
    setBusy(true);
    setError(null);
    setMessage(null);
    try {
      const album = await createAlbum(name);
      setState((s) => (s.kind === 'ready'
        ? { kind: 'ready', albums: [...s.albums, { ...toSummary(album) }] }
        : s));
      setSelectedAlbumId(album.id);
      setNewName('');
      await addTo(album.id, album.name);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      if (err instanceof ApiError && err.status === 409) {
        setError(t('albumPicker.duplicateName'));
      } else {
        setError(t('albumPicker.error'));
      }
    } finally {
      setBusy(false);
    }
  }

  return createPortal(
    <div className="assign-modal-backdrop" onClick={onClose}>
      <div
        className="assign-modal album-picker-modal"
        role="dialog"
        aria-modal="true"
        aria-label={t('albumPicker.title')}
        ref={dialogRef}
        tabIndex={-1}
        onClick={(e) => e.stopPropagation()}
      >
        <h3 className="assign-modal-title">{t('albumPicker.title')}</h3>
        <p className="assign-modal-current muted">{tn(fileItemIds.length, 'gallerySel.itemsSelected')}</p>

        {state.kind === 'loading' && <p className="muted" role="status">{t('common.loading')}</p>}
        {state.kind === 'error' && <p className="inline-error" role="alert">{t('albumPicker.loadError')}</p>}

        {state.kind === 'ready' && (
          <>
            {state.albums.length === 0 ? (
              <p className="muted">{t('albumPicker.noAlbums')}</p>
            ) : (
              <label className="assign-modal-field">
                <span>{t('albumPicker.chooseAlbum')}</span>
                <select
                  value={selectedAlbumId}
                  disabled={busy}
                  onChange={(e) => setSelectedAlbumId(e.target.value)}
                  aria-label={t('albumPicker.chooseAlbum')}
                  data-testid="album-picker-select"
                >
                  {state.albums.map((a) => (
                    <option key={a.id} value={a.id}>{a.name}</option>
                  ))}
                </select>
                <button
                  type="button"
                  className="row-action-primary"
                  disabled={busy || !selectedAlbumId}
                  onClick={() => void onAddExisting()}
                  data-testid="album-picker-add"
                >
                  {busy ? t('albumPicker.adding') : t('gallerySel.addToAlbum')}
                </button>
              </label>
            )}

            <div className="assign-modal-create album-picker-create">
              <span>{t('albumPicker.createNew')}</span>
              <input
                type="text"
                placeholder={t('albumPicker.newAlbumName')}
                aria-label={t('albumPicker.newAlbumName')}
                value={newName}
                maxLength={255}
                disabled={busy}
                onChange={(e) => setNewName(e.target.value)}
                data-testid="album-picker-new-name"
              />
              <button
                type="button"
                className="row-action"
                disabled={busy || newName.trim().length === 0}
                onClick={() => void onCreateAndAdd()}
                data-testid="album-picker-create"
              >
                {t('albumPicker.createAndAdd')}
              </button>
            </div>
          </>
        )}

        {message && <p className="muted" role="status" data-testid="album-picker-message">{message}</p>}
        {error && <p className="inline-error" role="alert">{error}</p>}

        <div className="assign-modal-actions">
          <button type="button" className="assign-modal-cancel" onClick={onClose} disabled={busy}>
            {t('common.close')}
          </button>
        </div>
      </div>
    </div>,
    document.body,
  );
}

// Project the AlbumDetail returned by createAlbum into the AlbumSummary shape
// used by the picker list (itemCount unknown yet → 0).
function toSummary(album: { id: string; name: string; description: string | null; showOnTv: boolean; createdAt: string; updatedAt: string }): AlbumSummary {
  return {
    id: album.id,
    name: album.name,
    description: album.description,
    itemCount: 0,
    showOnTv: album.showOnTv,
    createdAt: album.createdAt,
    updatedAt: album.updatedAt,
    // A freshly created album has no members yet.
    photoCount: 0,
    videoCount: 0,
    excludedCount: 0,
    coverItems: [],
  };
}
