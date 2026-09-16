import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  GUEST_CONSOLE_PARAMS,
  GUEST_DIRECTORY_LIMITS,
  GUEST_DIRECTORY_STATES,
  PARTY_INVITATION_DELIVERY_CHANNELS,
  PARTY_INVITATION_DELIVERY_STATUSES,
  PARTY_INVITATION_SHARE_CHANNELS,
  guestDirectoryItemKey,
  guestDirectoryStatesFor,
  invitationLine,
  isAttendancePhase,
  isGuestDirectoryState,
  partyGuestDirectoryPath,
  partyInvitationGroupDetailPath,
  partyInvitationSharePath,
  peoplePreview,
  primaryInvitationAction,
  type PartyInvitationDeliveryView,
} from './index.ts';

const view = (over: Partial<PartyInvitationDeliveryView> = {}): PartyInvitationDeliveryView => ({
  state: 'not_sent', lastAttemptAt: null, lastAttemptKind: null, lastAttemptStatus: null,
  lastSentAt: null, lastAttemptChannel: null, ...over,
});

test('the vocabularies are the server’s, and closed', () => {
  assert.deepEqual([...GUEST_DIRECTORY_STATES],
    ['all', 'pending', 'attending', 'declined', 'not_invited', 'to_arrive', 'arrived', 'unexpected']);
  assert.deepEqual([...PARTY_INVITATION_DELIVERY_CHANNELS], ['email', 'whatsapp', 'copy']);
  assert.deepEqual([...PARTY_INVITATION_SHARE_CHANNELS], ['whatsapp', 'copy']);
  // A share is its own status. There is no "delivered" and no "read".
  assert.deepEqual([...PARTY_INVITATION_DELIVERY_STATUSES], ['pending', 'sent', 'failed', 'shared']);
  assert.deepEqual(GUEST_DIRECTORY_LIMITS, { defaultTake: 40, maxTake: 100, query: 120 });
  assert.equal(isGuestDirectoryState('to_arrive'), true);
  assert.equal(isGuestDirectoryState('maybe'), false);
  assert.equal(isGuestDirectoryState(null), false);
});

test('each phase offers its own filters, and an open party none', () => {
  assert.deepEqual(guestDirectoryStatesFor('draft', true), ['all', 'pending', 'attending', 'declined', 'not_invited']);
  assert.deepEqual(guestDirectoryStatesFor('published', true), ['all', 'pending', 'attending', 'declined', 'not_invited']);
  assert.deepEqual(guestDirectoryStatesFor('live', true), ['all', 'to_arrive', 'arrived', 'unexpected']);
  assert.deepEqual(guestDirectoryStatesFor('ended', true), ['all', 'to_arrive', 'arrived', 'unexpected']);
  assert.deepEqual(guestDirectoryStatesFor('live', false), []);
  assert.deepEqual(guestDirectoryStatesFor('draft', false), []);
  assert.equal(isAttendancePhase('live'), true);
  assert.equal(isAttendancePhase('published'), false);
});

test('a card says one line about its invitation, and a share never claims a send', () => {
  assert.deepEqual(invitationLine(view()), { kind: 'not_shared', at: null, problem: false });
  const at = '2027-06-01T10:00:00Z';
  assert.deepEqual(
    invitationLine(view({ state: 'shared', lastAttemptChannel: 'whatsapp', lastAttemptStatus: 'shared', lastAttemptKind: 'initial', lastAttemptAt: at })),
    { kind: 'whatsapp_shared', at, problem: false });
  assert.equal(
    invitationLine(view({ state: 'shared', lastAttemptChannel: 'copy', lastAttemptStatus: 'shared', lastAttemptKind: 'resend', lastAttemptAt: at })).kind,
    'link_copied');
  assert.equal(
    invitationLine(view({ state: 'sent', lastAttemptChannel: 'email', lastAttemptStatus: 'sent', lastAttemptKind: 'initial', lastAttemptAt: at })).kind,
    'email_sent');
  assert.equal(
    invitationLine(view({ state: 'sent', lastAttemptChannel: 'email', lastAttemptStatus: 'sent', lastAttemptKind: 'reminder', lastAttemptAt: at })).kind,
    'reminder_sent');
  // The latest attempt is what the card shows, even over an earlier success.
  assert.deepEqual(
    invitationLine(view({ state: 'sent', lastAttemptChannel: 'email', lastAttemptStatus: 'failed', lastAttemptKind: 'resend', lastAttemptAt: at })),
    { kind: 'email_failed', at, problem: true });
  assert.deepEqual(
    invitationLine(view({ state: 'pending', lastAttemptChannel: 'email', lastAttemptStatus: 'pending', lastAttemptKind: 'initial', lastAttemptAt: at })),
    { kind: 'email_pending', at, problem: true });
});

test('a card previews two names and counts the rest', () => {
  const people = [{ name: 'Mario' }, { name: 'Laura' }, { name: 'Marco' }, { name: 'Giulia' }];
  assert.deepEqual(peoplePreview(people), { names: ['Mario', 'Laura'], more: 2 });
  assert.deepEqual(peoplePreview(people.slice(0, 1)), { names: ['Mario'], more: 0 });
  assert.deepEqual(peoplePreview([]), { names: [], more: 0 });
});

test('a card offers one primary invitation action, WhatsApp first when it opens the chat', () => {
  assert.equal(primaryInvitationAction({ canShare: true, canSend: true, whatsappDirect: true }), 'whatsapp');
  assert.equal(primaryInvitationAction({ canShare: true, canSend: true, whatsappDirect: false }), 'email');
  assert.equal(primaryInvitationAction({ canShare: true, canSend: false, whatsappDirect: false }), 'whatsapp');
  assert.equal(primaryInvitationAction({ canShare: false, canSend: false, whatsappDirect: true }), null);
});

test('items keep distinct keys across kinds', () => {
  assert.equal(guestDirectoryItemKey({
    kind: 'other', id: 'x', name: 'Zoë', checkedInAt: '2027-06-01T10:00:00Z', version: 1,
  }), 'o:x');
});

test('the routes, with the search encoded and the defaults left out', () => {
  assert.equal(partyGuestDirectoryPath('p1'), '/api/parties/p1/guest-directory');
  assert.equal(
    partyGuestDirectoryPath('p1', { q: '  Nicolò Rossi ', state: 'pending', cursor: 'a+b/c', take: 40 }),
    '/api/parties/p1/guest-directory?q=Nicol%C3%B2%20Rossi&state=pending&cursor=a%2Bb%2Fc&take=40');
  assert.equal(partyGuestDirectoryPath('p1', { q: '   ', state: 'all', take: 0 }), '/api/parties/p1/guest-directory?take=0');
  assert.equal(partyInvitationGroupDetailPath('p1', 'g1'), '/api/parties/p1/invitation-groups/g1');
  assert.equal(partyInvitationSharePath('p1', 'g1'), '/api/parties/p1/invitation-groups/g1/share');
  assert.deepEqual(GUEST_CONSOLE_PARAMS, { search: 'guestSearch', state: 'guestState', group: 'guestGroup' });
});
