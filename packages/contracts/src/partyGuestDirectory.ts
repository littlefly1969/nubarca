// Party guest directory and invitation sharing (NUBARCA-PARTY-WHATSAPP-01).
//
// The host's "Ospiti" console reads the guest list one PAGE at a time. The
// server searches (accents and case folded), filters and orders it, and a
// client holds only the pages it has shown — so a party of a thousand groups is
// as quick to open as one of ten. What lives here is what every client must
// agree on and none may decide for itself:
//
//   1. THE VOCABULARY of the directory's filters, and which of them a phase offers.
//   2. THE SHAPES of a page, a card, a group's detail, a share, and the minimal
//      answers a paging client asks for with `Prefer: return=minimal`.
//   3. THE ONE LINE a card says about its invitation. `shared` is not `sent`:
//      it means the host was handed the link — never that a WhatsApp message
//      was sent, delivered or read, which NubArca cannot know.
//   4. THE ROUTES.
//
// Everything here is owner-private. The personal invitation token never
// appears in any of these shapes except a share's `url`, which the server
// composes on its own public origin and a client passes on untouched: no client
// ever builds, stores or logs a personal link.

import { withQuery } from './query.ts';
import type {
  PartyAttendanceGuest,
  PartyAttendanceOtherGuest,
  PartyAttendanceSource,
  PartyAttendanceSummary,
} from './partyAttendance.ts';
import {
  partyInvitationGroupActionPath,
  partyInvitationGroupPath,
  type InvitationDeliveryChannel,
  type InvitationDeliveryStatus,
  type InvitationShareChannel,
  type PartyInvitationDelivery,
  type PartyInvitationDeliveryKind,
  type PartyInvitationDeliveryView,
  type PartyInvitationGroup,
  type PartyRsvpQuestion,
  type PartyRsvpStatus,
  type PartyRsvpSummary,
} from './partyRsvp.ts';

type PartyStatus = 'draft' | 'published' | 'live' | 'ended';

// ── Vocabulary ──────────────────────────────────────────────────────────────

/**
 * Every filter, one closed vocabulary. A group matches when at least one of its
 * people does; `not_invited` is about its link. Other arrivals belong to
 * `all`, `arrived` and `unexpected` only.
 */
export const GUEST_DIRECTORY_STATES = [
  'all', 'pending', 'attending', 'declined', 'not_invited', 'to_arrive', 'arrived', 'unexpected',
] as const;
export type GuestDirectoryState = (typeof GUEST_DIRECTORY_STATES)[number];

export const GUEST_DIRECTORY_LIMITS = {
  defaultTake: 40,
  maxTake: 100,
  /** Unicode code points, like every Party text limit. */
  query: 120,
} as const;

export function isGuestDirectoryState(value: unknown): value is GuestDirectoryState {
  return typeof value === 'string' && (GUEST_DIRECTORY_STATES as readonly string[]).includes(value);
}

/** Arrivals are recorded while the party is live and after it. */
export function isAttendancePhase(partyStatus: PartyStatus): boolean {
  return partyStatus === 'live' || partyStatus === 'ended';
}

/**
 * The filters a phase offers. Before the party: what people declared, and who
 * has not been invited yet. From the moment it is live: who arrived. With no
 * guest list there is nothing to filter by — everybody listed is an arrival.
 */
export function guestDirectoryStatesFor(partyStatus: PartyStatus, hasGuestList: boolean): GuestDirectoryState[] {
  if (!hasGuestList) return [];
  return isAttendancePhase(partyStatus)
    ? ['all', 'to_arrive', 'arrived', 'unexpected']
    : ['all', 'pending', 'attending', 'declined', 'not_invited'];
}

// ── Shapes ──────────────────────────────────────────────────────────────────

export interface GuestDirectoryQuery {
  q?: string | null;
  state?: GuestDirectoryState | null;
  /** Opaque, and valid only for the party, search and filter it was issued with. */
  cursor?: string | null;
  /** 1–100, default 40; 0 asks for the counts alone. */
  take?: number | null;
}

/**
 * The party's counts. `rsvp` is what people declared and `attendance` who
 * arrived — each with the definitions their own contracts state.
 */
export interface GuestDirectorySummary {
  groups: number;
  otherArrivals: number;
  rsvp: PartyRsvpSummary;
  attendance: PartyAttendanceSummary;
}

