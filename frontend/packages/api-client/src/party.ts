import { api, ApiError } from './client';
import type {
  AlbumPartyStatus,
  GuestDirectoryPage,
  GuestDirectoryQuery,
  InvitationShareRequest,
  InvitationShareResult,
  PartyGuestList,
  PartyGuestListMinimal,
  PartyInvitationGroupDetail,
  PartyInvitationGroupWrite,
  PartyInvitationSendMinimal,
  PartyInvitationView,
  PartyMessageAction,
  PartyMessageList,
  PartyRsvpQuestion,
  PartyRsvpQuestionWrite,
  PartyRsvpWrite,
  PartyUploadList,
} from '@nubarca/contracts';
import type {
  PartyAttendance,
  PartyAttendanceChange,
  PartyAttendanceOtherGuestCreate,
  PartyAttendanceOtherGuestUpdate,
} from '@nubarca/contracts';
import {
  PREFER_RETURN_MINIMAL,
  partyGuestDirectoryPath,
  partyInvitationGroupDetailPath,
  partyInvitationSharePath,
} from '@nubarca/contracts';
import {
  partyAttendanceGuestPath,
  partyAttendanceOtherGuestPath,
  partyAttendanceOtherGuestsPath,
  partyAttendancePath,
  partyInvitationAttendanceGuestPath,
} from '@nubarca/contracts';
import {
  partyGuestListPath,
  partyInvitationGroupActionPath,
  partyInvitationGroupPath,
  partyInvitationGroupsPath,
  partyInvitationPath,
  partyInvitationRsvpPath,
  partyRsvpQuestionOrderPath,
  partyRsvpQuestionPath,
  partyRsvpQuestionsPath,
} from '@nubarca/contracts';

// Web TRANSPORT for Party. The DTOs, the validation RANGES and the message
// transition matrix are canonical in @nubarca/contracts, shared with the phone
// (§33, §34, §36) — the transition rules in particular used to live inlined in
// this app's JSX, which is not somewhere a second client can read them.
// Everything is re-exported under its existing name, so every web call site is
// unchanged.

export type {
  AlbumPartyStatus,
  PartyGameSettings,
  PartyMessage,
  PartyMessageAction,
  PartyMessageList,
  PartyMessageModeration,
  PartyMessageStatus,
  PartySlideshowSettings,
  PartyUploadItem,
  PartyUploadList,
  PartyUploadStatus,
} from '@nubarca/contracts';
export {
  DESTRUCTIVE_PARTY_MESSAGE_ACTIONS,
  PARTY_GAME_RANGES,
  PARTY_SLIDESHOW_RANGES,
  clampToRange,
  invalidGameFields,
  invalidSlideshowFields,
  isPartyMessageActionAllowed,
  partyGuestUrl,
  partyMessageActions,
} from '@nubarca/contracts';

// The guest list and the personal invitation: canonical in @nubarca/contracts,
// re-exported here so a web call site imports one package.
export type {
  InvitationDeliveryChannel,
  InvitationDeliveryStatus,
  InvitationShareChannel,
  PartyGuest,
  PartyGuestList,
  PartyInvitationDelivery,
  PartyInvitationDeliveryKind,
  PartyInvitationDeliveryState,
  PartyInvitationDeliveryView,
  PartyInvitationGroup,
  PartyInvitationGroupWrite,
  PartyInvitationGuest,
  PartyInvitationQuestion,
  PartyInvitationRsvp,
  PartyNamedGuestWrite,
  PartyRsvpAnswer,
  PartyRsvpFormProblem,
  PartyRsvpQuestion,
  PartyRsvpQuestionKind,
  PartyRsvpQuestionWrite,
  PartyRsvpStatus,
  PartyRsvpSummary,
  PartyRsvpWrite,
} from '@nubarca/contracts';
export {
  PARTY_INVITATION_DELIVERY_CHANNELS,
  PARTY_INVITATION_DELIVERY_STATUSES,
  PARTY_INVITATION_LIMITS,
  PARTY_INVITATION_SHARE_CHANNELS,
  PARTY_RSVP_QUESTION_KINDS,
  PARTY_RSVP_STATUSES,
  codePoints,
  isPlausibleEmail,
  normalizeQuestionOptions,
  normalizeText,
  rsvpFormProblems,
} from '@nubarca/contracts';

// Attendance: canonical in @nubarca/contracts as well — the counts' definitions
// and the source vocabulary.
export type {
  PartyAttendance,
  PartyAttendanceChange,
  PartyAttendanceGroup,
  PartyAttendanceGuest,
  PartyAttendanceOtherGuest,
  PartyAttendanceOtherGuestCreate,
  PartyAttendanceOtherGuestUpdate,
  PartyAttendanceSource,
  PartyAttendanceSummary,
} from '@nubarca/contracts';
export {
  PARTY_ATTENDANCE_LIMITS,
  PARTY_ATTENDANCE_SOURCES,
  unexpectedArrivals,
} from '@nubarca/contracts';

// The guest directory and sharing: the host's console, read in pages.
export type {
  GuestDirectoryCounts,
  GuestDirectoryGroupItem,
  GuestDirectoryItem,
  GuestDirectoryOtherItem,
  GuestDirectoryPage,
  GuestDirectoryPerson,
  GuestDirectoryQuery,
  GuestDirectoryState,
  GuestDirectorySummary,
  InvitationLine,
  InvitationLineKind,
  InvitationPrimaryAction,
  InvitationShare,
  InvitationShareRequest,
  InvitationShareResult,
  PartyGuestArrival,
  PartyGuestListMinimal,
  PartyInvitationGroupDetail,
  PartyInvitationHistoryEntry,
} from '@nubarca/contracts';
export {
  GUEST_CONSOLE_PARAMS,
  GUEST_DIRECTORY_LIMITS,
  GUEST_DIRECTORY_STATES,
  guestDirectoryItemKey,
  guestDirectoryStatesFor,
  invitationLine,
  isAttendancePhase,
  isGuestDirectoryState,
  peoplePreview,
  primaryInvitationAction,
} from '@nubarca/contracts';


// --- The Party ROOT (owner, normal user auth) ---
//
// Only what the root itself owns: the host's parties, one party's own data,
// where it draws its media from, and its lifecycle. Everything a party is
// CONFIGURED with — contributions, moderation, slideshow, games, printing —
// keeps using the album-scoped functions below with the party's `mainAlbumId`.
// There is no Party V2 client, because there is no Party V2 backend.

/** One album a party draws on. `role` is `main` in this release. */
export interface PartyMediaSource {
  albumId: string;
  albumName: string;
  role: string;
  sortOrder: number;
}

export type PartyStatus = 'draft' | 'published' | 'live' | 'ended';

