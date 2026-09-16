import { useCallback, useEffect, useState } from 'react';
import {
  ApiError,
  getPartyInvitationGroup,
  type GuestDirectoryPerson,
  type InvitationShareChannel,
  type PartyInvitationGroupDetail,
} from '@nubarca/api-client';
import { useI18n, type MessageKey } from '../i18n';
import { historyLine, invitationStatusLine } from './guestConsoleFormat';
import { GuestSharedLink } from './GuestSharedLink';
import type { ShareOutcome } from './guestConsoleActions';

// ONE GROUP, IN FULL — read when the host asks for it, never with the list.
//
// It is the same surface on both layouts: a pane beside the list on a wide
// screen, a full-screen sheet over it on a phone. The invitation comes first
// (where it stands, and the three ways to hand it over), then the people with
// their answers and their arrivals, then contacts, then what the group replied
// to the host's questions.
//
// The personal link itself appears only as the answer to a share the host just
// asked for: it is passed to WhatsApp or the clipboard and never stored,
// rendered into a URL or written to a log.

export interface GuestDetailActions {
  share(group: DetailTarget, channel: InvitationShareChannel): void;
  email(group: DetailTarget): void;
  remind(group: DetailTarget): void;
  rotate(group: DetailTarget): void;
  remove(group: DetailTarget): void;
  edit(detail: PartyInvitationGroupDetail): void;
  checkIn(group: DetailTarget, person: GuestDirectoryPerson): void;
  undo(group: DetailTarget, person: GuestDirectoryPerson): void;
}

export interface DetailTarget {
  groupId: string;
  label: string;
  version: number;
}

