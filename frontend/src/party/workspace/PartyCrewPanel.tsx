import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  PARTY_CREW_ASSIGNABLE_ROLES,
  createPartyCollaborator,
  getPartyCrew,
  revokePartyCollaborator,
  revokePartyCollaboratorDevice,
  rotatePartyCollaboratorInvite,
  updatePartyCollaborator,
  type PartyCollaborator,
  type PartyCollaboratorInvite,
  type PartyCrewOverview,
} from '@nubarca/api-client';
import { useAuth } from '../../auth/useAuth';
import { useI18n } from '../../i18n';
import type { MessageKey } from '../../i18n';
import { Badge, Button, Notice, Panel, PanelSkeleton, Row } from './ui';

// "CHI TI AIUTA" — the people who run the evening with the host.
//
// THE PANEL IS ABOUT PEOPLE, NOT ABOUT ACCESS. A host adds a name, an address
// and what that person is there to do; everything underneath — the link, the
// code, the devices, the capabilities — is the product's problem. The words
// "capability", "token", "grant", "challenge" and "collaborator id" appear
// nowhere a host can read them.
//
// THE LINK IS SHOWN ONCE. It comes back from the call that created it and is
// never re-read, so the panel holds it in memory for as long as the host is
// looking at it and offers a new one afterwards — which is also what stops the
// old one working. That is stated plainly rather than implied.
//
// TWO DEVICES, SAID OUT LOUD. "1/2" is not a diagnostic: it is how a host knows
// why their co-organizer's new phone will not pair, and the row next to it is
// how they fix it without removing the person.

type Draft = { displayName: string; email: string; roleKey: string };

const EMPTY: Draft = { displayName: '', email: '', roleKey: PARTY_CREW_ASSIGNABLE_ROLES[0] };

const ROLE_LABEL: Record<string, MessageKey> = {
  co_organizer: 'party.crew.role.coOrganizer',
  director: 'party.crew.role.director',
  dj: 'party.crew.role.dj',
  reception: 'party.crew.role.reception',
  honoree: 'party.crew.role.honoree',
};

const ROLE_HELP: Record<string, MessageKey> = {
  co_organizer: 'party.crew.role.coOrganizer.help',
  director: 'party.crew.role.director.help',
};

/** An error code from the server, as one sentence a host can act on. */
const FAILURE: Record<string, MessageKey> = {
  invalid_name: 'party.crew.error.name',
  invalid_email: 'party.crew.error.email',
  email_in_use: 'party.crew.error.emailInUse',
  invalid_role: 'party.crew.error.role',
  role_not_assignable: 'party.crew.error.role',
  version_conflict: 'party.crew.error.conflict',
  mail_unavailable: 'party.crew.error.mail',
};

