// Party: owner settings, guest-media moderation and message moderation
// (§30-§37).
//
// Three things live here that a client must not decide for itself:
//
//   1. THE VALIDATION RANGES. §33/§34 are explicit that these must not be
//      duplicated in a UI file. The server is still the validator; these let a
//      client refuse a bad value before a round-trip, and — the part that
//      matters — let both clients refuse the SAME values.
//   2. THE MESSAGE TRANSITION MATRIX. Which actions a message admits follows
//      from its state, and the rules were previously inlined in the web's JSX.
//      A second client recreating them from the markup is how two surfaces come
//      to offer different buttons for the same message.
//   3. THE ROUTES AND PAYLOADS.
//
// Guest-MEDIA moderation and MESSAGE moderation are kept apart on purpose
// (§35). They are separate domains with separate states; merging them into one
// artificial state machine would make every future change to either one a
// change to both.

import { QueryBuilder, type QueryParams } from './query.ts';

// ── Owner-side status ──────────────────────────────────────────────────────

export interface AlbumPartyStatus {
  albumId: string;
  showOnTv: boolean;
  partyMode: boolean;
  /** Relative public landing URL while party mode is active, else null.
   * NEVER a token hash. A client prepends its own origin. */
  partyUrl: string | null;
  uploadEnabled: boolean;
  /** Relative public upload URL — a SEPARATE token from partyUrl. */
  uploadUrl: string | null;
  /** New guest uploads wait for approval before appearing publicly. */
  requireUploadApproval: boolean;
  /** New guest MESSAGES wait for approval. Independent of the upload flag. */
  requireMessageApproval: boolean;
  photoSlideSeconds: number;
  maxVideoSlideSeconds: number;
  /** 0 means unlimited. */
  maxPhotoUploadsPerParticipant: number;
  maxVideoUploadsPerParticipant: number;
  // Optional for rolling compatibility with a pre-game backend.
  gameEnabled?: boolean;
  minChallengeIntervalSeconds?: number;
  maxChallengeIntervalSeconds?: number;
  votesPerGuest?: number;
  maxChallengesPerSession?: number | null;
}

// ── Validation ranges (§33, §34) ───────────────────────────────────────────

export const PARTY_SLIDESHOW_RANGES = {
  photoSeconds: { min: 3, max: 60 },
  maxVideoSeconds: { min: 5, max: 600 },
  quota: { min: 0, max: 10000 },
} as const;

export const PARTY_GAME_RANGES = {
  intervalSeconds: { min: 30, max: 86400 },
  votes: { min: 1, max: 20 },
  maxPerSession: { min: 1, max: 100 },
} as const;

export interface NumericRange { min: number; max: number; }

export function isWithinRange(value: number, range: NumericRange): boolean {
  return Number.isFinite(value) && value >= range.min && value <= range.max;
}

/** Pull a value inside its range. For steppers and sliders, not for hiding a
 * server rejection: an out-of-range value the user TYPED should be refused
 * visibly, not silently corrected. */
export function clampToRange(value: number, range: NumericRange): number {
  if (!Number.isFinite(value)) return range.min;
  return Math.min(range.max, Math.max(range.min, value));
}

export interface PartySlideshowSettings {
  photoSlideSeconds: number;
  maxVideoSlideSeconds: number;
  maxPhotoUploadsPerParticipant: number;
  maxVideoUploadsPerParticipant: number;
}

/** Every field that is out of range, so a form can mark them all at once
 * rather than one per round-trip. Empty means the server will accept it. */
export function invalidSlideshowFields(s: PartySlideshowSettings): string[] {
  const bad: string[] = [];
  if (!isWithinRange(s.photoSlideSeconds, PARTY_SLIDESHOW_RANGES.photoSeconds)) {
    bad.push('photoSlideSeconds');
  }
  if (!isWithinRange(s.maxVideoSlideSeconds, PARTY_SLIDESHOW_RANGES.maxVideoSeconds)) {
    bad.push('maxVideoSlideSeconds');
  }
  if (!isWithinRange(s.maxPhotoUploadsPerParticipant, PARTY_SLIDESHOW_RANGES.quota)) {
    bad.push('maxPhotoUploadsPerParticipant');
  }
  if (!isWithinRange(s.maxVideoUploadsPerParticipant, PARTY_SLIDESHOW_RANGES.quota)) {
    bad.push('maxVideoUploadsPerParticipant');
  }
  return bad;
}

export interface PartyGameSettings {
  gameEnabled: boolean;
  minChallengeIntervalSeconds: number;
  maxChallengeIntervalSeconds: number;
  votesPerGuest: number;
  /** null means no cap for the session. */
  maxChallengesPerSession: number | null;
}

