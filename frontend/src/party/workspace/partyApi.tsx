import { createContext, useContext, type ReactNode } from 'react';
import {
  checkInPartyGuest,
  createPartyAttendanceGuest,
  createPartyChallenge,
  createPartyInvitationGroup,
  createPartyRsvpQuestion,
  crewGetPartyPrintSettings,
  crewListAlbumItems,
  crewListPrintStations,
  crewSetPartyGameSettings,
  crewSetPartyPrintSettings,
  crewCheckInPartyGuest,
  crewCreatePartyAttendanceGuest,
  crewCreatePartyChallenge,
  crewCreatePartyInvitationGroup,
  crewCreatePartyRsvpQuestion,
  crewDeletePartyAttendanceGuest,
  crewDeletePartyChallenge,
  crewDeletePartyInvitationGroup,
  crewGetAlbumPartySettings,
  crewGetParty,
  crewGetPartyGameSnapshot,
  crewGetPartyInvitationGroup,
  crewGetPartyRsvpQuestions,
  crewListPartyChallenges,
  crewListPartyGuestContent,
  crewListPartyMessages,
  crewListPartyUploads,
  crewModeratePartyMessage,
  crewModeratePartyUpload,
  crewPlanPartyGame,
  crewQueryPartyGuestDirectory,
  crewRemindPartyInvitation,
  crewReorderPartyChallenges,
  crewReorderPartyRsvpQuestions,
  crewRotatePartyInvitationLink,
  crewSendPartyGameCommand,
  crewSendPartyInvitation,
  crewSetAlbumPartyMode,
  crewSetAlbumTvVisibility,
  crewSetPartyCovers,
  crewSetPartyGuestContent,
  crewSetPartySlideshowSettings,
  crewSharePartyInvitation,
  crewTransitionParty,
  crewUndoPartyGuestCheckIn,
  crewUpdatePartyAttendanceGuest,
  crewUpdatePartyChallenge,
  crewUpdatePartyInvitationGroup,
  crewUpdatePartyDetails,
  crewUpdatePartyRsvpQuestion,
  deletePartyAttendanceGuest,
  deletePartyChallenge,
  deletePartyInvitationGroup,
  getAlbumPartySettings,
  getParty,
  getPartyPrintSettings,
  getPartyGameSnapshot,
  getPartyInvitationGroup,
  getPartyRsvpQuestions,
  listAlbumItems,
  listPartyChallenges,
  listPartyGuestContent,
  listPrintStations,
  listPartyMessages,
  listPartyUploads,
  moderatePartyMessage,
  moderatePartyUpload,
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
  setPartyGuestContent,
  setPartyPrintSettings,
  setPartySlideshowSettings,
  sharePartyInvitation,
  transitionParty,
  undoPartyGuestCheckIn,
  updatePartyAttendanceGuest,
  updatePartyChallenge,
  updatePartyInvitationGroup,
  updatePartyRsvpQuestion,
  updateParty,
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
  setPartySlideshowSettings,
  setPartyGameSettings,
  setAlbumTvVisibility,
  getPartyPrintSettings,
  setPartyPrintSettings,
  listPrintStations,

  listPartyGuestContent,
  setPartyGuestContent,

  queryPartyGuestDirectory,
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

  listAlbumItems,
  listPartyChallenges,
  createPartyChallenge,
  updatePartyChallenge,
  deletePartyChallenge,
  reorderPartyChallenges,
  getPartyGameSnapshot,
  sendPartyGameCommand,
  planPartyGame,
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

/** The same party, reached by a paired Party Crew device. */
export const crewPartyApi: PartyApi = {
  getParty: crewGetParty,
  updateParty: crewUpdatePartyDetails,
  transitionParty: crewTransitionParty,
  setPartyCovers: crewSetPartyCovers,

  getAlbumPartySettings: crewGetAlbumPartySettings,
  setAlbumPartyMode: crewSetAlbumPartyMode,
  setPartySlideshowSettings: crewSetPartySlideshowSettings,
  setPartyGameSettings: crewSetPartyGameSettings,
  setAlbumTvVisibility: crewSetAlbumTvVisibility,
  getPartyPrintSettings: crewGetPartyPrintSettings,
  setPartyPrintSettings: crewSetPartyPrintSettings,
  listPrintStations: crewListPrintStations,

  listPartyGuestContent: crewListPartyGuestContent,
  setPartyGuestContent: crewSetPartyGuestContent,

  queryPartyGuestDirectory: crewQueryPartyGuestDirectory,
  getPartyInvitationGroup: crewGetPartyInvitationGroup,
  getPartyRsvpQuestions: crewGetPartyRsvpQuestions,
  createPartyInvitationGroup: crewCreatePartyInvitationGroup,
  updatePartyInvitationGroup: crewUpdatePartyInvitationGroup,
  deletePartyInvitationGroup: crewDeletePartyInvitationGroup,
  rotatePartyInvitationLink: crewRotatePartyInvitationLink,
  sendPartyInvitation: crewSendPartyInvitation,
  remindPartyInvitation: crewRemindPartyInvitation,
  sharePartyInvitation: crewSharePartyInvitation,
  createPartyRsvpQuestion: crewCreatePartyRsvpQuestion,
  updatePartyRsvpQuestion: crewUpdatePartyRsvpQuestion,
  reorderPartyRsvpQuestions: crewReorderPartyRsvpQuestions,

  checkInPartyGuest: crewCheckInPartyGuest,
  undoPartyGuestCheckIn: crewUndoPartyGuestCheckIn,
  createPartyAttendanceGuest: crewCreatePartyAttendanceGuest,
  updatePartyAttendanceGuest: crewUpdatePartyAttendanceGuest,
  deletePartyAttendanceGuest: crewDeletePartyAttendanceGuest,

  listPartyUploads: crewListPartyUploads,
  moderatePartyUpload: crewModeratePartyUpload,
  listPartyMessages: crewListPartyMessages,
  moderatePartyMessage: crewModeratePartyMessage,

  listAlbumItems: crewListAlbumItems,
  listPartyChallenges: crewListPartyChallenges,
  createPartyChallenge: crewCreatePartyChallenge,
  updatePartyChallenge: crewUpdatePartyChallenge,
  deletePartyChallenge: crewDeletePartyChallenge,
  reorderPartyChallenges: crewReorderPartyChallenges,
  getPartyGameSnapshot: crewGetPartyGameSnapshot,
  sendPartyGameCommand: crewSendPartyGameCommand,
  planPartyGame: crewPlanPartyGame,

  isOwner: false,
};

/**
 * Where a section's "open the queue" row points.
 *
 * The host's queues are album-scoped routes under their authenticated shell;
 * the crew's are party-scoped routes under theirs. Same three destinations,
 * same three names, decided in one place so a section does not have to know
 * which surface it is on.
 */
export function partyDeepLink(
  api: PartyApi, kind: 'photos' | 'messages' | 'game', partyId: string, albumId: string,
): string {
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
