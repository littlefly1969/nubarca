import { api } from './client';

// Mirrors NubArca.Api.ShareLinks.ShareLinkCreatedResponse on the backend.
// Critically, the backend never returns `TokenHash` (only the raw `token`
// + a relative URL) and we never model it here — the raw token is the only
// shareable artifact and exists ONLY in transient component state. We do
// NOT persist anything to localStorage / sessionStorage.
export interface CreatedShareLink {
  id: string;
  token: string;
  url: string; // backend returns same-origin relative path: "/s/{token}"
  expiresAt: string | null;
  maxDownloads: number | null;
}

export interface CreateShareLinkOptions {
  expiresAt?: string;
  maxDownloads?: number;
}

export function createShareLink(
  fileId: string,
  options: CreateShareLinkOptions = {},
  signal?: AbortSignal,
): Promise<CreatedShareLink> {
  const body: Record<string, unknown> = {};
  if (options.expiresAt !== undefined) body.expiresAt = options.expiresAt;
  if (options.maxDownloads !== undefined) body.maxDownloads = options.maxDownloads;
  return api<CreatedShareLink>(`/api/files/${fileId}/share-links`, {
    method: 'POST',
    json: body,
    signal,
  });
}

export function revokeShareLink(shareLinkId: string, signal?: AbortSignal): Promise<void> {
  return api<void>(`/api/share-links/${shareLinkId}/revoke`, {
    method: 'POST',
    signal,
  });
}

// Mirrors NubArca.Api.ShareLinks.ShareLinkSummary on the backend.
// Critically, the backend does NOT return the raw token or `TokenHash` for
// existing links — the raw token is recoverable ONLY at creation time. We do
// not model either field here, matching the no-leak contract.
export interface ShareLinkSummary {
  id: string;
  createdAt: string;
  expiresAt: string | null;
  revokedAt: string | null;
  maxDownloads: number | null;
  downloadCount: number;
  lastAccessedAt: string | null;
  isRevoked: boolean;
  isExpired: boolean;
  isExhausted: boolean;
}

// Lists every share link an owner has ever created for a given file
// (active, revoked, expired, exhausted), ordered by createdAt desc. The
// backend returns 404 for missing / soft-deleted files; the caller
// is expected to surface that as "file no longer exists".
export function listShareLinksForFile(
  fileId: string,
  signal?: AbortSignal,
): Promise<ShareLinkSummary[]> {
  return api<ShareLinkSummary[]>(`/api/files/${fileId}/share-links`, { signal });
}

export type ShareLinkStatusFilter = 'all' | 'active' | 'expired' | 'revoked';

// Mirrors NubArca.Api.ShareLinks.ShareLinkListItem. Like ShareLinkSummary it
// never carries the raw token or TokenHash; it additionally carries the file
// name + logical folder path so the global page can show which file each link
// points at. `folderPath` is "/" for a root file, "/A/B" for a nested one.
export interface ShareLinkListItem {
  id: string;
  fileName: string;
  folderPath: string | null;
  createdAt: string;
  expiresAt: string | null;
  revokedAt: string | null;
  maxDownloads: number | null;
  downloadCount: number;
  lastAccessedAt: string | null;
  isRevoked: boolean;
  isExpired: boolean;
  isExhausted: boolean;
}

// Mirrors NubArca.Api.ShareLinks.ShareLinkListResponse. `total` is the full
// owner-scoped count for the active filter (not just the current page) so the
// page can render "N of total" and stop paging at the end.
export interface ShareLinkListResponse {
  items: ShareLinkListItem[];
  limit: number;
  offset: number;
  total: number;
}

export interface ListShareLinksOptions {
  status?: ShareLinkStatusFilter;
  limit?: number;
  offset?: number;
}

// Owner-scoped global listing across every file the caller owns. Paginated +
// status-filterable. 401 when unauthenticated; 400 on an unknown status.
export function listShareLinks(
  options: ListShareLinksOptions = {},
  signal?: AbortSignal,
): Promise<ShareLinkListResponse> {
  const params = new URLSearchParams();
  if (options.status !== undefined) params.set('status', options.status);
  if (options.limit !== undefined) params.set('limit', String(options.limit));
  if (options.offset !== undefined) params.set('offset', String(options.offset));

  const qs = params.toString();
  const path = qs.length === 0 ? '/api/share-links' : `/api/share-links?${qs}`;
  return api<ShareLinkListResponse>(path, { signal });
}
