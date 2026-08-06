import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  cancelAlbumTransfer,
  listSentAlbumTransfers,
  previewAlbumTransfer,
  sendAlbumTransfer,
  type AlbumTransferBlocker,
  type AlbumTransferPreview,
  type SentAlbumTransfer,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';
import { formatDate, formatSize } from '../components/format';

// SHARE-COPY-01, sender side.
//
// Deliberately a SEPARATE panel from AlbumSharePanel, reached by its own button.
// "Share" and "Send a copy" must not look like two settings of one thing: one
// grants revocable access to media that stays yours, the other gives away an
// independent album you can never take back. Merging them would invite exactly
// the mistake that cannot be undone.
//
// Only ever rendered from the OWNER's album page. There is no role check here
// because there is no role that reaches it — an Editor curates, they do not
// redistribute, and the backend refuses them with a 404 regardless.

interface Props {
  albumId: string;
  albumName: string;
  onClose(): void;
  returnFocusRef?: React.RefObject<HTMLButtonElement | null>;
}

type Status =
  | { kind: 'loading' }
  | { kind: 'error' }
  | { kind: 'ready'; preview: AlbumTransferPreview; sent: SentAlbumTransfer[] };

export function AlbumCopyPanel({ albumId, albumName, onClose, returnFocusRef }: Props) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const [status, setStatus] = useState<Status>({ kind: 'loading' });
  const [email, setEmail] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const dialogRef = useRef<HTMLDivElement>(null);
  // Synchronous in-flight guard. `busy` state cannot serve as one: setState is
  // asynchronous, so a fast double click passes the check twice and fires two
  // sends — and a duplicate send is not idempotent, it is a second offer.
  const inFlight = useRef(false);

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      const [preview, sent] = await Promise.all([
        previewAlbumTransfer(albumId, signal),
        listSentAlbumTransfers(signal),
      ]);
      setStatus({
        kind: 'ready',
        preview,
        // The endpoint returns every copy this user ever sent; this panel is
        // about ONE album.
        sent: sent.filter((x) => x.sourceAlbumId === albumId),
      });
    } catch (err) {
      if (signal?.aborted) return;
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setStatus({ kind: 'error' });
    }
  }, [albumId, invalidateAuth]);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  useEffect(() => {
    dialogRef.current?.querySelector<HTMLInputElement>('input')?.focus();
    return () => returnFocusRef?.current?.focus();
  }, [returnFocusRef]);

  async function send() {
    const trimmed = email.trim();
    if (trimmed.length === 0) { setError(t('albumCopy.emailRequired')); return; }
    if (inFlight.current) return;
    inFlight.current = true;
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      const created = await sendAlbumTransfer(albumId, trimmed);
      setNotice(t('albumCopy.sent', { name: created.recipientDisplayName }));
      setEmail('');
      await load();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setError(sendErrorMessage(err, t));
      // The album's content may be what changed; re-read so the summary and any
      // blockers reflect reality rather than what we showed a moment ago.
      await load();
    } finally {
      inFlight.current = false;
      setBusy(false);
    }
  }

  async function cancel(transferId: string) {
    if (inFlight.current) return;
    inFlight.current = true;
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      await cancelAlbumTransfer(transferId);
      setNotice(t('albumCopy.cancelled'));
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      // 409 here means the recipient accepted first. Their copy is theirs; say
      // so plainly rather than offering a retry that can never succeed.
      setError(err instanceof ApiError && err.status === 409
        ? t('albumCopy.cancelTooLate')
        : t('albumCopy.cancelError'));
    } finally {
      inFlight.current = false;
      setBusy(false);
      await load();
    }
  }

  return (
    <div
      className="ws-sheet-backdrop"
      data-testid="album-copy-backdrop"
      onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}
    >
      <div
        ref={dialogRef}
        className="ws-sheet album-copy-panel"
        role="dialog"
        aria-modal="true"
        aria-label={t('albumCopy.title', { name: albumName })}
        data-testid="album-copy-panel"
        onKeyDown={(e) => { if (e.key === 'Escape') { e.stopPropagation(); onClose(); } }}
      >
        <header className="ws-sheet-head">
          <h2 className="ws-sheet-title">{t('albumCopy.title', { name: albumName })}</h2>
          <button
            type="button" className="ws-icon-button"
            aria-label={t('common.close')}
            data-testid="album-copy-close"
            onClick={onClose}
          >✕</button>
        </header>

        <div className="ws-sheet-body">
          <p className="muted" data-testid="album-copy-intro">{t('albumCopy.intro')}</p>

          {status.kind === 'loading' && <p role="status">{t('common.loading')}</p>}
          {status.kind === 'error' && (
            <p className="inline-error" role="alert">{t('albumCopy.sendError')}</p>
          )}

          {status.kind === 'ready' && (
            <>
              {status.preview.blockers.length > 0 ? (
                <section
                  className="album-copy-blocked"
                  aria-label={t('albumCopy.blockedHeading')}
                  data-testid="album-copy-blocked"
                >
                  <h3>{t('albumCopy.blockedHeading')}</h3>
                  <ul>
                    {status.preview.blockers.map((b) => (
                      <li key={b.reason} data-testid={`album-copy-blocker-${b.reason}`}>
                        {blockerMessage(b, t)}
                      </li>
                    ))}
                  </ul>
                </section>
              ) : (
                <section aria-label={t('albumCopy.summaryHeading')} data-testid="album-copy-summary">
                  <h3>{t('albumCopy.summaryHeading')}</h3>
                  <p data-testid="album-copy-count">
                    {status.preview.eligibleItemCount === 1
                      ? t('albumCopy.summaryItemsOne')
                      : t('albumCopy.summaryItems', {
                          count: String(status.preview.eligibleItemCount),
                        })}
                  </p>
                  <p data-testid="album-copy-size">
                    {t('albumCopy.summarySize', {
                      size: formatSize(status.preview.eligibleSizeBytes),
                    })}
                  </p>
                  <p className="muted">{t('albumCopy.snapshotExplain')}</p>
                  <p className="muted">{t('albumCopy.permanentWarning')}</p>
                </section>
              )}

              {status.preview.canSend && (
                <section aria-label={t('albumCopy.emailLabel')}>
                  <label>
                    {t('albumCopy.emailLabel')}
                    <input
                      type="email"
                      data-testid="album-copy-email"
                      value={email}
                      disabled={busy}
                      placeholder={t('albumCopy.emailPlaceholder')}
                      onChange={(e) => setEmail(e.target.value)}
                    />
                  </label>
                  <button
                    type="button"
                    className="row-action-primary"
                    data-testid="album-copy-send"
                    disabled={busy}
                    onClick={() => void send()}
                  >
                    {busy ? t('albumCopy.sending') : t('albumCopy.send')}
                  </button>
                </section>
              )}

              {error && (
                <p className="inline-error" role="alert" data-testid="album-copy-error">{error}</p>
              )}
              {notice && (
                <p className="inline-notice" role="status" data-testid="album-copy-notice">
                  {notice}
                </p>
              )}

              <section
                aria-label={t('albumCopy.sentHeading')}
                data-testid="album-copy-sent-section"
              >
                <h3>{t('albumCopy.sentHeading')}</h3>
                {status.sent.length === 0 ? (
                  <p className="muted" data-testid="album-copy-sent-empty">
                    {t('albumCopy.sentEmpty')}
                  </p>
                ) : (
                  <ul className="album-copy-sent" data-testid="album-copy-sent">
                    {status.sent.map((transfer) => (
                      <li key={transfer.id} data-testid={`album-copy-sent-${transfer.id}`}>
                        <span className="album-copy-sent-who">
                          {t('albumCopy.sentTo', { name: transfer.recipientDisplayName })}
                          {transfer.recipientEmailMask
                            ? ` (${transfer.recipientEmailMask})`
                            : ''}
                        </span>
                        <span
                          className="album-copy-sent-state"
                          data-testid={`album-copy-state-${transfer.id}`}
                        >
                          {t(`albumCopy.state.${transfer.state}`)}
                        </span>
                        <span className="muted">
                          {t('albumCopy.snapshotDate', { date: formatDate(transfer.createdAt) })}
                        </span>
                        {transfer.state === 'pending' && (
                          <>
                            <span className="muted">
                              {t('albumCopy.expiresOn', {
                                date: formatDate(transfer.expiresAt),
                              })}
                            </span>
                            <button
                              type="button"
                              className="row-action"
                              data-testid={`album-copy-cancel-${transfer.id}`}
                              disabled={busy}
                              onClick={() => void cancel(transfer.id)}
                            >
                              {busy ? t('albumCopy.cancelling') : t('albumCopy.cancel')}
                            </button>
                          </>
                        )}
                      </li>
                    ))}
                  </ul>
                )}
              </section>
            </>
          )}
        </div>

        <footer className="ws-sheet-foot">
          <div className="ws-sheet-foot-right">
            <button type="button" className="row-action" onClick={onClose}>
              {t('common.close')}
            </button>
          </div>
        </footer>
      </div>
    </div>
  );
}