export function PartyCrewPanel({ partyId }: { partyId: string }) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const [load, setLoad] = useState<'loading' | 'ready' | 'failed'>('loading');
  const [overview, setOverview] = useState<PartyCrewOverview | null>(null);
  const [adding, setAdding] = useState(false);
  // The freshly minted link, by collaborator. Never read back from the server.
  const [links, setLinks] = useState<Record<string, PartyCollaboratorInvite>>({});

  const reload = useCallback(async () => {
    try {
      setOverview(await getPartyCrew(partyId));
      setLoad('ready');
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setLoad('failed');
    }
  }, [partyId, invalidateAuth]);

  useEffect(() => { void reload(); }, [reload]);

  const remember = (invite: PartyCollaboratorInvite) =>
    setLinks((prev) => ({ ...prev, [invite.collaboratorId]: invite }));

  const forget = (collaboratorId: string) =>
    setLinks((prev) => {
      const next = { ...prev };
      delete next[collaboratorId];
      return next;
    });

  return (
    <Panel
      title={t('party.crew.heading')}
      note={t('party.crew.help')}
      testId="party-crew"
      actions={load === 'ready' && !adding ? (
        <Button data-testid="party-crew-add" onClick={() => setAdding(true)}>
          {t('party.crew.add')}
        </Button>
      ) : undefined}
    >
      {load === 'loading' && <PanelSkeleton rows={2} />}

      {load === 'failed' && (
        <Notice tone="error" testId="party-crew-failed">
          <p>{t('party.crew.loadFailed')}</p>
          <Button onClick={() => { setLoad('loading'); void reload(); }}>
            {t('party.console.retry')}
          </Button>
        </Notice>
      )}

      {load === 'ready' && overview && (
        <>
          {/* Party Crew IS an email. Saying so beats minting a link that
              nobody can finish using. */}
          {!overview.mailAvailable && (
            <Notice tone="warn" testId="party-crew-no-mail">
              <p>{t('party.crew.noMail')}</p>
            </Notice>
          )}

          {overview.collaborators.length === 0 && !adding && (
            <p className="pw-small pw-muted" data-testid="party-crew-empty">
              {t('party.crew.empty')}
            </p>
          )}

          {overview.collaborators.length > 0 && (
            <div className="pw-rows" data-testid="party-crew-list">
              {overview.collaborators.map((person) => (
                <CollaboratorRow
                  key={person.id}
                  partyId={partyId}
                  person={person}
                  roles={overview.assignableRoles}
                  mailAvailable={overview.mailAvailable}
                  link={links[person.id] ?? null}
                  onLink={remember}
                  onLinkDone={() => forget(person.id)}
                  onChanged={() => void reload()}
                />
              ))}
            </div>
          )}

          {adding && (
            <CollaboratorForm
              testId="party-crew-new"
              roles={overview.assignableRoles}
              mailAvailable={overview.mailAvailable}
              initial={EMPTY}
              submitLabel={t('party.crew.addSubmit')}
              onCancel={() => setAdding(false)}
              onSubmit={async (draft) => {
                const invite = await createPartyCollaborator(partyId, draft);
                remember(invite);
                setAdding(false);
                await reload();
              }}
            />
          )}
        </>
      )}
    </Panel>
  );
}

