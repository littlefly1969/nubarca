import { useState } from 'react';
import { Link } from 'react-router';
import {
  peoplePreview,
  primaryInvitationAction,
  primaryInvitationLabel,
  type GuestDirectoryGroupItem,
  type GuestDirectoryOtherItem,
  type GuestDirectoryPerson,
  type InvitationPrimaryAction,
  type InvitationPrimaryLabel,
} from '@nubarca/api-client';
import { useI18n, type MessageKey } from '../i18n';
import { countsPhrase, formatWhen, invitationStatusLine, peopleInReadingOrder } from './guestConsoleFormat';

// ONE GROUP, AS A CARD — a unit of the guest list, not a row of a table.
//
// Before the party it says who the invitation is for, how they answered, and
// where the invitation itself stands; its one primary action shares that
// invitation, and everything else is in the menu. From the moment the party is
// live the same card becomes the door: its people, and one tap each to record
// that they arrived — no opening the group, no going through the RSVP first.
//
// THREE LEVELS, ALWAYS IN THE SAME ORDER: the recommended action, Dettagli,
// and the menu.
//
//   [ Azione primaria ]  what to do next, chosen by primaryInvitationLabel
//   [ Dettagli ]         everything about this group — NEVER only in the menu,
//                        because reading a group is not a secondary action and
//                        a host should not have to guess which icon hides it
//   [ ⋮ ]                the other channels, the link, editing, deleting
//
// The first can be absent (invitations closed, no link to share) and in Live
// there is none at all — the card's work there is the per-person buttons, and
// a third generic primary above them would compete with the door. The other
// two are on every card of both phases.
//
// A group can hold thirty people and a card cannot show thirty rows, so it
// shows the ones that matter now — whoever the search matched, then whoever is
// still expected — and offers the rest.

const PEOPLE_ON_A_CARD = 4;

export function GuestGroupCard({
  item, live, selected, busy, personBusy, detailHref, onPrimary, onMenu, onCheckIn, onUndo,
}: {
  item: GuestDirectoryGroupItem;
  /** The party is live or over: the card records arrivals. */
  live: boolean;
  selected: boolean;
  /** A request is in flight for this group, whatever it was. */
  busy: boolean;
  /** …and for one of its people: an arrival is recorded per person, not per group. */
  personBusy(guestId: string): boolean;
  detailHref: string;
  onPrimary(action: InvitationPrimaryAction): void;
  onMenu(): void;
  onCheckIn(person: GuestDirectoryPerson): void;
  onUndo(person: GuestDirectoryPerson): void;
}) {
  const { t, formatDate } = useI18n();
  const [expanded, setExpanded] = useState(false);

  const people = peopleInReadingOrder(item.people, live);
  const shown = live && !expanded ? people.slice(0, PEOPLE_ON_A_CARD) : people;
  const hidden = people.length - shown.length;
  const primary = primaryInvitationAction(item);
  const primaryLabel = primaryInvitationLabel(item);
  const line = invitationStatusLine(item.invitation, t, formatDate);
  const preview = peoplePreview(item.people);

  return (
    // A card, not a list item: the list positions its own rows, and the row is
    // what the virtualizer measures.
    <div
      className="guest-card" data-testid={`guest-group-${item.groupId}`}
      data-selected={selected} data-kind="group"
    >
      <div className="guest-card-head">
        <h3 className="guest-card-title">
          <Link
            to={detailHref} state={{ guestDetail: true }} className="guest-card-open"
            data-testid={`guest-open-${item.groupId}`}
          >
            {item.label}
          </Link>
        </h3>
        {live ? (
          <span className="guest-card-arrived" data-testid={`guest-arrived-${item.groupId}`}>
            {t('party.console.card.arrived', { arrived: item.counts.arrived, people: item.people.length })}
          </span>
        ) : (
          <span
            className={`guest-card-line${line.problem ? ' is-problem' : ''}`}
            data-testid={`guest-invite-${item.groupId}`}
          >
            {line.text}
          </span>
        )}
      </div>

      {live ? (
        <ul className="guest-card-people-list">
          {shown.map((person) => (
            <GuestPersonRow
              key={person.guestId} person={person} busy={busy || personBusy(person.guestId)}
              onCheckIn={() => onCheckIn(person)} onUndo={() => onUndo(person)}
            />
          ))}
        </ul>
      ) : (
        <>
          <p className="guest-card-people">
            {preview.names.join(' · ')}
            {preview.more > 0 && ` · +${preview.more}`}
          </p>
          <p className="guest-card-counts muted">{countsPhrase(item.counts, item.people.length, t)}</p>
        </>
      )}

      <div className="guest-card-actions">
        {live && hidden > 0 && (
          <button
            type="button" className="row-action guest-card-expand"
            data-testid={`guest-expand-${item.groupId}`} onClick={() => setExpanded(true)}
          >
            {t('party.console.card.showAll', { count: people.length })}
          </button>
        )}
        {live && expanded && people.length > PEOPLE_ON_A_CARD && (
          <button type="button" className="row-action" onClick={() => setExpanded(false)}>
            {t('party.console.card.showFewer')}
          </button>
        )}
        {!live && primary !== null && primaryLabel !== null && (
          <button
            type="button" className="row-action-primary guest-card-primary"
            data-testid={`guest-primary-${item.groupId}`} disabled={busy}
            onClick={() => onPrimary(primary)}
          >
            {t(PRIMARY_LABEL_KEYS[primaryLabel])}
          </button>
        )}
        {/* Second in the order and on every card, both phases. */}
        <Link
          to={detailHref} state={{ guestDetail: true }} className="row-action guest-card-details"
          aria-label={t('party.console.card.details', { label: item.label })}
          data-testid={`guest-details-${item.groupId}`}
        >
          {t('party.console.action.details')}
        </Link>
        <button
          type="button" className="icon-button guest-card-menu"
          aria-label={t('party.console.card.more', { label: item.label })}
          data-testid={`guest-menu-${item.groupId}`} disabled={busy} onClick={onMenu}
        >
          ⋮
        </button>
      </div>
    </div>
  );
}

