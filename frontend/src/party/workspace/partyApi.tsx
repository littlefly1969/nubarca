import { createContext, useContext, type ReactNode } from 'react';
import {
  checkInPartyGuest,
  createPartyAttendanceGuest,
  createPartyChallenge,
  createPartyInvitationGroup,
  createPartyRsvpQuestion,
  deletePartyAttendanceGuest,
  deletePartyChallenge,
  deletePartyInvitationGroup,
  getAlbumPartySettings,
  getParty,
  getPartyGameSnapshot,
  getPartyInvitationGroup,
  getPartyPrintSettings,
  getPartyRsvpQuestions,
  listAlbumItems,
  listPartyChallenges,
  listPartyGuestbook,
  listPartyGuestContent,
  listPartyMessages,
  listPartyUploads,
  listPrintStations,
  moderatePartyGuestbookEntry,
  moderatePartyMessage,
  moderatePartyUpload,
  partyCrewRoutes,
  planPartyGame,
  queryPartyGuestDirectory,
  remindPartyInvitation,
  reorderPartyChallenges,
  reorderPartyRsvpQuestions,
  rotatePartyInvitationLink,
  sendPartyGameCommand,
  sendPartyInvitation,
  setAlbumPartyMode,
  setAlbumTvVisibility,
  setPartyCovers,
  setPartyGameSettings,
  setPartyContributionSettings,
  setPartyGuestContent,
  setPartyPrintSettings,
  setPartySlideshowSettings,
  sharePartyAddress,
  sharePartyInvitation,
  transitionParty,
  undoPartyGuestCheckIn,
  updateParty,
  updatePartyAttendanceGuest,
  updatePartyChallenge,
  updatePartyInvitationGroup,
  updatePartyRsvpQuestion,
} from '@nubarca/api-client';

// WHO IS ASKING — the one difference between a host's workspace and a
// collaborator's.
//
// The workspace is the same product for both. Same sections, same panels, same
// words, same rules about what is loading and what failed. What differs is the
// family of routes underneath: the host calls `/api/parties/{id}` and
// `/api/albums/{id}/party-*` with their session; a collaborator calls
// `/api/party-crew/*` with a device cookie that already knows which party it
// is, and whose routes therefore carry no ids at all.
//
// So the difference lives HERE, in one object, and nowhere else. Every surface
// calls `usePartyApi()` and passes the ids it has; the crew implementation
// accepts them and ignores them. No section knows which surface it is
// rendering on — which is what stops the two drifting into two products.
//
// THE TYPE IS THE OWNER IMPLEMENTATION. `PartyApi` is derived from `owner`
// below rather than written out beside it, so the two cannot disagree: adding
// a function to the host's set is a compile error in the crew's until it is
// added there too, with the same signature.
//
// BOTH IMPLEMENTATIONS ARE MODULE CONSTANTS, so `usePartyApi()` returns a
// stable identity for the life of a tree. Effects and callbacks that close over
// it therefore never re-run because of it, whether or not it is in their
// dependency list — which is why threading it through the workspace did not
// change when anything reloads.
//
// THE DEFAULT IS THE HOST. A tree with no provider behaves exactly as it did
// before this file existed, so the owner's page, its tests and its fixtures
// needed no change at all.