function CollaboratorRow({
  partyId, person, roles, mailAvailable, link, onLink, onLinkDone, onChanged,
}: {
  partyId: string;
  person: PartyCollaborator;
  roles: string[];
  mailAvailable: boolean;
  link: PartyCollaboratorInvite | null;
  onLink(invite: PartyCollaboratorInvite): void;
  onLinkDone(): void;
  onChanged(): void;
}) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const [editing, setEditing] = useState(false);
  const [removing, setRemoving] = useState(false);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState<MessageKey | null>(null);

  async function guarded(work: () => Promise<void>) {
    setBusy(true); setFailed(null);
    try {
      await work();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      const code = (err as ApiError).body as { error?: string } | undefined;
      setFailed(FAILURE[code?.error ?? ''] ?? 'party.overview.saveFailed');
    } finally { setBusy(false); }
  }

  const roleLabel = t(ROLE_LABEL[person.roleKey] ?? 'party.crew.role.other');

  return (
    <div className="pw-crew" data-testid={`party-crew-row-${person.id}`}>
      <Row
        label={person.displayName}
        note={(
          <>
            {roleLabel}
            {' · '}
            {/* The host chose this address and must be able to check it. */}
            <span className="pw-crew-email">{person.email}</span>
          </>
        )}
      >
        <Badge
          kind={person.activeDevices >= person.maxDevices ? 'warn' : 'plain'}
          testId={`party-crew-devices-${person.id}`}
        >
          {t('party.crew.devices', {
            used: person.activeDevices, max: person.maxDevices,
          })}
        </Badge>
      </Row>

      {!editing && !removing && (
        <div className="pw-crew-actions">
          <Button
            data-testid={`party-crew-edit-${person.id}`}
            onClick={() => setEditing(true)}
          >
            {t('party.crew.edit')}
          </Button>
          <Button
            busy={busy}
            disabled={!mailAvailable}
            data-testid={`party-crew-relink-${person.id}`}
            onClick={() => void guarded(async () => {
              onLink(await rotatePartyCollaboratorInvite(partyId, person.id));
              onChanged();
            })}
          >
            {person.hasPendingInvite ? t('party.crew.newLink') : t('party.crew.link')}
          </Button>
          <Button
            tone="quiet"
            data-testid={`party-crew-remove-${person.id}`}
            onClick={() => setRemoving(true)}
          >
            {t('party.crew.remove')}
          </Button>
        </div>
      )}

      {/* The link, exactly once, on the three channels every other party link
          uses. There is no way back to it: that is the point. */}
      {link && (
        <div className="pw-crew-link" data-testid={`party-crew-link-${person.id}`}>
          <p className="pw-small">{t('party.crew.linkReady', { name: person.displayName })}</p>
          <CrewLink url={link.inviteUrl} />
          <p className="pw-small pw-muted">{t('party.crew.linkOnce')}</p>
          <Button onClick={onLinkDone} data-testid={`party-crew-link-done-${person.id}`}>
            {t('party.crew.linkDone')}
          </Button>
        </div>
      )}

      {person.devices.length > 0 && (
        <ul className="pw-crew-devices" data-testid={`party-crew-device-list-${person.id}`}>
          {person.devices.map((device) => (
            <li key={device.grantId} className="pw-crew-device">
              <span className="pw-crew-device-text">
                <span>{device.label}</span>
                <span className="pw-small pw-muted">
                  {t('party.crew.device.paired', { when: formatWhen(device.pairedAt) })}
                </span>
              </span>
              <Button
                tone="quiet"
                busy={busy}
                data-testid={`party-crew-device-revoke-${device.grantId}`}
                onClick={() => void guarded(async () => {
                  await revokePartyCollaboratorDevice(partyId, person.id, device.grantId);
                  onChanged();
                })}
              >
                {t('party.crew.device.remove')}
              </Button>
            </li>
          ))}
        </ul>
      )}

      {removing && (
        <Notice tone="warn" testId={`party-crew-confirm-${person.id}`}>
          <p>{t('party.crew.removeConfirm', { name: person.displayName })}</p>
          <div className="pw-crew-actions">
            <Button
              tone="danger"
              busy={busy}
              data-testid={`party-crew-remove-confirm-${person.id}`}
              onClick={() => void guarded(async () => {
                await revokePartyCollaborator(partyId, person.id);
                setRemoving(false);
                onChanged();
              })}
            >
              {t('party.crew.removeYes')}
            </Button>
            <Button onClick={() => setRemoving(false)}>{t('party.crew.cancel')}</Button>
          </div>
        </Notice>
      )}

      {editing && (
        <CollaboratorForm
          testId={`party-crew-edit-form-${person.id}`}
          roles={roles}
          mailAvailable={mailAvailable}
          initial={{
            displayName: person.displayName, email: person.email, roleKey: person.roleKey,
          }}
          submitLabel={t('party.overview.save')}
          onCancel={() => setEditing(false)}
          onSubmit={async (draft) => {
            await updatePartyCollaborator(partyId, person.id, {
              ...draft, version: person.version,
            });
            setEditing(false);
            onChanged();
          }}
        />
      )}

      {failed && <Notice tone="error"><p>{t(failed)}</p></Notice>}
    </div>
  );
}

