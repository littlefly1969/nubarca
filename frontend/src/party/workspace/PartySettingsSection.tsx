import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router';
import {
  ApiError,
  duplicateParty,
  setAlbumPartyMode,
  tearDownParty,
  updateParty,
  type AlbumPartyStatus,
  type Party,
} from '@nubarca/api-client';
import { useAuth } from '../../auth/useAuth';
import { useI18n } from '../../i18n';
import { mainMediaSource } from '../partyModel';
import { PartyCrewPanel } from './PartyCrewPanel';
import { fromLocalInput, toLocalInput } from './partyDateInput';
import { guestAccessExpired } from './partyWorkspaceModel';
import { Button, Notice, Panel, SectionHead, SwitchRow } from './ui';

// "IMPOSTAZIONI" — the party's own facts, when it is open, and the two
// irreversible things a host can do to it.
//
// ONE FORM, ONE SAVE, ONE CONCURRENCY CHECK. The name, the date, the
// description and both closing times are all fields of the party and all share
// its version, so they are edited together and saved once. Two forms over one
// version would mean saving either of them silently discarded whatever the host
// had typed in the other.
//
// The guest-access SWITCH is deliberately not part of that form: it is a
// capability on the album, with its own endpoint, and turning it on is what
// publishes a draft. It is an act, not a field.

interface Draft {
  title: string;
  description: string;
  eventStartsAt: string;
  guestAccessExpiresAt: string;
  libraryAccessExpiresAt: string;
}

const draftOf = (party: Party): Draft => ({
  title: party.title,
  description: party.description ?? '',
  eventStartsAt: toLocalInput(party.eventStartsAt),
  guestAccessExpiresAt: toLocalInput(party.guestAccessExpiresAt),
  libraryAccessExpiresAt: toLocalInput(party.libraryAccessExpiresAt),
});

