import { useEffect, useRef, useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { ApiError, createParty, listParties, type PartySummary } from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';
import { partyStatusLabelKey, sortParties } from '../party/partyModel';
import '../party/Party.css';

// THE party destination.
//
// A party is a first-class thing in NubArca now, so it has a place of its own
// rather than living inside an album's settings. What this page shows is what a
// host actually scans for: what is happening, what is coming, and what is over.

type Status =
  | { kind: 'loading' }
  | { kind: 'ready'; parties: PartySummary[] }
  | { kind: 'error' };

export function PartiesPage() {
  const { t, formatDate } = useI18n();
  const { invalidateAuth } = useAuth();
  const navigate = useNavigate();
  const [status, setStatus] = useState<Status>({ kind: 'loading' });
  const [creating, setCreating] = useState(false);
  const abortRef = useRef<AbortController | null>(null);

  useEffect(() => {
    abortRef.current?.abort();
    const ctrl = new AbortController();
    abortRef.current = ctrl;
    listParties(ctrl.signal)
      // Re-sorted with the SAME rule the server used. Not because the order
      // arrives wrong, but so a party created on this page can join the list
      // without a round trip and still land where the server would have put it.
      .then((parties) => setStatus({ kind: 'ready', parties: sortParties(parties) }))
      .catch((err) => {
        if ((err as Error).name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        setStatus({ kind: 'error' });
      });
    return () => ctrl.abort();
  }, [invalidateAuth]);

  if (status.kind === 'loading') {
    return <main className="party-page"><p role="status">{t('common.loading')}</p></main>;
  }
  if (status.kind === 'error') {
    return (
      <main className="party-page">
        <p className="inline-error" role="alert">{t('party.loadError')}</p>
      </main>
    );
  }

  return (
    <main className="party-page" data-testid="parties-page">
      <header className="party-page-header">
        <div>
          <h1>{t('party.title')}</h1>
          <p className="muted">{t('party.subtitle')}</p>
        </div>
        {status.parties.length > 0 && (
          <button
            type="button" className="row-action-primary" data-testid="party-new"
            onClick={() => setCreating(true)}
          >
            {t('party.new')}
          </button>
        )}
      </header>

      {creating && (
        <CreatePartyForm
          onCancel={() => setCreating(false)}
          onCreated={(party) => void navigate(`/parties/${party.id}`)}
        />
      )}

      {status.parties.length === 0 && !creating && (
        <section className="party-empty" data-testid="parties-empty">
          <h2>{t('party.empty.heading')}</h2>
          <p className="muted">{t('party.empty.body')}</p>
          <button
            type="button" className="row-action-primary" data-testid="party-new"
            onClick={() => setCreating(true)}
          >
            {t('party.empty.cta')}
          </button>
        </section>
      )}

      {status.parties.length > 0 && (
        <ul className="party-list" data-testid="party-list">
          {status.parties.map((party) => (
            <li key={party.id} className="party-list-item">
              <Link to={`/parties/${party.id}`} className="party-list-link">
                <span className="party-list-title">{party.title}</span>
                {/* State is never carried by colour alone: the badge says the
                    word as well, and it is a product label, never a raw
                    `draft` off the wire. */}
                <span
                  className={`party-badge party-badge--${party.status}`}
                  data-status={party.status}
                >
                  {t(partyStatusLabelKey(party.status))}
                </span>
                <span className="party-list-meta">
                  {party.eventStartsAt
                    ? formatDate(party.eventStartsAt)
                    : t('party.overview.noDate')}
                  {party.mainAlbumName && <> · {party.mainAlbumName}</>}
                </span>
              </Link>
            </li>
          ))}
        </ul>
      )}
    </main>
  );
}

// Only what a party genuinely needs to exist: a name, and optionally when it
// happens. No album, no television, no game, no printing, no quotas — every one
// of those is a decision the host takes later, in the party's own workspace.
function CreatePartyForm({
  onCreated, onCancel,
}: {
  onCreated(party: { id: string }): void;
  onCancel(): void;
}) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const [title, setTitle] = useState('');
  const [eventStartsAt, setEventStartsAt] = useState('');
  const [description, setDescription] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const firstFieldRef = useRef<HTMLInputElement>(null);

  useEffect(() => { firstFieldRef.current?.focus(); }, []);

  async function submit() {
    const name = title.trim();
    if (name === '') { setError(t('party.create.invalid')); return; }
    setBusy(true); setError(null);
    try {
      const party = await createParty({
        title: name,
        description: description.trim() || null,
        eventStartsAt: eventStartsAt === '' ? null : new Date(eventStartsAt).toISOString(),
      });
      onCreated(party);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setError(err instanceof ApiError && err.status === 400
        ? t('party.create.invalid') : t('party.create.failed'));
    } finally { setBusy(false); }
  }

  return (
    <section className="party-card party-create" data-testid="party-create">
      <h2>{t('party.create.heading')}</h2>
      <label className="party-field">
        <span>{t('party.create.titleLabel')}</span>
        <input
          ref={firstFieldRef} value={title} disabled={busy}
          placeholder={t('party.create.titlePlaceholder')}
          aria-label={t('party.create.titleLabel')}
          onChange={(e) => setTitle(e.target.value)}
        />
      </label>
      <label className="party-field">
        <span>{t('party.create.dateLabel')}</span>
        <input
          type="datetime-local" value={eventStartsAt} disabled={busy}
          aria-label={t('party.create.dateLabel')}
          onChange={(e) => setEventStartsAt(e.target.value)}
        />
      </label>
      <label className="party-field">
        <span>{t('party.create.descriptionLabel')}</span>
        <textarea
          value={description} rows={2} disabled={busy}
          aria-label={t('party.create.descriptionLabel')}
          onChange={(e) => setDescription(e.target.value)}
        />
      </label>
      {error && <p className="inline-error" role="alert">{error}</p>}
      <div className="party-card-actions">
        <button
          type="button" className="row-action-primary" data-testid="party-create-submit"
          disabled={busy || title.trim() === ''} onClick={() => void submit()}
        >
          {t('party.create.submit')}
        </button>
        <button type="button" className="row-action" disabled={busy} onClick={onCancel}>
          {t('party.create.cancel')}
        </button>
      </div>
    </section>
  );
}
