import { Link } from 'react-router';
import type { PartyInvitationRsvp } from '@nubarca/api-client';
import { useI18n, type MessageKey } from '../i18n';
import './PartyRsvp.css';

// "SONO QUI" — the group's own people arriving, on its personal invitation.
//
// While the party is live, each person of THIS group can be marked as arrived
// from the group's own link, and the group can take back its own mark. An
// arrival the host recorded at the door is shown and left alone: it is the
// host's record. Nobody else's arrival is ever on this page.
//
// "Entra nel Party" is the party's ordinary public page — the same one the
// room's QR opens, with the same powers and the same quotas. Following it is
// navigation, not identity: the phone becomes a browser at the party exactly
// as any other does, and nothing ties it to the names above.

export function PartySelfCheckInCard({
  invitation, partyUrl, busyGuestId, notice, onCheckIn, onUndo,
}: {
  invitation: PartyInvitationRsvp;
  partyUrl: string | null;
  busyGuestId: string | null;
  notice: MessageKey | null;
  onCheckIn(guestId: string): void;
  onUndo(guestId: string): void;
}) {
  const { t, formatDate } = useI18n();

  return (
    <section className="party-rsvp party-checkin" data-testid="party-checkin" aria-labelledby="party-checkin-title">
      <h2 className="party-rsvp-title" id="party-checkin-title">{t('partyRsvp.checkIn.heading')}</h2>
      {invitation.canCheckIn && <p className="party-rsvp-help">{t('partyRsvp.checkIn.help')}</p>}
      <ul className="party-checkin-people">
        {invitation.guests.map((guest) => (
          <li
            key={guest.id} className="party-checkin-person"
            data-testid={`party-checkin-person-${guest.id}`} data-arrived={guest.checkedInAt !== null}
          >
            <span className="party-checkin-name">
              <strong>{guest.name}</strong>
              {guest.isAdditionalGuest && <span className="party-rsvp-chip">+1</span>}
            </span>
            {guest.checkedInAt ? (
              <span className="party-checkin-state">
                <span>
                  {t('partyRsvp.checkIn.arrivedAt', { time: formatDate(guest.checkedInAt, { timeStyle: 'short' }) })}
                  {guest.checkInSource === 'owner' && <> · {t('partyRsvp.checkIn.byHost')}</>}
                </span>
                {invitation.canCheckIn && guest.checkInSource === 'invitation' && (
                  <button
                    type="button" className="party-rsvp-link" data-testid={`party-checkin-undo-${guest.id}`}
                    disabled={busyGuestId !== null} onClick={() => onUndo(guest.id)}
                  >
                    {t('partyRsvp.checkIn.undo')}
                  </button>
                )}
              </span>
            ) : invitation.canCheckIn && (
              <button
                type="button" className="party-checkin-here" data-testid={`party-checkin-here-${guest.id}`}
                disabled={busyGuestId !== null} onClick={() => onCheckIn(guest.id)}
              >
                {t('partyRsvp.checkIn.here')}
              </button>
            )}
          </li>
        ))}
      </ul>
      {notice && (
        <p className="party-rsvp-notice party-rsvp-notice--error" role="alert" data-testid="party-checkin-notice">
          {t(notice)}
        </p>
      )}
      {partyUrl && (
        <div className="party-checkin-enter-block">
          <Link className="party-rsvp-submit party-checkin-enter" to={partyUrl} data-testid="party-checkin-enter">
            {t('partyRsvp.checkIn.enter')}
          </Link>
          <p className="party-rsvp-help">{t('partyRsvp.checkIn.enterHelp')}</p>
        </div>
      )}
    </section>
  );
}
