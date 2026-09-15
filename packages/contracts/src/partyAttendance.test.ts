import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  PARTY_ATTENDANCE_FILTERS,
  PARTY_ATTENDANCE_LIMITS,
  PARTY_ATTENDANCE_SOURCES,
  attendanceFiltersFor,
  guestMatchesAttendanceFilter,
  hasGuestList,
  partyAttendanceGuestPath,
  partyAttendanceOtherGuestPath,
  partyAttendanceOtherGuestsPath,
  partyAttendancePath,
  partyInvitationAttendanceGuestPath,
  unexpectedArrivals,
  visibleAttendance,
  type PartyAttendance,
  type PartyAttendanceGuest,
} from './index.ts';

const guest = (
  guestId: string, name: string, rsvpStatus: PartyAttendanceGuest['rsvpStatus'], arrived: boolean,
): PartyAttendanceGuest => ({
  guestId, name, isAdditionalGuest: false, rsvpStatus,
  checkedInAt: arrived ? '2027-06-12T18:00:00Z' : null,
  checkInSource: arrived ? 'owner' : null,
});

const invited: Pick<PartyAttendance, 'groups' | 'otherGuests'> = {
  groups: [
    {
      groupId: 'rossi', label: 'Famiglia Rossi',
      guests: [guest('mario', 'Mario', 'attending', false), guest('luisa', 'Luisa', 'declined', true)],
    },
    { groupId: 'verdi', label: 'Casa Verdi', guests: [guest('sara', 'Sara', 'pending', true), guest('ugo', 'Ugo', 'attending', true)] },
  ],
  otherGuests: [{ id: 'o1', name: 'Nicolò Esterno', checkedInAt: '2027-06-12T18:30:00Z', version: 1 }],
};

const names = (shown: ReturnType<typeof visibleAttendance>) => [
  ...shown.groups.flatMap((g) => g.guests.map((x) => x.name)),
  ...shown.otherGuests.map((o) => o.name),
];

test('the vocabularies are the server’s, and closed', () => {
  assert.deepEqual([...PARTY_ATTENDANCE_SOURCES], ['owner', 'invitation']);
  assert.deepEqual([...PARTY_ATTENDANCE_FILTERS], ['all', 'to_arrive', 'arrived', 'unexpected']);
  assert.equal(PARTY_ATTENDANCE_LIMITS.name, 120);
});

test('a guest list is a fact about the rows, and an open party has nothing to filter by', () => {
  assert.equal(hasGuestList({ groups: [] }), false);
  assert.deepEqual(attendanceFiltersFor({ groups: [] }), []);
  assert.equal(hasGuestList(invited), true);
  assert.deepEqual(attendanceFiltersFor(invited), ['all', 'to_arrive', 'arrived', 'unexpected']);
});

test('the filters follow one definition each', () => {
  const [mario, luisa] = invited.groups[0].guests;
  assert.equal(guestMatchesAttendanceFilter(mario, 'to_arrive'), true);
  assert.equal(guestMatchesAttendanceFilter(mario, 'arrived'), false);
  assert.equal(guestMatchesAttendanceFilter(luisa, 'to_arrive'), false);
  assert.equal(guestMatchesAttendanceFilter(luisa, 'unexpected'), true);

  assert.deepEqual(names(visibleAttendance(invited, '', 'all')), ['Mario', 'Luisa', 'Sara', 'Ugo', 'Nicolò Esterno']);
  assert.deepEqual(names(visibleAttendance(invited, '', 'to_arrive')), ['Mario']);
  assert.deepEqual(names(visibleAttendance(invited, '', 'arrived')), ['Luisa', 'Sara', 'Ugo', 'Nicolò Esterno']);
  // Unexpected: on the list without having confirmed, or not on it at all.
  assert.deepEqual(names(visibleAttendance(invited, '', 'unexpected')), ['Luisa', 'Sara', 'Nicolò Esterno']);
});

test('one search reads a person’s name, their group’s label and an other arrival’s name', () => {
  assert.deepEqual(names(visibleAttendance(invited, 'verdi', 'all')), ['Sara', 'Ugo']);
  assert.deepEqual(names(visibleAttendance(invited, 'LUISA', 'all')), ['Luisa']);
  // Accents fold: a host typing "nicolo" is looking for Nicolò.
  assert.deepEqual(names(visibleAttendance(invited, 'nicolo', 'all')), ['Nicolò Esterno']);
  // And the search narrows the filter, never widens it.
  assert.deepEqual(names(visibleAttendance(invited, 'verdi', 'to_arrive')), []);
  // A group with nobody left to show is not shown.
  assert.deepEqual(visibleAttendance(invited, 'mario', 'all').groups.map((g) => g.groupId), ['rossi']);
});

test('"Altri arrivi" is everybody who came without being expected', () => {
  const summary = {
    expectedPeople: 2, expectedArrived: 1, expectedMissing: 1,
    unexpectedKnownGuests: 2, otherArrivals: 1, totalArrivals: 4,
  };
  assert.equal(unexpectedArrivals(summary), 3);
  assert.equal(summary.totalArrivals, summary.expectedArrived + unexpectedArrivals(summary));
});

test('every route is the host’s, except the group’s own "Sono qui"', () => {
  assert.equal(partyAttendancePath('p1'), '/api/parties/p1/attendance');
  assert.equal(partyAttendanceGuestPath('p1', 'g1'), '/api/parties/p1/attendance/guests/g1');
  assert.equal(partyAttendanceOtherGuestsPath('p1'), '/api/parties/p1/attendance/other-guests');
  assert.equal(partyAttendanceOtherGuestPath('p1', 'o1'), '/api/parties/p1/attendance/other-guests/o1');
  assert.equal(
    partyInvitationAttendanceGuestPath('a/b', 'g1'),
    '/api/party-invitations/a%2Fb/attendance/guests/g1',
  );
});
