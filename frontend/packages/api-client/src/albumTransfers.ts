import { api } from './client';

// SHARE-COPY-01: the one-time DETACHED album copy.
//
// Deliberately a separate module from albumSharing: a copy is not a share.
// A live share grants bounded, revocable access to media that stays the owner's;
// an accepted transfer produces an INDEPENDENT album owned by the recipient that
// the sender can never edit, revoke or recall.
//
// Nothing here carries a blob id, storage key, SHA, source file id, or any part
// of the sender's private semantic layer. A pending offer exposes no media at
// all — only a title, a count, a size and who sent it.

export type AlbumTransferState =
  | 'pending'
  | 'accepted'
  | 'declined'
  | 'cancelled'
  | 'expired'
  | 'failed';

// Why an album cannot be sent. Counts only — the API deliberately never names
// the blocking files, because for a collaborator's contribution that would leak
// across the very ownership boundary the refusal exists to protect.
// Mirrors AlbumTransferBlockReasons on the server, which are const STRINGS
// rather than a C# enum precisely so these values survive JSON serialisation.
// An enum there would arrive as a number and every switch here would fall
// through to the generic wording.
export type AlbumTransferBlockReason =
  | 'ContributedByAnotherUser'
  | 'InPrivateVault'
  | 'Trashed'
  | 'Unavailable';

export interface AlbumTransferBlocker {
  reason: AlbumTransferBlockReason;
  itemCount: number;
}

// What the OWNER sees before sending: exactly what would be copied and exactly
// what stops it. Computed with the same predicate the send uses, so a clean
// preview is never followed by a surprising rejection.
export interface AlbumTransferPreview {
  albumTitle: string;
  eligibleItemCount: number;
  eligibleSizeBytes: number;
  blockers: AlbumTransferBlocker[];
  canSend: boolean;
}

// An offer the caller SENT.
export interface SentAlbumTransfer {
  id: string;
  sourceAlbumId: string;
  title: string;
  recipientDisplayName: string;
  // Masked ("m•••i@nubarca.local"). Display names are not unique, so without it
  // a sender with two contacts of the same name cannot tell which offer to
  // cancel. Never the full address.
  recipientEmailMask: string | null;
  itemCount: number;
  totalSizeBytes: number;
  state: AlbumTransferState;
  createdAt: string;
  expiresAt: string;
  respondedAt: string | null;
  cancelledAt: string | null;
}

// An offer the caller RECEIVED. The minimum needed to decide: what it is called,
// how much of it there is, and who it is from. No media, no source album id.
//
// The masked address is appropriate here and is NOT a directory: the row is
// visible only to the one person the offer was addressed to.
export interface ReceivedAlbumTransfer {
  id: string;
  title: string;
  description: string | null;
  senderDisplayName: string;
  senderEmailMask: string | null;
  itemCount: number;
  totalSizeBytes: number;
  state: AlbumTransferState;
  createdAt: string;
  expiresAt: string;
  // Set once accepted, so the client can navigate straight to the new album.
  createdAlbumId: string | null;
}

export async function previewAlbumTransfer(
  albumId: string,
  signal?: AbortSignal,
): Promise<AlbumTransferPreview> {
  return api<AlbumTransferPreview>(`/api/albums/${albumId}/transfer-preview`, { signal });
}

// POST so the recipient's address never lands in a URL, a server log or a
// Referer header — the same reasoning as the invite flow.
export async function sendAlbumTransfer(
  albumId: string,
  email: string,
  signal?: AbortSignal,
): Promise<SentAlbumTransfer> {
  return api<SentAlbumTransfer>(`/api/albums/${albumId}/transfers`, {
    method: 'POST',
    json: { email },
    signal,
  });
}

export async function listSentAlbumTransfers(
  signal?: AbortSignal,
): Promise<SentAlbumTransfer[]> {
  return api<SentAlbumTransfer[]>('/api/album-transfers/sent', { signal });
}

export async function listReceivedAlbumTransfers(
  signal?: AbortSignal,
): Promise<ReceivedAlbumTransfer[]> {
  return api<ReceivedAlbumTransfer[]>('/api/album-transfers/received', { signal });
}

// Withdraw a PENDING offer. An accepted copy is the recipient's and is never
// recallable — that case answers 409.
export async function cancelAlbumTransfer(
  transferId: string,
  signal?: AbortSignal,
): Promise<void> {
  await api<void>(`/api/album-transfers/${transferId}/cancel`, {
    method: 'POST',
    signal,
  });
}

// Materialises the copy. IDEMPOTENT: a repeated call returns the SAME album id
// rather than creating a second one, so a double submit is harmless.
export async function acceptAlbumTransfer(
  transferId: string,
  signal?: AbortSignal,
): Promise<{ albumId: string }> {
  return api<{ albumId: string }>(`/api/album-transfers/${transferId}/accept`, {
    method: 'POST',
    signal,
  });
}

export async function declineAlbumTransfer(
  transferId: string,
  signal?: AbortSignal,
): Promise<void> {
  await api<void>(`/api/album-transfers/${transferId}/decline`, {
    method: 'POST',
    signal,
  });
}