const owner = {
  getParty,
  updateParty,
  transitionParty,
  setPartyCovers,

  getAlbumPartySettings,
  setAlbumPartyMode,
  setPartyContributionSettings,
  setPartySlideshowSettings,
  setPartyGameSettings,
  setAlbumTvVisibility,
  getPartyPrintSettings,
  setPartyPrintSettings,
  listPrintStations,

  listPartyGuestContent,
  setPartyGuestContent,

  queryPartyGuestDirectory,

  /**
   * The aggregate counts, for a surface with no guest list.
   *
   * The HOST reads them from the directory they already have; a Party Crew role
   * without `guests.read` reads them from a route that answers the summary and
   * nothing else. Same shape either way, so the console does not branch.
   */
  getGuestCounts: (partyId: string, signal?: AbortSignal) =>
    queryPartyGuestDirectory(partyId, { take: 0 }, signal)
      .then((page) => ({ summary: page.summary })),

  getPartyInvitationGroup,
  getPartyRsvpQuestions,
  createPartyInvitationGroup,
  updatePartyInvitationGroup,
  deletePartyInvitationGroup,
  rotatePartyInvitationLink,
  sendPartyInvitation,
  remindPartyInvitation,
  sharePartyInvitation,
  createPartyRsvpQuestion,
  updatePartyRsvpQuestion,
  reorderPartyRsvpQuestions,

  checkInPartyGuest,
  undoPartyGuestCheckIn,
  createPartyAttendanceGuest,
  updatePartyAttendanceGuest,
  deletePartyAttendanceGuest,

  listPartyUploads,
  moderatePartyUpload,
  listPartyMessages,
  moderatePartyMessage,
  listPartyGuestbook,
  moderatePartyGuestbookEntry,

  /**
   * WHERE THE PARTY IS, ready to hand to somebody.
   *
   * Nothing about it reads a guest, a group, an invitation or an RSVP — which
   * is the point of it. `details.manage` on the crew side; the host always
   * holds it over their own party.
   */
  sharePartyAddress,

  listAlbumItems,
  listPartyChallenges,
  createPartyChallenge,
  updatePartyChallenge,
  deletePartyChallenge,
  reorderPartyChallenges,
  getPartyGameSnapshot,
  sendPartyGameCommand,
  planPartyGame,

  /**
   * Whether this surface may do a thing.
   *
   * The HOST may do everything their own permissions allow, and the server is
   * the judge of that — so this is always true for them. A Party Crew surface
   * answers from the capabilities the server sent back with its session, which
   * is how a panel hides a control a role does not hold instead of drawing one
   * that answers 404. It is never the authority: every route re-checks.
   */
  can: (_capability: string) => true,
};

export type PartyApi = typeof owner & {
  /**
   * Whether this surface is the party's OWNER.
   *
   * Read only where the product genuinely differs — Impostazioni, which is the
   * host's alone — and never as a substitute for a capability. What a
   * collaborator may do is what the server granted them, not this flag.
   */
  readonly isOwner: boolean;
};

/** The host's own workspace: the routes that have always existed. */
export const ownerPartyApi: PartyApi = { ...owner, isOwner: true };

/**
 * The same party, reached by a paired Party Crew device.
 *
 * A FACTORY and not a constant, because a crew surface is opened at exactly one
 * party and holds exactly one set of capabilities. Binding both here is what
 * lets every function keep the owner's signature — the ids they are handed are
 * accepted and ignored, and the bound party is the truth.
 */
export function crewPartyApi(partyId: string, capabilities: readonly string[]): PartyApi {
  const routes = partyCrewRoutes(partyId);
  return {
    ...routes,
    isOwner: false,
    can: (capability: string) => capabilities.includes(capability),
  };
}

/**
 * Where a section's "open the queue" row points.
 *
 * The host's queues are album-scoped routes under their authenticated shell;
 * the crew's are party-scoped routes under theirs. Same three destinations,
 * same three names, decided in one place so a section does not have to know
 * which surface it is on.
 */
export function partyDeepLink(
  api: PartyApi,
  kind: 'photos' | 'messages' | 'game' | 'guestbook',
  partyId: string,
  albumId: string,
): string {
  // THE BOOK IS THE PARTY'S, not the album's, so its queue is named by the
  // party on BOTH surfaces — the one destination here that does not follow the
  // album-scoped shape, because the resource does not either.
  if (kind === 'guestbook') {
    return api.isOwner ? `/parties/${partyId}/guestbook` : `/party/crew/${partyId}/guestbook`;
  }
  return api.isOwner
    ? `/albums/${albumId}/party-${kind === 'photos' ? 'uploads' : kind}?party=${partyId}`
    : `/party/crew/${partyId}/${kind}`;
}

const PartyApiContext = createContext<PartyApi>(ownerPartyApi);

export function PartyApiProvider({ api, children }: { api: PartyApi; children: ReactNode }) {
  return <PartyApiContext.Provider value={api}>{children}</PartyApiContext.Provider>;
}

export function usePartyApi(): PartyApi {
  return useContext(PartyApiContext);
}
