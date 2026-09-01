// Mobile TRANSPORT for Party (§30-§37).
//
// The DTOs, the validation RANGES and the message transition matrix come from
// @nubarca/contracts. §33 and §34 are explicit that the ranges must not be
// duplicated in a UI file, and §36 that the transition rules must not be
// recreated here — they were previously inlined in the web's JSX, which is not
// somewhere a second client can read them.
//
// Guest-MEDIA moderation and MESSAGE moderation stay separate (§35). Same
// album, two different state machines; merging them would make every future
// change to one a change to both.

import { apiGet, apiPatch, apiPost } from './client.ts';
import type {
  AlbumPartyStatus,
  PartyGameSettings,
  PartyMessageAction,
  PartyMessageList,
  PartySlideshowSettings,
  PartyUploadList,
} from '@nubarca/contracts';
import {
  albumPartyGamePath,
  albumPartyMessageActionPath,
  albumPartyMessagesPath,
  albumPartySlideshowPath,
  albumPartyStatusPath,
  albumPartyUploadActionPath,
  albumPartyUploadsPath,
} from '@nubarca/contracts';

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

// ── Core settings (§31) ────────────────────────────────────────────────────

export function getPartyStatus(
  albumId: string,
  signal?: AbortSignal,
): Promise<AlbumPartyStatus> {
  return apiGet<AlbumPartyStatus>(albumPartyStatusPath(albumId), signal);
}

export function setPartyMode(
  albumId: string,
  partyMode: boolean,
  signal?: AbortSignal,
): Promise<AlbumPartyStatus> {
  return apiPatch<AlbumPartyStatus>(albumPartyStatusPath(albumId), { partyMode }, { signal });
}

export function setPartyUploads(
  albumId: string,
  uploadEnabled: boolean,
  signal?: AbortSignal,
): Promise<AlbumPartyStatus> {
  return apiPatch<AlbumPartyStatus>(albumPartyStatusPath(albumId), { uploadEnabled }, { signal });
}

export function setRequireUploadApproval(
  albumId: string,
  requireUploadApproval: boolean,
  signal?: AbortSignal,
): Promise<AlbumPartyStatus> {
  return apiPatch<AlbumPartyStatus>(
    albumPartyStatusPath(albumId), { requireUploadApproval }, { signal },
  );
}

export function setRequireMessageApproval(
  albumId: string,
  requireMessageApproval: boolean,
  signal?: AbortSignal,
): Promise<AlbumPartyStatus> {
  return apiPatch<AlbumPartyStatus>(
    albumPartyStatusPath(albumId), { requireMessageApproval }, { signal },
  );
}

// ── Slideshow and game settings (§33, §34) ─────────────────────────────────
// The caller validates with invalidSlideshowFields / invalidGameFields before
// sending. That is a courtesy to the user, not the check: the SERVER validates.

export function setPartySlideshowSettings(
  albumId: string,
  settings: PartySlideshowSettings,
  signal?: AbortSignal,
): Promise<AlbumPartyStatus> {
  return apiPatch<AlbumPartyStatus>(albumPartySlideshowPath(albumId), settings, { signal });
}

export function setPartyGameSettings(
  albumId: string,
  settings: PartyGameSettings,
  signal?: AbortSignal,
): Promise<AlbumPartyStatus> {
  return apiPatch<AlbumPartyStatus>(albumPartyGamePath(albumId), settings, { signal });
}

// ── Guest media moderation (§35) ───────────────────────────────────────────

export function listPartyUploads(
  albumId: string,
  signal?: AbortSignal,
): Promise<PartyUploadList> {
  return apiGet<PartyUploadList>(albumPartyUploadsPath(albumId), signal);
}

export function moderatePartyUpload(
  albumId: string,
  fileItemId: string,
  action: 'approve' | 'hide' | 'reject' | 'restore',
  signal?: AbortSignal,
): Promise<void> {
  return apiPost<void>(
    albumPartyUploadActionPath(albumId, fileItemId, action), undefined, { signal },
  );
}

// ── Message moderation (§36, §37) ──────────────────────────────────────────

export function listPartyMessages(
  albumId: string,
  signal?: AbortSignal,
): Promise<PartyMessageList> {
  return apiGet<PartyMessageList>(albumPartyMessagesPath(albumId), signal);
}

/**
 * Owner or delegate. Which actions to OFFER comes from partyMessageActions;
 * what is PERMITTED is decided by the server, which rejects an invalid
 * transition regardless of what a client believed.
 */
export function moderatePartyMessage(
  albumId: string,
  messageId: string,
  action: PartyMessageAction,
  signal?: AbortSignal,
): Promise<void> {
  return apiPost<void>(
    albumPartyMessageActionPath(albumId, messageId, action), undefined, { signal },
  );
}
