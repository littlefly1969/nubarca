import {
  invitationLine,
  type GuestDirectoryCounts,
  type GuestDirectoryPerson,
  type InvitationDeliveryChannel,
  type InvitationDeliveryStatus,
  type PartyInvitationDeliveryKind,
  type PartyInvitationDeliveryView,
} from '@nubarca/api-client';
import type { MessageKey } from '../i18n';

// What a guest card SAYS, as pure functions: the one line about its invitation,
// the moment that line names, and the counts beneath its people. They take the
// translate and date functions rather than reaching for them, so each is
// testable on its own and none of them decides policy — `invitationLine` in
// @nubarca/contracts does that, once, for every client.

type Translate = (key: MessageKey, params?: Record<string, string | number>) => string;
type FormatDate = (iso: string, options?: Intl.DateTimeFormatOptions) => string;

export interface InvitationStatusLine {
  text: string;
  /** An email that failed or was never confirmed: something the host should look at. */
  problem: boolean;
}

/**
 * "WhatsApp condiviso · oggi 10:12", "Email non partita · ieri 18:42",
 * "Invito non ancora condiviso". One line, whatever the history behind it.
 */
export function invitationStatusLine(
  view: PartyInvitationDeliveryView, t: Translate, formatDate: FormatDate, now?: Date,
): InvitationStatusLine {
  const line = invitationLine(view);
  const key = `party.console.line.${line.kind}` as MessageKey;
  return {
    text: line.at === null ? t(key) : t(key, { when: formatWhen(line.at, t, formatDate, now) }),
    problem: line.problem,
  };
}

/** The same line for one entry of a group's history, which states its own channel. */
export function historyLine(
  entry: { channel: InvitationDeliveryChannel; kind: PartyInvitationDeliveryKind; status: InvitationDeliveryStatus; createdAt: string },
  t: Translate, formatDate: FormatDate, now?: Date,
): InvitationStatusLine {
  return invitationStatusLine(
    {
      state: 'not_sent',
      lastAttemptAt: entry.createdAt,
      lastAttemptKind: entry.kind,
      lastAttemptStatus: entry.status,
      lastAttemptChannel: entry.channel,
      lastSentAt: null,
    },
    t, formatDate, now);
}

/**
 * A moment a host reads at a glance: the hour for today and yesterday, the day
 * for anything older. Compared in CALENDAR days in the reader's own timezone —
 * "yesterday at 23:40" is yesterday however few hours ago it was.
 */
export function formatWhen(iso: string, t: Translate, formatDate: FormatDate, now: Date = new Date()): string {
  const at = new Date(iso);
  if (Number.isNaN(at.getTime())) return '';
  const days = calendarDaysAgo(at, now);
  if (days === 0) return t('party.console.when.today', { time: formatDate(iso, { timeStyle: 'short' }) });
  if (days === 1) return t('party.console.when.yesterday', { time: formatDate(iso, { timeStyle: 'short' }) });
  return formatDate(iso, { dateStyle: 'medium' });
}

function calendarDaysAgo(at: Date, now: Date): number {
  const startOfDay = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
  return Math.round((startOfDay(now) - startOfDay(at)) / 86_400_000);
}

/**
 * "2 confermati · 1 da rispondere". Only the counts that are not zero, and the
 * number of people when none of them says anything yet.
 */
export function countsPhrase(counts: GuestDirectoryCounts, people: number, t: Translate): string {
  const parts: string[] = [];
  if (counts.attending > 0) parts.push(t('party.console.card.counts.attending', { count: counts.attending }));
  if (counts.pending > 0) parts.push(t('party.console.card.counts.pending', { count: counts.pending }));
  if (counts.declined > 0) parts.push(t('party.console.card.counts.declined', { count: counts.declined }));
  if (parts.length === 0) parts.push(t('party.console.card.counts.people', { count: people }));
  return parts.join(' · ');
}

/**
 * Which of a group's people a card shows before "Mostra tutti": the ones the
 * search matched first, then the ones still expected, then the rest — so the
 * person the host is looking for is the one they can see.
 */
export function peopleInReadingOrder(
  people: readonly GuestDirectoryPerson[], live: boolean,
): GuestDirectoryPerson[] {
  const rank = (person: GuestDirectoryPerson): number => {
    if (person.matched) return 0;
    if (live && person.rsvpStatus === 'attending' && person.checkedInAt === null) return 1;
    return 2;
  };
  return [...people].map((person, index) => ({ person, index }))
    .sort((a, b) => rank(a.person) - rank(b.person) || a.index - b.index)
    .map((x) => x.person);
}
