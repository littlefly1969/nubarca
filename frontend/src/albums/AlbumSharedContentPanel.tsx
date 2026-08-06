import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  listAlbumContent,
  removeSharedAlbumItem,
  reorderSharedAlbum,
  setSharedAlbumCover,
  type AlbumContentItem,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';

// SHARE-ALBUM-02/03: the CURATION surface for an album's live content — the
// owner's items plus every collaborator contribution, in the order members see
// them. Reachable by the Owner and by an Editor, through the same grant the
// mutations use.
//
// WHY THIS IS A SEPARATE VIEW, NOT THE ALBUM WORKSPACE:
// contributions are media the caller does NOT own. Merging them into the
// workspace would mean widening the owner's core library query, which backs
// their gallery, their folders and /api/media — and every affordance there
// (delete, move, metadata, exclude, Private Vault) assumes the caller owns the
// file. This surface reads one dedicated endpoint, so a collaborator's media
// can never acquire an owner-only action by inheriting one from a shared
// component.
//
// The only mutations offered are curation: reorder, choose a cover, and
// "Remove from album". "Delete" is deliberately absent for every row — for a
// contribution the caller has no right to delete, and for the owner's own item
// removing it from an album is curation, not deletion.
//
// CONCURRENCY: every mutation echoes the version last read. A 409 reloads and
// explains; it never retries, because a silent retry of a reorder or a removal
// would apply an intent formed against a state that no longer exists.

interface Props {
  albumId: string;
  onClose(): void;
  returnFocusRef?: React.RefObject<HTMLButtonElement | null>;
}

type Status =
  | { kind: 'loading' }
  | { kind: 'ready'; version: number; canEdit: boolean; items: AlbumContentItem[] }
  | { kind: 'gone' }
  | { kind: 'error'; message: string };

