import { api } from './client';

// Party Crew: the host's collaborator panel, the pairing surface, and the
// collaborator's own session.
//
// NOTHING here ever holds a raw token in JavaScript. The invite link's token is
// read once from the URL fragment by the page that received it and posted
// straight back; the challenge and the device are HttpOnly cookies the browser
// manages and this module never sees. There is no localStorage, no
// sessionStorage and no IndexedDB anywhere in this file, deliberately.

// ── What the HOST sees ──────────────────────────────────────────────────────

/** One paired device, in the only terms anybody needs to recognise it. */
export interface PartyCrewDevice {
  grantId: string;
  label: string;
  pairedAt: string;
  lastUsedAt: string | null;
  /** Only ever true in the crew's own view of itself. */
  isCurrent: boolean;
}

export interface PartyCollaborator {
  id: string;
  displayName: string;
  /** Owner-private. It is in this shape because the host chose it. */
  email: string;
  roleKey: string;
  version: number;
  createdAt: string;
  capabilities: string[];
  activeDevices: number;
  maxDevices: number;
  /** A link exists and has not expired or been used. The link itself is not here. */
  hasPendingInvite: boolean;
  inviteExpiresAt: string | null;
  devices: PartyCrewDevice[];
}

export interface PartyCrewOverview {
  collaborators: PartyCollaborator[];
  /** Party Crew cannot work without outbound mail: the second factor IS an email. */
  mailAvailable: boolean;
  assignableRoles: string[];
}

/** Returned exactly once, by the mutation that minted it. Never by a read. */
export interface PartyCollaboratorInvite {
  collaboratorId: string;
  inviteUrl: string;
  expiresAt: string;
}

export interface PartyCollaboratorWrite {
  displayName: string;
  email: string;
  roleKey: string;
}

const crewPath = (partyId: string) => `/api/parties/${encodeURIComponent(partyId)}/crew`;

const collaboratorPath = (partyId: string, collaboratorId: string) =>
  `${crewPath(partyId)}/${encodeURIComponent(collaboratorId)}`;

export function getPartyCrew(partyId: string, signal?: AbortSignal): Promise<PartyCrewOverview> {
  return api<PartyCrewOverview>(crewPath(partyId), { signal });
}

export function createPartyCollaborator(
  partyId: string,
  body: PartyCollaboratorWrite,
): Promise<PartyCollaboratorInvite> {
  return api<PartyCollaboratorInvite>(crewPath(partyId), { method: 'POST', json: body });
}

export function updatePartyCollaborator(
  partyId: string,
  collaboratorId: string,
  body: PartyCollaboratorWrite & { version: number },
): Promise<PartyCollaborator> {
  return api<PartyCollaborator>(collaboratorPath(partyId, collaboratorId), {
    method: 'PUT',
    json: body,
  });
}

/** A new link, which is also how the old one stops working. */
export function rotatePartyCollaboratorInvite(
  partyId: string,
  collaboratorId: string,
): Promise<PartyCollaboratorInvite> {
  return api<PartyCollaboratorInvite>(`${collaboratorPath(partyId, collaboratorId)}/invite`, {
    method: 'POST',
  });
}

export function revokePartyCollaborator(partyId: string, collaboratorId: string): Promise<void> {
  return api<void>(collaboratorPath(partyId, collaboratorId), { method: 'DELETE' });
}

/** One device, not the person: "they lost their phone" is the smaller decision. */
export function revokePartyCollaboratorDevice(
  partyId: string,
  collaboratorId: string,
  grantId: string,
): Promise<void> {
  return api<void>(
    `${collaboratorPath(partyId, collaboratorId)}/devices/${encodeURIComponent(grantId)}`,
    { method: 'DELETE' },
  );
}

// ── Pairing ─────────────────────────────────────────────────────────────────

/** Party-safe only: the party's name, the role, and a MASKED address. */
export interface PartyCrewChallengeStarted {
  partyTitle: string;
  roleKey: string;
  maskedEmail: string;
  expiresAt: string;
}

export type PartyCrewVerifyOutcome = 'Paired' | 'DeviceLimitReached';