function CollaboratorForm({
  testId, roles, mailAvailable, initial, submitLabel, onCancel, onSubmit,
}: {
  testId: string;
  roles: string[];
  mailAvailable: boolean;
  initial: Draft;
  submitLabel: string;
  onCancel(): void;
  onSubmit(draft: Draft): Promise<void>;
}) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const [draft, setDraft] = useState<Draft>(initial);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState<MessageKey | null>(null);

  const incomplete = draft.displayName.trim() === '' || draft.email.trim() === '';

  async function submit() {
    if (incomplete) return;
    setBusy(true); setFailed(null);
    try {
      await onSubmit({
        displayName: draft.displayName.trim(),
        email: draft.email.trim(),
        roleKey: draft.roleKey,
      });
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      const code = (err as ApiError).body as { error?: string } | undefined;
      setFailed(FAILURE[code?.error ?? ''] ?? 'party.overview.saveFailed');
    } finally { setBusy(false); }
  }

  const offered = roles.length > 0 ? roles : [...PARTY_CREW_ASSIGNABLE_ROLES];

  return (
    <form
      className="pw-crew-form"
      data-testid={testId}
      onSubmit={(event) => { event.preventDefault(); void submit(); }}
    >
      <label className="pw-field">
        <span className="pw-field-label">{t('party.crew.field.name')}</span>
        <input
          value={draft.displayName}
          data-testid={`${testId}-name`}
          onChange={(event) => setDraft((d) => ({ ...d, displayName: event.target.value }))}
        />
      </label>

      <label className="pw-field">
        <span className="pw-field-label">{t('party.crew.field.email')}</span>
        <input
          type="email"
          inputMode="email"
          autoComplete="email"
          value={draft.email}
          data-testid={`${testId}-email`}
          onChange={(event) => setDraft((d) => ({ ...d, email: event.target.value }))}
        />
        <span className="pw-field-help">{t('party.crew.field.emailHelp')}</span>
      </label>

      {/* The stacked option pattern the workspace already uses wherever a
          choice needs a sentence: each role says what it grants, so a host is
          not asked to guess what "Regista" means. */}
      <fieldset className="pw-options">
        <legend>{t('party.crew.field.role')}</legend>
        {offered.map((role) => (
          <label key={role} className="pw-option" data-testid={`${testId}-role-${role}`}>
            <input
              type="radio"
              name={`${testId}-role`}
              value={role}
              checked={draft.roleKey === role}
              onChange={() => setDraft((d) => ({ ...d, roleKey: role }))}
            />
            <span>
              <span className="pw-option-title">
                {t(ROLE_LABEL[role] ?? 'party.crew.role.other')}
              </span>
              {ROLE_HELP[role] && (
                <span className="pw-option-help">{t(ROLE_HELP[role])}</span>
              )}
            </span>
          </label>
        ))}
      </fieldset>

      {!mailAvailable && (
        <p className="pw-small pw-muted">{t('party.crew.noMail')}</p>
      )}

      <div className="pw-crew-actions">
        <Button tone="primary" type="submit" busy={busy} disabled={incomplete}>
          {submitLabel}
        </Button>
        <Button onClick={onCancel}>{t('party.crew.cancel')}</Button>
      </div>

      {failed && <Notice tone="error"><p>{t(failed)}</p></Notice>}
    </form>
  );
}

/**
 * The link itself, once.
 *
 * A read-only field plus a copy button, and no QR: this link is handed to ONE
 * named person through whatever channel the host already talks to them on, and
 * a code posted on a wall is the opposite of what it is for. Copy can fail —
 * clipboard access needs a secure context — so the field is selectable and the
 * failure says to copy it by hand instead of pretending it worked.
 */
function CrewLink({ url }: { url: string }) {
  const { t } = useI18n();
  const field = useRef<HTMLInputElement>(null);
  const [copy, setCopy] = useState<'idle' | 'copied' | 'failed'>('idle');

  return (
    <div className="pw-crew-link-row">
      <input
        ref={field}
        readOnly
        value={url}
        aria-label={t('party.crew.linkLabel')}
        data-testid="party-crew-link-url"
        onFocus={(event) => event.currentTarget.select()}
      />
      <Button
        data-testid="party-crew-link-copy"
        onClick={() => {
          void navigator.clipboard.writeText(url)
            .then(() => setCopy('copied'))
            .catch(() => { setCopy('failed'); field.current?.select(); });
        }}
      >
        {t('party.crew.linkCopy')}
      </Button>
      <span aria-live="polite" className="pw-small pw-muted">
        {copy === 'copied' && t('party.crew.linkCopied')}
        {copy === 'failed' && t('party.crew.linkCopyFailed')}
      </span>
    </div>
  );
}

// A date, in the reader's locale, with no time: "paired on 14 September" is
// what a person needs to tell two phones apart, and the minute is noise.
function formatWhen(iso: string): string {
  const parsed = new Date(iso);
  return Number.isNaN(parsed.getTime())
    ? iso
    : parsed.toLocaleDateString(undefined, { day: 'numeric', month: 'short' });
}