export interface GuestDirectoryPerson {
  guestId: string;
  name: string;
  isAdditionalGuest: boolean;
  rsvpStatus: PartyRsvpStatus;
  checkedInAt: string | null;
  checkInSource: PartyAttendanceSource | null;
  /** This person — not only their group — matched the search. */
  matched: boolean;
}

/** Pending and declined count NAMED guests; attending and arrived count everybody. */
export interface GuestDirectoryCounts {
  attending: number;
  pending: number;
  declined: number;
  arrived: number;
}

/** One invitation group, as wide as its card. No address, no number, no link. */
export interface GuestDirectoryGroupItem {
  kind: 'group';
  groupId: string;
  label: string;
  version: number;
  maxAdditionalGuests: number;
  additionalGuestsUsed: number;
  /** Named guests in the host's order, then the group's +1s. */
  people: GuestDirectoryPerson[];
  counts: GuestDirectoryCounts;
  invitation: PartyInvitationDeliveryView;
  /** WhatsApp will open this group's chat directly; otherwise it asks whom to send to. */
  whatsappDirect: boolean;
  canSend: boolean;
  canRemind: boolean;
  canShare: boolean;
}

/** Somebody recorded as arrived who is not on the guest list. */
export interface GuestDirectoryOtherItem {
  kind: 'other';
  id: string;
  name: string;
  checkedInAt: string;
  version: number;
}

export type GuestDirectoryItem = GuestDirectoryGroupItem | GuestDirectoryOtherItem;

export interface GuestDirectoryPage {
  partyId: string;
  partyStatus: PartyStatus;
  mailAvailable: boolean;
  shareAvailable: boolean;
  /** On the first page only. */
  summary: GuestDirectorySummary | null;
  /** Other arrivals first, latest first; then groups by label. */
  items: GuestDirectoryItem[];
  nextCursor: string | null;
}

/** A stable key for one item across pages — groups and other arrivals never collide. */
export function guestDirectoryItemKey(item: GuestDirectoryItem): string {
  return item.kind === 'group' ? `g:${item.groupId}` : `o:${item.id}`;
}

export interface InvitationShareRequest {
  channel: InvitationShareChannel;
  /** Minted once per click and reused for that click's retries. */
  clientRequestId: string;
  /** The party version the page read — needed when this share publishes a Draft. */
  partyVersion?: number | null;
}

/** The current personal link, handed to the host. Pass it on; never keep it. */
export interface InvitationShare {
  channel: InvitationShareChannel;
  kind: PartyInvitationDeliveryKind;
  status: 'shared';
  url: string;
  /** The message, composed by the server in the host's language. */
  text: string;
  /** WhatsApp click-to-chat, for the whatsapp channel. */
  whatsappUrl: string | null;
  createdAt: string;
  /** This click had already been answered: the same link, no second share. */
  replayed: boolean;
}

export interface InvitationShareResult<TParty = unknown> {
  share: InvitationShare;
  /** The party as it is now — a first share publishes a Draft. */
  party: TParty;
  /** The group's card as the directory now lists it. */
  item: GuestDirectoryGroupItem | null;
}

export interface PartyGuestArrival {
  guestId: string;
  checkedInAt: string;
  checkInSource: PartyAttendanceSource;
}

export interface PartyInvitationHistoryEntry {
  channel: InvitationDeliveryChannel;
  kind: PartyInvitationDeliveryKind;
  status: InvitationDeliveryStatus;
  createdAt: string;
  completedAt: string | null;
  /** It carried the link that opens now; any other was replaced since. */
  currentLink: boolean;
}

/** One group on demand: what the host may see about it, its card, and the party's counts. */
export interface PartyInvitationGroupDetail {
  partyId: string;
  partyStatus: PartyStatus;
  mailAvailable: boolean;
  shareAvailable: boolean;
  group: PartyInvitationGroup;
  whatsappDirect: boolean;
  arrivals: PartyGuestArrival[];
  /** Latest first, at most twenty. */
  history: PartyInvitationHistoryEntry[];
  /** The questions this group's answers refer to, retired ones included. */
  questions: PartyRsvpQuestion[];
  item: GuestDirectoryGroupItem;
  summary: GuestDirectorySummary;
}

// ── Minimal answers ─────────────────────────────────────────────────────────
//
// A client that pages the list asks every owner mutation for `Prefer:
// return=minimal` and receives only what changed — never the whole list back.

export const PREFER_RETURN_MINIMAL = 'return=minimal';

