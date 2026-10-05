import type { PartyInvitationViewModel } from '@nubarca/api-client';

/**
 * Whether opening this invitation should lead straight into the party: when it
 * has nothing left to ask. During the party that is once everybody of the
 * group who is coming has arrived — a group still arriving keeps its "Sono qui"
 * — and afterwards it is always, the party being its memories. Before the
 * party an invitation is an invitation. Only where the party's page really
 * opens (`partyUrl`).
 */
export function invitationEntersParty(view: PartyInvitationViewModel): boolean {
  const { party, invitation } = view;
  if (!party.partyUrl) return false;
  if (party.phase === 'after') return true;
  if (party.phase !== 'live') return false;
  const coming = invitation.guests.filter((guest) => guest.status !== 'declined');
  return coming.length > 0 && coming.every((guest) => guest.checkedInAt !== null);
}
