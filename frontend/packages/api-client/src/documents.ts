import { api } from './client';

/// Where a passage sits in its own document, in that document's own units.
/// `page` is a real PDF page; a slide, a sheet or a Word section is its own
/// kind and never pretends to be one.
export interface DocumentLocator {
  kind: 'page' | 'section' | 'sheet' | 'slide' | string;
  index: number;
  label: string | null;
}

export interface DocumentSearchPassage {
  rank: number;
  snippet: string;
  section: string | null;
  page: number | null;
  ordinal: number | null;
  locator: DocumentLocator | null;
}

/// One document the search found. `sourceId` is the caller's OWN id for it —
/// for `sourceKind: 'file'`, their FileItem id, the same id the file browser
/// already uses to open it. Nothing else here is an identifier.
export interface DocumentSearchHit {
  sourceKind: 'file' | string;
  sourceId: string;
  name: string;
  rank: number;
  passages: DocumentSearchPassage[];
}

export type DocumentSearchStatus = 'ok' | 'no_evidence' | 'not_indexed' | 'unavailable';

export interface DocumentSearchResult {
  status: DocumentSearchStatus;
  reason: string | null;
  /// `lexical`, `hybrid` or `lexical-fallback-<reason>`.
  mode: string;
  semantic: boolean;
  fallbackReason: string | null;
  visualMode: string;
  visualUsed: boolean;
  documents: DocumentSearchHit[];
}

export interface DocumentSignal {
  available: boolean;
  reason: string | null;
}

export interface DocumentsStatus {
  documents: number;
  chunks: number;
  semanticEnabled: boolean;
  visualEnabled: boolean;
  visualReason: string | null;
  indexing: {
    candidateFiles: number;
    indexedFiles: number;
    notIndexedFiles: number;
    chunks: number;
    embeddedChunks: number | null;
  } | null;
  availability?: {
    domain: string;
    kinds: { kind: string; lexical: DocumentSignal; semantic: DocumentSignal; visual: DocumentSignal }[];
  };
}

export function getDocumentsStatus(signal?: AbortSignal): Promise<DocumentsStatus> {
  return api<DocumentsStatus>('/api/assistant/documents/status', { signal });
}

/**
 * Search the caller's own documents.
 *
 * The request carries a QUERY and a result count and nothing else: no owner,
 * no folder, no file, no source and no mode. Which library is searched is the
 * signed-in account's, decided server-side.
 */
export function searchDocuments(
  query: string,
  limit?: number,
  signal?: AbortSignal,
): Promise<DocumentSearchResult> {
  return api<DocumentSearchResult>('/api/documents/search', {
    method: 'POST',
    json: limit === undefined ? { query } : { query, limit },
    signal,
  });
}
