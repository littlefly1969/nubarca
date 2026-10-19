import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import {
  ApiError,
  deleteMediaList,
  duplicateMediaList,
  getMediaList,
  renameMediaList,
  type MediaListSummary,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';
import { Modal } from '../components/Overlay';
import { MediaWorkspace } from '../media/workspace/MediaWorkspace';
import { useLibraryPhotoDestinations } from '../media/workspace/useLibraryPhotoDestinations';
import {
  filtersToUrlParams,
  identityFromUrlParams,
  type MediaWorkspaceIdentity,
  type MediaWorkspaceSource,
} from '../media/workspace/mediaWorkspaceQuery';

// One owner list, browsed with the same workspace as the library: the same
// grid, viewer, filters and selection, and the same operations (album, list,
// Trash, Trascurate, Personal, print). "Remove from this list" replaces
// "Remove from this album", which a list never offers.
export function ListDetailPage() {
  const { listId = '' } = useParams();
  const { t, tn } = useI18n();
  const { state: auth, invalidateAuth } = useAuth();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const [list, setList] = useState<MediaListSummary | null>(null);
  const [failed, setFailed] = useState(false);
  const [renaming, setRenaming] = useState(false);
  const [draft, setDraft] = useState('');
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const photoDestinations = useLibraryPhotoDestinations();

  const source = useRef<MediaWorkspaceSource>({ kind: 'list', listId });
  if (source.current.kind !== 'list' || source.current.listId !== listId) source.current = { kind: 'list', listId };
  const initialParamsRef = useRef(searchParams);
  const [identity, setIdentity] = useState<MediaWorkspaceIdentity>(
    () => identityFromUrlParams(source.current, initialParamsRef.current),
  );

  const onIdentityChange = useCallback((next: MediaWorkspaceIdentity) => {
    setIdentity(next);
    setSearchParams(filtersToUrlParams(next), { replace: true });
  }, [setSearchParams]);

  const fail = useCallback((err: unknown, fallback: string) => {
    if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
    if (err instanceof ApiError && err.status === 404) { void navigate('/lists'); return; }
    setNotice(fallback);
  }, [invalidateAuth, navigate]);

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      setList(await getMediaList(listId, signal));
    } catch (err) {
      if ((err as Error)?.name === 'AbortError') return;
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      if (err instanceof ApiError && err.status === 404) { void navigate('/lists'); return; }
      setFailed(true);
    }
  }, [listId, invalidateAuth, navigate]);

  useEffect(() => {
    const ctrl = new AbortController();
    void load(ctrl.signal);
    return () => ctrl.abort();
  }, [load]);

  async function rename(e: FormEvent) {
    e.preventDefault();
    if (!list || busy || draft.trim().length === 0) return;
    setBusy(true);
    try {
      setList(await renameMediaList(list.id, draft.trim(), list.version));
      setRenaming(false);
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        // Renamed (or filled) on another device meanwhile: show what is there now.
        setNotice(t('mediaLists.renameConflict'));
        setRenaming(false);
        void load();
      } else {
        fail(err, t('mediaLists.renameError'));
      }
    } finally {
      setBusy(false);
    }
  }

  async function duplicate() {
    if (!list || busy) return;
    setBusy(true);
    try {
      const copy = await duplicateMediaList(list.id);
      void navigate(`/lists/${copy.id}`);
    } catch (err) {
      fail(err, t('mediaLists.duplicateError'));
    } finally {
      setBusy(false);
    }
  }

  async function remove() {
    if (!list || busy) return;
    setBusy(true);
    try {
      await deleteMediaList(list.id);
      void navigate('/lists');
    } catch (err) {
      fail(err, t('mediaLists.deleteError'));
      setBusy(false);
    }
  }

  if (auth.status !== 'authed') return null;
  if (failed) return <p className="inline-error" role="alert">{t('mediaLists.loadError')}</p>;

  return (
    <section className="ws-page-outer" data-testid="media-list-page">
      <header className="ws-page-header media-list-header">
        <Link to="/lists" className="muted" data-testid="media-list-back">{t('mediaLists.back')}</Link>
        {renaming && list ? (
          <form className="media-lists-create" onSubmit={(e) => void rename(e)}>
            <input
              type="text"
              value={draft}
              maxLength={120}
              aria-label={t('mediaLists.rename')}
              disabled={busy}
              onChange={(e) => setDraft(e.target.value)}
              data-testid="media-list-rename-input"
              autoFocus
            />
            <button type="submit" className="row-action-primary" disabled={busy || draft.trim().length === 0}
              data-testid="media-list-rename-save">
              {t('mediaLists.renameSave')}
            </button>
            <button type="button" className="row-action" disabled={busy} onClick={() => setRenaming(false)}>
              {t('common.cancel')}
            </button>
          </form>
        ) : (
          <h1 data-testid="media-list-title">{list?.name ?? ''}</h1>
        )}
        {list && <p className="muted" data-testid="media-list-count">{tn(list.count, 'mediaLists.count')}</p>}
        {list && !renaming && (
          <div className="media-list-actions">
            <button type="button" className="row-action" disabled={busy} data-testid="media-list-rename"
              onClick={() => { setDraft(list.name ?? ''); setRenaming(true); }}>
              {t('mediaLists.rename')}
            </button>
            <button type="button" className="row-action" disabled={busy} data-testid="media-list-duplicate"
              onClick={() => void duplicate()}>
              {t('mediaLists.duplicate')}
            </button>
            <button type="button" className="row-action" disabled={busy} data-testid="media-list-delete"
              onClick={() => setConfirmDelete(true)}>
              {t('mediaLists.delete')}
            </button>
          </div>
        )}
        {notice && <p className="muted" role="status" data-testid="media-list-notice">{notice}</p>}
      </header>

      <MediaWorkspace
        source={source.current}
        identity={identity}
        onIdentityChange={onIdentityChange}
        searchPlaceholder={t('mediaLists.search')}
        photoDestinations={photoDestinations}
        // Removing from the list changes its count; re-read the header.
        onAlbumMembershipChanged={() => { void load(); }}
      />

      {confirmDelete && list && (
        <Modal
          title={t('mediaLists.deleteTitle', { name: list.name ?? '' })}
          onClose={() => setConfirmDelete(false)}
          dismissable={!busy}
          testId="media-list-delete-dialog"
          footer={(
            <>
              <button type="button" className="row-action" disabled={busy} onClick={() => setConfirmDelete(false)}>
                {t('common.cancel')}
              </button>
              <button type="button" className="row-action-primary" disabled={busy}
                data-testid="media-list-delete-confirm" onClick={() => void remove()}>
                {t('mediaLists.deleteConfirm')}
              </button>
            </>
          )}
        >
          <p>{t('mediaLists.deleteBody')}</p>
        </Modal>
      )}
    </section>
  );
}