export interface PartyCrewVerifyResult {
  outcome: PartyCrewVerifyOutcome;
  partyId: string | null;
  partyTitle: string | null;
  roleKey: string | null;
  capabilities: string[] | null;
  /** Present only for DeviceLimitReached: the two devices, so one can be dropped. */
  devices: PartyCrewDevice[] | null;
}

export interface PartyCrewSession {
  partyId: string;
  partyTitle: string;
  displayName: string;
  roleKey: string;
  capabilities: string[];
}

/**
 * Step one: the personal link.
 *
 * The token travels in the BODY. It arrived in the URL fragment, which browsers
 * do not send to a server, do not write to an access log and do not put in a
 * Referer — and the page removes it from the address bar before this call. A
 * token in a query string would be in the server log of every hop.
 */
export function startPartyCrewPairing(token: string): Promise<PartyCrewChallengeStarted> {
  return api<PartyCrewChallengeStarted>('/api/party-crew/auth/invite', {
    method: 'POST',
    json: { token },
  });
}

/** What this browser's challenge is about, so the code screen survives a refresh. */
export function getPartyCrewChallenge(signal?: AbortSignal): Promise<PartyCrewChallengeStarted> {
  return api<PartyCrewChallengeStarted>('/api/party-crew/auth/challenge', { signal });
}

export function resendPartyCrewCode(): Promise<void> {
  return api<void>('/api/party-crew/auth/resend', { method: 'POST' });
}

export function verifyPartyCrewCode(code: string): Promise<PartyCrewVerifyResult> {
  return api<PartyCrewVerifyResult>('/api/party-crew/auth/verify', {
    method: 'POST',
    json: { code },
  });
}

/** Reachable only with a verified challenge, and only about its own collaborator. */
export function listPartyCrewPairingDevices(signal?: AbortSignal): Promise<PartyCrewDevice[]> {
  return api<PartyCrewDevice[]>('/api/party-crew/auth/devices', { signal });
}

export function revokePartyCrewPairingDevice(grantId: string): Promise<void> {
  return api<void>(`/api/party-crew/auth/devices/${encodeURIComponent(grantId)}`, {
    method: 'DELETE',
  });
}

/** Finishing after a slot was freed. No second code: the challenge is already verified. */
export function completePartyCrewPairing(): Promise<PartyCrewVerifyResult> {
  return api<PartyCrewVerifyResult>('/api/party-crew/auth/complete', { method: 'POST' });
}

// ── The crew's own session ──────────────────────────────────────────────────

export function getPartyCrewSession(signal?: AbortSignal): Promise<PartyCrewSession> {
  return api<PartyCrewSession>('/api/party-crew/session', { signal });
}

export function endPartyCrewSession(): Promise<void> {
  return api<void>('/api/party-crew/session', { method: 'DELETE' });
}

export function listMyPartyCrewDevices(signal?: AbortSignal): Promise<PartyCrewDevice[]> {
  return api<PartyCrewDevice[]>('/api/party-crew/devices', { signal });
}

export function revokeMyPartyCrewDevice(grantId: string): Promise<void> {
  return api<void>(`/api/party-crew/devices/${encodeURIComponent(grantId)}`, { method: 'DELETE' });
}

// ── The vocabulary ──────────────────────────────────────────────────────────

/**
 * The two roles a host may hand out in this release.
 *
 * The server knows five and refuses the other three; this is the product's
 * list, in product order, and the server's `assignableRoles` is what the panel
 * actually renders — this constant is the fallback and the type source.
 */
export const PARTY_CREW_ASSIGNABLE_ROLES = ['co_organizer', 'director'] as const;

export type PartyCrewRole = (typeof PARTY_CREW_ASSIGNABLE_ROLES)[number];

/** Two devices per person. The product says so out loud, so the client knows it too. */
export const PARTY_CREW_MAX_DEVICES = 2;

// ── The same party, through the crew's own routes ───────────────────────────
//
// One function per owner function, with the SAME name and the SAME parameters,
// so a surface can be handed either set and cannot tell which it has. The ids
// are accepted and IGNORED: a crew route names no party, no album and no owner,
// because the device cookie already resolves to exactly one of each. That is
// what makes it impossible for a collaborator to reach another party by
// changing something — there is nothing in the URL to change.

