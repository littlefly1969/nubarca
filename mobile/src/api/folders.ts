// Folders: read-only directory browsing for the secondary Files tab. Mirrors
// frontend/packages/api-client/src/folders.ts (NubArca.Api.Folders contracts).
// Mobile uses ONLY the listing surface — no move/rename/delete in this slice.

import { apiGet } from './client.ts';

export interface FolderSummary {
  id: string;
  name: string;
  createdAt: string;
}

export interface FileSummary {
  id: string;
  name: string;
  mimeType: string;
  sizeBytes: number;
  createdAt: string;
  width?: number | null;
  height?: number | null;
}

// `folderId === null` lists the owner's root. `folders` is the full ordered
// set on the first (cursorless) page; `files` is one seek-paginated page.
export interface FolderChildrenResponse {
  folderId: string | null;
  folders: FolderSummary[];
  files: FileSummary[];
  nextCursor?: string | null;
  hasMore?: boolean;
}

export interface DirectoryListingParams {
  limit?: number;
  cursor?: string | null;
}

export function getDirectoryChildren(
  folderId: string | null,
  params: DirectoryListingParams = {},
  signal?: AbortSignal,
): Promise<FolderChildrenResponse> {
  const base =
    folderId === null
      ? '/api/folders/children'
      : `/api/folders/${folderId}/children`;
  const search = new URLSearchParams();
  if (params.limit !== undefined) search.set('limit', String(params.limit));
  if (params.cursor) search.set('cursor', params.cursor);
  const qs = search.toString();
  return apiGet<FolderChildrenResponse>(`${base}${qs ? `?${qs}` : ''}`, signal);
}
