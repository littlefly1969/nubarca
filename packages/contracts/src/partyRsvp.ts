// Party guest list and personal invitation (NUBARCA-PARTY-RSVP-01).
//
// What lives here is what a client must not decide for itself:
//
//   1. THE CLOSED VOCABULARIES. Three RSVP answers and no "maybe"; three question
//      kinds and no fourth. The server refuses anything else; these let every
//      client offer exactly the same choices.
//   2. THE LIMITS, quoted from the server's own numbers and measured in Unicode
//      code points, the one unit .NET and a browser agree on.
//   3. THE FORM RULES a guest's reply is judged by — so a page can say what is
//      missing before a round-trip, and two clients cannot come to disagree.
//      The server remains the authority on every one of them.
//   4. THE ROUTES.
//
// Two capabilities are named here and they never meet: the HOST's routes under
// /api/parties/{partyId}, and the GUEST's personal invitation under
// /api/party-invitations/{token}. The latter is not the party's QR token and is
// never built from one.

// ── Vocabularies ────────────────────────────────────────────────────────────

export const PARTY_RSVP_STATUSES = ['pending', 'attending', 'declined'] as const;
export type PartyRsvpStatus = (typeof PARTY_RSVP_STATUSES)[number];

export const PARTY_RSVP_QUESTION_KINDS = ['short_text', 'single_choice', 'yes_no'] as const;
export type PartyRsvpQuestionKind = (typeof PARTY_RSVP_QUESTION_KINDS)[number];

/** Where the CURRENT personal link's invitation stands. A rotation starts it again. */
export type PartyInvitationDeliveryState = 'not_sent' | 'pending' | 'sent' | 'failed';
export type PartyInvitationDeliveryKind = 'initial' | 'resend' | 'reminder';
export type PartyInvitationDeliveryStatus = 'pending' | 'sent' | 'failed';

export const PARTY_INVITATION_LIMITS = {
  label: 120,
  email: 254,
  phone: 40,
  guestName: 120,
  namedGuests: 20,
  additionalGuests: 10,
  dietaryNotes: 500,
  questionPrompt: 300,
  shortTextAnswer: 500,
  options: 20,
  optionLength: 120,
  activeQuestions: 20,
} as const;

/** Unicode code points — how the server counts every limit above. */
export function codePoints(value: string): number {
  return [...value].length;
}

// ── Owner DTOs (party.access, owner-private) ────────────────────────────────

export interface PartyGuest {
  id: string;
  name: string;
  email: string | null;
  phone: string | null;
  /** A +1 the group added through its reply, not somebody the host named. */
  isAdditionalGuest: boolean;
  status: PartyRsvpStatus;
  dietaryNotes: string | null;
  respondedAt: string | null;
}

export interface PartyRsvpAnswer {
  questionId: string;
  value: string | boolean;
}

export interface PartyInvitationDeliveryView {
  state: PartyInvitationDeliveryState;
  lastAttemptAt: string | null;
  lastAttemptKind: PartyInvitationDeliveryKind | null;
  lastAttemptStatus: PartyInvitationDeliveryStatus | null;
  lastSentAt: string | null;
}

export interface PartyInvitationGroup {
  id: string;
  label: string;
  /** Where the invitation is SENT. */
  recipientEmail: string;
  phone: string | null;
  maxAdditionalGuests: number;
  version: number;
  /** Named guests in the host's order, then the group's +1s. */
  guests: PartyGuest[];
  additionalGuestsUsed: number;
  pendingCount: number;
  attendingCount: number;
  declinedCount: number;
  /** Every stored answer, including answers to questions since retired. */
  answers: PartyRsvpAnswer[];
  delivery: PartyInvitationDeliveryView;
  canSend: boolean;
  /** Invited on the link it holds now, and somebody has not answered. */
  canRemind: boolean;
}

/**
 * ONE definition per count. `invited` counts named guests; `missingResponses`
 * named guests still pending; `attending` every guest, named or +1, who is
 * coming; `declined` named guests who said no; `expectedPeople` equals
 * `attending`. None of it is attendance: nobody has checked in.
 */
export interface PartyRsvpSummary {
  groups: number;
  invited: number;
  missingResponses: number;
  attending: number;
  declined: number;
  expectedPeople: number;
  unansweredGroups: number;
}

export interface PartyRsvpQuestion {
  id: string;
  prompt: string;
  kind: PartyRsvpQuestionKind;
  required: boolean;
  options: string[];
  isActive: boolean;
  sortOrder: number;
  version: number;
  answerCount: number;
  /** Answered at least once: only its activation and position may change. */
  locked: boolean;
}