import type {
  AlbumPartyStatus,
  GuestDirectoryPage,
  GuestDirectoryQuery,
  InvitationShareRequest,
  InvitationShareResult,
  Party,
  PartyAttendanceChange,
  PartyAttendanceOtherGuestCreate,
  PartyAttendanceOtherGuestUpdate,
  PartyChallenge,
  PartyChallengeList,
  PartyChallengeWrite,
  PartyGuestContentKind,
  PartyGuestContentSlot,
  PartyInvitationGroupDetail,
  PartyInvitationGroupWrite,
  PartyInvitationSendResult,
  PartyGuestListMinimal,
  PartyLifecycleAction,
  PartyMessageAction,
  PartyMessageList,
  PartyPrintSettings,
  PartyPrintSettingsPatch,
  PartyRsvpQuestion,
  PartyRsvpQuestionWrite,
  PartyUploadList,
} from './party';
import type { AlbumDetail, AlbumItemSummary } from './albums';
import type { PrintStation } from './printStations';
import type {
  PartyGameCommand,
  PartyGamePlanAction,
  PartyGameSnapshot,
} from './partyGame';

const CREW = '/api/party-crew';

/* The party itself. */

export const crewGetParty = (_partyId: string, signal?: AbortSignal): Promise<Party> =>
  api<Party>(`${CREW}/party`, { signal });

export const crewUpdatePartyDetails = (
  _partyId: string,
  body: {
    title: string;
    description?: string | null;
    eventStartsAt?: string | null;
    guestAccessExpiresAt?: string | null;
    libraryAccessExpiresAt?: string | null;
    version: number;
  },
  signal?: AbortSignal,
): Promise<Party> => api<Party>(`${CREW}/party`, { method: 'PATCH', json: body, signal });

export const crewTransitionParty = (
  _partyId: string,
  action: PartyLifecycleAction,
  version: number,
  signal?: AbortSignal,
): Promise<Party> =>
  api<Party>(`${CREW}/party/${action}`, { method: 'POST', json: { version }, signal });

export const crewSetPartyCovers = (
  _partyId: string,
  body: {
    invitationCoverFileItemId: string | null;
    liveCoverFileItemId: string | null;
    version: number;
  },
  signal?: AbortSignal,
): Promise<Party> => api<Party>(`${CREW}/party/covers`, { method: 'PUT', json: body, signal });

/* The album's party settings. */

export const crewGetAlbumPartySettings = (
  _albumId: string, signal?: AbortSignal,
): Promise<AlbumPartyStatus> => api<AlbumPartyStatus>(`${CREW}/album-settings`, { signal });

export const crewSetAlbumPartyMode = (
  _albumId: string,
  enabled: boolean,
  uploadEnabled?: boolean,
  requireUploadApproval?: boolean,
  signal?: AbortSignal,
  requireMessageApproval?: boolean,
): Promise<AlbumPartyStatus> =>
  api<AlbumPartyStatus>(`${CREW}/album-settings`, {
    method: 'PATCH',
    json: { enabled, uploadEnabled, requireUploadApproval, requireMessageApproval },
    signal,
  });

export const crewSetPartySlideshowSettings = (
  _albumId: string,
  settings: {
    photoSlideSeconds?: number;
    maxVideoSlideSeconds?: number;
    maxPhotoUploadsPerParticipant?: number;
    maxVideoUploadsPerParticipant?: number;
    maxMessagesPerParticipant?: number;
  },
  signal?: AbortSignal,
): Promise<AlbumPartyStatus> =>
  api<AlbumPartyStatus>(`${CREW}/slideshow-settings`, {
    method: 'PATCH', json: settings, signal,
  });

export const crewSetAlbumTvVisibility = (
  _albumId: string, showOnTv: boolean, signal?: AbortSignal,
): Promise<AlbumDetail> =>
  api<AlbumDetail>(`${CREW}/tv-visibility`, { method: 'PUT', json: { showOnTv }, signal });