export function invalidGameFields(s: PartyGameSettings): string[] {
  const bad: string[] = [];
  if (!isWithinRange(s.minChallengeIntervalSeconds, PARTY_GAME_RANGES.intervalSeconds)) {
    bad.push('minChallengeIntervalSeconds');
  }
  if (!isWithinRange(s.maxChallengeIntervalSeconds, PARTY_GAME_RANGES.intervalSeconds)) {
    bad.push('maxChallengeIntervalSeconds');
  }
  // An inverted interval is in range field-by-field and still nonsense.
  if (s.minChallengeIntervalSeconds > s.maxChallengeIntervalSeconds) {
    bad.push('maxChallengeIntervalSeconds');
  }
  if (!isWithinRange(s.votesPerGuest, PARTY_GAME_RANGES.votes)) bad.push('votesPerGuest');
  if (
    s.maxChallengesPerSession !== null
    && !isWithinRange(s.maxChallengesPerSession, PARTY_GAME_RANGES.maxPerSession)
  ) {
    bad.push('maxChallengesPerSession');
  }
  return bad;
}

// ── The settings PATCH payload ─────────────────────────────────────────────

/**
 * THE wire body for PATCH /api/albums/{id}/party-settings.
 *
 * `enabled` is the MASTER SWITCH and is REQUIRED. On the server it is a
 * non-nullable bool, so a body that omits it deserialises to `false` and
 * DISABLES the party — revoking every public link. A sub-toggle that forgot to
 * carry it would therefore turn the party off while claiming to change guest
 * uploads, which is why this type has no optional `enabled`.
 */
export interface PartySettingsPatch {
  enabled: boolean;
  uploadEnabled?: boolean;
  requireUploadApproval?: boolean;
  requireMessageApproval?: boolean;
}

/**
 * Build a settings patch from the CURRENT status plus the one thing being
 * changed.
 *
 * `enabled` always comes from `current.partyMode` unless the caller is
 * explicitly changing it. It is never derived from `uploadEnabled` or from an
 * approval flag: those are sub-switches of a running party, and inferring the
 * master switch from one of them is exactly the mistake this builder exists to
 * make impossible.
 */
export function partySettingsPatch(
  current: Pick<AlbumPartyStatus, 'partyMode'>,
  changes: Partial<{
    enabled: boolean;
    uploadEnabled: boolean;
    requireUploadApproval: boolean;
    requireMessageApproval: boolean;
  }> = {},
): PartySettingsPatch {
  const patch: PartySettingsPatch = {
    enabled: changes.enabled ?? current.partyMode,
  };
  if (changes.uploadEnabled !== undefined) patch.uploadEnabled = changes.uploadEnabled;
  if (changes.requireUploadApproval !== undefined) {
    patch.requireUploadApproval = changes.requireUploadApproval;
  }
  if (changes.requireMessageApproval !== undefined) {
    patch.requireMessageApproval = changes.requireMessageApproval;
  }
  return patch;
}

// ── Game defaults ──────────────────────────────────────────────────────────

/**
 * The server's own defaults, so a client filling a form for an album whose
 * game has never been configured offers what the server would have used.
 * Inventing softer numbers locally is how two clients come to disagree about
 * an unset value.
 */
export const PARTY_GAME_DEFAULTS = {
  minChallengeIntervalSeconds: 300,
  maxChallengeIntervalSeconds: 540,
  votesPerGuest: 3,
  maxChallengesPerSession: null,
} as const;

/** Read the game settings out of a status, filling unset fields with the
 * SERVER defaults rather than with numbers a client made up. */
export function gameSettingsFromStatus(status: AlbumPartyStatus): PartyGameSettings {
  return {
    gameEnabled: status.gameEnabled ?? false,
    minChallengeIntervalSeconds:
      status.minChallengeIntervalSeconds ?? PARTY_GAME_DEFAULTS.minChallengeIntervalSeconds,
    maxChallengeIntervalSeconds:
      status.maxChallengeIntervalSeconds ?? PARTY_GAME_DEFAULTS.maxChallengeIntervalSeconds,
    votesPerGuest: status.votesPerGuest ?? PARTY_GAME_DEFAULTS.votesPerGuest,
    maxChallengesPerSession:
      status.maxChallengesPerSession ?? PARTY_GAME_DEFAULTS.maxChallengesPerSession,
  };
}

/** The slideshow settings a status carries. Always present on the wire, so
 * there are no defaults to invent here. */
export function slideshowSettingsFromStatus(status: AlbumPartyStatus): PartySlideshowSettings {
  return {
    photoSlideSeconds: status.photoSlideSeconds,
    maxVideoSlideSeconds: status.maxVideoSlideSeconds,
    maxPhotoUploadsPerParticipant: status.maxPhotoUploadsPerParticipant,
    maxVideoUploadsPerParticipant: status.maxVideoUploadsPerParticipant,
  };
}

// ── Guest MEDIA moderation (§35) ───────────────────────────────────────────
// A separate domain from messages. Same album, different state machine.

export type PartyUploadStatus =
  | 'approved' | 'pending' | 'hidden' | 'rejected' | 'removed_from_album';