export interface Party {
  id: string;
  title: string;
  description: string | null;
  status: PartyStatus;
  /** When the host says it begins. Scheduling: nothing acts on it. */
  eventStartsAt: string | null;
  /** Written by a real transition, never by a clock. */
  liveStartedAt: string | null;
  liveEndedAt: string | null;
  /** A hard stop for guest access, enforced at the public seam. */
  guestAccessExpiresAt: string | null;
  /** Reserved for the post-event library. Nothing reads it yet — no UI. */
  libraryAccessExpiresAt: string | null;
  version: number;
  createdAt: string;
  updatedAt: string;
  mediaSources: PartyMediaSource[];
  /**
   * False once any capability has ever been minted for this party. The main
   * album is fixed from that moment, because guests, greetings, prints and
   * games are scoped to a link that names it.
   */
  canChangeMainMediaSource: boolean;
  /**
   * The invitation's cover as stored, and the owner's own preview of it — an
   * id with no url means the photograph went to Trash or into the Private
   * Vault. Null: the album's chosen cover opens the invitation.
   */
  invitationCoverFileItemId?: string | null;
  invitationCoverUrl?: string | null;
  /** The cover while the party is on. Null: the invitation's cover continues. */
  liveCoverFileItemId?: string | null;
  liveCoverUrl?: string | null;
}

/** What a party card needs, and deliberately nothing else. */
export interface PartySummary {
  id: string;
  title: string;
  status: PartyStatus;
  eventStartsAt: string | null;
  liveStartedAt: string | null;
  liveEndedAt: string | null;
  updatedAt: string;
  mainAlbumId: string | null;
  mainAlbumName: string | null;
}

export function listParties(signal?: AbortSignal): Promise<PartySummary[]> {
  return api<PartySummary[]>('/api/parties', { signal });
}

export function getParty(partyId: string, signal?: AbortSignal): Promise<Party> {
  return api<Party>(`/api/parties/${partyId}`, { signal });
}

// A party begins as a name and a date. No album, no capability, no token, no
// television, no game, no print configuration: the event exists before the
// photographs, and every one of those is a later decision by the host.
export function createParty(
  body: { title: string; description?: string | null; eventStartsAt?: string | null },
  signal?: AbortSignal,
): Promise<Party> {
  return api<Party>('/api/parties', { method: 'POST', json: body, signal });
}

// The party's own DATA, version-checked. It cannot write `status`,
// `liveStartedAt` or `liveEndedAt` — those belong to the transitions below.
export function updateParty(
  partyId: string,
  body: {
    title: string;
    description?: string | null;
    eventStartsAt?: string | null;
    guestAccessExpiresAt?: string | null;
    /** When the MEMORIES stop, which may outlive guest access. */
    libraryAccessExpiresAt?: string | null;
    version: number;
  },
  signal?: AbortSignal,
): Promise<Party> {
  return api<Party>(`/api/parties/${partyId}`, { method: 'PATCH', json: body, signal });
}

// PUT because it states the whole fact — this party's main album is that one.
// Refused with 409 once any capability has existed (`media_source_locked`) or
// when the album already serves another party (`album_already_in_use`).
export function setPartyMainMediaSource(
  partyId: string,
  body: { albumId: string; version: number },
  signal?: AbortSignal,
): Promise<Party> {
  return api<Party>(`/api/parties/${partyId}/media/main`, { method: 'PUT', json: body, signal });
}

/**
 * The party's two covers, stated whole: the invitation's, and the one that
 * takes over while the party is on. null means none chosen — the next cover in
 * line applies. A photograph that is not one of the owner's own images is
 * refused as `invalid_media`; choosing one files it in no album.
 */
export function setPartyCovers(
  partyId: string,
  body: {
    invitationCoverFileItemId: string | null;
    liveCoverFileItemId: string | null;
    version: number;
  },
  signal?: AbortSignal,
): Promise<Party> {
  return api<Party>(`/api/parties/${partyId}/covers`, { method: 'PUT', json: body, signal });
}

/**
 * A party made from another party's CONFIGURATION — the same evening, set up
 * again.
 *
 * It copies decisions and no history. The clone is a Draft with its own album
 * (the same photographs, shared through ordinary membership rows — no byte is
 * duplicated), its own deck, its own settings and brand-new tokens, and none of
 * the guests, preferences, votes, rounds, uploads, greetings, prints,
 * televisions or display grants of the party it was copied from. An omitted
 * title keeps the original's.
 */
export function duplicateParty(
  partyId: string, title?: string, signal?: AbortSignal,
): Promise<Party> {
  return api<Party>(`/api/parties/${partyId}/duplicate`, {
    method: 'POST', json: { title: title ?? null }, signal,
  });
}

// Tearing a party down KEEPS its album. The photographs the guests were allowed
// to see stay; the ones the host never let through go to Trash the ordinary way;
// the party's own rows go with it.
export function tearDownParty(
  partyId: string, version: number, signal?: AbortSignal,
): Promise<void> {
  return api<void>(`/api/parties/${partyId}?version=${version}`, { method: 'DELETE', signal });
}

/** The three lifecycle moves. The caller names an ACTION, never a target state. */
export type PartyLifecycleAction = 'publish' | 'start-live' | 'end-live';

export function transitionParty(
  partyId: string,
  action: PartyLifecycleAction,
  version: number,
  signal?: AbortSignal,
): Promise<Party> {
  return api<Party>(`/api/parties/${partyId}/${action}`, {
    method: 'POST', json: { version }, signal,
  });
}

// --- Owner-side party settings (normal user auth) ---

export function setPartyGameSettings(
  albumId: string,
  settings: {
    gameEnabled: boolean; minChallengeIntervalSeconds: number;
    maxChallengeIntervalSeconds: number; votesPerGuest: number;
    maxChallengesPerSession: number | null;
    // Whether the guests are asked which activities they would like to see,
    // before the match starts, and how many each may pick (`votesPerGuest`).
    // Omitted means unchanged, so a save cannot switch them off by accident.
    priorityVotingEnabled?: boolean;
  },
  signal?: AbortSignal,
): Promise<AlbumPartyStatus> {
  return api<AlbumPartyStatus>(`/api/albums/${albumId}/party-game-settings`, {
    method: 'PATCH', json: settings, signal,
  });
}

// Saves ONLY the four numeric settings. Deliberately a different endpoint from
// setAlbumPartyMode so saving them cannot rotate a token, toggle party/upload,
// or change approval mode as a side effect.
export function setPartySlideshowSettings(
  albumId: string,
  settings: {
    photoSlideSeconds?: number;
    maxVideoSlideSeconds?: number;
    maxPhotoUploadsPerParticipant?: number;
    maxVideoUploadsPerParticipant?: number;
    maxMessagesPerParticipant?: number;
  },
  signal?: AbortSignal,
): Promise<AlbumPartyStatus> {
  return api<AlbumPartyStatus>(`/api/albums/${albumId}/party-slideshow-settings`, {
    method: 'PATCH',
    json: settings,
    signal,
  });
}