/* What the party tells its guests. */

export const crewListPartyGuestContent = (
  _partyId: string, signal?: AbortSignal,
): Promise<PartyGuestContentSlot[]> =>
  api<PartyGuestContentSlot[]>(`${CREW}/guest-content`, { signal });

export const crewSetPartyGuestContent = (
  _partyId: string,
  kind: PartyGuestContentKind,
  body: Record<string, unknown>,
  signal?: AbortSignal,
): Promise<PartyGuestContentSlot> =>
  api<PartyGuestContentSlot>(`${CREW}/guest-content/${encodeURIComponent(kind)}`, {
    method: 'PUT', json: body, signal,
  });

/* The guest list. */

export const crewQueryPartyGuestDirectory = (
  _partyId: string, query: GuestDirectoryQuery, signal?: AbortSignal,
): Promise<GuestDirectoryPage> =>
  api<GuestDirectoryPage>(`${CREW}/guest-directory/query`, {
    method: 'POST', json: query, signal,
  });

export const crewGetPartyInvitationGroup = (
  _partyId: string, groupId: string, signal?: AbortSignal,
): Promise<PartyInvitationGroupDetail> =>
  api<PartyInvitationGroupDetail>(
    `${CREW}/invitation-groups/${encodeURIComponent(groupId)}`, { signal });

export const crewGetPartyRsvpQuestions = (
  _partyId: string, signal?: AbortSignal,
): Promise<{ questions: PartyRsvpQuestion[] }> =>
  api<{ questions: PartyRsvpQuestion[] }>(`${CREW}/rsvp-questions`, { signal });

export const crewCreatePartyInvitationGroup = (
  _partyId: string, body: PartyInvitationGroupWrite, signal?: AbortSignal,
): Promise<PartyGuestListMinimal> =>
  api<PartyGuestListMinimal>(`${CREW}/invitation-groups`, {
    method: 'POST', json: body, signal, headers: MINIMAL,
  });

export const crewUpdatePartyInvitationGroup = (
  _partyId: string, groupId: string, body: PartyInvitationGroupWrite, signal?: AbortSignal,
): Promise<PartyGuestListMinimal> =>
  api<PartyGuestListMinimal>(`${CREW}/invitation-groups/${encodeURIComponent(groupId)}`, {
    method: 'PUT', json: body, signal, headers: MINIMAL,
  });

export const crewDeletePartyInvitationGroup = (
  _partyId: string, groupId: string, version: number, signal?: AbortSignal,
): Promise<PartyGuestListMinimal> =>
  api<PartyGuestListMinimal>(
    `${CREW}/invitation-groups/${encodeURIComponent(groupId)}?version=${version}`,
    { method: 'DELETE', signal, headers: MINIMAL });

export const crewRotatePartyInvitationLink = (
  _partyId: string, groupId: string, version: number, signal?: AbortSignal,
): Promise<PartyGuestListMinimal> =>
  api<PartyGuestListMinimal>(
    `${CREW}/invitation-groups/${encodeURIComponent(groupId)}/rotate-link`,
    { method: 'POST', json: { version }, signal, headers: MINIMAL });

export const crewSendPartyInvitation = (
  _partyId: string,
  groupId: string,
  body: { clientRequestId: string; partyVersion?: number },
  signal?: AbortSignal,
): Promise<PartyInvitationSendResult> =>
  api<PartyInvitationSendResult>(
    `${CREW}/invitation-groups/${encodeURIComponent(groupId)}/send`,
    { method: 'POST', json: body, signal, headers: MINIMAL });

export const crewRemindPartyInvitation = (
  _partyId: string, groupId: string, clientRequestId: string, signal?: AbortSignal,
): Promise<PartyInvitationSendResult> =>
  api<PartyInvitationSendResult>(
    `${CREW}/invitation-groups/${encodeURIComponent(groupId)}/remind`,
    { method: 'POST', json: { clientRequestId }, signal, headers: MINIMAL });

