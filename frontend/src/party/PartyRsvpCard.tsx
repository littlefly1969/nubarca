import { useState } from 'react';
import {
  normalizeText,
  rsvpFormProblems,
  type PartyInvitationRsvp,
  type PartyRsvpFormProblem,
  type PartyRsvpStatus,
  type PartyRsvpWrite,
} from '@nubarca/api-client';
import { useI18n, type MessageKey } from '../i18n';
import './PartyRsvp.css';

// ONE group's reply, on its personal invitation.
//
// It is composed INTO the invitation — between the cover and what the host
// wrote — rather than being a second page with a design of its own: the guest
// is reading an invitation, and answering it is part of reading it.
//
// The form holds a draft and hands the page a WHOLE reply to send. The page
// owns the round-trip; this owns what the guest has typed, which is why a
// failed send never loses it. The page KEYS this card by the server's version,
// so a new server reply — a save, or a conflict it adopted — is a fresh card
// with a fresh draft, and there is no reseeding effect that could land after
// the guest's first tap and quietly undo it.

interface ExtraDraft {
  key: string;
  guestId?: string;
  name: string;
  dietaryNotes: string;
}

interface Draft {
  people: Record<string, { status: PartyRsvpStatus; dietaryNotes: string }>;
  extras: ExtraDraft[];
  answers: Record<string, string | boolean | null>;
}

let extraKey = 0;

function draftFrom(invitation: PartyInvitationRsvp): Draft {
  const people: Draft['people'] = {};
  const extras: ExtraDraft[] = [];
  for (const guest of invitation.guests) {
    if (guest.isAdditionalGuest) {
      extras.push({ key: guest.id, guestId: guest.id, name: guest.name, dietaryNotes: guest.dietaryNotes ?? '' });
    } else {
      people[guest.id] = { status: guest.status, dietaryNotes: guest.dietaryNotes ?? '' };
    }
  }
  const answers: Draft['answers'] = {};
  for (const question of invitation.questions) answers[question.id] = question.answer;
  return { people, extras, answers };
}

/**
 * The reply the draft describes. Nobody coming means nobody brought: a group
 * that declines takes its +1s with it, which is exactly what the server would
 * otherwise refuse the reply for.
 */
function replyFrom(invitation: PartyInvitationRsvp, draft: Draft): PartyRsvpWrite {
  const guests = invitation.guests
    .filter((g) => !g.isAdditionalGuest)
    .map((g) => ({
      guestId: g.id,
      status: draft.people[g.id]?.status ?? g.status,
      dietaryNotes: normalizeText(draft.people[g.id]?.dietaryNotes),
    }));
  const coming = guests.some((g) => g.status === 'attending');
  return {
    version: invitation.version,
    guests,
    additionalGuests: coming
      ? draft.extras.map((e) => ({
        ...(e.guestId ? { guestId: e.guestId } : {}),
        name: e.name.trim(),
        dietaryNotes: normalizeText(e.dietaryNotes),
      }))
      : [],
    answers: invitation.questions.flatMap((q) => {
      const value = draft.answers[q.id];
      if (value === null || value === undefined) return [];
      if (typeof value === 'string' && normalizeText(value) === null) return [];
      return [{ questionId: q.id, value }];
    }),
  };
}

const PROBLEM_KEYS: Record<PartyRsvpFormProblem, MessageKey> = {
  required_answer_missing: 'partyRsvp.problem.required_answer_missing',
  additional_guests_need_attendee: 'partyRsvp.problem.additional_guests_need_attendee',
  too_many_additional_guests: 'partyRsvp.problem.too_many_additional_guests',
  additional_guest_name_missing: 'partyRsvp.problem.additional_guest_name_missing',
  text_too_long: 'partyRsvp.problem.text_too_long',
};

export interface PartyRsvpNotice {
  tone: 'ok' | 'error';
  messageKey: MessageKey;
}

