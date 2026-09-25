import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  GUEST_CONSOLE_PARAMS,
  GUEST_DIRECTORY_LIMITS,
  GUEST_DIRECTORY_STATES,
  LEGACY_GUEST_SEARCH_PARAM,
  PARTY_INVITATION_DELIVERY_CHANNELS,
  PARTY_INVITATION_DELIVERY_STATUSES,
  PARTY_INVITATION_SHARE_CHANNELS,
  guestDirectoryItemKey,
  guestDirectoryStatesFor,
  invitationLine,
  isAttendancePhase,
  isGuestDirectoryState,
  partyGuestDirectoryQueryPath,
  partyInvitationGroupDetailPath,
  partyInvitationSharePath,
  peoplePreview,
  primaryInvitationAction,
  primaryInvitationLabel,
  type GuestDirectoryGroupItem,
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
  assert.deepEqual(guestDirectoryStatesFor('draft', true), ['all', 'attending', 'pending', 'declined', 'not_invited']);
  assert.deepEqual(guestDirectoryStatesFor('published', true), ['all', 'attending', 'pending', 'declined', 'not_invited']);
  assert.deepEqual(guestDirectoryStatesFor('live', true), ['all', 'attending', 'arrived', 'to_arrive', 'unexpected']);
  assert.deepEqual(guestDirectoryStatesFor('ended', true), ['all', 'attending', 'arrived', 'to_arrive', 'unexpected']);
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

test('the directory is read at ONE address, which carries no query at all', () => {
  // The search travels in the body. There is no helper that puts it in a URL,
  // and this route takes no parameters to put it in.
  assert.equal(partyGuestDirectoryQueryPath('p1'), '/api/parties/p1/guest-directory/query');
  assert.equal(partyGuestDirectoryQueryPath('p1').includes('?'), false);
  assert.equal(partyInvitationGroupDetailPath('p1', 'g1'), '/api/parties/p1/invitation-groups/g1');
  assert.equal(partyInvitationSharePath('p1', 'g1'), '/api/parties/p1/invitation-groups/g1/share');
});

test('the console keeps its filter and its open group in the URL, and never its search', () => {
  // Exhaustive on purpose: adding a key here would fail this line, which is
  // where somebody would otherwise put the search back. The type says the same
  // thing at compile time — the union of these values no longer admits
  // 'guestSearch', so even comparing them is an error.
  assert.deepEqual(GUEST_CONSOLE_PARAMS, { state: 'guestState', group: 'guestGroup' });
  // The old key is still named — so it can be recognised and removed.
  assert.equal(LEGACY_GUEST_SEARCH_PARAM, 'guestSearch');
});

test('the primary button says which email it is about to send', () => {
  const group = { canShare: true, canSend: true, whatsappDirect: false };
  const invitation = (state: string) => ({
    state, lastAttemptAt: null, lastAttemptKind: null, lastAttemptStatus: null,
    lastSentAt: null, lastAttemptChannel: null,
  } as GuestDirectoryGroupItem['invitation']);

  assert.equal(primaryInvitationLabel({ ...group, invitation: invitation('not_sent') }), 'email_first');
  assert.equal(primaryInvitationLabel({ ...group, invitation: invitation('sent') }), 'email_again');
  // A link shared on WhatsApp or copied has also gone out: the next email is
  // another copy of an invitation the group already holds.
  assert.equal(primaryInvitationLabel({ ...group, invitation: invitation('shared') }), 'email_again');
  // The other actions say what they do; there is nothing ambiguous to resolve.
  assert.equal(primaryInvitationLabel({
    canShare: true, canSend: true, whatsappDirect: true, invitation: invitation('not_sent'),
  }), 'whatsapp');
  assert.equal(primaryInvitationLabel({
    canShare: false, canSend: false, whatsappDirect: true, invitation: invitation('sent'),
  }), null);
});