export const crewSharePartyInvitation = (
  _partyId: string, groupId: string, body: InvitationShareRequest, signal?: AbortSignal,
): Promise<InvitationShareResult<Party>> =>
  api<InvitationShareResult<Party>>(
    `${CREW}/invitation-groups/${encodeURIComponent(groupId)}/share`,
    { method: 'POST', json: body, signal, headers: MINIMAL });

export const crewCreatePartyRsvpQuestion = (
  _partyId: string, body: PartyRsvpQuestionWrite, signal?: AbortSignal,
): Promise<PartyGuestListMinimal> =>
  api<PartyGuestListMinimal>(`${CREW}/rsvp-questions`, {
    method: 'POST', json: body, signal, headers: MINIMAL,
  });

export const crewUpdatePartyRsvpQuestion = (
  _partyId: string, questionId: string, body: PartyRsvpQuestionWrite, signal?: AbortSignal,
): Promise<PartyGuestListMinimal> =>
  api<PartyGuestListMinimal>(`${CREW}/rsvp-questions/${encodeURIComponent(questionId)}`, {
    method: 'PUT', json: body, signal, headers: MINIMAL,
  });

export const crewReorderPartyRsvpQuestions = (
  _partyId: string, questionIds: string[], signal?: AbortSignal,
): Promise<PartyGuestListMinimal> =>
  api<PartyGuestListMinimal>(`${CREW}/rsvp-questions/order`, {
    method: 'PUT', json: { questionIds }, signal, headers: MINIMAL,
  });

/* Who arrived. */

export const crewCheckInPartyGuest = (
  _partyId: string, guestId: string, signal?: AbortSignal,
): Promise<PartyAttendanceChange> =>
  api<PartyAttendanceChange>(`${CREW}/attendance/guests/${encodeURIComponent(guestId)}`, {
    method: 'PUT', signal, headers: MINIMAL,
  });

export const crewUndoPartyGuestCheckIn = (
  _partyId: string, guestId: string, signal?: AbortSignal,
): Promise<PartyAttendanceChange> =>
  api<PartyAttendanceChange>(`${CREW}/attendance/guests/${encodeURIComponent(guestId)}`, {
    method: 'DELETE', signal, headers: MINIMAL,
  });

export const crewCreatePartyAttendanceGuest = (
  _partyId: string, body: PartyAttendanceOtherGuestCreate, signal?: AbortSignal,
): Promise<PartyAttendanceChange> =>
  api<PartyAttendanceChange>(`${CREW}/attendance/other-guests`, {
    method: 'POST', json: body, signal, headers: MINIMAL,
  });

export const crewUpdatePartyAttendanceGuest = (
  _partyId: string, attendanceGuestId: string, body: PartyAttendanceOtherGuestUpdate,
  signal?: AbortSignal,
): Promise<PartyAttendanceChange> =>
  api<PartyAttendanceChange>(
    `${CREW}/attendance/other-guests/${encodeURIComponent(attendanceGuestId)}`,
    { method: 'PUT', json: body, signal, headers: MINIMAL });

export const crewDeletePartyAttendanceGuest = (
  _partyId: string, attendanceGuestId: string, signal?: AbortSignal,
): Promise<PartyAttendanceChange> =>
  api<PartyAttendanceChange>(
    `${CREW}/attendance/other-guests/${encodeURIComponent(attendanceGuestId)}`,
    { method: 'DELETE', signal, headers: MINIMAL });

/* The photographs and the greetings. */

export const crewListPartyUploads = (
  _albumId: string, signal?: AbortSignal,
): Promise<PartyUploadList> => api<PartyUploadList>(`${CREW}/uploads`, { signal });

export const crewModeratePartyUpload = (
  _albumId: string,
  fileItemId: string,
  action: 'hide' | 'approve' | 'reject' | 'restore',
  signal?: AbortSignal,
): Promise<void> =>
  api<void>(`${CREW}/uploads/${encodeURIComponent(fileItemId)}/${action}`, {
    method: 'POST', signal,
  });

export const crewListPartyMessages = (
  _albumId: string, signal?: AbortSignal,
): Promise<PartyMessageList> => api<PartyMessageList>(`${CREW}/messages`, { signal });