/** A guest-list mutation's answer: the list's header, and the group it touched. */
export interface PartyGuestListMinimal {
  partyId: string;
  partyStatus: PartyStatus;
  mailAvailable: boolean;
  shareAvailable: boolean;
  summary: PartyRsvpSummary;
  questions: PartyRsvpQuestion[];
  groupId: string | null;
  /** A new recipient address replaced the personal link. */
  linkRotated: boolean;
}

export interface PartyInvitationSendMinimal<TParty = unknown> {
  delivery: PartyInvitationDelivery;
  party: TParty;
}

/** An attendance mutation's answer: whether it changed anything, the counts, and the one person. */
export interface PartyAttendanceChange {
  changed: boolean;
  summary: PartyAttendanceSummary;
  guest: PartyAttendanceGuest | null;
  otherGuest: PartyAttendanceOtherGuest | null;
}

// ── Rules ───────────────────────────────────────────────────────────────────

export type InvitationLineKind =
  | 'not_shared'
  | 'email_sent'
  | 'reminder_sent'
  | 'email_failed'
  | 'email_pending'
  | 'whatsapp_shared'
  | 'link_copied';

export interface InvitationLine {
  kind: InvitationLineKind;
  at: string | null;
  /** Something the host should look at: an email that failed or was never confirmed. */
  problem: boolean;
}

/**
 * The ONE line a card says about its invitation: the most recent delivery of
 * the CURRENT link, on any channel. A link replaced since has been shared with
 * nobody. A WhatsApp or copy line never claims that anything was sent.
 */
export function invitationLine(view: PartyInvitationDeliveryView): InvitationLine {
  const at = view.lastAttemptAt;
  switch (view.lastAttemptChannel) {
    case null: return { kind: 'not_shared', at: null, problem: false };
    case 'whatsapp': return { kind: 'whatsapp_shared', at, problem: false };
    case 'copy': return { kind: 'link_copied', at, problem: false };
    case 'email':
      if (view.lastAttemptStatus === 'failed') return { kind: 'email_failed', at, problem: true };
      if (view.lastAttemptStatus === 'pending') return { kind: 'email_pending', at, problem: true };
      return { kind: view.lastAttemptKind === 'reminder' ? 'reminder_sent' : 'email_sent', at, problem: false };
  }
}

/** "Mario · Laura · +1": the names a card has room for, and how many more there are. */
export function peoplePreview(people: readonly { name: string }[], room = 2): { names: string[]; more: number } {
  return { names: people.slice(0, room).map((p) => p.name), more: Math.max(people.length - room, 0) };
}

export type InvitationPrimaryAction = 'whatsapp' | 'email' | 'copy';

/**
 * A card's ONE primary invitation action; everything else is in its menu.
 * WhatsApp when it opens the group's own chat, else email when it can be sent,
 * else WhatsApp with the host choosing the recipient. Null once invitations
 * are closed or no link can be built.
 */
export function primaryInvitationAction(
  item: Pick<GuestDirectoryGroupItem, 'canShare' | 'canSend' | 'whatsappDirect'>,
): InvitationPrimaryAction | null {
  if (item.canShare && item.whatsappDirect) return 'whatsapp';
  if (item.canSend) return 'email';
  if (item.canShare) return 'whatsapp';
  return null;
}

// ── Routes ──────────────────────────────────────────────────────────────────

export function partyGuestDirectoryPath(partyId: string, query: GuestDirectoryQuery = {}): string {
  const params: [string, string][] = [];
  const q = query.q?.trim();
  if (q) params.push(['q', q]);
  if (query.state && query.state !== 'all') params.push(['state', query.state]);
  if (query.cursor) params.push(['cursor', query.cursor]);
  if (query.take !== undefined && query.take !== null) params.push(['take', String(query.take)]);
  return withQuery(`/api/parties/${partyId}/guest-directory`, params);
}

export function partyInvitationGroupDetailPath(partyId: string, groupId: string): string {
  return partyInvitationGroupPath(partyId, groupId);
}

export function partyInvitationSharePath(partyId: string, groupId: string): string {
  return partyInvitationGroupActionPath(partyId, groupId, 'share');
}

/**
 * The web console's URL state — the search, the filter and the open group — so
 * a reload or Back returns the host to exactly what they were looking at.
 */
export const GUEST_CONSOLE_PARAMS = {
  search: 'guestSearch',
  state: 'guestState',
  group: 'guestGroup',
} as const;