export function AlbumSharedContentPanel({ albumId, onClose, returnFocusRef }: Props) {
  const { t, formatDate } = useI18n();
  const { invalidateAuth } = useAuth();
  const [status, setStatus] = useState<Status>({ kind: 'loading' });
  const [busy, setBusy] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  // Announced politely after a move so a screen-reader user hears the result
  // without the focus being stolen.
  const [liveMessage, setLiveMessage] = useState('');
  const abortRef = useRef<AbortController | null>(null);
  const dialogRef = useRef<HTMLDivElement>(null);
  // The row that was just moved keeps focus across the re-render, so repeated
  // keyboard moves do not send the user back to the top of the list.
  const focusAfterRenderRef = useRef<string | null>(null);

  const load = useCallback((notice?: string) => {
    abortRef.current?.abort();
    const ctrl = new AbortController();
    abortRef.current = ctrl;
    setStatus({ kind: 'loading' });
    listAlbumContent(albumId, ctrl.signal)
      .then((page) => {
        setStatus({
          kind: 'ready', version: page.version, canEdit: page.canEdit, items: page.items,
        });
        if (notice) setActionError(notice);
      })
      .catch((err) => {
        if ((err as Error).name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        if (err instanceof ApiError && err.status === 404) { setStatus({ kind: 'gone' }); return; }
        setStatus({ kind: 'error', message: t('albumContent.loadError') });
      });
  }, [albumId, invalidateAuth, t]);

  useEffect(() => {
    load();
    return () => abortRef.current?.abort();
  }, [load]);

  useEffect(() => {
    dialogRef.current?.querySelector<HTMLElement>('button')?.focus();
    return () => returnFocusRef?.current?.focus();
  }, [returnFocusRef]);

  useEffect(() => {
    if (status.kind !== 'ready' || !focusAfterRenderRef.current) return;
    const id = focusAfterRenderRef.current;
    focusAfterRenderRef.current = null;
    dialogRef.current
      ?.querySelector<HTMLElement>(`[data-item-id="${id}"] [data-testid="album-content-move-up"]`)
      ?.focus();
  }, [status]);

  // Central handling for every editorial mutation, so the conflict rule cannot
  // be forgotten at one call site: on 409 reload and explain, never retry.
  async function mutate(
    key: string,
    run: (version: number) => Promise<unknown>,
    announce?: string,
  ) {
    if (status.kind !== 'ready') return;
    setBusy(key);
    setActionError(null);
    try {
      await run(status.version);
      if (announce) setLiveMessage(announce);
      load();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      if (err instanceof ApiError && err.status === 409) {
        // Somebody else changed the album first. Reload to the current truth and
        // say so — the user decides whether to redo their change.
        load(t('albumContent.conflict'));
        return;
      }
      if (err instanceof ApiError && err.status === 403) {
        // The caller's role changed under them. The controls they are looking
        // at no longer exist; close rather than leave them there.
        setActionError(t('albumContent.noLongerAllowed'));
        onClose();
        return;
      }
      if (err instanceof ApiError && err.status === 404) { load(); return; }
      setActionError(t('albumContent.removeError'));
    } finally {
      setBusy(null);
    }
  }

  async function remove(item: AlbumContentItem) {
    const question = item.origin === 'contribution'
      // Names the contributor with the same disambiguated label the member list
      // uses, so a curator with two identically-named collaborators is never
      // asked to confirm an ambiguous removal.
      ? t('albumContent.confirmRemoveContribution', { name: contributorLabel(item) })
      : t('albumContent.confirmRemoveOwn');
    const withCover = item.isCover
      ? `${question} ${t('albumContent.confirmRemoveCover')}`
      : question;
    if (!window.confirm(withCover)) return;
    await mutate(item.albumItemId,
      (v) => removeSharedAlbumItem(albumId, item.albumItemId, v));
  }

  async function setCover(item: AlbumContentItem | null) {
    await mutate(item?.albumItemId ?? 'cover',
      (v) => setSharedAlbumCover(albumId, v, item?.fileItemId ?? null));
  }

  // Reorder is expressed as the COMPLETE ordered list of AlbumItem ids, which is
  // what the server requires — a partial list is refused rather than guessed at.
  async function move(index: number, to: number) {
    if (status.kind !== 'ready') return;
    const ids = status.items.map((i) => i.albumItemId);
    const target = Math.max(0, Math.min(ids.length - 1, to));
    if (target === index) return;
    const [moved] = ids.splice(index, 1);
    ids.splice(target, 0, moved);

    focusAfterRenderRef.current = moved;
    await mutate(moved, (v) => reorderSharedAlbum(albumId, v, ids),
      t('albumContent.moved', { position: target + 1, total: ids.length }));
  }

  function contributorLabel(item: AlbumContentItem): string {
    if (!item.contributorDisplayName) return '';
    return item.contributorMaskedEmail
      ? `${item.contributorDisplayName} (${item.contributorMaskedEmail})`
      : item.contributorDisplayName;
  }

  const items = status.kind === 'ready' ? status.items : [];
  const canEdit = status.kind === 'ready' && status.canEdit;

  return (
    <div
      className="ws-sheet-backdrop"
      data-testid="album-content-backdrop"
      onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}
    >
      <div
        ref={dialogRef}
        className="ws-sheet album-content-panel"
        role="dialog"
        aria-modal="true"
        aria-label={t('albumContent.heading')}
        data-testid="album-content-panel"
        onKeyDown={(e) => { if (e.key === 'Escape') { e.stopPropagation(); onClose(); } }}
      >
        <header className="ws-sheet-head">
          <h2 className="ws-sheet-title">{t('albumContent.heading')}</h2>
          <button
            type="button"
            className="ws-icon-button"
            aria-label={t('common.close')}
            data-testid="album-content-close"
            onClick={onClose}
          >
            ✕
          </button>
        </header>

        <div className="ws-sheet-body">
          <p className="muted">{t('albumContent.intro')}</p>
          {canEdit && <p className="muted">{t('albumContent.reorderHelp')}</p>}

          {/* Move results are announced, not focused. */}
          <p className="visually-hidden" role="status" aria-live="polite"
            data-testid="album-content-live">{liveMessage}</p>

          {status.kind === 'loading' && <p>{t('common.loading')}</p>}
          {status.kind === 'gone' && (
            <p className="empty-state" role="status" data-testid="album-content-gone">
              {t('albumContent.loadError')}
            </p>
          )}
          {status.kind === 'error' && (
            <p className="inline-error" role="alert">{status.message}</p>
          )}
          {actionError && (
            <p className="inline-error" role="alert" data-testid="album-content-notice">
              {actionError}
            </p>
          )}

          {status.kind === 'ready' && items.length === 0 && (
            <p className="empty-state" data-testid="album-content-empty">
              {t('albumContent.empty')}
            </p>
          )}

          {status.kind === 'ready' && items.length > 0 && (
            <ul className="album-content-list" data-testid="album-content-list">
              {items.map((item, index) => (
                <li
                  key={item.albumItemId}
                  className="album-content-row"
                  data-testid="album-content-row"
                  data-origin={item.origin}
                  data-item-id={item.albumItemId}
                >
                  <div className="album-content-thumb">
                    {item.sourceState === 'available' ? (
                      <img src={item.thumbnailUrl} alt="" loading="lazy" />
                    ) : (
                      <span className="album-content-thumb-missing" aria-hidden="true">⚠</span>
                    )}
                  </div>

                  <div className="album-content-meta">
                    {/* Provenance is visible but discreet: the owner's own
                        items carry no redundant badge on every card. */}
                    {item.origin === 'contribution' ? (
                      <p className="album-content-provenance" data-testid="album-content-provenance">
                        {t('albumContent.addedBy', { name: item.contributorDisplayName ?? '' })}
                        {item.contributorMaskedEmail && (
                          <span className="album-share-member-hint">
                            {' '}{item.contributorMaskedEmail}
                          </span>
                        )}
                      </p>
                    ) : (
                      <p className="album-content-provenance muted">{t('albumContent.ownerItem')}</p>
                    )}
                    <p className="muted album-content-when">
                      {t('albumContent.position', { position: index + 1, total: items.length })}
                      {' · '}{formatDate(item.addedAt)}
                    </p>
                    {item.isCover && (
                      <p className="album-content-cover-badge" data-testid="album-content-is-cover">
                        {t('albumContent.isCover')}
                      </p>
                    )}
                    {item.sourceState === 'unavailable' && (
                      <p className="album-content-unavailable" data-testid="album-content-unavailable">
                        {t('albumContent.unavailable')}
                        <span className="muted"> — {t('albumContent.unavailableHelp')}</span>
                      </p>
                    )}
                  </div>

                  {/* Editorial controls exist ONLY for a curator. For a Viewer
                      or Contributor they are absent entirely — not disabled,
                      which would advertise a capability they do not have. */}
                  {canEdit && (
                    <div className="album-content-actions">
                      {/* Keyboard-, touch- and screen-reader-operable reorder.
                          Explicit buttons rather than drag-only: a pointer
                          gesture is not reachable by any of those three. */}
                      <div className="album-content-move" role="group"
                        aria-label={t('albumContent.moveGroup', { position: index + 1 })}>
                        <button
                          type="button" className="ws-icon-button"
                          data-testid="album-content-move-up"
                          disabled={index === 0 || busy !== null}
                          aria-label={t('albumContent.moveUp', { position: index + 1 })}
                          onClick={() => void move(index, index - 1)}
                        >↑</button>
                        <button
                          type="button" className="ws-icon-button"
                          data-testid="album-content-move-down"
                          disabled={index === items.length - 1 || busy !== null}
                          aria-label={t('albumContent.moveDown', { position: index + 1 })}
                          onClick={() => void move(index, index + 1)}
                        >↓</button>
                        <button
                          type="button" className="ws-icon-button"
                          data-testid="album-content-move-first"
                          disabled={index === 0 || busy !== null}
                          aria-label={t('albumContent.moveFirst', { position: index + 1 })}
                          onClick={() => void move(index, 0)}
                        >⇈</button>
                        <button
                          type="button" className="ws-icon-button"
                          data-testid="album-content-move-last"
                          disabled={index === items.length - 1 || busy !== null}
                          aria-label={t('albumContent.moveLast', { position: index + 1 })}
                          onClick={() => void move(index, items.length - 1)}
                        >⇊</button>
                      </div>

                      {/* Only a currently-servable member may become the cover;
                          the server refuses anything else, and offering it
                          would be a control that always fails. */}
                      {item.sourceState === 'available' && !item.isCover && (
                        <button
                          type="button" className="row-action"
                          data-testid="album-content-set-cover"
                          disabled={busy !== null}
                          onClick={() => void setCover(item)}
                        >
                          {t('albumContent.useAsCover')}
                        </button>
                      )}
                      {item.isCover && (
                        <button
                          type="button" className="row-action"
                          data-testid="album-content-clear-cover"
                          disabled={busy !== null}
                          onClick={() => void setCover(null)}
                        >
                          {t('albumContent.clearCover')}
                        </button>
                      )}

                      <button
                        type="button"
                        className="row-action"
                        data-testid="album-content-remove"
                        disabled={busy !== null}
                        aria-label={t('albumContent.removeAria', {
                          index: index + 1, total: items.length,
                        })}
                        onClick={() => void remove(item)}
                      >
                        {t('albumContent.remove')}
                      </button>
                    </div>
                  )}
                </li>
              ))}
            </ul>
          )}
        </div>
      </div>
    </div>
  );
}