export interface PartyGuestList {
  partyId: string;
  partyStatus: 'draft' | 'published' | 'live' | 'ended';
  /** Whether this installation can email an invitation at all. */
  mailAvailable: boolean;
  summary: PartyRsvpSummary;
  groups: PartyInvitationGroup[];
  questions: PartyRsvpQuestion[];
}

export interface PartyInvitationDelivery {
  kind: PartyInvitationDeliveryKind;
  status: PartyInvitationDeliveryStatus;
  createdAt: string;
  completedAt: string | null;
  /** This click's id had already been used: nothing was sent again. */
  replayed: boolean;
}

export interface PartyNamedGuestWrite {
  /** Present for a guest the group already has; absent for a new one. */
  id?: string;
  name: string;
  email?: string | null;
  phone?: string | null;
}

/** NAMED guests only: the group's own +1s are never the host's to state. */
export interface PartyInvitationGroupWrite {
  label: string;
  recipientEmail: string;
  phone?: string | null;
  maxAdditionalGuests: number;
  guests: PartyNamedGuestWrite[];
  /** The group's version; 0 or absent to create. */
  version?: number;
}

export interface PartyRsvpQuestionWrite {
  prompt: string;
  kind: PartyRsvpQuestionKind;
  required: boolean;
  options?: string[] | null;
  isActive?: boolean;
  version?: number;
}

// ── Guest DTOs (the personal invitation token) ──────────────────────────────

export interface PartyInvitationGuest {
  id: string;
  name: string;
  isAdditionalGuest: boolean;
  status: PartyRsvpStatus;
  dietaryNotes: string | null;
}

export interface PartyInvitationQuestion {
  id: string;
  prompt: string;
  kind: PartyRsvpQuestionKind;
  required: boolean;
  options: string[];
  answer: string | boolean | null;
}

export interface PartyInvitationRsvp {
  label: string;
  version: number;
  /** Replies are open while the party is announced; Live and Ended are read-only. */
  canRespond: boolean;
  maxAdditionalGuests: number;
  additionalGuestsUsed: number;
  guests: PartyInvitationGuest[];
  questions: PartyInvitationQuestion[];
}

/**
 * What a personal link opens. `TContent` is the client's own guest-content slot
 * shape, which this package deliberately does not restate.
 */
export interface PartyInvitationView<TContent = unknown> {
  party: {
    title: string;
    description: string | null;
    eventStartsAt: string | null;
    phase: 'before' | 'live' | 'after';
    coverUrl: string | null;
    content: TContent[];
  };
  invitation: PartyInvitationRsvp;
}

/** The WHOLE reply, every time. */
export interface PartyRsvpWrite {
  version: number;
  guests: { guestId: string; status: PartyRsvpStatus; dietaryNotes: string | null }[];
  additionalGuests: { guestId?: string; name: string; dietaryNotes: string | null }[];
  answers: { questionId: string; value: string | boolean }[];
}

// ── Rules ───────────────────────────────────────────────────────────────────

/** Trimmed, or null when there was nothing but whitespace — the server's normalisation. */
export function normalizeText(value: string | null | undefined): string | null {
  const trimmed = (value ?? '').trim();
  return trimmed === '' ? null : trimmed;
}

/**
 * One mailbox, plainly written. Only the shape — whether it exists is SMTP's to
 * say, and the server has the last word on the shape too.
 */
export function isPlausibleEmail(value: string): boolean {
  const email = value.trim();
  if (email.length === 0 || email.length > PARTY_INVITATION_LIMITS.email || /\s/.test(email)) return false;
  const at = email.indexOf('@');
  if (at <= 0 || at !== email.lastIndexOf('@')) return false;
  const domain = email.slice(at + 1);
  return domain.includes('.') && !domain.startsWith('.') && !domain.endsWith('.');
}

/**
 * The options a question of this kind would be stored with, or null when the
 * server would refuse them: a single choice needs 2–20 distinct, non-empty,
 * bounded options (distinct regardless of case); every other kind needs none.
 */
export function normalizeQuestionOptions(
  kind: PartyRsvpQuestionKind,
  options: readonly string[] | null | undefined,
): string[] | null {
  if (kind !== 'single_choice') return !options || options.length === 0 ? [] : null;
  if (!options || options.length < 2 || options.length > PARTY_INVITATION_LIMITS.options) return null;
  const seen = new Set<string>();
  const normalized: string[] = [];
  for (const option of options) {
    const value = normalizeText(option);
    if (value === null || codePoints(value) > PARTY_INVITATION_LIMITS.optionLength) return null;
    const key = value.toLocaleLowerCase('en');
    if (seen.has(key)) return null;
    seen.add(key);
    normalized.push(value);
  }
  return normalized;
}

