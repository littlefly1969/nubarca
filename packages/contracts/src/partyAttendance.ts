// Party attendance (NUBARCA-PARTY-CHECKIN-01).
//
// Who ARRIVED, recorded by the host. A third fact beside the RSVP (what a
// person declared) and the PartyParticipant (the anonymous browser at the
// party), bound to neither and never inferred from either: scanning the
// party's QR is not an arrival, and an arrival never changes an RSVP.
//
// The guest list is OPTIONAL. An open party has no groups and records everybody
// as an other arrival; a party with a guest list records the people on it by
// guest and anybody else as an other arrival. Nothing here names a party
// "type" — which kind a party is follows from which rows exist.
//
// Every route is the HOST's, under /api/parties/{partyId}. Neither the party's
// QR token nor a personal invitation token can reach any of it.

import { foldSearchText, type PartyRsvpStatus } from './partyRsvp.ts';

export const PARTY_ATTENDANCE_LIMITS = {
  /** Unicode code points, like every Party text limit. */
  name: 120,
} as const;

/**
 * Where an arrival was first recorded: by the host, or by the guest's own group
 * from its personal invitation ("Sono qui", while the party is live). Both write
 * the same fact, and the first one to succeed sets its moment and its source.
 */
export const PARTY_ATTENDANCE_SOURCES = ['owner', 'invitation'] as const;
export type PartyAttendanceSource = (typeof PARTY_ATTENDANCE_SOURCES)[number];

// ── Owner DTOs (party.access, owner-private) ────────────────────────────────

/**
 * ONE definition per count, separate from `PartyRsvpSummary`. `expectedPeople`
 * counts guests whose RSVP is attending (named or +1); `expectedArrived` those
 * of them who arrived and `expectedMissing` those who have not;
 * `unexpectedKnownGuests` guests on the list who arrived with a pending or
 * declined RSVP; `otherArrivals` people recorded who are not on the list;
 * `totalArrivals` every arrival of either kind.
 */
export interface PartyAttendanceSummary {
  expectedPeople: number;
  expectedArrived: number;
  expectedMissing: number;
  unexpectedKnownGuests: number;
  otherArrivals: number;
  totalArrivals: number;
}

export interface PartyAttendanceGuest {
  guestId: string;
  name: string;
  isAdditionalGuest: boolean;
  /** What the person declared. An arrival never changes it. */
  rsvpStatus: PartyRsvpStatus;
  /** Null: not recorded as arrived. */
  checkedInAt: string | null;
  /** Who recorded it first. Null exactly when `checkedInAt` is. */
  checkInSource: PartyAttendanceSource | null;
}

export interface PartyAttendanceGroup {
  groupId: string;
  label: string;
  /** Named guests in the host's order, then the group's +1s. */
  guests: PartyAttendanceGuest[];
}

/** Somebody recorded as arrived who is not on the guest list. */
export interface PartyAttendanceOtherGuest {
  id: string;
  name: string;
  checkedInAt: string;
  version: number;
}

export interface PartyAttendance {
  partyId: string;
  partyStatus: 'draft' | 'published' | 'live' | 'ended';
  /** Live and Ended: arrivals may be recorded and corrected. */
  canEdit: boolean;
  summary: PartyAttendanceSummary;
  groups: PartyAttendanceGroup[];
  /** Latest first. */
  otherGuests: PartyAttendanceOtherGuest[];
}

/** One "add". The id is minted once per add and reused for its retries. */
export interface PartyAttendanceOtherGuestCreate {
  name: string;
  clientRequestId: string;
}

export interface PartyAttendanceOtherGuestUpdate {
  name: string;
  version: number;
}

// ── Rules ───────────────────────────────────────────────────────────────────

/** Whether the party has a guest list at all — read from the rows, never stored. */
export function hasGuestList(attendance: Pick<PartyAttendance, 'groups'>): boolean {
  return attendance.groups.length > 0;
}

/**
 * "Altri arrivi": everybody who arrived without an attending RSVP — guests on
 * the list who had not confirmed, and people not on it. It is exactly what the
 * `unexpected` filter shows, so Arrivati = (Attesi − Mancano) + Altri arrivi.
 */
export function unexpectedArrivals(summary: PartyAttendanceSummary): number {
  return summary.unexpectedKnownGuests + summary.otherArrivals;
}

export const PARTY_ATTENDANCE_FILTERS = ['all', 'to_arrive', 'arrived', 'unexpected'] as const;
export type PartyAttendanceFilter = (typeof PARTY_ATTENDANCE_FILTERS)[number];

/**
 * The filters worth offering. With no guest list nobody is expected and
 * everybody listed has arrived, so there is nothing to filter by.
 */
export function attendanceFiltersFor(attendance: Pick<PartyAttendance, 'groups'>): PartyAttendanceFilter[] {
  return hasGuestList(attendance) ? [...PARTY_ATTENDANCE_FILTERS] : [];
}

export function guestMatchesAttendanceFilter(guest: PartyAttendanceGuest, filter: PartyAttendanceFilter): boolean {
  const arrived = guest.checkedInAt !== null;
  switch (filter) {
    case 'all': return true;
    case 'to_arrive': return guest.rsvpStatus === 'attending' && !arrived;
    case 'arrived': return arrived;
    case 'unexpected': return arrived && guest.rsvpStatus !== 'attending';
  }
}

/** An other arrival has arrived and was not expected, by definition. */
export function otherGuestMatchesAttendanceFilter(filter: PartyAttendanceFilter): boolean {
  return filter !== 'to_arrive';
}

/**
 * What the door sees for one search and one filter, over the list the page
 * already holds. The search reads a guest's name, the label of their group and
 * an other arrival's name — case- and accent-insensitive. A group appears when
 * at least one of its people does.
 */
export function visibleAttendance(
  attendance: Pick<PartyAttendance, 'groups' | 'otherGuests'>,
  query: string,
  filter: PartyAttendanceFilter,
): { groups: PartyAttendanceGroup[]; otherGuests: PartyAttendanceOtherGuest[] } {
  const needle = foldSearchText(query.trim());
  const matches = (value: string) => needle === '' || foldSearchText(value).includes(needle);
  const groups = attendance.groups
    .map((group) => ({
      ...group,
      guests: group.guests.filter((guest) =>
        guestMatchesAttendanceFilter(guest, filter) && (matches(guest.name) || matches(group.label))),
    }))
    .filter((group) => group.guests.length > 0);
  const otherGuests = otherGuestMatchesAttendanceFilter(filter)
    ? attendance.otherGuests.filter((other) => matches(other.name))
    : [];
  return { groups, otherGuests };
}

// ── Routes ──────────────────────────────────────────────────────────────────

export function partyAttendancePath(partyId: string): string {
  return `/api/parties/${partyId}/attendance`;
}
export function partyAttendanceGuestPath(partyId: string, guestId: string): string {
  return `${partyAttendancePath(partyId)}/guests/${guestId}`;
}
export function partyAttendanceOtherGuestsPath(partyId: string): string {
  return `${partyAttendancePath(partyId)}/other-guests`;
}
export function partyAttendanceOtherGuestPath(partyId: string, attendanceGuestId: string): string {
  return `${partyAttendanceOtherGuestsPath(partyId)}/${attendanceGuestId}`;
}

/** "Sono qui" for one of the group's OWN people, on its personal invitation token. */
export function partyInvitationAttendanceGuestPath(token: string, guestId: string): string {
  return `/api/party-invitations/${encodeURIComponent(token)}/attendance/guests/${guestId}`;
}
