import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  PARTY_ATTENDANCE_LIMITS,
  PARTY_ATTENDANCE_SOURCES,
  partyAttendanceGuestPath,
  partyAttendanceOtherGuestPath,
  partyAttendanceOtherGuestsPath,
  partyAttendancePath,
  partyInvitationAttendanceGuestPath,
  unexpectedArrivals,
} from './index.ts';

// Who a search or a filter shows is the guest directory's to say, in the
// database — see partyGuestDirectory.test.ts for the filters a phase offers.

test('the vocabularies are the server’s, and closed', () => {
  assert.deepEqual([...PARTY_ATTENDANCE_SOURCES], ['owner', 'invitation']);
  assert.equal(PARTY_ATTENDANCE_LIMITS.name, 120);
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