export interface PartyUploadItem {
  fileItemId: string;
  name: string;
  mediaType: 'image' | 'video';
  status: PartyUploadStatus;
  /** Owner-auth thumbnail path. Never a storage key. */
  thumbnailUrl: string;
  uploadedAt: string;
  moderatedAt: string | null;
}

export interface PartyUploadList {
  albumId: string;
  requireUploadApproval: boolean;
  items: PartyUploadItem[];
}

// ── MESSAGE moderation (§36) ───────────────────────────────────────────────

export type PartyMessageStatus = 'pending' | 'visible' | 'hidden' | 'rejected';

export type PartyMessageAction =
  | 'approve' | 'reject' | 'hide' | 'restore' | 'promote-hero' | 'demote-hero';

export interface PartyMessage {
  id: string;
  /** The name the guest typed, or null when they signed nothing. Never an
   * empty string, so a UI has one case to handle. */
  displayName: string | null;
  /** PLAIN TEXT. Render as text — never through a markup or URI interpreter. */
  text: string;
  status: PartyMessageStatus;
  createdAt: string;
  moderatedAt: string | null;
  isHero: boolean;
  heroPromotedAt: string | null;
}

export interface PartyMessageList {
  albumId: string;
  /** False when no party is running: the queue is empty because there is no
   * event, not because nobody has written anything. */
  partyActive: boolean;
  requireMessageApproval: boolean;
  /** False for a DELEGATE. A delegate moderates messages and never sees the
   * owner-only party settings — though the SERVER, not this flag, enforces it
   * (§37: the delegation is narrow, and a client flag is not the boundary). */
  isOwner: boolean;
  items: PartyMessage[];
}

/**
 * The minimal shape the transition matrix needs. A full PartyMessage satisfies
 * it structurally, and so does a list row that carries only these two fields —
 * the matrix has no business requiring the text of a message to decide what may
 * be done to it.
 */
export interface PartyMessageModeration {
  status: PartyMessageStatus;
  isHero: boolean;
}

/**
 * Which actions this message admits, in display order.
 *
 * THE TRANSITION MATRIX, in one place. It used to be a set of conditions
 * inlined in the web's markup, which is not somewhere a second client can read
 * it — so a phone would have had to infer the rules from the buttons, and the
 * two surfaces would drift the first time either changed.
 *
 * Hero is offered only on a LIVE message, matching the server, which refuses
 * to promote anything not currently visible. The server remains the authority:
 * this decides what to OFFER, never what is permitted.
 */
export function partyMessageActions(message: PartyMessageModeration): PartyMessageAction[] {
  const actions: PartyMessageAction[] = [];
  if (message.status === 'pending') actions.push('approve', 'reject');
  if (message.status === 'visible') actions.push('hide');
  if (message.status === 'hidden' || message.status === 'rejected') actions.push('restore');
  if (message.status === 'visible' && !message.isHero) actions.push('promote-hero');
  if (message.isHero) actions.push('demote-hero');
  return actions;
}

/** Whether an action may be offered for this message. */
export function isPartyMessageActionAllowed(
  message: PartyMessageModeration,
  action: PartyMessageAction,
): boolean {
  return partyMessageActions(message).includes(action);
}

/** Actions that destroy visibility, so a client can confirm before running one. */
export const DESTRUCTIVE_PARTY_MESSAGE_ACTIONS: readonly PartyMessageAction[] =
  ['reject', 'hide'];

// ── Routes and payloads (§43) ──────────────────────────────────────────────

export function albumPartyStatusPath(albumId: string): string {
  return `/api/albums/${albumId}/party-settings`;
}
export function albumPartySlideshowPath(albumId: string): string {
  return `/api/albums/${albumId}/party-slideshow-settings`;
}
export function albumPartyGamePath(albumId: string): string {
  return `/api/albums/${albumId}/party-game-settings`;
}
export function albumPartyUploadsPath(albumId: string): string {
  return `/api/albums/${albumId}/party-uploads`;
}
export function albumPartyUploadActionPath(
  albumId: string,
  uploadId: string,
  action: string,
): string {
  return `${albumPartyUploadsPath(albumId)}/${uploadId}/${action}`;
}
export function albumPartyMessagesPath(albumId: string): string {
  return `/api/albums/${albumId}/party-messages`;
}
export function albumPartyMessageActionPath(
  albumId: string,
  messageId: string,
  action: PartyMessageAction,
): string {
  return `${albumPartyMessagesPath(albumId)}/${messageId}/${action}`;
}

/** The public guest URL, built from a client's own origin (§32). The server
 * returns a RELATIVE url and never a token hash; a client must not mint one. */
export function partyGuestUrl(origin: string, partyUrl: string | null): string | null {
  if (partyUrl === null || partyUrl.length === 0) return null;
  return `${origin.replace(/\/+$/, '')}${partyUrl}`;
}

export function partyMessagesQueryToParams(input: { includeHidden?: boolean }): QueryParams {
  const b = new QueryBuilder();
  b.setBool('includeHidden', input.includeHidden);
  return b.build();
}