export function getAlbumPartySettings(
  albumId: string,
  signal?: AbortSignal,
): Promise<AlbumPartyStatus> {
  return api<AlbumPartyStatus>(`/api/albums/${albumId}/party-settings`, { signal });
}

export function setAlbumPartyMode(
  albumId: string,
  enabled: boolean,
  uploadEnabled?: boolean,
  requireUploadApproval?: boolean,
  signal?: AbortSignal,
  requireMessageApproval?: boolean,
): Promise<AlbumPartyStatus> {
  const json: Record<string, boolean> = { enabled };
  if (uploadEnabled !== undefined) json.uploadEnabled = uploadEnabled;
  if (requireUploadApproval !== undefined) json.requireUploadApproval = requireUploadApproval;
  if (requireMessageApproval !== undefined) json.requireMessageApproval = requireMessageApproval;
  return api<AlbumPartyStatus>(`/api/albums/${albumId}/party-settings`, {
    method: 'PATCH',
    json,
    signal,
  });
}

// --- Owner-side guest CONTENT (normal user auth) ---
//
// Six typed slots, at most one per kind, validated server-side. Every kind comes
// back whether or not the host has written it: an untouched slot arrives at
// version 0 with the product's own default visibility, so the editor renders
// what the server says rather than holding a second opinion about it.

export function listPartyGuestContent(
  partyId: string, signal?: AbortSignal,
): Promise<PartyGuestContentSlot[]> {
  return api<PartyGuestContentSlot[]>(`/api/parties/${partyId}/guest-content`, { signal });
}

// PUT because it states the whole slot. The version is the SLOT's own — editing
// the menu never contends with renaming the party — and 0 creates it.
export function setPartyGuestContent(
  partyId: string,
  kind: PartyGuestContentKind,
  body: {
    enabled: boolean;
    visibleBefore: boolean;
    visibleLive: boolean;
    visibleAfter: boolean;
    content: Record<string, unknown>;
    /**
     * The slot's one photograph: any of the owner's own images, in the party's
     * album or not. Choosing one files it nowhere. null clears it.
     */
    mediaFileItemId: string | null;
    /**
     * How to present it. The server refuses `poster` with no photograph, so a
     * caller clearing the image clears this too.
     */
    mediaPresentation: PartyMediaPresentation;
    /**
     * The words' alignment. Omitted or null leaves the stored choice as it is,
     * so saving the rest of the card never resets it.
     */
    textAlign?: PartyTextAlign | null;
    /** "auto" returns to the whole photograph; omitted leaves the frame. */
    mediaOrientation?: PartyMediaOrientation | 'auto';
    /** Where a fixed frame sits. Omitted or null leaves it; "auto" clears it. */
    mediaCrop?: PartyMediaCrop | null;
    /** Where the words sit; omitted leaves it as it is. */
    textPlacement?: PartyTextPlacement;
    version: number;
  },
  signal?: AbortSignal,
): Promise<PartyGuestContentSlot> {
  return api<PartyGuestContentSlot>(
    `/api/parties/${partyId}/guest-content/${kind}`,
    { method: 'PUT', json: body, signal },
  );
}

// --- Owner-side party upload moderation (normal user auth) ---

export function listPartyUploads(
  albumId: string,
  signal?: AbortSignal,
): Promise<PartyUploadList> {
  return api<PartyUploadList>(`/api/albums/${albumId}/party-uploads`, { signal });
}

// Hide a previously-visible guest upload, approve a pending one, or reject a
// pending one — each removes/adds it from the public party + TV surfaces on the
// next poll. 204 No Content; the caller refreshes the list.
export function moderatePartyUpload(
  albumId: string,
  fileItemId: string,
  action: 'hide' | 'approve' | 'reject' | 'restore',
  signal?: AbortSignal,
): Promise<void> {
  return api<void>(
    `/api/albums/${albumId}/party-uploads/${fileItemId}/${action}`,
    { method: 'POST', signal },
  );
}

// --- Owner-side party PRINT settings (normal user auth) ---

/** One product's own switch, its own budget, and its own usage. */
export interface PartyPrintProductSettings {
  enabled: boolean;
  maxPrints: number;
  /** Prints already accepted into the queue. History: never reset. */
  used: number;
  remaining: number;
  /** What ONE guest may take. 0 means no per-guest limit. */
  perGuest: number;
}

export interface PartyPrintSettings {
  enabled: boolean;
  printStationId: string | null;
  printerDeviceId: string | null;
  // Photo and strip are NEVER summed: they cost different things and the host
  // set them separately.
  photo: PartyPrintProductSettings;
  strip: PartyPrintProductSettings;
  footerText: string | null;
  footerMaxLength: number;
  minBudget: number;
  maxBudget: number;
}

/** Every field optional: an omitted one keeps its value rather than clearing it. */
export interface PartyPrintSettingsPatch {
  enabled?: boolean;
  printStationId?: string;
  printerDeviceId?: string;
  photoEnabled?: boolean;
  photoMaxPrints?: number;
  photoPrintsPerGuest?: number;
  stripEnabled?: boolean;
  stripMaxPrints?: number;
  stripPrintsPerGuest?: number;
  footerText?: string;
}

export function getPartyPrintSettings(
  albumId: string, signal?: AbortSignal,
): Promise<PartyPrintSettings> {
  return api<PartyPrintSettings>(`/api/albums/${albumId}/party-print-settings`, { signal });
}

// A separate endpoint from party-settings on purpose: saving a print budget
// must not be able to rotate a token, flip party mode, or change moderation.
export function setPartyPrintSettings(
  albumId: string, patch: PartyPrintSettingsPatch, signal?: AbortSignal,
): Promise<PartyPrintSettings> {
  return api<PartyPrintSettings>(`/api/albums/${albumId}/party-print-settings`, {
    method: 'PATCH', json: patch, signal,
  });
}

// --- Public party landing (anonymous, token-scoped) ---

// THE guest context: one authoritative answer for the canonical QR route.
//
// The URL never changes; what it opens does. The same code carries a guest from
// the invitation, through the party, to the memories — so the landing asks once
// what they are looking at rather than combining two reads to guess.
//
// Deliberately not a row of `showX` booleans. `content` holds only the slots
// that belong to this phase, and `capabilities` only what is genuinely
// available: absence IS the answer, which is what keeps a disabled tile from
// ever being rendered.

