import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  PARTY_INVITATION_LIMITS,
  PARTY_RSVP_QUESTION_KINDS,
  PARTY_RSVP_STATUSES,
  isPlausibleEmail,
  normalizeQuestionOptions,
  partyGuestListPath,
  partyInvitationGroupActionPath,
  partyInvitationPath,
  partyInvitationRsvpPath,
  partyRsvpQuestionOrderPath,
  rsvpFormProblems,
  type PartyInvitationQuestion,
} from './index.ts';

test('the vocabularies are the server’s, and closed', () => {
  assert.deepEqual([...PARTY_RSVP_STATUSES], ['pending', 'attending', 'declined']);
  assert.deepEqual([...PARTY_RSVP_QUESTION_KINDS], ['short_text', 'single_choice', 'yes_no']);
  assert.equal(PARTY_INVITATION_LIMITS.additionalGuests, 10);
  assert.equal(PARTY_INVITATION_LIMITS.activeQuestions, 20);
});

test('an address is one plainly written mailbox', () => {
  for (const ok of ['mario@example.com', ' mario.rossi+festa@posta.example.it ']) {
    assert.equal(isPlausibleEmail(ok), true, ok);
  }
  for (const bad of ['', 'mario', 'mario@localhost', 'mario @example.com', 'mario@@example.com', '@example.com']) {
    assert.equal(isPlausibleEmail(bad), false, bad);
  }
});

test('a single choice needs two to twenty distinct options; other kinds none', () => {
  assert.deepEqual(normalizeQuestionOptions('single_choice', [' Carne ', 'Pesce']), ['Carne', 'Pesce']);
  assert.equal(normalizeQuestionOptions('single_choice', ['Carne']), null);
  assert.equal(normalizeQuestionOptions('single_choice', ['Carne', 'carne']), null);
  assert.equal(normalizeQuestionOptions('single_choice', ['Carne', '  ']), null);
  assert.equal(normalizeQuestionOptions('single_choice', Array.from({ length: 21 }, (_, i) => `P${i}`)), null);
  assert.deepEqual(normalizeQuestionOptions('yes_no', null), []);
  assert.equal(normalizeQuestionOptions('yes_no', ['Sì', 'No']), null);
});

const menu: PartyInvitationQuestion = {
  id: 'q-menu', prompt: 'Carne o pesce?', kind: 'single_choice', required: true,
  options: ['Carne', 'Pesce'], answer: null,
};

test('required means required of a group that is coming', () => {
  const invitation = { maxAdditionalGuests: 1, questions: [menu] };
  const declining = {
    guests: [{ guestId: 'a', status: 'declined' as const, dietaryNotes: null }],
    additionalGuests: [], answers: [],
  };
  assert.deepEqual(rsvpFormProblems(invitation, declining), []);

  const coming = { ...declining, guests: [{ guestId: 'a', status: 'attending' as const, dietaryNotes: null }] };
  assert.deepEqual(rsvpFormProblems(invitation, coming), ['required_answer_missing']);
  assert.deepEqual(
    rsvpFormProblems(invitation, { ...coming, answers: [{ questionId: 'q-menu', value: 'Carne' }] }), []);
  // A blank text is not an answer.
  assert.deepEqual(
    rsvpFormProblems(invitation, { ...coming, answers: [{ questionId: 'q-menu', value: '   ' }] }),
    ['required_answer_missing']);
});

test('a +1 comes with somebody, has a name, and fits the allowance', () => {
  const invitation = { maxAdditionalGuests: 1, questions: [] };
  const extras = [{ name: 'Giulia', dietaryNotes: null }, { name: ' ', dietaryNotes: null }];
  assert.deepEqual(
    rsvpFormProblems(invitation, {
      guests: [{ guestId: 'a', status: 'declined', dietaryNotes: null }], additionalGuests: extras, answers: [],
    }),
    ['additional_guests_need_attendee', 'too_many_additional_guests', 'additional_guest_name_missing']);
});

test('over-long text is caught in code points, as the server counts', () => {
  const invitation = { maxAdditionalGuests: 0, questions: [] };
  const fits = '🎉'.repeat(PARTY_INVITATION_LIMITS.dietaryNotes);
  const reply = (notes: string) => ({
    guests: [{ guestId: 'a', status: 'attending' as const, dietaryNotes: notes }], additionalGuests: [], answers: [],
  });
  assert.deepEqual(rsvpFormProblems(invitation, reply(fits)), []);
  assert.deepEqual(rsvpFormProblems(invitation, reply(`${fits}!`)), ['text_too_long']);
});

test('two capabilities, two route families', () => {
  assert.equal(partyGuestListPath('p1'), '/api/parties/p1/guest-list');
  assert.equal(partyInvitationGroupActionPath('p1', 'g1', 'remind'), '/api/parties/p1/invitation-groups/g1/remind');
  assert.equal(partyInvitationGroupActionPath('p1', 'g1', 'share'), '/api/parties/p1/invitation-groups/g1/share');
  assert.equal(partyRsvpQuestionOrderPath('p1'), '/api/parties/p1/rsvp-questions/order');
  assert.equal(partyInvitationPath('a/b'), '/api/party-invitations/a%2Fb');
  assert.equal(partyInvitationRsvpPath('tok'), '/api/party-invitations/tok/rsvp');
  assert.ok(!partyInvitationPath('tok').startsWith('/api/party/'));
});
