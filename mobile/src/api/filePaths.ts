// Centralized owner-scoped derivative endpoint builders.
//
// These are SERVER CONTRACTS (cookie-authenticated, owner-scoped, derivative-
// only — never originals). Every screen builds media paths through here so a
// contract change is a one-file edit and no screen ever hand-assembles a URL.
//
//   thumbnail → GET /api/files/{id}/thumbnail?size=small  (grid tiles)
//   preview   → GET /api/files/{id}/preview               (viewer, medium)
//   poster    → GET /api/files/{id}/poster                (video tile/player)
//   video     → GET /api/files/{id}/video                 (Range-enabled)

export function fileThumbnailPath(fileId: string): string {
  return `/api/files/${fileId}/thumbnail?size=small`;
}

export function filePreviewPath(fileId: string): string {
  return `/api/files/${fileId}/preview`;
}

export function filePosterPath(fileId: string): string {
  return `/api/files/${fileId}/poster`;
}

export function fileVideoPath(fileId: string): string {
  return `/api/files/${fileId}/video`;
}