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

/** One party this browser may operate. */
export interface PartyCrewAssignment {
  partyId: string;
  partyTitle: string;
  displayName: string;
  roleKey: string;
}

/**
 * EVERY party this browser may operate.
 *
 * The one crew read with no party in it, because it is what a person needs
 * before they have chosen one: the same phone may legitimately hold two
 * assignments. Derived from this device's own grants — never from the owner's
 * list of parties.
 */
export function getPartyCrewAssignments(
  signal?: AbortSignal,
): Promise<{ assignments: PartyCrewAssignment[] }> {
  return api<{ assignments: PartyCrewAssignment[] }>('/api/party-crew/me', { signal });
}

const partyPath = (partyId: string) => `/api/party-crew/parties/${encodeURIComponent(partyId)}`;

/** What this device is AT one party, and what that party's shell draws from it. */
export function getPartyCrewSession(
  partyId: string, signal?: AbortSignal,
): Promise<PartyCrewSession> {
  return api<PartyCrewSession>(`${partyPath(partyId)}/session`, { signal });
}

/** LEAVE one party. The browser keeps its credential if it helps at another. */
export function leavePartyCrewParty(partyId: string): Promise<void> {
  return api<void>(`${partyPath(partyId)}/session`, { method: 'DELETE' });
}

/**
 * DISCONNECT this browser from Party Crew entirely — every party at once.
 *
 * Deliberately a different call from leaving one party: a product that spells
 * them the same loses somebody two jobs when they meant to leave one.
 */
export function disconnectPartyCrewDevice(): Promise<void> {
  return api<void>('/api/party-crew/device', { method: 'DELETE' });
}

export function listMyPartyCrewDevices(
  partyId: string, signal?: AbortSignal,
): Promise<PartyCrewDevice[]> {
  return api<PartyCrewDevice[]>(`${partyPath(partyId)}/devices`, { signal });
}