/** Which of the three surfaces the guest is standing in front of. */
export type PartyGuestPhase = 'before' | 'live' | 'after';

/** How much of that surface is open. */
export type PartyGuestAccessMode = 'full' | 'library-only';

/**
 * How a slot's photograph participates in the surface.
 *
 * `inline` — the picture sits in the slot's composition, above its words.
 * `poster` — the picture IS the document: the Party shows a deterministic
 * navigation row and opens it whole in the shared viewer.
 *
 * It selects a RENDERING and confers no authority: both resolve the same
 * reference through the same rule, served by the same relation-scoped route.
 */
export type PartyMediaPresentation = 'inline' | 'poster';

/**
 * How a slot's words are aligned. Absent or null means the surface's own
 * default: left in a section, centred in the thank-you.
 */
export type PartyTextAlign = 'left' | 'center';

/**
 * How a slot's inline photograph is framed in its section. Absent or null is
 * the whole photograph at its own proportions; a fixed format crops it where
 * the host placed it, with the party print's own crop maths.
 */
export type PartyMediaOrientation = 'portrait' | 'landscape';

/** Where a fixed frame sits: the print editor's zoom (1..4) and centre (0..1). */
export interface PartyMediaCrop {
  zoom: number;
  centerX: number;
  centerY: number;
}

/**
 * Where a slot's words sit beside its inline photograph: below it — the
 * default — or on it, across the lower part of the picture.
 */
export type PartyTextPlacement = 'below' | 'overlay';

/** What every projection of a slot shares. `content` is the shape its `kind` declares. */
interface PartyGuestContentFields {
  kind: PartyGuestContentKind;
  enabled: boolean;
  visibleBefore: boolean;
  visibleLive: boolean;
  visibleAfter: boolean;
  content: Record<string, unknown>;
  version: number;
  /** Absent on a pre-P5 payload, which meant `inline`. */
  mediaPresentation: PartyMediaPresentation;
  /** The host's alignment, or null/absent for the surface's own default. */
  textAlign?: PartyTextAlign | null;
  /** The inline photograph's frame, or null/absent for the whole photograph. */
  mediaOrientation?: PartyMediaOrientation | null;
  mediaCrop?: PartyMediaCrop | null;
  /** "overlay" puts the words on the photograph; absent or null is below it. */
  textPlacement?: 'overlay' | null;
}

/**
 * One slot as the OWNER edits it. `mediaFileItemId` is the photograph's
 * reference as stored; `mediaUrl` is the owner's own preview of it, present only
 * while the file still qualifies — an id with no url means the photograph went
 * to Trash or into the Private Vault.
 */
export interface PartyGuestContentSlot extends PartyGuestContentFields {
  mediaFileItemId: string | null;
  mediaUrl: string | null;
}

/**
 * One slot as a GUEST receives it. The photograph is an address on the guest's
 * own token, present exactly when there is a picture to show — never the
 * owner's file id.
 */
export interface PartyGuestContentView extends PartyGuestContentFields {
  mediaUrl: string | null;
}

/** Where a capability LIVES, or nothing. The hub builds no route of its own. */
export interface PartyGuestCapabilities {
  contributionUrl: string | null;
  gameUrl: string | null;
  printUrl: string | null;
  faceSearch: boolean;
}

export interface PartyGuestLibrary {
  available: boolean;
  accessEndsAt: string | null;
}

export interface PartyGuestContext {
  /** The PARTY's name, which is not its album's — they are separate things. */
  title: string;
  phase: PartyGuestPhase;
  accessMode: PartyGuestAccessMode;
  eventStartsAt: string | null;
  /** Named only where an album means something: at the party, and afterwards. */
  albumName: string | null;
  itemCount: number;
  /**
   * The invitation's hero before the party, already resolved by the server:
   * the invitation's own photograph, else the album's chosen cover, else null
   * for a composition.
   */
  coverUrl: string | null;
  content: PartyGuestContentView[];
  capabilities: PartyGuestCapabilities;
  library: PartyGuestLibrary;
}

/** The six things a party has to say, in the order it says them. */
export const PARTY_GUEST_CONTENT_KINDS = [
  'invitation', 'location', 'dress-code', 'menu', 'info', 'thank-you',
] as const;

export type PartyGuestContentKind = (typeof PARTY_GUEST_CONTENT_KINDS)[number];

// --- Party print studio (anonymous, print-token scoped) ---

export type PartyPrintProduct = 'photo' | 'strip4';
export type PartyPrintTheme = 'pure' | 'midnight' | 'event';
/** Absent means the sheet follows the photograph, which is the default. */
export type PartyPrintOrientation = 'portrait' | 'landscape';

export interface PartyPrintFormat {
  type: PartyPrintProduct;
  enabled: boolean;
  /** This product's OWN remaining count for the party. The two are never summed. */
  remaining: number;
  requiredPhotos: number;
  /**
   * What is left of THIS guest's allowance, or null when the host set no
   * per-guest limit. Null is not zero — it means the ceiling does not exist.
   */
  remainingForYou: number | null;
}

/** A choosable photograph: safe derived URLs only, never an original. */
export interface PartyPrintPhoto {
  id: string;
  thumbnailUrl: string;
  previewUrl: string;
}

export interface PartyPrintManifest {
  partyName: string;
  footerText: string | null;
  formats: PartyPrintFormat[];
  photos: PartyPrintPhoto[];
}

/** A crop, normalised to the auto-oriented source so the server reads it the same. */
export interface PartyPrintSlot {
  itemId: string;
  cropX: number;
  cropY: number;
  cropWidth: number;
  cropHeight: number;
}

export interface PartyPrintAccepted {
  jobId: string;
  publicSequence: number;
  product: PartyPrintProduct;
  remainingForProduct: number;
  /** Sheets ahead of this one on the printer. Zero means it is next. */
  queueAhead: number;
}

/** The pipeline's states, reduced to what a guest can act on. */
export type PartyPrintState =
  | 'preparing' | 'queued' | 'printing' | 'completed' | 'failed' | 'unknown';

export interface PartyPrintStatus {
  jobId: string;
  state: PartyPrintState;
  publicSequence: number;
  product: PartyPrintProduct;
}

export function getPartyPrintManifest(
  printToken: string, signal?: AbortSignal,
): Promise<PartyPrintManifest> {
  return api<PartyPrintManifest>(
    `/api/party/${encodeURIComponent(printToken)}/print`, { signal });
}

/**
 * Submit a composition.
 *
 * `idempotencyKey` is minted by the caller and REUSED for retries of the same
 * submission: printing has a physical effect, so a double tap or a replayed
 * request must return the first job rather than start a second sheet.
 */
