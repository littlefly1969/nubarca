import { api } from './client';

// SHARE BY LINK — one album, one address, whoever is holding it.
//
// Distinct from the shared-album MEMBER surface in `albums.ts`: that one is an
// account the owner invited, who signs in and holds a role. This is a token,
// and the two never meet.
//
// None of these routes mention a party, and none of them can reach one: the
// tokens are derived under different purpose strings on the server, so an album
// token and a party token cannot open each other's surfaces.

export interface AlbumShareGuest {
  id: string;
  email: string;
  displayName: string | null;
  createdAt: string;
}

/** The owner's view. `url` is the only place the raw token exists. */
export interface AlbumShareLink {
  id: string;
  url: string;
  enabled: boolean;
  uploadEnabled: boolean;
  /** Off means a download hands over a safe rendition, not the camera's file. */
  allowOriginalDownload: boolean;
  requireSecondFactor: boolean;
  label: string | null;
  /** 0 = no ceiling. */
  maxUploads: number;
  uploadCount: number;
  createdAt: string;
  expiresAt: string | null;
  guests: AlbumShareGuest[];
}

/** Every field optional: omitted means UNCHANGED, one switch at a time. */
export interface AlbumShareUpdate {
  uploadEnabled?: boolean;
  allowOriginalDownload?: boolean;
  requireSecondFactor?: boolean;
  label?: string | null;
  maxUploads?: number;
  expiresAt?: string | null;
  /**
   * Removes the expiry.
   *
   * A nullable date alone cannot ask for this: "no expiry" IS null, and null
   * already means unchanged everywhere else here — so without this an owner
   * who once set a date could never take it off through the typed client, even
   * though the server had gained the ability.
   */
  clearExpiry?: boolean;
}

/** The album as somebody holding the link sees it. Names nothing else. */
export interface AlbumSharePublic {
  albumName: string;
  coverUrl: string | null;
  itemCount: number;
  canUpload: boolean;
  canDownloadOriginal: boolean;
  /** Null when the link has no ceiling. */
  uploadsRemaining: number | null;
}

export interface AlbumShareItem {
  id: string;
  thumbnailUrl: string;
  previewUrl: string;
  /**
   * Null when there is nothing safe to hand over — today, a video on a link
   * whose owner has not allowed originals. The page offers no button rather
   * than one that answers 404.
   */
  downloadUrl: string | null;
  /**
   * The HLS ladder, for a video.
   *
   * Offered whatever the download switch says, because watching and taking a
   * copy are different powers: playback is a transcoded rendition and never
   * the camera's file. Null for an image, and null when the installation has
   * no HLS provider — in which case the poster is the honest fallback.
   */
  playbackUrl: string | null;
  isVideo: boolean;
}

export interface AlbumShareItems {
  items: AlbumShareItem[];
  nextCursor: string | null;
}

export interface AlbumShareUploadReport {
  accepted: number;
  rejected: number;
  /** A stable code when the run stopped early — today, the link's ceiling. */
  stopped: string | null;
}

// The whole public vocabulary. There is deliberately no code for "too many
// attempts" or "asked again too soon": the server answers the same way for a
// listed and an unlisted address in both cases, because any difference is a
// way to read the owner's guest list.
export const ALBUM_SHARE_ERRORS = {
  secondFactorRequired: 'second_factor_required',
  uploadsDisabled: 'uploads_disabled',
  uploadLimitReached: 'upload_limit_reached',
  invalidCode: 'invalid_code',
} as const;

// ── Owner ─────────────────────────────────────────────────────────────────

export function getAlbumShareLink(
  albumId: string, signal?: AbortSignal,
): Promise<AlbumShareLink> {
  return api<AlbumShareLink>(`/api/albums/${albumId}/share-link`, { signal });
}

/** Makes the link, or hands back the one that already exists. */
export function createAlbumShareLink(albumId: string): Promise<AlbumShareLink> {
  return api<AlbumShareLink>(`/api/albums/${albumId}/share-link`, { method: 'POST' });
}

