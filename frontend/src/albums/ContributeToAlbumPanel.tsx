import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  contributeToSharedAlbum,
  listImages,
  type ImageItem,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';

// SHARE-ALBUM-02: a Contributor picking media from THEIR OWN library to link
// into a shared album.
//
// The picker reads the caller's own media endpoint, so it can only ever show
// files they own — there is no code path here that could list somebody else's
// library, and the server re-checks ownership on every add regardless.
//
// The copy states the contract plainly, because the mental model is the whole
// point: the file is LINKED, not copied or moved, and it can be withdrawn.

interface Props {
  albumId: string;
  // File ids already in the album, so items cannot be offered twice.
  presentFileIds: ReadonlySet<string>;
  onContributed(): void;
  onClose(): void;
  returnFocusRef?: React.RefObject<HTMLButtonElement | null>;
}

type Status =
  | { kind: 'loading' }
  | { kind: 'ready'; items: ImageItem[] }
  | { kind: 'error'; message: string };

export function ContributeToAlbumPanel({
  albumId, presentFileIds, onContributed, onClose, returnFocusRef,
}: Props) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const [status, setStatus] = useState<Status>({ kind: 'loading' });
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const abortRef = useRef<AbortController | null>(null);
  const dialogRef = useRef<HTMLDivElement>(null);

  const load = useCallback(() => {
    abortRef.current?.abort();
    const ctrl = new AbortController();
    abortRef.current = ctrl;
    setStatus({ kind: 'loading' });
    listImages({ limit: 100 }, ctrl.signal)
      .then((page) => setStatus({ kind: 'ready', items: page.items }))
      .catch((err) => {
        if ((err as Error).name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        setStatus({ kind: 'error', message: t('sharedAlbum.addPickerError') });
      });
  }, [invalidateAuth, t]);

  useEffect(() => {
    load();
    return () => abortRef.current?.abort();
  }, [load]);

  useEffect(() => {
    dialogRef.current?.querySelector<HTMLElement>('button')?.focus();
    return () => returnFocusRef?.current?.focus();
  }, [returnFocusRef]);

  function toggle(id: string) {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  async function submit() {
    if (selected.size === 0 || submitting) return;
    setSubmitting(true);
    setError(null);

    // Sequential and independent: one ineligible file (withdrawn from the
    // library, vaulted, or already added by another device between opening the
    // picker and submitting) must not discard the rest of the batch.
    let alreadyPresent = 0;
    let failed = 0;
    let roleLost = false;
    for (const fileItemId of selected) {
      try {
        await contributeToSharedAlbum(albumId, fileItemId);
      } catch (err) {
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        if (err instanceof ApiError && err.status === 409) { alreadyPresent += 1; continue; }
        // 403 means the role changed under us — a demotion to Viewer mid-session.
        if (err instanceof ApiError && err.status === 403) { roleLost = true; break; }
        failed += 1;
      }
    }

    setSubmitting(false);
    onContributed();

    if (roleLost) {
      // The server is authoritative and has just said no. Close rather than
      // leave an "Add" dialog the caller can no longer use.
      setError(t('sharedAlbum.addNotAllowed'));
      onClose();
      return;
    }
    if (failed > 0) { setError(t('sharedAlbum.addError')); load(); setSelected(new Set()); return; }
    if (alreadyPresent > 0) { setError(t('sharedAlbum.addAlreadyPresent')); }
    onClose();
  }

  // Items already linked are not offered again — the server would 409 anyway,
  // and offering them invites a confusing partial result.
  const offerable = status.kind === 'ready'
    ? status.items.filter((i) => !presentFileIds.has(i.id))
    : [];

  return (
    <div
      className="ws-sheet-backdrop"
      data-testid="contribute-backdrop"
      onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}
    >
      <div
        ref={dialogRef}
        className="ws-sheet contribute-panel"
        role="dialog"
        aria-modal="true"
        aria-label={t('sharedAlbum.addHeading')}
        data-testid="contribute-panel"
        onKeyDown={(e) => { if (e.key === 'Escape') { e.stopPropagation(); onClose(); } }}
      >
        <header className="ws-sheet-head">
          <h2 className="ws-sheet-title">{t('sharedAlbum.addHeading')}</h2>
          <button
            type="button"
            className="ws-icon-button"
            aria-label={t('common.close')}
            data-testid="contribute-close"
            onClick={onClose}
          >
            ✕
          </button>
        </header>

        <div className="ws-sheet-body">
          {/* States the contract before anything is picked. */}
          <p className="muted">{t('sharedAlbum.addIntro')}</p>

          {status.kind === 'loading' && <p>{t('common.loading')}</p>}
          {status.kind === 'error' && <p className="inline-error" role="alert">{status.message}</p>}
          {error && <p className="inline-error" role="alert">{error}</p>}

          {status.kind === 'ready' && offerable.length === 0 && (
            <p className="empty-state" data-testid="contribute-empty">
              {t('sharedAlbum.addPickerEmpty')}
            </p>
          )}

          {status.kind === 'ready' && offerable.length > 0 && (
            <ul className="contribute-grid" data-testid="contribute-grid">
              {offerable.map((item) => {
                const isSelected = selected.has(item.id);
                return (
                  <li key={item.id}>
                    <button
                      type="button"
                      className={`contribute-tile${isSelected ? ' is-selected' : ''}`}
                      data-testid="contribute-tile"
                      aria-pressed={isSelected}
                      aria-label={item.displayName}
                      onClick={() => toggle(item.id)}
                    >
                      <img src={`/api/files/${item.id}/thumbnail?size=small`} alt="" loading="lazy" />
                      {isSelected && <span className="contribute-tick" aria-hidden="true">✓</span>}
                    </button>
                  </li>
                );
              })}
            </ul>
          )}
        </div>

        <footer className="ws-sheet-foot">
          <div className="ws-sheet-foot-right">
            <button type="button" className="row-action" onClick={onClose}>
              {t('common.cancel')}
            </button>
            <button
              type="button"
              className="row-action-primary"
              data-testid="contribute-submit"
              disabled={selected.size === 0 || submitting}
              onClick={() => void submit()}
            >
              {submitting
                ? t('sharedAlbum.adding')
                : selected.size === 1
                  ? t('sharedAlbum.addSelectedOne')
                  : t('sharedAlbum.addSelected', { count: selected.size })}
            </button>
          </div>
        </footer>
      </div>
    </div>
  );
}