export function PartySettingsSection({
  party, albumParty, onPartyUpdated, onAlbumPartyUpdated,
}: {
  party: Party;
  albumParty: AlbumPartyStatus | null;
  onPartyUpdated(next: Party): void;
  onAlbumPartyUpdated(next: AlbumPartyStatus): void;
}) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const [draft, setDraft] = useState<Draft>(() => draftOf(party));
  const [busy, setBusy] = useState(false);
  const [state, setState] = useState<'idle' | 'saved' | 'conflict' | 'failed'>('idle');

  // Re-seeded whenever the SERVER's version moves — including after a conflict,
  // which is how the form adopts what actually happened instead of insisting on
  // what the host typed against a stale read.
  useEffect(() => { setDraft(draftOf(party)); }, [party.version]);

  const clean = draftOf(party);
  const dirty = (Object.keys(clean) as (keyof Draft)[]).some((key) =>
    (key === 'title' || key === 'description' ? draft[key].trim() : draft[key]) !== clean[key]);
  const nameless = draft.title.trim() === '';

  async function save() {
    if (nameless) return;
    setBusy(true); setState('idle');
    try {
      onPartyUpdated(await updateParty(party.id, {
        title: draft.title.trim(),
        description: draft.description.trim() || null,
        eventStartsAt: fromLocalInput(draft.eventStartsAt),
        guestAccessExpiresAt: fromLocalInput(draft.guestAccessExpiresAt),
        libraryAccessExpiresAt: fromLocalInput(draft.libraryAccessExpiresAt),
        version: party.version,
      }));
      setState('saved');
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      const body = (err as ApiError).body as { party?: Party } | undefined;
      if (err instanceof ApiError && err.status === 409 && body?.party) {
        onPartyUpdated(body.party);
        setState('conflict');
      } else {
        setState('failed');
      }
    } finally { setBusy(false); }
  }

  const field = (key: keyof Draft) => ({
    value: draft[key],
    disabled: busy,
    onChange: (e: { target: { value: string } }) =>
      setDraft((d) => ({ ...d, [key]: e.target.value })),
  });

  return (
    <>
      <SectionHead title={t('party.section.settings')} lede={t('party.settings.lede')} />

      {/* One form across two panels: the fields are grouped by what they are
          about, and saved by the single control at the end. */}
      <form
        className="pw-panels-form"
        data-testid="party-settings-form"
        onSubmit={(e) => { e.preventDefault(); void save(); }}
      >
        <Panel title={t('party.overview.details')} testId="party-details">
          <div className="pw-fields pw-fields--2">
            <label className="pw-field pw-field--wide">
              {/* Renaming a party renames the PARTY. The album keeps its own
                  name: they were only ever the same string because one was made
                  from the other, and there is no sync in either direction. */}
              <span className="pw-field-label">{t('party.overview.titleLabel')}</span>
              <input aria-label={t('party.overview.titleLabel')} {...field('title')} />
            </label>
            <label className="pw-field pw-field--wide">
              <span className="pw-field-label">{t('party.overview.dateLabel')}</span>
              <input
                type="datetime-local" aria-label={t('party.overview.dateLabel')}
                {...field('eventStartsAt')}
              />
              <span className="pw-field-help">{t('party.settings.dateHelp')}</span>
            </label>
            <label className="pw-field pw-field--wide">
              <span className="pw-field-label">{t('party.overview.descriptionLabel')}</span>
              <textarea
                rows={3} aria-label={t('party.overview.descriptionLabel')}
                {...field('description')}
              />
            </label>
          </div>
          {nameless && <p className="pw-field-error" role="alert">{t('party.settings.nameRequired')}</p>}
        </Panel>

        <Panel
          title={t('party.settings.windows')}
          note={t('party.settings.windowsNote')}
          testId="party-windows"
        >
          <div className="pw-fields pw-fields--2">
            <label className="pw-field">
              <span className="pw-field-label">{t('party.guest.expiresLabel')}</span>
              <input
                type="datetime-local" aria-label={t('party.guest.expiresLabel')}
                {...field('guestAccessExpiresAt')}
              />
              <span className="pw-field-help">{t('party.guest.expiresHelp')}</span>
            </label>
            <label className="pw-field">
              <span className="pw-field-label">{t('party.after.libraryLabel')}</span>
              <input
                type="datetime-local" aria-label={t('party.after.libraryLabel')}
                data-testid="party-library-window"
                {...field('libraryAccessExpiresAt')}
              />
              <span className="pw-field-help">{t('party.after.libraryHelp')}</span>
            </label>
          </div>
          {guestAccessExpired(party) && (
            <Notice tone="warn" testId="party-guest-expired">
              <p>{t('party.guest.expired')}</p>
            </Notice>
          )}
        </Panel>

        <div className="pw-save-bar">
          <Button
            tone="primary" type="submit" busy={busy} disabled={!dirty || nameless}
            data-testid="party-details-save"
          >
            {t('party.overview.save')}
          </Button>
          <span aria-live="polite" className="pw-save-bar-status">
            {state === 'saved' && <span className="pw-small pw-muted" role="status">{t('party.overview.saved')}</span>}
            {dirty && state !== 'saved' && (
              <span className="pw-small pw-muted">{t('party.settings.unsaved')}</span>
            )}
          </span>
        </div>
        {state === 'conflict' && (
          <Notice tone="warn" testId="party-conflict"><p>{t('party.overview.conflict')}</p></Notice>
        )}
        {state === 'failed' && (
          <Notice tone="error"><p>{t('party.overview.saveFailed')}</p></Notice>
        )}
      </form>

      <GuestAccessPanel
        party={party} albumParty={albumParty} onAlbumPartyUpdated={onAlbumPartyUpdated}
      />

      <PartyCrewPanel partyId={party.id} />

      <DuplicatePanel party={party} />
      <TeardownPanel party={party} onPartyUpdated={onPartyUpdated} />
    </>
  );
}

/**
 * Whether the guests can reach this party at all.
 *
 * The source of truth is the CAPABILITY, not the party's status: this switch
 * calls the same endpoint the album panel always called, and the backend
 * performs the Draft → Published transition as part of minting the link. There
 * is no second token-minting path here, and turning access off deliberately
 * does NOT move the status back — a party that was published was published.
 */
function GuestAccessPanel({
  party, albumParty, onAlbumPartyUpdated,
}: {
  party: Party;
  albumParty: AlbumPartyStatus | null;
  onAlbumPartyUpdated(next: AlbumPartyStatus): void;
}) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);
  const album = mainMediaSource(party);

  async function toggle(next: boolean) {
    if (!album) return;
    setBusy(true); setFailed(false);
    try {
      onAlbumPartyUpdated(await setAlbumPartyMode(album.albumId, next));
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setFailed(true);
    } finally { setBusy(false); }
  }

  return (
    <Panel title={t('party.guest.heading')} note={t('party.guest.help')} testId="party-guest-access">
      {album === null ? (
        <p className="pw-small pw-muted" data-testid="party-guest-needs-album">
          {t('party.guest.needsAlbum')}
        </p>
      ) : (
        <>
          <div className="pw-rows">
            <SwitchRow
              testId="party-guest-access-switch"
              label={t('party.guest.on')}
              note={t('party.guest.onNote')}
              checked={albumParty?.partyMode ?? false}
              disabled={busy || albumParty === null}
              onChange={(next) => void toggle(next)}
            />
          </div>
          {failed && <Notice tone="error"><p>{t('party.overview.saveFailed')}</p></Notice>}
        </>
      )}
    </Panel>
  );
}

