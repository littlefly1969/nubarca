import type { FileMetadata } from '@nubarca/api-client';

// Resolution rules for the viewer's summary line (original size · Date Taken).
// Pure, so the "never show a fallback as Date Taken" rule is pinned by tests
// rather than by reading the JSX.

export interface ViewerSummaryInputs {
  // Size already carried by the loaded grid item — the FileItem's own
  // SizeBytes, i.e. the size of the immutable original blob, which is exactly
  // what GET /api/files/{id}/content streams. Preferred because it is available
  // with no request at all.
  itemSizeBytes?: number | null;
  // The metadata document for the CURRENTLY OPEN item, once it has loaded.
  metadata: FileMetadata | null;
}

export interface ViewerSummary {
  // Original/blob size in bytes, never a thumbnail or preview size.
  sizeBytes: number | null;
  // Effective Date Taken — present ONLY when it is a real capture date.
  dateTaken: string | null;
}

export function resolveViewerSummary({
  itemSizeBytes,
  metadata,
}: ViewerSummaryInputs): ViewerSummary {
  // FileMetadata.sizeBytes is the same FileItem.SizeBytes value, so it is a
  // consistent fallback for callers that do not carry the item's size (e.g. the
  // similar-photo explorer, whose result rows are leaner than MediaItem).
  const sizeBytes = itemSizeBytes ?? metadata?.sizeBytes ?? null;

  // `effective.dateTaken` ALWAYS has a value: the backend falls back to the
  // upload time when there is no user override and no embedded capture date,
  // and reports that as dateTakenSource === 'uploaded'. Presenting that as
  // "Date Taken" would be a lie, so the fallback is suppressed here and the
  // summary simply shows the size alone.
  const dateTaken =
    metadata !== null
    && (metadata.effective.dateTakenSource === 'user'
      || metadata.effective.dateTakenSource === 'embedded')
      ? metadata.effective.dateTaken
      : null;

  return { sizeBytes, dateTaken };
}