/** Mints a NEW address and kills the old one. Deliberately its own verb. */
export function rotateAlbumShareLink(albumId: string): Promise<AlbumShareLink> {
  return api<AlbumShareLink>(`/api/albums/${albumId}/share-link/rotate`, { method: 'POST' });
}

export function updateAlbumShareLink(
  albumId: string, value: AlbumShareUpdate,
): Promise<AlbumShareLink> {
  return api<AlbumShareLink>(`/api/albums/${albumId}/share-link`, {
    method: 'PATCH', json: value,
  });
}

export function revokeAlbumShareLink(albumId: string): Promise<void> {
  return api<void>(`/api/albums/${albumId}/share-link`, { method: 'DELETE' });
}

export function addAlbumShareGuest(
  albumId: string, email: string, displayName?: string | null,
): Promise<AlbumShareGuest> {
  return api<AlbumShareGuest>(`/api/albums/${albumId}/share-link/guests`, {
    method: 'POST', json: { email, displayName: displayName ?? null },
  });
}

export function removeAlbumShareGuest(albumId: string, guestId: string): Promise<void> {
  return api<void>(`/api/albums/${albumId}/share-link/guests/${guestId}`, { method: 'DELETE' });
}

// ── Public ────────────────────────────────────────────────────────────────

export function getAlbumShare(
  token: string, signal?: AbortSignal,
): Promise<AlbumSharePublic> {
  return api<AlbumSharePublic>(`/api/album-share/${encodeURIComponent(token)}`, { signal });
}

export function getAlbumShareItems(
  token: string, signal?: AbortSignal,
): Promise<AlbumShareItems> {
  return api<AlbumShareItems>(`/api/album-share/${encodeURIComponent(token)}/items`, { signal });
}

/** Asks for a code. Always succeeds, whether or not the address is listed. */
export function challengeAlbumShare(token: string, email: string): Promise<void> {
  return api<void>(`/api/album-share/${encodeURIComponent(token)}/challenge`, {
    method: 'POST', json: { email },
  });
}

/** On success the server sets an HTTP-only cookie; nothing comes back to read. */
export function verifyAlbumShare(
  token: string, email: string, code: string,
): Promise<void> {
  return api<void>(`/api/album-share/${encodeURIComponent(token)}/verify`, {
    method: 'POST', json: { email, code },
  });
}

/**
 * ONE file per request, to the same endpoint, exactly as the party's queue
 * does. No chunking and no second upload protocol: each file settles on its
 * own, so a failure loses one file rather than a batch, and the ceiling the
 * response reports steers the rest of the run.
 */
export function uploadToAlbumShareWithProgress(
  token: string,
  file: File,
  onProgress?: (fraction: number) => void,
  signal?: AbortSignal,
): Promise<AlbumShareUploadReport> {
  const form = new FormData();
  form.append('file', file, file.name);
  const url = `/api/album-share/${encodeURIComponent(token)}/upload`;

  return new Promise<AlbumShareUploadReport>((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('POST', url);
    xhr.withCredentials = true;
    if (onProgress && xhr.upload) {
      xhr.upload.onprogress = (e) => {
        if (e.lengthComputable && e.total > 0) onProgress(e.loaded / e.total);
      };
    }
    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) {
        try {
          resolve(JSON.parse(xhr.responseText) as AlbumShareUploadReport);
        } catch {
          resolve({ accepted: 1, rejected: 0, stopped: null });
        }
        return;
      }
      // A refusal carries WHY, and the two reasons ask the owner for different
      // things — one is a switch, the other a number.
      let code: string | null = null;
      try {
        code = (JSON.parse(xhr.responseText) as { error?: string }).error ?? null;
      } catch { /* a body we cannot read is simply a failure */ }
      reject(Object.assign(new Error(`upload failed (${xhr.status})`), {
        status: xhr.status, code,
      }));
    };
    xhr.onerror = () => reject(new Error('network error'));
    xhr.onabort = () => reject(new DOMException('aborted', 'AbortError'));
    signal?.addEventListener('abort', () => xhr.abort(), { once: true });
    xhr.send(form);
  });
}