// Derived from the provider rather than re-declared: message keys are a
// strict literal union, so a hand-written `string` signature would silently
// accept a key that does not exist.
type Translate = ReturnType<typeof useI18n>['t'];

// Counts and a category only. The API deliberately does not return which files
// blocked the send, and this must not invent a way to imply them.
function blockerMessage(blocker: AlbumTransferBlocker, t: Translate): string {
  const one = blocker.itemCount === 1;
  const count = String(blocker.itemCount);
  switch (blocker.reason) {
    case 'ContributedByAnotherUser':
      return one
        ? t('albumCopy.blockedContributedOne')
        : t('albumCopy.blockedContributed', { count });
    case 'InPrivateVault':
      return one ? t('albumCopy.blockedVaultOne') : t('albumCopy.blockedVault', { count });
    case 'Trashed':
      return one ? t('albumCopy.blockedTrashedOne') : t('albumCopy.blockedTrashed', { count });
    default:
      return one
        ? t('albumCopy.blockedUnavailableOne')
        : t('albumCopy.blockedUnavailable', { count });
  }
}

function sendErrorMessage(err: unknown, t: Translate): string {
  if (!(err instanceof ApiError)) {
    return t('albumCopy.sendError');
  }
  const code = (err.body as { error?: string } | null)?.error;
  switch (code) {
    case 'recipient_not_found': return t('albumCopy.recipientNotFound');
    case 'recipient_is_sender': return t('albumCopy.recipientIsSender');
    case 'already_pending': return t('albumCopy.alreadyPending');
    case 'empty_album': return t('albumCopy.emptyAlbum');
    // The album's content changed under us; the refreshed blocker list above
    // now says what is wrong, so this heading is enough here.
    case 'contains_ineligible_items': return t('albumCopy.blockedHeading');
    default: return t('albumCopy.sendError');
  }
}
