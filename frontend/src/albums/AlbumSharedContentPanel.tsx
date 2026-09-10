import { useCallback, useEffect, useRef, useState } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import {
  ApiError,
  moveSharedAlbumItem,
  removeSharedAlbumItem,
  setSharedAlbumCover,
  type AlbumContentItem,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';
import { useAlbumContent } from './useAlbumContent';

// SHARE-ALBUM-02/03: the CURATION surface for an album's live content — the
// owner's items plus every collaborator contribution, in the order members see
// them. Reachable by the Owner and by an Editor, through the same grant the
// mutations use, and rendered by this ONE component for both.
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
//
// SCALE: opening this costs the same for twenty items or two thousand.
//   * The list is read one page at a time (useAlbumContent), and the next page
//     only as the viewport nears the end of the rows held.
//   * The rows are VIRTUALIZED: only those in the viewport plus a small overscan
//     exist in the DOM, so neither nodes nor thumbnail requests grow with the
//     album. Positions stay absolute — "37 of 520" — because the server sends
//     the album's total.
//   * Each row carries ONE control. Moves, the cover and removal sit behind a
//     per-row Actions disclosure, rendered for the single row that is open.
//   * A move sends the item, its destination and the version — never the
//     album's id sequence — so "to the end" works on a row the list never read.
//   * Thumbnails are the Micro icon the server addresses for this surface.

interface Props {
  albumId: string;
  onClose(): void;
  returnFocusRef?: React.RefObject<HTMLButtonElement | null>;
}

// A row's usual height; the virtualizer measures the real one.
const ROW_ESTIMATE_PX = 84;
const OVERSCAN_ROWS = 6;
// Ask for the next page this many rows before the held rows run out, so a
// steady scroll rarely reaches a row that is not there yet.
const LOAD_AHEAD_ROWS = 10;

type MoveAction = 'up' | 'down' | 'first' | 'last';
type RowAction = MoveAction | 'set-cover' | 'clear-cover' | 'remove' | 'toggle';
type FocusRequest = { albumItemId: string; action: RowAction } | 'notice';

export function AlbumSharedContentPanel({ albumId, onClose, returnFocusRef }: Props) {
  const { t, formatDate } = useI18n();
  const { invalidateAuth } = useAuth();
  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);
  // Announced politely after a move so a screen-reader user hears the result
  // without the focus being stolen.
  const [liveMessage, setLiveMessage] = useState('');
  // At most ONE row shows its actions; every other row has a single control.
  const [openRowId, setOpenRowId] = useState<string | null>(null);
  const dialogRef = useRef<HTMLDivElement>(null);
  const scrollerRef = useRef<HTMLDivElement>(null);
  const noticeRef = useRef<HTMLParagraphElement>(null);
  // Focus to deliver once a render has the element. A moved row keeps focus
  // across the re-render, so repeated keyboard moves stay on the same item.
  const focusRef = useRef<FocusRequest | null>(null);

  const onChangedWhileBrowsing = useCallback(() => {
    setOpenRowId(null);
    setActionError(t('albumContent.changedWhileBrowsing'));
  }, [t]);
  const content = useAlbumContent(albumId, invalidateAuth, onChangedWhileBrowsing);
  const { phase, items, totalCount, canEdit, hasMore, loadingMore, loadMoreFailed, loadMore } = content;

  const virtualizer = useVirtualizer({
    count: items.length,
    getScrollElement: () => scrollerRef.current,
    estimateSize: () => ROW_ESTIMATE_PX,
    overscan: OVERSCAN_ROWS,
    getItemKey: (index) => items[index]?.albumItemId ?? index,
  });
  const virtualRows = virtualizer.getVirtualItems();
  const lastRenderedIndex = virtualRows.length > 0 ? virtualRows[virtualRows.length - 1].index : -1;

  // The next page, as the viewport nears the end of the rows held. Not while
  // an edit is in flight: that page would be read at the version it replaces.
  useEffect(() => {
    if (phase !== 'ready' || !hasMore || loadingMore || loadMoreFailed || busy) return;
    if (lastRenderedIndex >= items.length - 1 - LOAD_AHEAD_ROWS) loadMore();
  }, [phase, hasMore, loadingMore, loadMoreFailed, busy, lastRenderedIndex, items.length, loadMore]);

  // Every read from the top is a new list, so it starts at the top — not at
  // the offset the previous rows had been scrolled to.
  const { reads } = content;
  useEffect(() => {
    virtualizer.scrollToOffset(0);
  }, [reads, virtualizer]);

  useEffect(() => {
    dialogRef.current?.querySelector<HTMLElement>('button')?.focus();
    return () => returnFocusRef?.current?.focus();
  }, [returnFocusRef]);

  // After every render, deliver a pending focus once its target exists. A row
  // far from the viewport is only mounted by the scroll that reveals it.
  useEffect(() => {
    const request = focusRef.current;
    if (request === null) return;
    if (request === 'notice') {
      focusRef.current = null;
      noticeRef.current?.focus();
      return;
    }
    const row = dialogRef.current?.querySelector<HTMLElement>(`[data-item-id="${request.albumItemId}"]`);
    if (!row) {
      if (!items.some((i) => i.albumItemId === request.albumItemId)) focusRef.current = null;
      return;
    }
    focusRef.current = null;
    (row.querySelector<HTMLElement>(`[data-action="${request.action}"]:not(:disabled)`)
      ?? row.querySelector<HTMLElement>('[data-action="toggle"]'))?.focus();
  });

  // Central handling for every editorial mutation, so the conflict rule cannot
  // be forgotten at one call site: on 409 reload and explain, never retry.
  async function mutate<T>(
    run: (version: number) => Promise<T>,
    failureMessage: string,
  ): Promise<{ value: T } | null> {
    if (phase !== 'ready' || busy) return null;
    setBusy(true);
    setActionError(null);
    content.discardPendingPage();
    try {
      return { value: await run(content.version) };
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return null; }
      if (err instanceof ApiError && err.status === 409) {
        // Somebody else changed the album first. Reload to the current truth and
        // say so — the user decides whether to redo their change.
        setOpenRowId(null);
        setActionError(t('albumContent.conflict'));
        focusRef.current = 'notice';
        content.reload();
        return null;
      }
      if (err instanceof ApiError && err.status === 403) {
        // The caller's role changed under them. The controls they are looking
        // at no longer exist; close rather than leave them there.
        setActionError(t('albumContent.noLongerAllowed'));
        onClose();
        return null;
      }
      if (err instanceof ApiError && err.status === 404) {
        setOpenRowId(null);
        content.reload();
        return null;
      }
      setActionError(failureMessage);
      return null;
    } finally {
      setBusy(false);
    }
  }

  function focusSuccessorOf(item: AlbumContentItem, index: number) {
    const rest = items.filter((i) => i.albumItemId !== item.albumItemId);
    const successor = rest[Math.min(index, rest.length - 1)];
    if (successor) {
      focusRef.current = { albumItemId: successor.albumItemId, action: 'toggle' };
    } else {
      dialogRef.current?.querySelector<HTMLElement>('button')?.focus();
    }
  }

  // Up / Down / Start / End as ONE item and ONE destination. The end is the
  // album's end, not the end of the rows held.
  async function move(item: AlbumContentItem, index: number, action: MoveAction) {
    const target = action === 'up' ? index - 1
      : action === 'down' ? index + 1
        : action === 'first' ? 0
          : totalCount - 1;
    if (target < 0 || target >= totalCount || target === index) return;

    const held = items.length;
    const outcome = await mutate(
      (v) => moveSharedAlbumItem(albumId, item.albumItemId, v, target),
      t('albumContent.actionError'),
    );
    if (!outcome) return;
    const { position, version, totalCount: total } = outcome.value;
    content.applyMove(item.albumItemId, position, version);
    setLiveMessage(t('albumContent.moved', { position: position + 1, total }));

    if (position < held) {
      // Still among the rows held: follow it, and bring it into view if the
      // move took it away from the viewport.
      focusRef.current = { albumItemId: item.albumItemId, action };
      if (!virtualRows.some((v) => v.index === position)) {
        virtualizer.scrollToIndex(position, { align: 'center' });
      }
    } else {
      // Moved past the rows held. Focus stays where the user was — on the row
      // that took its place — and the announcement says where it went.
      setOpenRowId(null);
      focusSuccessorOf(item, index);
    }
  }

  function contributorLabel(item: AlbumContentItem): string {
    if (!item.contributorDisplayName) return '';
    return item.contributorMaskedEmail
      ? `${item.contributorDisplayName} (${item.contributorMaskedEmail})`
      : item.contributorDisplayName;
  }

  async function remove(item: AlbumContentItem, index: number) {
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

    const outcome = await mutate(
      (v) => removeSharedAlbumItem(albumId, item.albumItemId, v),
      t('albumContent.removeError'),
    );
    if (!outcome) return;
    content.applyRemove(item.albumItemId, outcome.value.version, outcome.value.coverFileItemId);
    setOpenRowId(null);
    focusSuccessorOf(item, index);
  }

  async function setCover(item: AlbumContentItem, makeCover: boolean) {
    const outcome = await mutate(
      (v) => setSharedAlbumCover(albumId, v, makeCover ? item.fileItemId : null),
      t('albumContent.actionError'),
    );
    if (!outcome) return;
    content.applyCover(outcome.value.coverFileItemId, outcome.value.version);
    focusRef.current = { albumItemId: item.albumItemId, action: makeCover ? 'clear-cover' : 'set-cover' };
  }

  function toggleRow(item: AlbumContentItem) {
    setOpenRowId((open) => (open === item.albumItemId ? null : item.albumItemId));
  }

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

          {phase === 'loading' && <p>{t('common.loading')}</p>}
          {phase === 'gone' && (
            <p className="empty-state" role="status" data-testid="album-content-gone">
              {t('albumContent.loadError')}
            </p>
          )}
          {phase === 'error' && (
            <p className="inline-error" role="alert">{t('albumContent.loadError')}</p>
          )}
          {actionError && (
            <p ref={noticeRef} tabIndex={-1} className="inline-error" role="alert"
              data-testid="album-content-notice">
              {actionError}
            </p>
          )}

          {phase === 'ready' && totalCount === 0 && (
            <p className="empty-state" data-testid="album-content-empty">
              {t('albumContent.empty')}
            </p>
          )}

          {/* The scroll container lives as long as the panel — hidden, not
              unmounted, while there is nothing to list — so the virtualizer
              keeps one scroll element across reloads. */}
          <div
            ref={scrollerRef}
            className="album-content-scroller"
            data-testid="album-content-scroller"
            hidden={!(phase === 'ready' && totalCount > 0)}
          >
            <ul
              className="album-content-list"
              data-testid="album-content-list"
              aria-label={t('albumContent.listAria')}
              aria-busy={busy || loadingMore}
              style={{ height: `${virtualizer.getTotalSize()}px` }}
            >
              {virtualRows.map((virtualRow) => {
                const index = virtualRow.index;
                const item = items[index];
                const open = canEdit && openRowId === item.albumItemId;
                const actionsId = `album-content-actions-${item.albumItemId}`;
                return (
                  <li
                    key={item.albumItemId}
                    ref={virtualizer.measureElement}
                    data-index={index}
                    className="album-content-row"
                    data-testid="album-content-row"
                    data-origin={item.origin}
                    data-item-id={item.albumItemId}
                    // The list is windowed: say where each row sits in the
                    // whole album, not in what happens to be rendered.
                    aria-posinset={index + 1}
                    aria-setsize={totalCount}
                    style={{ transform: `translateY(${virtualRow.start}px)` }}
                  >
                    <div className="album-content-card">
                      <div className="album-content-thumb">
                        {item.sourceState === 'available' ? (
                          <img src={item.thumbnailUrl} alt="" loading="lazy" decoding="async"
                            width={56} height={56} />
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
                          {t('albumContent.position', { position: index + 1, total: totalCount })}
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

                      {/* Editorial controls exist ONLY for a curator. For a
                          Viewer or Contributor they are absent entirely — not
                          disabled, which would advertise a capability they do
                          not have. */}
                      {canEdit && (
                        <button
                          type="button"
                          className="ws-icon-button album-content-actions-toggle"
                          data-testid="album-content-actions"
                          data-action="toggle"
                          aria-expanded={open}
                          aria-controls={open ? actionsId : undefined}
                          aria-label={t('albumContent.actionsFor', { position: index + 1, total: totalCount })}
                          onClick={() => toggleRow(item)}
                        >
                          ⋯
                        </button>
                      )}

                      {/* Keyboard-, touch- and screen-reader-operable. Explicit
                          buttons rather than drag-only: a pointer gesture is not
                          reachable by any of those three. */}
                      {open && (
                        <div
                          id={actionsId}
                          className="album-content-actions"
                          role="group"
                          aria-label={t('albumContent.actionsFor', { position: index + 1, total: totalCount })}
                          data-testid="album-content-actions-panel"
                          onKeyDown={(e) => {
                            if (e.key !== 'Escape') return;
                            // Escape closes the row first, the dialog second.
                            e.stopPropagation();
                            setOpenRowId(null);
                            focusRef.current = { albumItemId: item.albumItemId, action: 'toggle' };
                          }}
                        >
                          <button type="button" className="row-action" data-action="up"
                            data-testid="album-content-move-up"
                            disabled={index === 0 || busy}
                            onClick={() => void move(item, index, 'up')}>
                            {t('albumContent.moveUp')}
                          </button>
                          <button type="button" className="row-action" data-action="down"
                            data-testid="album-content-move-down"
                            disabled={index === totalCount - 1 || busy}
                            onClick={() => void move(item, index, 'down')}>
                            {t('albumContent.moveDown')}
                          </button>
                          <button type="button" className="row-action" data-action="first"
                            data-testid="album-content-move-first"
                            disabled={index === 0 || busy}
                            onClick={() => void move(item, index, 'first')}>
                            {t('albumContent.moveFirst')}
                          </button>
                          <button type="button" className="row-action" data-action="last"
                            data-testid="album-content-move-last"
                            disabled={index === totalCount - 1 || busy}
                            onClick={() => void move(item, index, 'last')}>
                            {t('albumContent.moveLast')}
                          </button>

                          {/* Only a currently-servable member may become the
                              cover; the server refuses anything else, and
                              offering it would be a control that always fails. */}
                          {item.sourceState === 'available' && !item.isCover && (
                            <button type="button" className="row-action" data-action="set-cover"
                              data-testid="album-content-set-cover"
                              disabled={busy}
                              onClick={() => void setCover(item, true)}>
                              {t('albumContent.useAsCover')}
                            </button>
                          )}
                          {item.isCover && (
                            <button type="button" className="row-action" data-action="clear-cover"
                              data-testid="album-content-clear-cover"
                              disabled={busy}
                              onClick={() => void setCover(item, false)}>
                              {t('albumContent.clearCover')}
                            </button>
                          )}

                          <button type="button" className="row-action" data-action="remove"
                            data-testid="album-content-remove"
                            disabled={busy}
                            aria-label={t('albumContent.removeAria', { index: index + 1, total: totalCount })}
                            onClick={() => void remove(item, index)}>
                            {t('albumContent.remove')}
                          </button>
                        </div>
                      )}
                    </div>
                  </li>
                );
              })}
            </ul>

            {loadingMore && (
              <p className="muted album-content-more" role="status">{t('common.loading')}</p>
            )}
            {loadMoreFailed && (
              <p className="inline-error album-content-more" role="alert">
                {t('albumContent.loadMoreError')}{' '}
                <button type="button" className="row-action" onClick={loadMore}>
                  {t('common.retry')}
                </button>
              </p>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
