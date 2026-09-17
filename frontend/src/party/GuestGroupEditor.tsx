import { useState } from 'react';
import {
  PARTY_INVITATION_LIMITS,
  isPlausibleEmail,
  normalizeText,
  type PartyGuestListMinimal,
  type PartyInvitationGroup,
  type PartyInvitationGroupWrite,
} from '@nubarca/api-client';
import { Modal } from '../components/Overlay';
import { usePartyApi } from './workspace/partyApi';
import { useI18n } from '../i18n';

// WHO IS INVITED, AS A FORM SOMEBODY FILLS IN ON A PHONE.
//
// One column, one question at a time: what to call the group, where its
// invitation goes, who is in it. A person is a NAME — their address and number
// are behind a disclosure, because most guests need neither — and the +1
// allowance is a stepper rather than a number field nobody can hit. The save
// action is pinned to the bottom of the sheet, so it is reachable without
// scrolling back up through the people.
//
// The form states the NAMED guests whole; the +1s a group added on its own
// invitation are never round-tripped through here, so saving cannot lose one.

interface PersonDraft {
  key: string;
  id?: string;
  name: string;
  email: string;
  phone: string;
  /** The host asked for this person's own address and number. */
  open: boolean;
}

let personKey = 0;
const blankPerson = (): PersonDraft => ({ key: `p-${(personKey += 1)}`, name: '', email: '', phone: '', open: false });

