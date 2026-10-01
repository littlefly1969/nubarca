import { useCallback, useEffect, useState } from 'react';
import { getPartyGuestbookPhotos, type PartyGuestbookPhoto } from '@nubarca/api-client';
import { useI18n } from '../i18n';

// STEP ONE OF A MEMORY: which photograph.
//
// The party's album, as the book may use it — photographs only, never a video,
// and never an album to choose: there is one party and one album behind it,
// and the server already knows which. A tap chooses and moves on; there is no
// "confirm", because changing one's mind is one "change photo" away.
//
// The list is the server's whole answer, like every party gallery: the album
// is not paginated anywhere in Party, so the grid relies on the browser's own
// lazy loading for the thumbnails rather than inventing a pager here.

type Status =
  | { kind: 'loading' }
  | { kind: 'ready'; photos: PartyGuestbookPhoto[] }
  | { kind: 'error' };

export function PartyGuestbookPhotoPicker({
  token, selectedId, notice, onChoose, onCancel,
}: {
  token: string;
  /** The photograph already chosen, when coming back to change it. */
  selectedId: string | null;
  /** Said above the grid — why the guest is back here, when it is not by choice. */
  notice?: string | null;
  onChoose(photo: PartyGuestbookPhoto): void;
  onCancel(): void;
}) {
  const { t } = useI18n();
  const [status, setStatus] = useState<Status>({ kind: 'loading' });

  const load = useCallback((signal?: AbortSignal) => {
    setStatus({ kind: 'loading' });
    getPartyGuestbookPhotos(token, signal)
      .then((result) => { if (!signal?.aborted) setStatus({ kind: 'ready', photos: result.photos }); })
      .catch(() => { if (!signal?.aborted) setStatus({ kind: 'error' }); });
  }, [token]);

  useEffect(() => {
    const ctrl = new AbortController();
    load(ctrl.signal);
    return () => ctrl.abort();
  }, [load]);

  return (
    <section className="guestbook-picker" data-testid="guestbook-picker" aria-labelledby="guestbook-picker-title">
      <div className="guestbook-step-head">
        <button
          type="button"
          className="guestbook-icon-button"
          onClick={onCancel}
          aria-label={t('partyGuestbookComposer.backToBook')}
          data-testid="guestbook-picker-cancel"
        >
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M15 5l-7 7 7 7" /></svg>
        </button>
        <h2 id="guestbook-picker-title" className="guestbook-step-title" tabIndex={-1}>
          {t('partyGuestbookComposer.choosePhoto')}
        </h2>
      </div>

      {notice && (
        <p className="guestbook-notice" role="alert" data-testid="guestbook-picker-notice">{notice}</p>
      )}

      {status.kind === 'loading' && (
        <div className="guestbook-picker-grid" role="status" aria-label={t('common.loading')}
          data-testid="guestbook-picker-loading">
          {Array.from({ length: 9 }, (_, i) => (
            <span key={i} className="guestbook-picker-cell is-skeleton" aria-hidden="true" />
          ))}
        </div>
      )}

      {status.kind === 'error' && (
        <div className="guestbook-state" data-testid="guestbook-picker-error">
          <p role="alert">{t('partyGuestbookComposer.photosError')}</p>
          <button type="button" className="party-contribution-secondary" onClick={() => load()}>
            {t('common.retry')}
          </button>
        </div>
      )}

      {status.kind === 'ready' && status.photos.length === 0 && (
        <p className="guestbook-state" data-testid="guestbook-picker-empty">
          {t('partyGuestbookComposer.noPhotos')}
        </p>
      )}

      {status.kind === 'ready' && status.photos.length > 0 && (
        <ul className="guestbook-picker-grid" data-testid="guestbook-picker-grid">
          {status.photos.map((photo, index) => {
            const selected = photo.id === selectedId;
            return (
              <li key={photo.id}>
                <button
                  type="button"
                  className={selected ? 'guestbook-picker-cell is-selected' : 'guestbook-picker-cell'}
                  aria-pressed={selected}
                  aria-label={t('partyGuestbookComposer.photoLabel', {
                    index: String(index + 1), count: String(status.photos.length),
                  })}
                  onClick={() => onChoose(photo)}
                  data-testid={`guestbook-photo-${photo.id}`}
                >
                  <img src={photo.thumbnailUrl} alt="" loading="lazy" decoding="async" draggable={false} />
                  {selected && (
                    <span className="guestbook-picker-check" aria-hidden="true">
                      <svg viewBox="0 0 24 24"><path d="m6.5 12.4 3.6 3.6 7.4-7.6" /></svg>
                    </span>
                  )}
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