export const crewModeratePartyMessage = (
  _albumId: string, messageId: string, action: PartyMessageAction, signal?: AbortSignal,
): Promise<void> =>
  api<void>(`${CREW}/messages/${encodeURIComponent(messageId)}/${action}`, {
    method: 'POST', signal,
  });

/* The activities. */

export const crewListAlbumItems = (
  _albumId: string, signal?: AbortSignal,
): Promise<AlbumItemSummary[]> => api<AlbumItemSummary[]>(`${CREW}/album-items`, { signal });

export const crewSetPartyGameSettings = (
  _albumId: string,
  settings: {
    gameEnabled: boolean;
    minChallengeIntervalSeconds: number;
    maxChallengeIntervalSeconds: number;
    votesPerGuest: number;
    maxChallengesPerSession: number | null;
    priorityVotingEnabled?: boolean;
  },
  signal?: AbortSignal,
): Promise<AlbumPartyStatus> =>
  api<AlbumPartyStatus>(`${CREW}/game-settings`, { method: 'PATCH', json: settings, signal });

export const crewListPrintStations = (signal?: AbortSignal): Promise<PrintStation[]> =>
  api<PrintStation[]>(`${CREW}/print-stations`, { signal });

export const crewGetPartyPrintSettings = (
  _albumId: string, signal?: AbortSignal,
): Promise<PartyPrintSettings> => api<PartyPrintSettings>(`${CREW}/print-settings`, { signal });

export const crewSetPartyPrintSettings = (
  _albumId: string, patch: PartyPrintSettingsPatch, signal?: AbortSignal,
): Promise<PartyPrintSettings> =>
  api<PartyPrintSettings>(`${CREW}/print-settings`, { method: 'PATCH', json: patch, signal });


export const crewListPartyChallenges = (
  _albumId: string, signal?: AbortSignal,
): Promise<PartyChallengeList> => api<PartyChallengeList>(`${CREW}/challenges`, { signal });

export const crewCreatePartyChallenge = (
  _albumId: string, value: PartyChallengeWrite,
): Promise<PartyChallenge> =>
  api<PartyChallenge>(`${CREW}/challenges`, { method: 'POST', json: value });

export const crewUpdatePartyChallenge = (
  _albumId: string, id: string, value: PartyChallengeWrite,
): Promise<PartyChallenge> =>
  api<PartyChallenge>(`${CREW}/challenges/${encodeURIComponent(id)}`, {
    method: 'PUT', json: value,
  });

export const crewDeletePartyChallenge = (_albumId: string, id: string): Promise<void> =>
  api<void>(`${CREW}/challenges/${encodeURIComponent(id)}`, { method: 'DELETE' });

export const crewReorderPartyChallenges = (
  _albumId: string, challengeIds: string[],
): Promise<void> =>
  api<void>(`${CREW}/challenges/order`, { method: 'PUT', json: { challengeIds } });

export const crewGetPartyGameSnapshot = (
  _albumId: string, signal?: AbortSignal,
): Promise<PartyGameSnapshot> => api<PartyGameSnapshot>(`${CREW}/game`, { signal });

export const crewSendPartyGameCommand = (
  _albumId: string, command: PartyGameCommand, expectedVersion: number, signal?: AbortSignal,
): Promise<PartyGameSnapshot> =>
  api<PartyGameSnapshot>(`${CREW}/game/commands`, {
    method: 'POST', json: { command, expectedVersion }, signal,
  });

export const crewPlanPartyGame = (
  _albumId: string,
  action: PartyGamePlanAction,
  challengeId: string,
  expectedVersion: number,
  position?: number,
  signal?: AbortSignal,
): Promise<PartyGameSnapshot> =>
  api<PartyGameSnapshot>(`${CREW}/game/plan`, {
    method: 'POST', json: { action, challengeId, expectedVersion, position }, signal,
  });

/**
 * `Prefer: return=minimal` on every guest-list and attendance mutation, exactly
 * as the host's client sends it: the answer is the list's header and the one
 * row that changed, never a thousand-group party re-serialised because somebody
 * ticked a name.
 */
const MINIMAL = { prefer: 'return=minimal' } as const;