export function GuestGroupEditor({
  partyId, group, onSaved, onClose, onRefused,
}: {
  partyId: string;
  /** The group being edited, or null for a new one. */
  group: PartyInvitationGroup | null;
  onSaved(result: PartyGuestListMinimal, label: string): void;
  onClose(): void;
  onRefused(err: unknown): void;
}) {
  const { t } = useI18n();
  const api = usePartyApi();
  const [label, setLabel] = useState(group?.label ?? '');
  const [email, setEmail] = useState(group?.recipientEmail ?? '');
  const [phone, setPhone] = useState(group?.phone ?? '');
  const [maxAdditional, setMaxAdditional] = useState(group?.maxAdditionalGuests ?? 0);
  const [people, setPeople] = useState<PersonDraft[]>(() => (group
    ? group.guests.filter((guest) => !guest.isAdditionalGuest).map((guest) => ({
      key: guest.id, id: guest.id, name: guest.name,
      email: guest.email ?? '', phone: guest.phone ?? '',
      open: Boolean(guest.email || guest.phone),
    }))
    : [blankPerson()]));
  const [saving, setSaving] = useState(false);

  const emailChanged = group !== null && email.trim().toLowerCase() !== group.recipientEmail.toLowerCase();
  const valid = normalizeText(label) !== null
    && isPlausibleEmail(email)
    && people.length > 0
    && people.length <= PARTY_INVITATION_LIMITS.namedGuests
    && people.every((person) => normalizeText(person.name) !== null
      && (person.email.trim() === '' || isPlausibleEmail(person.email)))
    && maxAdditional >= 0 && maxAdditional <= PARTY_INVITATION_LIMITS.additionalGuests;

  const editPerson = (key: string, change: Partial<PersonDraft>) =>
    setPeople((current) => current.map((person) => (person.key === key ? { ...person, ...change } : person)));

  async function save() {
    if (!valid || saving) return;
    const body: PartyInvitationGroupWrite = {
      label: label.trim(),
      recipientEmail: email.trim(),
      phone: normalizeText(phone),
      maxAdditionalGuests: maxAdditional,
      guests: people.map((person) => ({
        ...(person.id ? { id: person.id } : {}),
        name: person.name.trim(),
        email: normalizeText(person.email),
        phone: normalizeText(person.phone),
      })),
      version: group?.version ?? 0,
    };
    setSaving(true);
    try {
      const result = group
        ? await api.updatePartyInvitationGroup(partyId, group.id, body)
        : await api.createPartyInvitationGroup(partyId, body);
      onSaved(result, body.label);
    } catch (err) {
      onRefused(err);
    } finally {
      setSaving(false);
    }
  }

  return (
    <Modal
      title={group ? t('party.console.editor.editTitle', { label: group.label }) : t('party.console.editor.createTitle')}
      onClose={onClose}
      dismissable={!saving}
      testId="guest-editor"
      className="guest-sheet guest-sheet--form"
      footer={(
        <>
          <button type="button" className="row-action" disabled={saving} onClick={onClose}>
            {t('party.console.cancel')}
          </button>
          <button
            type="submit" form="guest-editor-form" className="row-action-primary"
            data-testid="guest-editor-save" disabled={saving || !valid}
          >
            {saving ? t('party.console.editor.saving') : t('party.console.editor.save')}
          </button>
        </>
      )}
    >
      <form
        id="guest-editor-form" className="guest-form"
        onSubmit={(e) => { e.preventDefault(); void save(); }}
      >
        <fieldset className="guest-form-block">
          <legend>{t('party.console.editor.group')}</legend>
          <label className="party-field">
            <span>{t('party.console.editor.label')}</span>
            <input
              value={label} disabled={saving} aria-label={t('party.console.editor.label')}
              placeholder={t('party.console.editor.labelHelp')} autoComplete="off"
              onChange={(e) => setLabel(e.target.value)}
            />
          </label>
          <label className="party-field">
            <span>{t('party.console.editor.email')}</span>
            <input
              type="email" inputMode="email" autoComplete="email" value={email} disabled={saving}
              aria-label={t('party.console.editor.email')} onChange={(e) => setEmail(e.target.value)}
            />
          </label>
          <p className="muted guest-form-help">{t('party.console.editor.emailHelp')}</p>
          {emailChanged && (
            <p className="inline-error" role="note" data-testid="guest-editor-email-changed">
              {t('party.console.editor.emailChanged')}
            </p>
          )}
          <label className="party-field">
            <span>{t('party.console.editor.phone')}</span>
            <input
              type="tel" inputMode="tel" autoComplete="tel" value={phone} disabled={saving}
              aria-label={t('party.console.editor.phone')} placeholder="+39 333 123 4567"
              onChange={(e) => setPhone(e.target.value)}
            />
          </label>
          <p className="muted guest-form-help">{t('party.console.editor.phoneHelp')}</p>
        </fieldset>

        <fieldset className="guest-form-block">
          <legend>{t('party.console.editor.people')}</legend>
          {people.map((person, index) => (
            <div key={person.key} className="guest-form-person" data-testid="guest-editor-person">
              <div className="guest-form-person-row">
                <input
                  className="guest-form-name" value={person.name} disabled={saving}
                  aria-label={t('party.console.editor.personName', { index: index + 1 })}
                  placeholder={t('party.console.editor.personPlaceholder')} autoComplete="off"
                  onChange={(e) => editPerson(person.key, { name: e.target.value })}
                />
                {people.length > 1 && (
                  <button
                    type="button" className="icon-button guest-form-remove" disabled={saving}
                    aria-label={t('party.console.editor.removePerson', { name: person.name || String(index + 1) })}
                    onClick={() => setPeople((current) => current.filter((p) => p.key !== person.key))}
                  >
                    ×
                  </button>
                )}
              </div>
              {person.open ? (
                <div className="guest-form-person-details">
                  <input
                    type="email" inputMode="email" value={person.email} disabled={saving}
                    aria-label={`${t('party.console.editor.personEmail')} ${index + 1}`}
                    placeholder={t('party.console.editor.personEmail')}
                    onChange={(e) => editPerson(person.key, { email: e.target.value })}
                  />
                  <input
                    type="tel" inputMode="tel" value={person.phone} disabled={saving}
                    aria-label={`${t('party.console.editor.personPhone')} ${index + 1}`}
                    placeholder={t('party.console.editor.personPhone')}
                    onChange={(e) => editPerson(person.key, { phone: e.target.value })}
                  />
                </div>
              ) : (
                <button
                  type="button" className="guest-form-expand" disabled={saving}
                  aria-label={t('party.console.editor.personDetailsFor', { name: person.name || String(index + 1) })}
                  onClick={() => editPerson(person.key, { open: true })}
                >
                  {t('party.console.editor.personDetails')}
                </button>
              )}
            </div>
          ))}
          {people.length < PARTY_INVITATION_LIMITS.namedGuests && (
            <button
              type="button" className="row-action" data-testid="guest-editor-add-person" disabled={saving}
              onClick={() => setPeople((current) => [...current, blankPerson()])}
            >
              {t('party.console.editor.addPerson')}
            </button>
          )}
        </fieldset>

        <fieldset className="guest-form-block">
          <legend>{t('party.console.editor.plusOnes')}</legend>
          <div className="guest-stepper">
            <button
              type="button" className="icon-button" disabled={saving || maxAdditional <= 0}
              aria-label={t('party.console.editor.fewer')} data-testid="guest-editor-plus-ones-fewer"
              onClick={() => setMaxAdditional((n) => Math.max(0, n - 1))}
            >
              −
            </button>
            <output data-testid="guest-editor-plus-ones" aria-live="polite">{maxAdditional}</output>
            <button
              type="button" className="icon-button"
              disabled={saving || maxAdditional >= PARTY_INVITATION_LIMITS.additionalGuests}
              aria-label={t('party.console.editor.more')} data-testid="guest-editor-plus-ones-more"
              onClick={() => setMaxAdditional((n) => Math.min(PARTY_INVITATION_LIMITS.additionalGuests, n + 1))}
            >
              +
            </button>
          </div>
          <p className="muted guest-form-help">{t('party.console.editor.plusOnesHelp')}</p>
          {group && group.additionalGuestsUsed > 0 && (
            <p className="muted">
              {t('party.console.editor.plusOnesKept', { count: group.additionalGuestsUsed })}
            </p>
          )}
        </fieldset>

        {!valid && <p className="muted" data-testid="guest-editor-invalid">{t('party.console.editor.invalid')}</p>}
      </form>
    </Modal>
  );
}