export function submitPartyPrint(
  printToken: string,
  body: {
    product: PartyPrintProduct;
    theme: PartyPrintTheme;
    slots: PartyPrintSlot[];
    orientation?: PartyPrintOrientation;
  },
  idempotencyKey: string,
  signal?: AbortSignal,
): Promise<PartyPrintAccepted> {
  return api<PartyPrintAccepted>(
    `/api/party/${encodeURIComponent(printToken)}/print`,
    { method: 'POST', json: body, headers: { 'Idempotency-Key': idempotencyKey }, signal });
}

export function getPartyPrintStatus(
  printToken: string, jobId: string, signal?: AbortSignal,
): Promise<PartyPrintStatus> {
  return api<PartyPrintStatus>(
    `/api/party/${encodeURIComponent(printToken)}/print/${encodeURIComponent(jobId)}`,
    { signal });
}

export interface PartyItem {
  id: string;
  mediaType: 'image' | 'video';
  thumbnailUrl: string;
  previewUrl: string;
  // Present for images (metadata-stripped medium download); null for videos.
  downloadUrl: string | null;
}

export interface PartyItems {
  albumName: string;
  items: PartyItem[];
}

// ONE fetch for the landing. What the guest is looking at, how much of it is
// open, what the host wants to tell them and which capabilities are real — all
// of it, from the URL that never changes.
export function getPartyGuestContext(
  token: string, signal?: AbortSignal,
): Promise<PartyGuestContext> {
  return api<PartyGuestContext>(`/api/party/${encodeURIComponent(token)}`, { signal });
}

export function getPartyItems(token: string, signal?: AbortSignal): Promise<PartyItems> {
  return api<PartyItems>(`/api/party/${encodeURIComponent(token)}/items`, { signal });
}

export type PartyChallengeKind = 'dare' | 'penalty' | 'guess' | 'custom';

// How the room decides. The set is designed to grow — rating, multiple choice,
// quiz — so this stays a string union whose members the server also knows;
// nothing here is an index into an ordering.
export type PartyChallengeVotingMode = 'none' | 'binary';

// The activity's rules, as fields rather than as prose inside `body`. Optional
// on the wire so a response from a server that predates them still parses.
export interface PartyChallengeRules {
  /** null = as long as it takes. */
  durationSeconds?: number | null;
  votingMode?: PartyChallengeVotingMode;
  /** null = the localized default question. */
  voteQuestion?: string | null;
}

export interface PartyChallenge extends PartyChallengeRules {
  id: string;
  title: string;
  body: string;
  kind: PartyChallengeKind;
  mediaFileItemId: string | null;
  mediaUrl: string | null;
  isEnabled: boolean;
  sortOrder: number;
  voteCount: number;
  createdAt: string;
  updatedAt: string;
}
export interface PartyChallengeList { albumId: string; items: PartyChallenge[]; }
/**
 * A write from the composer.
 *
 * `kind` is OPTIONAL and the composer no longer sends one: the product's
 * activities are activities, and a category the room never sees was decoration
 * on a form. The field stays on the wire for the adaptive game it was designed
 * for — omitted, the server gives a new activity `custom` and leaves an
 * existing one's kind exactly as it was.
 */
export interface PartyChallengeWrite extends PartyChallengeRules {
  title: string; body: string; kind?: PartyChallengeKind;
  mediaFileItemId: string | null; isEnabled: boolean;
}
export function listPartyChallenges(albumId: string, signal?: AbortSignal): Promise<PartyChallengeList> {
  return api<PartyChallengeList>(`/api/albums/${albumId}/party-challenges`, { signal });
}
export function createPartyChallenge(albumId: string, value: PartyChallengeWrite): Promise<PartyChallenge> {
  return api<PartyChallenge>(`/api/albums/${albumId}/party-challenges`, { method: 'POST', json: value });
}
export function updatePartyChallenge(albumId: string, id: string, value: PartyChallengeWrite): Promise<PartyChallenge> {
  return api<PartyChallenge>(`/api/albums/${albumId}/party-challenges/${id}`, { method: 'PUT', json: value });
}
export function deletePartyChallenge(albumId: string, id: string): Promise<void> {
  return api<void>(`/api/albums/${albumId}/party-challenges/${id}`, { method: 'DELETE' });
}
export function reorderPartyChallenges(albumId: string, challengeIds: string[]): Promise<void> {
  return api<void>(`/api/albums/${albumId}/party-challenges/order`, {
    method: 'PUT', json: { challengeIds },
  });
}

export interface PartyGuestChallenge {
  id: string; title: string; body: string; kind: PartyChallengeKind;
  mediaUrl: string | null; voted: boolean;
}
export interface PartyGuestChallenges {
  albumName: string; votesPerGuest: number; votesUsed: number; votesRemaining: number;
  items: PartyGuestChallenge[];
}
export interface PartyVoteResult { voted: boolean; votesUsed: number; votesRemaining: number; }
export function listPartyGuestChallenges(token: string, signal?: AbortSignal): Promise<PartyGuestChallenges> {
  return api<PartyGuestChallenges>(`/api/party/${encodeURIComponent(token)}/challenges`, { signal });
}
export function setPartyChallengeVote(token: string, id: string, voted: boolean): Promise<PartyVoteResult> {
  return api<PartyVoteResult>(
    `/api/party/${encodeURIComponent(token)}/challenges/${encodeURIComponent(id)}/vote`,
    { method: voted ? 'PUT' : 'DELETE' },
  );
}

// --- Public party UPLOAD (anonymous, upload-token scoped) ---

export interface PartyUploadResult {
  // Total accepted, kept for compatibility with the pre-video contract.
  accepted: number;
  rejected: number;
  // Per-kind breakdown and quota state. Optional so an older server response
  // still parses; the page treats a missing field as "not reported".
  acceptedPhotos?: number;
  acceptedVideos?: number;
  quotaRejectedPhotos?: number;
  quotaRejectedVideos?: number;
  // null = that kind is unlimited (never 0-means-unlimited on the wire).
  remainingPhotos?: number | null;
  remainingVideos?: number | null;
}

// What this guest may still upload on this link. Created or reused server-side;
// the participant identity itself lives in an HttpOnly cookie this code cannot
// read, which is the point — a quota the client could see is a quota the client
// could edit.
export interface PartyUploadSession {
  maxPhotos: number | null;
  maxVideos: number | null;
  usedPhotos: number;
  usedVideos: number;
  remainingPhotos: number | null;
  remainingVideos: number | null;
  // Greetings, reported the same way: null max and null remaining are the
  // host having set no limit, never "none left".
  maxMessages?: number | null;
  usedMessages?: number;
  remainingMessages?: number | null;
}