export type PartyRsvpFormProblem =
  | 'required_answer_missing'
  | 'additional_guests_need_attendee'
  | 'too_many_additional_guests'
  | 'additional_guest_name_missing'
  | 'text_too_long';

/**
 * What the server would refuse about this reply, in the order a form shows it.
 * Empty means it will be accepted, as far as a client can tell.
 *
 * Required means required of a group that is COMING: a family declining the
 * invitation is not asked which menu it will not eat.
 */
export function rsvpFormProblems(
  invitation: Pick<PartyInvitationRsvp, 'maxAdditionalGuests' | 'questions'>,
  reply: Pick<PartyRsvpWrite, 'guests' | 'additionalGuests' | 'answers'>,
): PartyRsvpFormProblem[] {
  const problems: PartyRsvpFormProblem[] = [];
  const coming = reply.guests.some((g) => g.status === 'attending');
  const answered = new Set(
    reply.answers
      .filter((a) => typeof a.value === 'boolean' || normalizeText(a.value) !== null)
      .map((a) => a.questionId),
  );
  if (coming && invitation.questions.some((q) => q.required && !answered.has(q.id))) {
    problems.push('required_answer_missing');
  }
  if (reply.additionalGuests.length > 0 && !coming) problems.push('additional_guests_need_attendee');
  if (reply.additionalGuests.length > invitation.maxAdditionalGuests) problems.push('too_many_additional_guests');
  if (reply.additionalGuests.some((g) => normalizeText(g.name) === null)) {
    problems.push('additional_guest_name_missing');
  }
  const tooLong = (value: string | null | undefined, max: number) =>
    codePoints(normalizeText(value) ?? '') > max;
  if (
    reply.guests.some((g) => tooLong(g.dietaryNotes, PARTY_INVITATION_LIMITS.dietaryNotes))
    || reply.additionalGuests.some((g) =>
      tooLong(g.name, PARTY_INVITATION_LIMITS.guestName)
      || tooLong(g.dietaryNotes, PARTY_INVITATION_LIMITS.dietaryNotes))
    || reply.answers.some((a) =>
      typeof a.value === 'string' && tooLong(a.value, PARTY_INVITATION_LIMITS.shortTextAnswer))
  ) {
    problems.push('text_too_long');
  }
  return problems;
}

/**
 * The host's search, over what the list already holds: the group's label, its
 * people, its address and phone. Case- and accent-insensitive, because a host
 * typing "nicolo" is looking for Nicolò.
 */
export function matchesGuestSearch(group: PartyInvitationGroup, query: string): boolean {
  const needle = fold(query.trim());
  if (needle === '') return true;
  const haystack = [
    group.label,
    group.recipientEmail,
    group.phone ?? '',
    ...group.guests.flatMap((g) => [g.name, g.email ?? '', g.phone ?? '']),
  ];
  return haystack.some((value) => fold(value).includes(needle));
}

function fold(value: string): string {
  return value.normalize('NFD').replace(/\p{Diacritic}/gu, '').toLocaleLowerCase('en');
}

// ── Routes ──────────────────────────────────────────────────────────────────

export function partyGuestListPath(partyId: string): string {
  return `/api/parties/${partyId}/guest-list`;
}
export function partyInvitationGroupsPath(partyId: string): string {
  return `/api/parties/${partyId}/invitation-groups`;
}
export function partyInvitationGroupPath(partyId: string, groupId: string): string {
  return `${partyInvitationGroupsPath(partyId)}/${groupId}`;
}
export function partyInvitationGroupActionPath(
  partyId: string,
  groupId: string,
  action: 'send' | 'remind' | 'rotate-link',
): string {
  return `${partyInvitationGroupPath(partyId, groupId)}/${action}`;
}
export function partyRsvpQuestionsPath(partyId: string): string {
  return `/api/parties/${partyId}/rsvp-questions`;
}
export function partyRsvpQuestionPath(partyId: string, questionId: string): string {
  return `${partyRsvpQuestionsPath(partyId)}/${questionId}`;
}
export function partyRsvpQuestionOrderPath(partyId: string): string {
  return `${partyRsvpQuestionsPath(partyId)}/order`;
}

/** The guest's API, on the personal token. */
export function partyInvitationPath(token: string): string {
  return `/api/party-invitations/${encodeURIComponent(token)}`;
}
export function partyInvitationRsvpPath(token: string): string {
  return `${partyInvitationPath(token)}/rsvp`;
}