export function PartyRsvpCard({
  invitation, phase, saving, notice, onSubmit,
}: {
  invitation: PartyInvitationRsvp;
  phase: 'before' | 'live' | 'after';
  saving: boolean;
  /** What the page's last send came to. */
  notice: PartyRsvpNotice | null;
  onSubmit(reply: PartyRsvpWrite): void;
}) {
  const { t } = useI18n();
  const [draft, setDraft] = useState<Draft>(() => draftFrom(invitation));

  const named = invitation.guests.filter((g) => !g.isAdditionalGuest);
  const answeredBefore = named.some((g) => g.status !== 'pending');

  if (!invitation.canRespond) {
    return (
      <section className="party-rsvp" data-testid="party-rsvp" data-mode="read-only" aria-labelledby="party-rsvp-title">
        <h2 className="party-rsvp-title" id="party-rsvp-title">{t('partyRsvp.heading')}</h2>
        <p className="party-rsvp-for">{t('partyRsvp.for', { label: invitation.label })}</p>
        <p className="party-rsvp-closed" role="status">
          {phase === 'after' ? t('partyRsvp.closed.after') : t('partyRsvp.closed.live')}
        </p>
        <ul className="party-rsvp-summary">
          {invitation.guests.map((g) => (
            <li key={g.id} data-status={g.status}>
              <strong>{g.name}</strong>
              {g.isAdditionalGuest && <span className="party-rsvp-chip">+1</span>}
              <span> — {t(`partyRsvp.status.${g.status}` as MessageKey)}</span>
            </li>
          ))}
        </ul>
        {invitation.questions.length > 0 && (
          <dl className="party-rsvp-answers">
            {invitation.questions.map((q) => (
              <div key={q.id}>
                <dt>{q.prompt}</dt>
                <dd>
                  {q.answer === null
                    ? t('partyRsvp.noAnswer')
                    : typeof q.answer === 'boolean'
                      ? (q.answer ? t('partyRsvp.yes') : t('partyRsvp.no'))
                      : q.answer}
                </dd>
              </div>
            ))}
          </dl>
        )}
      </section>
    );
  }

  const reply = replyFrom(invitation, draft);
  const problems = rsvpFormProblems(invitation, reply);
  const coming = reply.guests.some((g) => g.status === 'attending');
  const setPerson = (id: string, change: Partial<Draft['people'][string]>) =>
    setDraft((d) => ({ ...d, people: { ...d.people, [id]: { ...d.people[id], ...change } } }));
  const setExtra = (key: string, change: Partial<ExtraDraft>) =>
    setDraft((d) => ({ ...d, extras: d.extras.map((e) => (e.key === key ? { ...e, ...change } : e)) }));
  const setAnswer = (id: string, value: string | boolean | null) =>
    setDraft((d) => ({ ...d, answers: { ...d.answers, [id]: value } }));

  return (
    <section className="party-rsvp" data-testid="party-rsvp" data-mode="form" aria-labelledby="party-rsvp-title">
      <h2 className="party-rsvp-title" id="party-rsvp-title">{t('partyRsvp.heading')}</h2>
      <p className="party-rsvp-for">{t('partyRsvp.for', { label: invitation.label })}</p>

      <form
        className="party-rsvp-form"
        onSubmit={(e) => { e.preventDefault(); if (problems.length === 0 && !saving) onSubmit(reply); }}
      >
        {named.map((guest) => {
          const person = draft.people[guest.id];
          return (
            <fieldset key={guest.id} className="party-rsvp-person" data-testid={`party-rsvp-person-${guest.id}`}>
              <legend>{guest.name}</legend>
              <div className="party-rsvp-choice" role="radiogroup" aria-label={guest.name}>
                {(['attending', 'declined'] as const).map((status) => (
                  <label key={status} className="party-rsvp-option" data-selected={person?.status === status}>
                    <input
                      type="radio" name={`rsvp-${guest.id}`} value={status}
                      checked={person?.status === status} disabled={saving}
                      onChange={() => setPerson(guest.id, { status })}
                    />
                    <span>{t(`partyRsvp.${status}` as MessageKey)}</span>
                  </label>
                ))}
              </div>
              {person?.status === 'attending' && (
                <label className="party-rsvp-field">
                  <span>{t('partyRsvp.dietary')}</span>
                  <input
                    value={person.dietaryNotes} disabled={saving}
                    onChange={(e) => setPerson(guest.id, { dietaryNotes: e.target.value })}
                  />
                </label>
              )}
            </fieldset>
          );
        })}

        {/* The +1s come with somebody, so they are offered once somebody is. */}
        {invitation.maxAdditionalGuests > 0 && coming && (
          <fieldset className="party-rsvp-extras" data-testid="party-rsvp-extras">
            <legend>{t('partyRsvp.additionalHeading')}</legend>
            <p className="party-rsvp-help">{t('partyRsvp.additionalHelp', { max: invitation.maxAdditionalGuests })}</p>
            {draft.extras.map((extra, index) => (
              <div key={extra.key} className="party-rsvp-extra">
                <label className="party-rsvp-field">
                  <span>{t('partyRsvp.additionalName')} {index + 1}</span>
                  <input
                    value={extra.name} disabled={saving}
                    onChange={(e) => setExtra(extra.key, { name: e.target.value })}
                  />
                </label>
                <label className="party-rsvp-field">
                  <span>{t('partyRsvp.dietary')}</span>
                  <input
                    value={extra.dietaryNotes} disabled={saving}
                    onChange={(e) => setExtra(extra.key, { dietaryNotes: e.target.value })}
                  />
                </label>
                <button
                  type="button" className="party-rsvp-link" disabled={saving}
                  onClick={() => setDraft((d) => ({ ...d, extras: d.extras.filter((x) => x.key !== extra.key) }))}
                >
                  {t('partyRsvp.additionalRemove')}
                </button>
              </div>
            ))}
            {draft.extras.length < invitation.maxAdditionalGuests && (
              <button
                type="button" className="party-rsvp-link" data-testid="party-rsvp-add-extra" disabled={saving}
                onClick={() => setDraft((d) => ({
                  ...d,
                  extras: [...d.extras, { key: `extra-${(extraKey += 1)}`, name: '', dietaryNotes: '' }],
                }))}
              >
                {t('partyRsvp.additionalAdd')}
              </button>
            )}
          </fieldset>
        )}

        {invitation.questions.length > 0 && (
          <fieldset className="party-rsvp-questions">
            <legend>{t('partyRsvp.questionsHeading')}</legend>
            {invitation.questions.map((q) => {
              const value = draft.answers[q.id];
              const label = (
                <>
                  {q.prompt}
                  {q.required && <span className="party-rsvp-required"> ({t('partyRsvp.required')})</span>}
                </>
              );
              if (q.kind === 'short_text') {
                return (
                  <label key={q.id} className="party-rsvp-field" data-testid={`party-rsvp-question-${q.id}`}>
                    <span>{label}</span>
                    <input
                      value={typeof value === 'string' ? value : ''} disabled={saving}
                      onChange={(e) => setAnswer(q.id, e.target.value)}
                    />
                  </label>
                );
              }
              const choices: { value: string | boolean; text: string }[] = q.kind === 'yes_no'
                ? [{ value: true, text: t('partyRsvp.yes') }, { value: false, text: t('partyRsvp.no') }]
                : q.options.map((o) => ({ value: o, text: o }));
              return (
                <div
                  key={q.id} className="party-rsvp-question" role="radiogroup"
                  aria-label={q.prompt} data-testid={`party-rsvp-question-${q.id}`}
                >
                  <p className="party-rsvp-question-prompt">{label}</p>
                  <div className="party-rsvp-choice">
                    {choices.map((choice) => (
                      <label key={String(choice.value)} className="party-rsvp-option" data-selected={value === choice.value}>
                        <input
                          type="radio" name={`question-${q.id}`} checked={value === choice.value} disabled={saving}
                          onChange={() => setAnswer(q.id, choice.value)}
                        />
                        <span>{choice.text}</span>
                      </label>
                    ))}
                  </div>
                </div>
              );
            })}
          </fieldset>
        )}

        {problems.length > 0 && (
          <ul className="party-rsvp-problems" data-testid="party-rsvp-problems">
            {problems.map((p) => <li key={p}>{t(PROBLEM_KEYS[p])}</li>)}
          </ul>
        )}

        <button
          type="submit" className="party-rsvp-submit" data-testid="party-rsvp-submit"
          disabled={saving || problems.length > 0}
        >
          {saving ? t('partyRsvp.saving') : answeredBefore ? t('partyRsvp.update') : t('partyRsvp.submit')}
        </button>
        {notice && (
          <p
            className={`party-rsvp-notice party-rsvp-notice--${notice.tone}`}
            role={notice.tone === 'error' ? 'alert' : 'status'}
            data-testid="party-rsvp-notice"
          >
            {t(notice.messageKey)}
          </p>
        )}
      </form>
    </section>
  );
}