// Idempotent. Safe to call on every page load: it mints a session the first
// time and reuses it afterwards.
export function startPartyUploadSession(
  uploadToken: string,
  signal?: AbortSignal,
): Promise<PartyUploadSession> {
  return api<PartyUploadSession>(
    `/api/party/${encodeURIComponent(uploadToken)}/upload-session`,
    { method: 'POST', signal },
  );
}

// Declared types the party upload endpoint will consider. The SERVER decides
// what a file really is after ingest; this only keeps the picker and the
// obviously-pointless-upload check honest.
export const PARTY_VIDEO_TYPES = ['video/mp4', 'video/webm', 'video/quicktime'] as const;

export type PartyMediaKind = 'photo' | 'video' | 'unsupported';

// Best-effort client classification from the browser-reported type. UX only:
// it decides which counter a file is charged against locally and which files
// are obviously over quota, never whether the upload is allowed.
export function classifyPartyFile(file: File): PartyMediaKind {
  const type = (file.type || '').toLowerCase();
  if (type.startsWith('image/')) return 'photo';
  if ((PARTY_VIDEO_TYPES as readonly string[]).includes(type)) return 'video';
  return 'unsupported';
}

// Uploads one or more image files to a party album using the separate upload
// token. No auth; the multipart body is sent as-is (the browser sets the
// boundary). Safe count DTO back — no ids or storage internals.
export function uploadToParty(
  uploadToken: string,
  files: File[],
  signal?: AbortSignal,
): Promise<PartyUploadResult> {
  const form = new FormData();
  for (const file of files) {
    form.append('file', file, file.name);
  }
  return api<PartyUploadResult>(`/api/party/${encodeURIComponent(uploadToken)}/upload`, {
    method: 'POST',
    formData: form,
    signal,
  });
}

// Same public upload as uploadToParty, but via XMLHttpRequest so the UI can show
// real BYTE progress (fetch cannot report upload progress). `onProgress` gets a
// 0..1 fraction of bytes sent; it reaches 1 while the server is still processing
// (moderation / derivatives), so the caller should show a "processing" state
// after that until this promise resolves. Same-origin + credentials to match the
// fetch client; errors surface as ApiError so callers keep one error type.
export function uploadToPartyWithProgress(
  uploadToken: string,
  files: File[],
  onProgress?: (fraction: number) => void,
  signal?: AbortSignal,
): Promise<PartyUploadResult> {
  const form = new FormData();
  for (const file of files) {
    form.append('file', file, file.name);
  }
  const url = `/api/party/${encodeURIComponent(uploadToken)}/upload`;
  return new Promise<PartyUploadResult>((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('POST', url);
    xhr.withCredentials = true;
    if (xhr.upload && onProgress) {
      xhr.upload.onprogress = (e) => {
        if (e.lengthComputable && e.total > 0) onProgress(Math.min(1, e.loaded / e.total));
      };
    }
    xhr.onload = () => {
      let parsed: unknown = null;
      const text = xhr.responseText;
      if (text && text.length > 0) {
        try { parsed = JSON.parse(text); } catch { parsed = text; }
      }
      if (xhr.status >= 200 && xhr.status < 300) {
        resolve(parsed as PartyUploadResult);
      } else {
        reject(new ApiError(xhr.status, `Request failed: POST ${url} → ${xhr.status}`, parsed));
      }
    };
    xhr.onerror = () => reject(new ApiError(0, `Request failed: POST ${url}`, null));
    xhr.onabort = () => reject(new DOMException('Aborted', 'AbortError'));
    if (signal) {
      if (signal.aborted) { xhr.abort(); return; }
      signal.addEventListener('abort', () => xhr.abort(), { once: true });
    }
    xhr.send(form);
  });
}

// --- Public party FACE SEARCH ("find your face", anonymous, view-token scoped) ---

// Safe machine status the UI maps to localized copy. The selfie is processed in
// memory server-side and never stored; no similarity score / face id / person id
// / vector is ever returned.
export type PartyFaceSearchStatus = 'ready' | 'no_face' | 'invalid_image' | 'unavailable';

/**
 * Where the detected face is, as FRACTIONS of the analysed image.
 *
 * Fractions because the phone downscales the selfie before uploading — a pixel
 * box would be in the wrong units the moment it was drawn on what the phone
 * holds. And orientation agrees without arranging it: the browser decodes with
 * `imageOrientation: 'from-image'` and the server auto-orients, so "upright"
 * means the same thing at both ends.
 */
