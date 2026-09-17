import {
  type InvitationShareChannel,
  type InvitationShareResult,
  type Party,
} from '@nubarca/api-client';
import { newClientRequestId } from './clientRequestId';
import type { PartyApi } from './workspace/partyApi';
import { copyWhenReady, openExternal } from './guestShare';

// ONE TAP OF "WhatsApp" OR "Copia link".
//
// The server records the share and composes the link; the browser then does
// the one thing it can: open WhatsApp, or put the link on the clipboard. Both
// can be refused — a blocked window, a clipboard that only accepts writes
// inside the tap — so the outcome says what actually happened and the console
// offers the link itself as the way through.
//
// The link and the message live in this outcome, in memory, for as long as the
// host needs them. They are never stored, never put in a URL and never logged:
// the ledger records that a link was shared, not what it said.

export interface ShareOutcome {
  groupId: string;
  channel: InvitationShareChannel;
  url: string;
  whatsappUrl: string | null;
  /** WhatsApp actually opened. */
  opened: boolean;
  /** The link reached the clipboard. */
  copied: boolean;
}

export interface SharedInvitation {
  result: InvitationShareResult<Party>;
  outcome: ShareOutcome;
}

export async function shareInvitation(
  // The one call, handed in rather than imported: a collaborator shares the
  // same invitation through their own route family, and this helper does not
  // need to know which one it was given.
  share: PartyApi['sharePartyInvitation'],
  partyId: string,
  groupId: string,
  channel: InvitationShareChannel,
  partyVersion: number,
): Promise<SharedInvitation> {
  // One id per tap, reused by that tap's retries: the same click never records
  // a second share.
  const pending = share(partyId, groupId, {
    channel, clientRequestId: newClientRequestId(), partyVersion,
  });

  if (channel === 'copy') {
    // Started NOW, inside the tap, and resolved when the server answers —
    // which is the only way Safari allows a clipboard write across a request.
    const copying = copyWhenReady(pending.then((answer) => answer.share.url));
    const result = await pending;
    return {
      result,
      outcome: {
        groupId, channel, url: result.share.url, whatsappUrl: null, opened: false, copied: await copying,
      },
    };
  }

  const result = await pending;
  const whatsappUrl = result.share.whatsappUrl;
  return {
    result,
    outcome: {
      groupId,
      channel,
      url: result.share.url,
      whatsappUrl,
      opened: whatsappUrl !== null && openExternal(whatsappUrl),
      copied: false,
    },
  };
}
