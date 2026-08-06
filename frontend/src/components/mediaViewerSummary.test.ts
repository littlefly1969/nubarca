import { describe, expect, it } from 'vitest';
import type { FileMetadata } from '@nubarca/api-client';
import { resolveViewerSummary } from './mediaViewerSummary';

function meta(overrides: Partial<FileMetadata['effective']> = {}, sizeBytes = 4_194_304): FileMetadata {
  return {
    id: 'f1',
    name: 'IMG_1248.JPG',
    mimeType: 'image/jpeg',
    sizeBytes,
    createdAt: '2026-02-02T09:00:00Z',
    updatedAt: null,
    blob: {
      mediaCategory: 'image',
      detectedContentType: 'image/jpeg',
      detectedFormat: 'JPEG',
      width: 4000,
      height: 3000,
      pixelCount: 12_000_000,
      thumbnailStatus: 'ready',
      extractionStatus: 'ready',
      embedded: null,
      video: null,
    },
    user: {
      title: null, description: null, tags: [], rating: null, favorite: false,
      dateTakenOverride: null, locationOverride: null,
    },
    effective: {
      displayName: 'IMG_1248.JPG',
      dateTaken: '2025-07-14T18:42:00Z',
      dateTakenSource: 'embedded',
      location: null,
      ...overrides,
    },
  };
}

describe('viewer summary resolution', () => {
  it('prefers the size already on the loaded item (no request needed)', () => {
    const summary = resolveViewerSummary({ itemSizeBytes: 5_033_164, metadata: meta() });
    expect(summary.sizeBytes).toBe(5_033_164);
  });

  it('falls back to the metadata size when the item does not carry one', () => {
    expect(resolveViewerSummary({ itemSizeBytes: null, metadata: meta() }).sizeBytes).toBe(4_194_304);
    expect(resolveViewerSummary({ metadata: meta() }).sizeBytes).toBe(4_194_304);
  });

  it('shows the size before metadata has loaded', () => {
    const summary = resolveViewerSummary({ itemSizeBytes: 5_033_164, metadata: null });
    expect(summary.sizeBytes).toBe(5_033_164);
    expect(summary.dateTaken).toBeNull();
  });

  it('shows an embedded capture date as Date Taken', () => {
    const summary = resolveViewerSummary({ metadata: meta({ dateTakenSource: 'embedded' }) });
    expect(summary.dateTaken).toBe('2025-07-14T18:42:00Z');
  });

  it('shows a user override as Date Taken', () => {
    const summary = resolveViewerSummary({
      metadata: meta({ dateTakenSource: 'user', dateTaken: '2019-05-01T08:00:00Z' }),
    });
    expect(summary.dateTaken).toBe('2019-05-01T08:00:00Z');
  });

  it('never presents the upload-time fallback as Date Taken', () => {
    // The backend always fills effective.dateTaken — with the UPLOAD time when
    // there is no override and no embedded date. Showing that as "Date Taken"
    // would be wrong, so the field is omitted entirely.
    const summary = resolveViewerSummary({
      itemSizeBytes: 1000,
      metadata: meta({ dateTakenSource: 'uploaded', dateTaken: '2026-02-02T09:00:00Z' }),
    });
    expect(summary.dateTaken).toBeNull();
    // The available field is still rendered.
    expect(summary.sizeBytes).toBe(1000);
  });

  it('renders nothing when neither field is available', () => {
    const summary = resolveViewerSummary({ itemSizeBytes: null, metadata: null });
    expect(summary.sizeBytes).toBeNull();
    expect(summary.dateTaken).toBeNull();
  });

  it('reflects an edited Date Taken as soon as a fresh document is adopted', () => {
    const before = resolveViewerSummary({ metadata: meta({ dateTakenSource: 'uploaded' }) });
    expect(before.dateTaken).toBeNull();

    const after = resolveViewerSummary({
      metadata: meta({ dateTakenSource: 'user', dateTaken: '2001-09-11T12:00:00Z' }),
    });
    expect(after.dateTaken).toBe('2001-09-11T12:00:00Z');
  });
});