export function GuestGroupDetail({
  partyId, groupId, refreshKey, busy, share, actions, onLoaded, onUnauthorized,
}: {
  partyId: string;
  groupId: string;
  /** Changes whenever something this group owns was written. */
  refreshKey: number;
  busy: boolean;
  /** The link this group's last share handed over, for passing on again. */
  share: ShareOutcome | null;
  actions: GuestDetailActions;
  onLoaded(detail: PartyInvitationGroupDetail): void;
  onUnauthorized(): void;
}) {
  const { t, formatDate } = useI18n();
  const [detail, setDetail] = useState<PartyInvitationGroupDetail | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    const ctrl = new AbortController();
    setFailed(false);
    getPartyInvitationGroup(partyId, groupId, ctrl.signal)
      .then((next) => {
        setDetail(next);
        onLoaded(next);
      })
      .catch((err: unknown) => {
        if (ctrl.signal.aborted) return;
        if (err instanceof ApiError && err.status === 401) { onUnauthorized(); return; }
        setFailed(true);
      });
    return () => ctrl.abort();
  }, [partyId, groupId, refreshKey, onLoaded, onUnauthorized]);

  const dietaryOf = useCallback((guestId: string) =>
    detail?.group.guests.find((guest) => guest.id === guestId)?.dietaryNotes ?? null, [detail]);

  if (failed) {
    return (
      <div className="guest-detail" data-testid="guest-detail">
        <p className="inline-error" role="alert">{t('party.console.detail.loadError')}</p>
      </div>
    );
  }
  if (!detail) {
    return (
      <div className="guest-detail" data-testid="guest-detail">
        <p role="status">{t('party.console.detail.loading')}</p>
      </div>
    );
  }

  const { group, item } = detail;
  const target: DetailTarget = { groupId, label: group.label, version: group.version };
  const live = detail.partyStatus === 'live' || detail.partyStatus === 'ended';
  const line = invitationStatusLine(group.delivery, t, formatDate);

  return (
    <div className="guest-detail" data-testid="guest-detail" data-group={groupId}>
      <section className="guest-detail-section" aria-labelledby={`guest-detail-invitation-${groupId}`}>
        <h4 id={`guest-detail-invitation-${groupId}`}>{t('party.console.detail.invitation')}</h4>
        <p className={`guest-card-line${line.problem ? ' is-problem' : ''}`} data-testid="guest-detail-line">
          {line.text}
        </p>

        {(group.canShare || group.canSend) && (
          <div className="guest-detail-actions">
            {group.canShare && (
              <button
                type="button" className="row-action-primary" data-testid="guest-detail-whatsapp"
                disabled={busy} onClick={() => actions.share(target, 'whatsapp')}
              >
                {t('party.console.action.whatsapp')}
              </button>
            )}
            {group.canSend && (
              <button
                type="button" className="row-action" data-testid="guest-detail-email"
                disabled={busy} onClick={() => actions.email(target)}
              >
                {t(group.delivery.state === 'not_sent'
                  ? 'party.console.action.email'
                  : 'party.console.action.emailAgain')}
              </button>
            )}
            {group.canShare && (
              <button
                type="button" className="row-action" data-testid="guest-detail-copy"
                disabled={busy} onClick={() => actions.share(target, 'copy')}
              >
                {t('party.console.action.copy')}
              </button>
            )}
            {group.canRemind && (
              <button
                type="button" className="row-action" data-testid="guest-detail-remind"
                disabled={busy} onClick={() => actions.remind(target)}
              >
                {t('party.console.action.remind')}
              </button>
            )}
          </div>
        )}

        {group.canShare && (
          <p className="muted guest-share-note">
            {t(detail.whatsappDirect ? 'party.console.share.direct' : 'party.console.share.chooser')}
          </p>
        )}
        {!detail.mailAvailable && (
          <p className="muted" role="note" data-testid="guest-detail-mail-unavailable">
            {t('party.console.mailUnavailable')}
          </p>
        )}
        {!detail.shareAvailable && (
          <p className="muted" role="note" data-testid="guest-detail-share-unavailable">
            {t('party.console.shareUnavailable')}
          </p>
        )}

        {share && share.groupId === groupId && <GuestSharedLink share={share} label={group.label} />}

        {detail.history.length > 0 && (
          <details className="guest-history">
            <summary>{t('party.console.detail.history')}</summary>
            <ul className="guest-history-list" data-testid="guest-detail-history">
              {detail.history.map((entry) => (
                <li key={`${entry.channel}-${entry.createdAt}-${entry.kind}`}>
                  {historyLine(entry, t, formatDate).text}
                  {!entry.currentLink && <> · {t('party.console.detail.previousLink')}</>}
                </li>
              ))}
            </ul>
          </details>
        )}
      </section>

      <section className="guest-detail-section" aria-labelledby={`guest-detail-people-${groupId}`}>
        <h4 id={`guest-detail-people-${groupId}`}>{t('party.console.detail.people')}</h4>
        <ul className="guest-card-people-list">
          {item.people.map((person) => {
            const notes = dietaryOf(person.guestId);
            const arrived = person.checkedInAt !== null;
            return (
              <li
                key={person.guestId} className="guest-person"
                data-testid={`guest-detail-person-${person.guestId}`} data-arrived={arrived}
              >
                <span className="guest-person-who">
                  <span className="guest-person-name">
                    {person.name}
                    {person.isAdditionalGuest && <span className="guest-chip">{t('party.console.plusOne')}</span>}
                    <span className="guest-chip" data-status={person.rsvpStatus}>
                      {t(`party.console.rsvp.${person.rsvpStatus}` as MessageKey)}
                    </span>
                  </span>
                  {live && (
                    <span className="guest-person-state muted">
                      {arrived
                        ? t('party.console.arrival.at', {
                          time: formatDate(person.checkedInAt!, { timeStyle: 'short' }),
                        })
                        : t('party.console.arrival.none')}
                      {person.checkInSource === 'invitation' && <> · {t('party.console.arrival.bySelf')}</>}
                    </span>
                  )}
                  {notes && <span className="guest-person-state muted">{t('party.console.detail.dietary', { notes })}</span>}
                </span>
                {live && (arrived ? (
                  <button
                    type="button" className="row-action guest-person-action"
                    aria-label={t('party.console.undoFor', { name: person.name })}
                    data-testid={`guest-detail-undo-${person.guestId}`}
                    disabled={busy} onClick={() => actions.undo(target, person)}
                  >
                    {t('party.console.undo')}
                  </button>
                ) : (
                  <button
                    type="button" className="row-action-primary guest-person-action"
                    aria-label={t('party.console.checkInFor', { name: person.name })}
                    data-testid={`guest-detail-checkin-${person.guestId}`}
                    disabled={busy} onClick={() => actions.checkIn(target, person)}
                  >
                    {t('party.console.checkIn')}
                  </button>
                ))}
              </li>
            );
          })}
        </ul>
        {group.maxAdditionalGuests > 0 && (
          <p className="muted">
            {t('party.console.detail.plusOnes', {
              used: group.additionalGuestsUsed, max: group.maxAdditionalGuests,
            })}
          </p>
        )}
      </section>

      <section className="guest-detail-section" aria-labelledby={`guest-detail-contacts-${groupId}`}>
        <h4 id={`guest-detail-contacts-${groupId}`}>{t('party.console.detail.contacts')}</h4>
        <dl className="guest-contacts">
          <div>
            <dt>{t('party.console.detail.email')}</dt>
            <dd><a href={`mailto:${group.recipientEmail}`}>{group.recipientEmail}</a></dd>
          </div>
          {group.phone && (
            <div>
              <dt>{t('party.console.detail.phone')}</dt>
              <dd><a href={`tel:${group.phone.replace(/\s+/g, '')}`}>{group.phone}</a></dd>
            </div>
          )}
          {group.guests.filter((guest) => guest.email || guest.phone).map((guest) => (
            <div key={guest.id}>
              <dt>{guest.name}</dt>
              <dd>
                {guest.email && <a href={`mailto:${guest.email}`}>{guest.email}</a>}
                {guest.email && guest.phone && ' · '}
                {guest.phone && <a href={`tel:${guest.phone.replace(/\s+/g, '')}`}>{guest.phone}</a>}
              </dd>
            </div>
          ))}
        </dl>
      </section>

      {group.answers.length > 0 && (
        <section className="guest-detail-section" aria-labelledby={`guest-detail-answers-${groupId}`}>
          <h4 id={`guest-detail-answers-${groupId}`}>{t('party.console.detail.answers')}</h4>
          <dl className="guest-answers" data-testid="guest-detail-answers">
            {group.answers.map((answer) => {
              const question = detail.questions.find((q) => q.id === answer.questionId);
              return (
                <div key={answer.questionId}>
                  <dt>
                    {question?.prompt}
                    {question && !question.isActive && <> {t('party.console.detail.retired')}</>}
                  </dt>
                  <dd>
                    {typeof answer.value === 'boolean'
                      ? t(answer.value ? 'party.console.detail.yes' : 'party.console.detail.no')
                      : answer.value}
                  </dd>
                </div>
              );
            })}
          </dl>
        </section>
      )}

      <div className="guest-detail-actions guest-detail-foot">
        <button
          type="button" className="row-action" data-testid="guest-detail-edit"
          disabled={busy} onClick={() => actions.edit(detail)}
        >
          {t('party.console.action.edit')}
        </button>
        <button
          type="button" className="row-action" data-testid="guest-detail-rotate"
          disabled={busy} onClick={() => actions.rotate(target)}
        >
          {t('party.console.action.rotate')}
        </button>
        <button
          type="button" className="row-action row-action-danger" data-testid="guest-detail-remove"
          disabled={busy} onClick={() => actions.remove(target)}
        >
          {t('party.console.action.remove')}
        </button>
      </div>
    </div>
  );
}