/**
 * What the primary button says. The RULE lives in the contract
 * (`primaryInvitationLabel`) and is tested there; this is only the wording,
 * which has to be unambiguous about which email it is about to send — "Invia
 * invito" the first time this link goes out, "Invia di nuovo via email"
 * afterwards, never a bare "Invia di nuovo" that could mean any channel.
 */
const PRIMARY_LABEL_KEYS: Record<InvitationPrimaryLabel, MessageKey> = {
  whatsapp: 'party.console.action.whatsapp',
  copy: 'party.console.action.copy',
  email_first: 'party.console.action.email',
  email_again: 'party.console.action.emailAgain',
};

function GuestPersonRow({
  person, busy, onCheckIn, onUndo,
}: {
  person: GuestDirectoryPerson;
  busy: boolean;
  onCheckIn(): void;
  onUndo(): void;
}) {
  const { t, formatDate } = useI18n();
  const arrived = person.checkedInAt !== null;

  return (
    <li
      className="guest-person" data-testid={`guest-person-${person.guestId}`}
      data-arrived={arrived} data-matched={person.matched}
    >
      <span className="guest-person-who">
        <span className="guest-person-name">
          {person.name}
          {person.isAdditionalGuest && <span className="guest-chip">{t('party.console.plusOne')}</span>}
        </span>
        <span className="guest-person-state muted">
          {t(`party.console.rsvp.${person.rsvpStatus}` as MessageKey)}
          {' · '}
          {arrived
            ? t('party.console.arrival.at', { time: formatDate(person.checkedInAt!, { timeStyle: 'short' }) })
            : t('party.console.arrival.none')}
          {person.checkInSource === 'invitation' && <> · {t('party.console.arrival.bySelf')}</>}
        </span>
      </span>
      {arrived ? (
        <button
          type="button" className="row-action guest-person-action"
          aria-label={t('party.console.undoFor', { name: person.name })}
          data-testid={`guest-undo-${person.guestId}`} disabled={busy} onClick={onUndo}
        >
          {t('party.console.undo')}
        </button>
      ) : (
        <button
          type="button" className="row-action-primary guest-person-action"
          aria-label={t('party.console.checkInFor', { name: person.name })}
          data-testid={`guest-checkin-${person.guestId}`} disabled={busy} onClick={onCheckIn}
        >
          {t('party.console.checkIn')}
        </button>
      )}
    </li>
  );
}

/** Somebody at the door who is not on the guest list: a name, a time, a menu. */
export function GuestOtherCard({
  item, busy, onMenu,
}: {
  item: GuestDirectoryOtherItem;
  busy: boolean;
  onMenu(): void;
}) {
  const { t, formatDate } = useI18n();

  return (
    // Two lines and a menu: somebody at the door is a name and a time, and a
    // card that reserved a row for one button was mostly empty space.
    <div className="guest-card guest-card--other" data-testid={`guest-other-${item.id}`} data-kind="other">
      <div className="guest-card-head">
        <h3 className="guest-card-title">{item.name}</h3>
        <span className="guest-card-aside">
          <span className="guest-chip">{t('party.console.other.chip')}</span>
          <button
            type="button" className="icon-button guest-card-menu"
            aria-label={t('party.console.other.more', { name: item.name })}
            data-testid={`guest-other-menu-${item.id}`} disabled={busy} onClick={onMenu}
          >
            ⋮
          </button>
        </span>
      </div>
      <p className="guest-card-counts muted">
        {/* The day and the hour once: "oggi 09:22", not "alle 09:22 · oggi 09:22". */}
        {t('party.console.arrival.when', { when: formatWhen(item.checkedInAt, t, formatDate) })}
      </p>
    </div>
  );
}