export interface PartyFaceBox {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface PartyFaceSearchResponse {
  status: PartyFaceSearchStatus;
  // Present only for a ready search (so the guest/TV can re-fetch it).
  searchId: string | null;
  resultCount: number;
  // Party-safe media items (same metadata-stripped derived URLs as the grid).
  items: PartyItem[];
  // Only on a completed search, and only enough to frame the selfie the phone
  // already has — no landmarks, no descriptor, no score.
  face?: PartyFaceBox | null;
}

// Upload one selfie and search THIS party album for matching photos. The server
// returns the safe DTO both on success (200) and on the capability-unavailable
// (503) / invalid-image (400) paths, so we normalise the ApiError body back to a
// PartyFaceSearchResponse the UI can render as a localized state.
export async function partyFaceSearch(
  token: string,
  file: File,
  signal?: AbortSignal,
): Promise<PartyFaceSearchResponse> {
  const form = new FormData();
  form.append('file', file, file.name);
  try {
    return await api<PartyFaceSearchResponse>(
      `/api/party/${encodeURIComponent(token)}/face-search`,
      { method: 'POST', formData: form, signal },
    );
  } catch (err) {
    if (
      err instanceof ApiError
      && err.body
      && typeof err.body === 'object'
      && 'status' in (err.body as Record<string, unknown>)
    ) {
      return err.body as PartyFaceSearchResponse;
    }
    throw err;
  }
}

// Explicitly activate a completed face search as the paired TV's face filter
// ("Show these photos on TV"). Completing a search never touches the TV by
// itself. The server enforces ordering: 409 {error:"no_matches"} for an empty
// search, 409 {error:"stale_search"} when a newer search is already active,
// 404 for an unknown/expired search.
export interface PartyFaceSearchActivation {
  searchId: string;
  // Server-assigned monotonic activation order (opaque counter).
  activationVersion: number;
}

export function activatePartyFaceSearchTv(
  token: string,
  searchId: string,
  signal?: AbortSignal,
): Promise<PartyFaceSearchActivation> {
  return api<PartyFaceSearchActivation>(
    `/api/party/${encodeURIComponent(token)}/face-search/${encodeURIComponent(searchId)}/activate-tv`,
    { method: 'POST', signal },
  );
}

// Cancel/delete a face search (session + stored face crop server-side). If this
// search is the active TV filter, deleting it also deactivates the TV.
// Idempotent (204 even when already gone); row-scoped, so cancelling an older
// search never removes a newer active TV filter.
export function deletePartyFaceSearch(
  token: string,
  searchId: string,
  signal?: AbortSignal,
): Promise<void> {
  return api<void>(
    `/api/party/${encodeURIComponent(token)}/face-search/${encodeURIComponent(searchId)}`,
    { method: 'DELETE', signal },
  );
}

// Re-fetch a stored face search's currently-visible matches (rank order). Throws
// ApiError(404) once the search expires or the party is disabled.
export function getPartyFaceSearch(
  token: string,
  searchId: string,
  signal?: AbortSignal,
): Promise<PartyFaceSearchResponse> {
  return api<PartyFaceSearchResponse>(
    `/api/party/${encodeURIComponent(token)}/face-search/${encodeURIComponent(searchId)}`,
    { signal },
  );
}

// --- Party guest MESSAGES ---
//
// A text-only channel beside the photo/video stream. Nothing here touches the
// media contract: a message is never a PartyItem and never a TV album item.

export function listPartyMessages(
  albumId: string,
  signal?: AbortSignal,
): Promise<PartyMessageList> {
  return api<PartyMessageList>(`/api/albums/${albumId}/party-messages`, { signal });
}

// Owner or delegate. 204 No Content; the caller refreshes the list. Promoting
// a message that is not currently visible is a 400 — the UI only offers Hero on
// live messages, so this is the backstop rather than an expected path.
export function moderatePartyMessage(
  albumId: string,
  messageId: string,
  action: PartyMessageAction,
  signal?: AbortSignal,
): Promise<void> {
  return api<void>(
    `/api/albums/${albumId}/party-messages/${messageId}/${action}`,
    { method: 'POST', signal },
  );
}

// --- Public guest message submission (anonymous, upload-token scoped) ---

export interface PartyMessageSubmission {
  id: string;
  // 'pending' when the host reads greetings before they go up, else 'visible'.
  status: 'visible' | 'pending';
  createdAt: string;
}

// --- The GUEST CONSOLE (owner, party.access) ---
//
// The host's "Ospiti" reads the guest list in PAGES (the directory) and one
// group on demand (its detail); it never downloads the whole list. So every
// owner write here asks for `Prefer: return=minimal` and receives only what
// changed — the list's header and the group it touched, a delivery and the
// party, an arrival and the counts — and the page updates the card it holds.
// A refusal that describes a state — a stale version, a party already under
// way, a mailer that is not configured — is a 409 whose body says which
// (`error`, and `party` where a send or share would have published it).

const MINIMAL = { Prefer: PREFER_RETURN_MINIMAL } as const;

/** The whole list at once — for a caller that really needs every group. The console does not. */
export function getPartyGuestList(partyId: string, signal?: AbortSignal): Promise<PartyGuestList> {
  return api<PartyGuestList>(partyGuestListPath(partyId), { signal });
}

/** One page of the directory: searched, filtered and ordered by the server. */
export function getPartyGuestDirectory(
  partyId: string, query: GuestDirectoryQuery, signal?: AbortSignal,
): Promise<GuestDirectoryPage> {
  return api<GuestDirectoryPage>(partyGuestDirectoryPath(partyId, query), { signal });
}

/** One group in detail, with its card and the party's counts. */
export function getPartyInvitationGroup(
  partyId: string, groupId: string, signal?: AbortSignal,
): Promise<PartyInvitationGroupDetail> {
  return api<PartyInvitationGroupDetail>(partyInvitationGroupDetailPath(partyId, groupId), { signal });
}

export function getPartyRsvpQuestions(
  partyId: string, signal?: AbortSignal,
): Promise<{ questions: PartyRsvpQuestion[] }> {
  return api<{ questions: PartyRsvpQuestion[] }>(partyRsvpQuestionsPath(partyId), { signal });
}

export function createPartyInvitationGroup(
  partyId: string, body: PartyInvitationGroupWrite, signal?: AbortSignal,
): Promise<PartyGuestListMinimal> {
  return api<PartyGuestListMinimal>(
    partyInvitationGroupsPath(partyId), { method: 'POST', json: body, headers: MINIMAL, signal });
}

/** PUT states the NAMED guests whole; the group's own +1s are kept. */
export function updatePartyInvitationGroup(
  partyId: string, groupId: string, body: PartyInvitationGroupWrite, signal?: AbortSignal,
): Promise<PartyGuestListMinimal> {
  return api<PartyGuestListMinimal>(
    partyInvitationGroupPath(partyId, groupId), { method: 'PUT', json: body, headers: MINIMAL, signal });
}

/** Removing a group revokes its personal link and erases its answers. */
export function deletePartyInvitationGroup(
  partyId: string, groupId: string, version: number, signal?: AbortSignal,
): Promise<PartyGuestListMinimal> {
  return api<PartyGuestListMinimal>(
    `${partyInvitationGroupPath(partyId, groupId)}?version=${version}`, { method: 'DELETE', headers: MINIMAL, signal });
}

/** Every link sent or shared before stops opening anything; it must be shared again. */
export function rotatePartyInvitationLink(
  partyId: string, groupId: string, version: number, signal?: AbortSignal,
): Promise<PartyGuestListMinimal> {
  return api<PartyGuestListMinimal>(
    partyInvitationGroupActionPath(partyId, groupId, 'rotate-link'),
    { method: 'POST', json: { version }, headers: MINIMAL, signal });
}

export type PartyInvitationSendResult = PartyInvitationSendMinimal<Party>;

/**
 * One click. `clientRequestId` is minted per click and reused for that click's
 * retries: the same id never sends a second email. `partyVersion` is what lets
 * a first send publish a Draft party without overwriting somebody else's edit.
 */
export function sendPartyInvitation(
  partyId: string,
  groupId: string,
  body: { clientRequestId: string; partyVersion?: number },
  signal?: AbortSignal,
): Promise<PartyInvitationSendResult> {
  return api<PartyInvitationSendResult>(
    partyInvitationGroupActionPath(partyId, groupId, 'send'), { method: 'POST', json: body, headers: MINIMAL, signal });
}

/** Only for a group invited on the link it holds now that has not answered. */
export function remindPartyInvitation(
  partyId: string, groupId: string, clientRequestId: string, signal?: AbortSignal,
): Promise<PartyInvitationSendResult> {
  return api<PartyInvitationSendResult>(
    partyInvitationGroupActionPath(partyId, groupId, 'remind'),
    { method: 'POST', json: { clientRequestId }, headers: MINIMAL, signal });
}

/**
 * WhatsApp or Copia link: the group's CURRENT personal link, composed by the
 * server on its public origin, handed to the host to share. One click, one id;
 * a retry of the click hands back the same link and records nothing new. The
 * answer's link and message are for passing on, never for keeping.
 */
export function sharePartyInvitation(
  partyId: string, groupId: string, body: InvitationShareRequest, signal?: AbortSignal,
): Promise<InvitationShareResult<Party>> {
  return api<InvitationShareResult<Party>>(
    partyInvitationSharePath(partyId, groupId), { method: 'POST', json: body, signal });
}

export function createPartyRsvpQuestion(
  partyId: string, body: PartyRsvpQuestionWrite, signal?: AbortSignal,
): Promise<PartyGuestListMinimal> {
  return api<PartyGuestListMinimal>(
    partyRsvpQuestionsPath(partyId), { method: 'POST', json: body, headers: MINIMAL, signal });
}

/** A question that has been answered may only change its activation. */
export function updatePartyRsvpQuestion(
  partyId: string, questionId: string, body: PartyRsvpQuestionWrite, signal?: AbortSignal,
): Promise<PartyGuestListMinimal> {
  return api<PartyGuestListMinimal>(
    partyRsvpQuestionPath(partyId, questionId), { method: 'PUT', json: body, headers: MINIMAL, signal });
}

/** The whole order, every time: exactly this party's questions, once each. */
export function reorderPartyRsvpQuestions(
  partyId: string, questionIds: string[], signal?: AbortSignal,
): Promise<PartyGuestListMinimal> {
  return api<PartyGuestListMinimal>(partyRsvpQuestionOrderPath(partyId), {
    method: 'PUT', json: { questionIds }, headers: MINIMAL, signal,
  });
}

// --- ATTENDANCE (owner, party.access) ---
//
// Who arrived. Every write answers `{ changed, summary, guest | otherGuest }` —
// whether it changed anything, the counts, and the one person as the record
// now reads them. A refusal that describes a state — the party has not
// started, a name changed meanwhile — is a 409 carrying `{ error, summary }`.

/** The whole attendance at once. The console reads arrivals through the directory instead. */
export function getPartyAttendance(partyId: string, signal?: AbortSignal): Promise<PartyAttendance> {
  return api<PartyAttendance>(partyAttendancePath(partyId), { signal });
}

/** "This person arrived." Idempotent: the first moment recorded is kept. */
export function checkInPartyGuest(
  partyId: string, guestId: string, signal?: AbortSignal,
): Promise<PartyAttendanceChange> {
  return api<PartyAttendanceChange>(
    partyAttendanceGuestPath(partyId, guestId), { method: 'PUT', headers: MINIMAL, signal });
}

/** "That arrival was recorded by mistake" — a correction, never a check-out. */
export function undoPartyGuestCheckIn(
  partyId: string, guestId: string, signal?: AbortSignal,
): Promise<PartyAttendanceChange> {
  return api<PartyAttendanceChange>(
    partyAttendanceGuestPath(partyId, guestId), { method: 'DELETE', headers: MINIMAL, signal });
}

/** Somebody not on the guest list. The request id makes a retry of one add name them once. */
export function createPartyAttendanceGuest(
  partyId: string, body: PartyAttendanceOtherGuestCreate, signal?: AbortSignal,
): Promise<PartyAttendanceChange> {
  return api<PartyAttendanceChange>(
    partyAttendanceOtherGuestsPath(partyId), { method: 'POST', json: body, headers: MINIMAL, signal });
}

export function updatePartyAttendanceGuest(
  partyId: string, attendanceGuestId: string, body: PartyAttendanceOtherGuestUpdate, signal?: AbortSignal,
): Promise<PartyAttendanceChange> {
  return api<PartyAttendanceChange>(
    partyAttendanceOtherGuestPath(partyId, attendanceGuestId), { method: 'PUT', json: body, headers: MINIMAL, signal });
}

export function deletePartyAttendanceGuest(
  partyId: string, attendanceGuestId: string, signal?: AbortSignal,
): Promise<PartyAttendanceChange> {
  return api<PartyAttendanceChange>(
    partyAttendanceOtherGuestPath(partyId, attendanceGuestId), { method: 'DELETE', headers: MINIMAL, signal });
}

// --- The PERSONAL INVITATION (anonymous, invitation-token scoped) ---
//
// Not the party's QR token and never built from one. It opens one group's
// invitation and reply, and — while the party is live — its own people's
// "Sono qui". No upload, game, print or greeting.

/** What a personal link opens, with this client's own guest-content slot shape. */
export type PartyInvitationViewModel = PartyInvitationView<PartyGuestContentView>;

export function getPartyInvitation(token: string, signal?: AbortSignal): Promise<PartyInvitationViewModel> {
  return api<PartyInvitationViewModel>(partyInvitationPath(token), { signal });
}

/**
 * The WHOLE reply. A 409 carries `{ error, invitation }`: `version_conflict`
 * when the group's reply changed meanwhile, `rsvp_closed` once the party has
 * started — either way the page shows the invitation as it now is.
 */
export function submitPartyRsvp(
  token: string, reply: PartyRsvpWrite, signal?: AbortSignal,
): Promise<PartyInvitationViewModel> {
  return api<PartyInvitationViewModel>(partyInvitationRsvpPath(token), { method: 'PUT', json: reply, signal });
}

/**
 * "Sono qui" for one of the group's own people, while the party is live. It
 * answers with the invitation as it now is; a 409 carries `{ error, invitation }`
 * (`attendance_not_open`). It mints no participant: entering the party is a
 * separate step through the party's own public page.
 */
export function selfCheckInPartyGuest(
  token: string, guestId: string, signal?: AbortSignal,
): Promise<PartyInvitationViewModel> {
  return api<PartyInvitationViewModel>(partyInvitationAttendanceGuestPath(token, guestId), { method: 'PUT', signal });
}

/**
 * Takes back the group's OWN "Sono qui". An arrival the host recorded is
 * refused with `attendance_recorded_by_host`: only the host corrects it.
 */
export function undoSelfCheckInPartyGuest(
  token: string, guestId: string, signal?: AbortSignal,
): Promise<PartyInvitationViewModel> {
  return api<PartyInvitationViewModel>(partyInvitationAttendanceGuestPath(token, guestId), { method: 'DELETE', signal });
}

// The UPLOAD token, not the view token: writing is contributing, and the same
// switch that closes photo uploads closes this.
export function submitPartyMessage(
  uploadToken: string,
  message: { displayName?: string | null; text: string },
  signal?: AbortSignal,
): Promise<PartyMessageSubmission> {
  return api<PartyMessageSubmission>(
    `/api/party/${encodeURIComponent(uploadToken)}/messages`,
    { method: 'POST', json: message, signal },
  );
}