/**
 * Setting the same evening up again.
 *
 * A host who runs a monthly party rebuilds the same thing every time. This
 * copies the DECISIONS and none of the history — the clone is a draft with its
 * own album, its own deck and brand-new links, and nobody who came to the party
 * being copied is in it. The photographs are SHARED through ordinary album
 * membership, so no byte is written twice; that is stated before the host
 * presses, because "duplicate" could otherwise mean a second copy of every
 * photograph.
 */
function DuplicatePanel({ party }: { party: Party }) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const navigate = useNavigate();
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);

  async function run() {
    setBusy(true); setFailed(false);
    try {
      const copy = await duplicateParty(party.id);
      // Straight into the copy: the host duplicated it in order to edit it.
      void navigate(`/parties/${copy.id}`);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setFailed(true);
    } finally { setBusy(false); }
  }

  return (
    <Panel
      title={t('party.duplicate.heading')}
      note={t('party.duplicate.help')}
      testId="party-duplicate"
      actions={(
        <Button busy={busy} data-testid="party-duplicate-start" onClick={() => void run()}>
          {busy ? t('party.duplicate.busy') : t('party.duplicate.start')}
        </Button>
      )}
    >
      <p className="pw-small pw-muted">{t('party.duplicate.excludes')}</p>
      {failed && <Notice tone="error"><p>{t('party.duplicate.failed')}</p></Notice>}
    </Panel>
  );
}

/**
 * Deleting the party, and the ONE place in the product that can.
 *
 * The confirmation is INLINE rather than a browser dialog, because the sentence
 * that matters — the album and the approved photographs survive — does not fit
 * in one and is exactly what the host is afraid of when they hesitate here. The
 * version travels with the request, so a party somebody else has edited
 * meanwhile is refused with its current state rather than torn down from a
 * stale read.
 */
function TeardownPanel({
  party, onPartyUpdated,
}: {
  party: Party;
  onPartyUpdated(next: Party): void;
}) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const navigate = useNavigate();
  const [asking, setAsking] = useState(false);
  const [busy, setBusy] = useState(false);
  const [state, setState] = useState<'idle' | 'conflict' | 'failed'>('idle');

  async function run() {
    setBusy(true); setState('idle');
    try {
      await tearDownParty(party.id, party.version);
      // The party no longer exists, so there is nothing left for this route to
      // load: go back to the list rather than re-fetching a 404.
      void navigate('/parties');
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      const body = (err as ApiError).body as { party?: Party } | undefined;
      if (err instanceof ApiError && err.status === 409 && body?.party) {
        onPartyUpdated(body.party);
        setState('conflict');
      } else {
        setState('failed');
      }
      setAsking(false);
    } finally { setBusy(false); }
  }

  return (
    <Panel
      tone="danger"
      title={t('party.teardown.heading')}
      note={t('party.teardown.help')}
      testId="party-teardown"
    >
      {/* Stated BEFORE the host commits, not in a receipt afterwards. */}
      <p className="pw-small pw-muted">{t('party.teardown.keeps')}</p>

      {!asking ? (
        <div className="pw-panel-actions">
          <Button
            tone="danger" data-testid="party-teardown-start"
            onClick={() => { setState('idle'); setAsking(true); }}
          >
            {t('party.teardown.start')}
          </Button>
        </div>
      ) : (
        <Notice
          tone="warn"
          testId="party-teardown-confirm"
          title={t('party.teardown.confirmQuestion', { title: party.title })}
          actions={(
            <>
              <Button
                tone="danger" busy={busy} data-testid="party-teardown-confirm-yes"
                onClick={() => void run()}
              >
                {busy ? t('party.teardown.busy') : t('party.teardown.confirm')}
              </Button>
              <Button disabled={busy} onClick={() => setAsking(false)}>
                {t('party.teardown.cancel')}
              </Button>
            </>
          )}
        >
          <p>{t('party.teardown.keeps')}</p>
        </Notice>
      )}

      {state === 'conflict' && (
        <Notice tone="warn" testId="party-teardown-conflict">
          <p>{t('party.teardown.conflict')}</p>
        </Notice>
      )}
      {state === 'failed' && (
        <Notice tone="error"><p>{t('party.teardown.failed')}</p></Notice>
      )}
    </Panel>
  );
}