export function revokeMyPartyCrewDevice(partyId: string, grantId: string): Promise<void> {
  return api<void>(`${partyPath(partyId)}/devices/${encodeURIComponent(grantId)}`, {
    method: 'DELETE',
  });
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
// A FACTORY, bound to one party. The owner's functions take an id because the
// host has many parties; a crew surface is opened at exactly one, and every
// route carries it: `/api/party-crew/parties/{partyId}/…`.
//
// The id is a RESOURCE SELECTOR, never an authority. It says which of this
// browser's assignments the request is about — one device may legitimately hold
// several, and choosing one by whichever grant the database returned first
// would make "which party am I operating" a function of insertion order. What
// authorises the request is still only the device cookie resolved against a
// live grant, and a party this device has no grant for answers the same nothing
// as no device at all.
//
// Each function keeps the OWNER's exact signature so a surface can be handed
// either set and cannot tell which it has; the leading id argument is accepted
// and ignored, because the bound one is the truth.

import { partyContributionsPatch } from '@nubarca/contracts';
import type {
  AlbumPartyStatus,
  PartyAddressShare,
  PartyContributionsPatch,
  PartyGuestbookManagerList,
  GuestDirectoryPage,
  GuestDirectoryQuery,
  GuestDirectorySummary,
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
import type { PartyGameCommand, PartyGamePlanAction, PartyGameSnapshot } from './partyGame';
import type { PrintStation } from './printStations';

/**
 * `Prefer: return=minimal` on every guest-list and attendance mutation, exactly
 * as the host's client sends it: the answer is the list's header and the one
 * row that changed, never a thousand-group party re-serialised because somebody
 * ticked a name.
 */
const MINIMAL = { prefer: 'return=minimal' } as const;

export function partyCrewRoutes(partyId: string) {
  const at = `/api/party-crew/parties/${encodeURIComponent(partyId)}`;

  return {
    /* The party itself. */

    getParty: (_p: string, signal?: AbortSignal): Promise<Party> =>
      api<Party>(`${at}/party`, { signal }),

    updateParty: (
      _p: string,
      body: {
        title: string;
        description?: string | null;
        eventStartsAt?: string | null;
        guestAccessExpiresAt?: string | null;
        libraryAccessExpiresAt?: string | null;
        version: number;
      },
      signal?: AbortSignal,
    ): Promise<Party> => api<Party>(`${at}/party`, { method: 'PATCH', json: body, signal }),

    transitionParty: (
      _p: string, action: PartyLifecycleAction, version: number, signal?: AbortSignal,
    ): Promise<Party> =>
      api<Party>(`${at}/party/${action}`, { method: 'POST', json: { version }, signal }),

    setPartyCovers: (
      _p: string,
      body: {
        invitationCoverFileItemId: string | null;
        liveCoverFileItemId: string | null;
        version: number;
      },
      signal?: AbortSignal,
    ): Promise<Party> => api<Party>(`${at}/party/covers`, { method: 'PUT', json: body, signal }),

    /* The album's party settings. */

    getAlbumPartySettings: (_a: string, signal?: AbortSignal): Promise<AlbumPartyStatus> =>
      api<AlbumPartyStatus>(`${at}/album-settings`, { signal }),

    setAlbumPartyMode: (
      _a: string,
      enabled: boolean,
      uploadEnabled?: boolean,
      requireUploadApproval?: boolean,
      signal?: AbortSignal,
      requireMessageApproval?: boolean,
    ): Promise<AlbumPartyStatus> =>
      api<AlbumPartyStatus>(`${at}/album-settings`, {
        method: 'PATCH',
        json: { enabled, uploadEnabled, requireUploadApproval, requireMessageApproval },
        signal,
      }),

    setPartySlideshowSettings: (
      _a: string,
      settings: {
        photoSlideSeconds?: number;
        maxVideoSlideSeconds?: number;
        maxPhotoUploadsPerParticipant?: number;
        maxVideoUploadsPerParticipant?: number;
        maxMessagesPerParticipant?: number;
      },
      signal?: AbortSignal,
    ): Promise<AlbumPartyStatus> =>
      api<AlbumPartyStatus>(`${at}/slideshow-settings`, { method: 'PATCH', json: settings, signal }),

    /**
     * WHICH CONTRIBUTIONS THE PARTY TAKES. `contributions.configure` on the
     * server; a role without it gets the same generic nothing every capability
     * it does not hold gets, which is why the panel hides the card rather than
     * drawing one that answers 404.
     */
    setPartyContributionSettings: (
      _a: string,
      changes: PartyContributionsPatch,
      signal?: AbortSignal,
    ): Promise<AlbumPartyStatus> =>
      api<AlbumPartyStatus>(`${at}/contributions`, {
        method: 'PATCH', json: partyContributionsPatch(changes), signal,
      }),

    /* The guest book. Party-scoped for the crew exactly as for the host: the
       façade names the party and resolves the owner and album server-side. */

    listPartyGuestbook: (_p: string, signal?: AbortSignal): Promise<PartyGuestbookManagerList> =>
      api<PartyGuestbookManagerList>(`${at}/guestbook`, { signal }),

    moderatePartyGuestbookEntry: (
      _p: string, entryId: string, action: PartyMessageAction, signal?: AbortSignal,
    ): Promise<void> =>
      api<void>(`${at}/guestbook/${encodeURIComponent(entryId)}/${action}`, {
        method: 'POST', signal,
      }),

    /**
     * WHERE THE PARTY IS. `details.manage` on the server — the party's own
     * facts, not what the invitation says — so a Regista's request is refused
     * there and not merely hidden here.
     */
    sharePartyAddress: (_p: string, signal?: AbortSignal): Promise<PartyAddressShare> =>
      api<PartyAddressShare>(`${at}/address-share`, { method: 'POST', signal }),

    setPartyGameSettings: (
      _a: string,
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
      api<AlbumPartyStatus>(`${at}/game-settings`, { method: 'PATCH', json: settings, signal }),

    setAlbumTvVisibility: (
      _a: string, showOnTv: boolean, signal?: AbortSignal,
    ): Promise<AlbumDetail> =>
      api<AlbumDetail>(`${at}/tv-visibility`, { method: 'PUT', json: { showOnTv }, signal }),

    getPartyPrintSettings: (_a: string, signal?: AbortSignal): Promise<PartyPrintSettings> =>
      api<PartyPrintSettings>(`${at}/print-settings`, { signal }),

    setPartyPrintSettings: (
      _a: string, patch: PartyPrintSettingsPatch, signal?: AbortSignal,
    ): Promise<PartyPrintSettings> =>
      api<PartyPrintSettings>(`${at}/print-settings`, { method: 'PATCH', json: patch, signal }),

    /**
     * The venue's printers, which a collaborator does not get.
     *
     * Enumerating the owner's print stations is administering their
     * INSTALLATION, not running this evening — `print.manage` is the party's
     * print profile and nothing wider. So there is no crew route for it and
     * this resolves empty; the panel renders without a station picker.
     */
    listPrintStations: (_signal?: AbortSignal): Promise<PrintStation[]> =>
      Promise.resolve([]),

    /* What the party tells its guests. */

    listPartyGuestContent: (_p: string, signal?: AbortSignal): Promise<PartyGuestContentSlot[]> =>
      api<PartyGuestContentSlot[]>(`${at}/guest-content`, { signal }),

    setPartyGuestContent: (
      _p: string, kind: PartyGuestContentKind, body: Record<string, unknown>, signal?: AbortSignal,
    ): Promise<PartyGuestContentSlot> =>
      api<PartyGuestContentSlot>(`${at}/guest-content/${encodeURIComponent(kind)}`, {
        method: 'PUT', json: body, signal,
      }),

    /* The guest list. */

    /**
     * The numbers, for a role that holds no `guests.read`.
     *
     * A Regista runs the evening and needs to know how many are expected and
     * how many arrived; they never receive a name, an address or a note. The
     * counts-only query is still a query for people, so this is a different
     * route that answers the summary alone.
     */
    getGuestCounts: (
      _p: string, signal?: AbortSignal,
    ): Promise<{ summary: GuestDirectorySummary }> =>
      api<{ summary: GuestDirectorySummary }>(`${at}/guest-counts`, { signal }),

    queryPartyGuestDirectory: (
      _p: string, query: GuestDirectoryQuery, signal?: AbortSignal,
    ): Promise<GuestDirectoryPage> =>
      api<GuestDirectoryPage>(`${at}/guest-directory/query`, {
        method: 'POST', json: query, signal,
      }),

    getPartyInvitationGroup: (
      _p: string, groupId: string, signal?: AbortSignal,
    ): Promise<PartyInvitationGroupDetail> =>
      api<PartyInvitationGroupDetail>(
        `${at}/invitation-groups/${encodeURIComponent(groupId)}`, { signal }),

    getPartyRsvpQuestions: (
      _p: string, signal?: AbortSignal,
    ): Promise<{ questions: PartyRsvpQuestion[] }> =>
      api<{ questions: PartyRsvpQuestion[] }>(`${at}/rsvp-questions`, { signal }),

    createPartyInvitationGroup: (
      _p: string, body: PartyInvitationGroupWrite, signal?: AbortSignal,
    ): Promise<PartyGuestListMinimal> =>
      api<PartyGuestListMinimal>(`${at}/invitation-groups`, {
        method: 'POST', json: body, signal, headers: MINIMAL,
      }),

    updatePartyInvitationGroup: (
      _p: string, groupId: string, body: PartyInvitationGroupWrite, signal?: AbortSignal,
    ): Promise<PartyGuestListMinimal> =>
      api<PartyGuestListMinimal>(`${at}/invitation-groups/${encodeURIComponent(groupId)}`, {
        method: 'PUT', json: body, signal, headers: MINIMAL,
      }),

    deletePartyInvitationGroup: (
      _p: string, groupId: string, version: number, signal?: AbortSignal,
    ): Promise<PartyGuestListMinimal> =>
      api<PartyGuestListMinimal>(
        `${at}/invitation-groups/${encodeURIComponent(groupId)}?version=${version}`,
        { method: 'DELETE', signal, headers: MINIMAL }),

    rotatePartyInvitationLink: (
      _p: string, groupId: string, version: number, signal?: AbortSignal,
    ): Promise<PartyGuestListMinimal> =>
      api<PartyGuestListMinimal>(
        `${at}/invitation-groups/${encodeURIComponent(groupId)}/rotate-link`,
        { method: 'POST', json: { version }, signal, headers: MINIMAL }),

    sendPartyInvitation: (
      _p: string,
      groupId: string,
      body: { clientRequestId: string; partyVersion?: number },
      signal?: AbortSignal,
    ): Promise<PartyInvitationSendResult> =>
      api<PartyInvitationSendResult>(
        `${at}/invitation-groups/${encodeURIComponent(groupId)}/send`,
        { method: 'POST', json: body, signal, headers: MINIMAL }),

    remindPartyInvitation: (
      _p: string, groupId: string, clientRequestId: string, signal?: AbortSignal,
    ): Promise<PartyInvitationSendResult> =>
      api<PartyInvitationSendResult>(
        `${at}/invitation-groups/${encodeURIComponent(groupId)}/remind`,
        { method: 'POST', json: { clientRequestId }, signal, headers: MINIMAL }),

    sharePartyInvitation: (
      _p: string, groupId: string, body: InvitationShareRequest, signal?: AbortSignal,
    ): Promise<InvitationShareResult<Party>> =>
      api<InvitationShareResult<Party>>(
        `${at}/invitation-groups/${encodeURIComponent(groupId)}/share`,
        { method: 'POST', json: body, signal, headers: MINIMAL }),

    createPartyRsvpQuestion: (
      _p: string, body: PartyRsvpQuestionWrite, signal?: AbortSignal,
    ): Promise<PartyGuestListMinimal> =>
      api<PartyGuestListMinimal>(`${at}/rsvp-questions`, {
        method: 'POST', json: body, signal, headers: MINIMAL,
      }),

    updatePartyRsvpQuestion: (
      _p: string, questionId: string, body: PartyRsvpQuestionWrite, signal?: AbortSignal,
    ): Promise<PartyGuestListMinimal> =>
      api<PartyGuestListMinimal>(`${at}/rsvp-questions/${encodeURIComponent(questionId)}`, {
        method: 'PUT', json: body, signal, headers: MINIMAL,
      }),

    reorderPartyRsvpQuestions: (
      _p: string, questionIds: string[], signal?: AbortSignal,
    ): Promise<PartyGuestListMinimal> =>
      api<PartyGuestListMinimal>(`${at}/rsvp-questions/order`, {
        method: 'PUT', json: { questionIds }, signal, headers: MINIMAL,
      }),

    /* Who arrived. */

    checkInPartyGuest: (
      _p: string, guestId: string, signal?: AbortSignal,
    ): Promise<PartyAttendanceChange> =>
      api<PartyAttendanceChange>(`${at}/attendance/guests/${encodeURIComponent(guestId)}`, {
        method: 'PUT', signal, headers: MINIMAL,
      }),

    undoPartyGuestCheckIn: (
      _p: string, guestId: string, signal?: AbortSignal,
    ): Promise<PartyAttendanceChange> =>
      api<PartyAttendanceChange>(`${at}/attendance/guests/${encodeURIComponent(guestId)}`, {
        method: 'DELETE', signal, headers: MINIMAL,
      }),

    createPartyAttendanceGuest: (
      _p: string, body: PartyAttendanceOtherGuestCreate, signal?: AbortSignal,
    ): Promise<PartyAttendanceChange> =>
      api<PartyAttendanceChange>(`${at}/attendance/other-guests`, {
        method: 'POST', json: body, signal, headers: MINIMAL,
      }),

    updatePartyAttendanceGuest: (
      _p: string, attendanceGuestId: string, body: PartyAttendanceOtherGuestUpdate,
      signal?: AbortSignal,
    ): Promise<PartyAttendanceChange> =>
      api<PartyAttendanceChange>(
        `${at}/attendance/other-guests/${encodeURIComponent(attendanceGuestId)}`,
        { method: 'PUT', json: body, signal, headers: MINIMAL }),

    deletePartyAttendanceGuest: (
      _p: string, attendanceGuestId: string, signal?: AbortSignal,
    ): Promise<PartyAttendanceChange> =>
      api<PartyAttendanceChange>(
        `${at}/attendance/other-guests/${encodeURIComponent(attendanceGuestId)}`,
        { method: 'DELETE', signal, headers: MINIMAL }),

    /* The photographs and the greetings. */

    listPartyUploads: (_a: string, signal?: AbortSignal): Promise<PartyUploadList> =>
      api<PartyUploadList>(`${at}/uploads`, { signal }),

    moderatePartyUpload: (
      _a: string,
      fileItemId: string,
      action: 'hide' | 'approve' | 'reject' | 'restore',
      signal?: AbortSignal,
    ): Promise<void> =>
      api<void>(`${at}/uploads/${encodeURIComponent(fileItemId)}/${action}`, {
        method: 'POST', signal,
      }),

    listPartyMessages: (_a: string, signal?: AbortSignal): Promise<PartyMessageList> =>
      api<PartyMessageList>(`${at}/messages`, { signal }),

    moderatePartyMessage: (
      _a: string, messageId: string, action: PartyMessageAction, signal?: AbortSignal,
    ): Promise<void> =>
      api<void>(`${at}/messages/${encodeURIComponent(messageId)}/${action}`, {
        method: 'POST', signal,
      }),

    /* The activities. */

    listAlbumItems: (_a: string, signal?: AbortSignal): Promise<AlbumItemSummary[]> =>
      api<AlbumItemSummary[]>(`${at}/album-items`, { signal }),

    listPartyChallenges: (_a: string, signal?: AbortSignal): Promise<PartyChallengeList> =>
      api<PartyChallengeList>(`${at}/challenges`, { signal }),

    createPartyChallenge: (_a: string, value: PartyChallengeWrite): Promise<PartyChallenge> =>
      api<PartyChallenge>(`${at}/challenges`, { method: 'POST', json: value }),

    updatePartyChallenge: (
      _a: string, id: string, value: PartyChallengeWrite,
    ): Promise<PartyChallenge> =>
      api<PartyChallenge>(`${at}/challenges/${encodeURIComponent(id)}`, {
        method: 'PUT', json: value,
      }),

    deletePartyChallenge: (_a: string, id: string): Promise<void> =>
      api<void>(`${at}/challenges/${encodeURIComponent(id)}`, { method: 'DELETE' }),

    reorderPartyChallenges: (_a: string, challengeIds: string[]): Promise<void> =>
      api<void>(`${at}/challenges/order`, { method: 'PUT', json: { challengeIds } }),

    getPartyGameSnapshot: (_a: string, signal?: AbortSignal): Promise<PartyGameSnapshot> =>
      api<PartyGameSnapshot>(`${at}/game`, { signal }),

    sendPartyGameCommand: (
      _a: string, command: PartyGameCommand, expectedVersion: number, signal?: AbortSignal,
    ): Promise<PartyGameSnapshot> =>
      api<PartyGameSnapshot>(`${at}/game/commands`, {
        method: 'POST', json: { command, expectedVersion }, signal,
      }),

    planPartyGame: (
      _a: string,
      action: PartyGamePlanAction,
      challengeId: string,
      expectedVersion: number,
      position?: number,
      signal?: AbortSignal,
    ): Promise<PartyGameSnapshot> =>
      api<PartyGameSnapshot>(`${at}/game/plan`, {
        method: 'POST', json: { action, challengeId, expectedVersion, position }, signal,
      }),
  };
}
