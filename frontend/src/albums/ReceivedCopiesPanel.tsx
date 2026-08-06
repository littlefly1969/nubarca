import { useCallback, useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router';
import {
  ApiError,
  acceptAlbumTransfer,
  declineAlbumTransfer,
  listReceivedAlbumTransfers,
  type ReceivedAlbumTransfer,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';
import { formatDate, formatSize } from '../components/format';

// SHARE-COPY-01, recipient side.
//
// Rendered inside the "shared with me" page but kept visually and semantically
// apart from live-share invitations: accepting an invitation gives you a view of
// somebody else's album, accepting a copy gives you an album of your own. The
// consequences differ enough that they must not read as the same decision.
//
// Before deciding, the recipient sees a title, a count, a size and who sent it
// — and nothing else. A pending offer exposes no media whatsoever.
//
// NO AUTOMATIC RETRY. Every failure path here reports what happened and
// re-reads; acceptance is idempotent server-side, so a user-initiated retry is
// safe, but a silent one would be guessing about a state that has changed.

interface Props {
  // Injected by the page so a refresh can also refresh sibling sections.
  onAccepted?(albumId: string): void;
}

export function ReceivedCopiesPanel({ onAccepted }: Props) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const navigate = useNavigate();
  const [transfers, setTransfers] = useState<ReceivedAlbumTransfer[] | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  // The in-flight guard has to be a REF, not the busyId state. setState is
  // asynchronous, so three fast clicks all read `busyId === null` and all fire —
  // and `disabled={busyId !== null}` only takes effect after the re-render,
  // which a real double click beats. A ref flips synchronously on the first
  // click. (The server is idempotent, so the damage would have been wasted
  // requests rather than duplicate albums, but a UI that fires three writes for
  // one intent is still wrong.)
  const inFlight = useRef(false);

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      setTransfers(await listReceivedAlbumTransfers(signal));
    } catch (err) {
      if (signal?.aborted) return;
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setTransfers([]);
      setError(t('receivedCopies.acceptError'));
    }
  }, [invalidateAuth, t]);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  async function accept(transfer: ReceivedAlbumTransfer) {
    if (inFlight.current) return;
    inFlight.current = true;
    setBusyId(transfer.id);
    setError(null);
    setNotice(null);
    try {
      const { albumId } = await acceptAlbumTransfer(transfer.id);
      setNotice(t('receivedCopies.accepted'));
      // Quota and the inbox both changed; re-read before navigating away.
      await load();
      onAccepted?.(albumId);
      navigate(`/albums/${albumId}`);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setError(acceptErrorMessage(err, t));
      // Whatever went wrong, the offer's state is no longer what we rendered.
      await load();
    } finally {
      inFlight.current = false;
      setBusyId(null);
    }
  }

  async function decline(transfer: ReceivedAlbumTransfer) {
    if (inFlight.current) return;
    inFlight.current = true;
    setBusyId(transfer.id);
    setError(null);
    setNotice(null);
    try {
      await declineAlbumTransfer(transfer.id);
      setNotice(t('receivedCopies.declined'));
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setError(err instanceof ApiError && err.status === 404
        ? t('receivedCopies.gone')
        : t('receivedCopies.declineError'));
    } finally {
      inFlight.current = false;
      setBusyId(null);
      await load();
    }
  }

  if (transfers === null) {
    return <p role="status">{t('common.loading')}</p>;
  }

  return (
    <section
      aria-label={t('receivedCopies.heading')}
      data-testid="received-copies-section"
    >
      <h3>{t('receivedCopies.heading')}</h3>

      {error && (
        <p className="inline-error" role="alert" data-testid="received-copies-error">{error}</p>
      )}
      {notice && (
        <p className="inline-notice" role="status" data-testid="received-copies-notice">
          {notice}
        </p>
      )}

      {transfers.length === 0 ? (
        <p className="muted" data-testid="received-copies-empty">{t('receivedCopies.empty')}</p>
      ) : (
        <ul className="received-copies" data-testid="received-copies">
          {transfers.map((transfer) => (
            <li key={transfer.id} data-testid={`received-copy-${transfer.id}`}>
              <h4>{transfer.title}</h4>
              <p className="received-copy-from">
                {t('receivedCopies.from', { name: transfer.senderDisplayName })}
                {/* The mask is not a directory: this row is visible only to the
                    one person the offer was addressed to. It exists so two
                    contacts with the same display name are distinguishable. */}
                {transfer.senderEmailMask ? ` (${transfer.senderEmailMask})` : ''}
              </p>
              <p className="muted" data-testid={`received-copy-details-${transfer.id}`}>
                {transfer.itemCount === 1
                  ? t('receivedCopies.detailsOne', {
                      size: formatSize(transfer.totalSizeBytes),
                    })
                  : t('receivedCopies.details', {
                      count: String(transfer.itemCount),
                      size: formatSize(transfer.totalSizeBytes),
                    })}
              </p>
              <p className="muted">
                {t('receivedCopies.sentOn', { date: formatDate(transfer.createdAt) })}
              </p>

              {transfer.state === 'pending' ? (
                <>
                  <p className="muted">
                    {t('receivedCopies.expiresOn', { date: formatDate(transfer.expiresAt) })}
                  </p>
                  {/* Stated BEFORE the buttons: accepting is irreversible, costs
                      quota, and does not carry the sender's People data. */}
                  <details data-testid={`received-copy-explain-${transfer.id}`}>
                    <summary>{t('receivedCopies.whatHappens')}</summary>
                    <ul>
                      <li>{t('receivedCopies.explainIndependent')}</li>
                      <li>{t('receivedCopies.explainQuota')}</li>
                      <li>{t('receivedCopies.explainIrrevocable')}</li>
                      <li>{t('receivedCopies.explainNoPeople')}</li>
                    </ul>
                  </details>
                  <div className="received-copy-actions">
                  <button
                    type="button"
                    className="row-action-primary"
                    data-testid={`received-copy-accept-${transfer.id}`}
                    disabled={busyId !== null}
                    onClick={() => void accept(transfer)}
                  >
                    {busyId === transfer.id
                      ? t('receivedCopies.accepting')
                      : t('receivedCopies.accept')}
                  </button>
                  <button
                    type="button"
                    className="row-action"
                    data-testid={`received-copy-decline-${transfer.id}`}
                    disabled={busyId !== null}
                    onClick={() => void decline(transfer)}
                  >
                    {busyId === transfer.id
                      ? t('receivedCopies.declining')
                      : t('receivedCopies.decline')}
                  </button>
                  </div>
                </>
              ) : (
                <>
                  <span data-testid={`received-copy-state-${transfer.id}`}>
                    {t(`albumCopy.state.${transfer.state}`)}
                  </span>
                  {/* Accepted: no accept/decline controls remain — the decision
                      is made and the album is simply the recipient's. */}
                  {transfer.state === 'accepted' && transfer.createdAlbumId && (
                    <button
                      type="button"
                      className="row-action"
                      data-testid={`received-copy-open-${transfer.id}`}
                      onClick={() => navigate(`/albums/${transfer.createdAlbumId}`)}
                    >
                      {t('receivedCopies.openAlbum')}
                    </button>
                  )}
                </>
              )}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

// Derived from the provider rather than re-declared: message keys are a
// strict literal union, so a hand-written `string` signature would silently
// accept a key that does not exist.
type Translate = ReturnType<typeof useI18n>['t'];

function acceptErrorMessage(err: unknown, t: Translate): string {
  if (!(err instanceof ApiError)) {
    return t('receivedCopies.acceptError');
  }
  if (err.status === 404) {
    return t('receivedCopies.gone');
  }
  const body = err.body as
    { error?: string; requiredBytes?: number; remainingBytes?: number } | null;
  switch (body?.error) {
    case 'cancelled': return t('receivedCopies.cancelledBySender');
    case 'expired': return t('receivedCopies.expired');
    case 'sender_unavailable': return t('receivedCopies.senderUnavailable');
    case 'already_resolved': return t('receivedCopies.alreadyResolved');
    case 'quota_exceeded':
      return t('receivedCopies.quotaExceeded', {
        required: formatSize(body.requiredBytes ?? 0),
        remaining: formatSize(body.remainingBytes ?? 0),
      });
    default: return t('receivedCopies.acceptError');
  }
}
