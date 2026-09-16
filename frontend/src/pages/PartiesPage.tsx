import { useEffect, useRef, useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { ApiError, createParty, listParties, type PartyStatus, type PartySummary } from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n, type MessageKey } from '../i18n';
import { partyStatusLabelKey, sortParties } from '../party/partyModel';
import { Button, EmptyState, Notice, Panel, PanelSkeleton } from '../party/workspace/ui';
import '../party/Party.css';
import '../party/workspace/PartyWorkspace.css';

// THE party destination.
//
// A party is a first-class thing in NubArca, so it has a place of its own
// rather than living inside an album's settings. What this page shows is what a
// host actually scans for, in three groups they can tell apart without reading:
// what is happening tonight, what is being prepared, and what is over.
//
// The groups are a reading aid, not a filter — every party is on the page —
// and they appear only when there is more than one group to separate, because
// a heading over a list of one is noise.

type Status =
  | { kind: 'loading' }
  | { kind: 'ready'; parties: PartySummary[] }
  | { kind: 'error' };

/** The three things a host is looking for, and which statuses each collects. */
const GROUPS: readonly { id: 'now' | 'coming' | 'over'; statuses: readonly PartyStatus[] }[] = [
  { id: 'now', statuses: ['live'] },
  { id: 'coming', statuses: ['published', 'draft'] },
  { id: 'over', statuses: ['ended'] },
];

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
    return (
      <main className="pw" data-testid="parties-page">
        <header className="pw-head">
          <div className="pw-skeleton pw-skeleton--title" aria-hidden />
        </header>
        <PanelSkeleton rows={2} />
        <p role="status" className="visually-hidden">{t('common.loading')}</p>
      </main>
    );
  }

  if (status.kind === 'error') {
    return (
      <main className="pw" data-testid="parties-page">
        <header className="pw-head">
          <h1 className="pw-title">{t('party.title')}</h1>
        </header>
        <Notice tone="error" title={t('party.loadError')} testId="parties-load-error">
          <p>{t('party.loadErrorBody')}</p>
        </Notice>
      </main>
    );
  }

  const present = GROUPS
    .map((group) => ({
      id: group.id,
      parties: status.parties.filter((p) => group.statuses.includes(p.status)),
    }))
    .filter((group) => group.parties.length > 0);

  return (
    <main className="pw" data-testid="parties-page">
      <header className="pw-head">
        <div className="pw-head-main">
          <div className="pw-identity">
            <h1 className="pw-title">{t('party.title')}</h1>
            <p className="pw-section-lede">{t('party.subtitle')}</p>
          </div>
          {status.parties.length > 0 && (
            <div className="pw-head-actions">
              <Button
                tone="primary" size="lg" data-testid="party-new"
                onClick={() => setCreating(true)}
              >
                {t('party.new')}
              </Button>
            </div>
          )}
        </div>
      </header>

      <div className="pw-panels">
        {creating && (
          <CreatePartyForm
            onCancel={() => setCreating(false)}
            onCreated={(party) => void navigate(`/parties/${party.id}`)}
          />
        )}

        {status.parties.length === 0 && !creating && (
          <EmptyState
            testId="parties-empty"
            title={t('party.empty.heading')}
            body={t('party.empty.body')}
            action={(
              <Button tone="primary" size="lg" data-testid="party-new" onClick={() => setCreating(true)}>
                {t('party.empty.cta')}
              </Button>
            )}
          />
        )}

        {present.map((group) => (
          <section key={group.id} className="pw-group" data-testid={`party-group-${group.id}`}>
            {present.length > 1 && (
              <div className="pw-group-head">
                <h2 className="pw-group-title">{t(`party.group.${group.id}` as MessageKey)}</h2>
              </div>
            )}
            <ul className="pw-cards" data-testid="party-list">
              {group.parties.map((party) => (
                <li key={party.id}>
                  <Link to={`/parties/${party.id}`} className="pw-card">
                    <span className="pw-card-head">
                      <span className="pw-card-title party-list-title">{party.title}</span>
                      {/* State is never carried by colour alone: the badge says
                          the word, and it is a product label rather than a raw
                          `draft` off the wire. */}
                      <span
                        className={`pw-badge pw-badge--${party.status}`}
                        data-status={party.status}
                      >
                        {t(partyStatusLabelKey(party.status))}
                      </span>
                    </span>
                    <span className="pw-card-meta">
                      {party.eventStartsAt
                        ? formatDate(party.eventStartsAt)
                        : t('party.overview.noDate')}
                      {party.mainAlbumName && (
                        <>
                          <span className="pw-dot" aria-hidden> · </span>
                          {party.mainAlbumName}
                        </>
                      )}
                    </span>
                  </Link>
                </li>
              ))}
            </ul>
          </section>
        ))}
      </div>
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
    <Panel
      tone="feature"
      title={t('party.create.heading')}
      note={t('party.create.note')}
      testId="party-create"
      headingLevel={2}
    >
      <form
        className="pw-fields pw-fields--2"
        onSubmit={(e) => { e.preventDefault(); void submit(); }}
      >
        <label className="pw-field pw-field--wide">
          <span className="pw-field-label">{t('party.create.titleLabel')}</span>
          <input
            ref={firstFieldRef} value={title} disabled={busy}
            placeholder={t('party.create.titlePlaceholder')}
            aria-label={t('party.create.titleLabel')}
            onChange={(e) => setTitle(e.target.value)}
          />
        </label>
        <label className="pw-field">
          <span className="pw-field-label">{t('party.create.dateLabel')}</span>
          <input
            type="datetime-local" value={eventStartsAt} disabled={busy}
            aria-label={t('party.create.dateLabel')}
            onChange={(e) => setEventStartsAt(e.target.value)}
          />
        </label>
        <label className="pw-field pw-field--wide">
          <span className="pw-field-label">{t('party.create.descriptionLabel')}</span>
          <textarea
            value={description} rows={2} disabled={busy}
            aria-label={t('party.create.descriptionLabel')}
            onChange={(e) => setDescription(e.target.value)}
          />
        </label>
        <div className="pw-form-foot pw-field--wide">
          <Button
            tone="primary" type="submit" busy={busy} disabled={title.trim() === ''}
            data-testid="party-create-submit"
          >
            {t('party.create.submit')}
          </Button>
          <Button disabled={busy} onClick={onCancel}>{t('party.create.cancel')}</Button>
        </div>
      </form>
      {error && <Notice tone="error"><p>{error}</p></Notice>}
    </Panel>
  );
}
