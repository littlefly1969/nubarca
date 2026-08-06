import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { AdminStatsPage } from './AdminStatsPage';
import {
  AuthedWrapper,
  errorResponse,
  installFetchMock,
  jsonResponse,
} from '../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const sampleStats = {
  users: { total: 3, active: 3, disabled: 0 },
  folders: { total: 4, active: 4, softDeleted: 0 },
  files: {
    total: 10,
    active: 9,
    softDeleted: 1,
    logicalBytesTotal: 12345,
    logicalBytesIncludingTrash: 23456,
  },
  blobs: {
    total: 9,
    zeroReference: 1,
    zeroReferenceBeyondGrace: 0,
    physicalBytesTotal: 23456,
  },
  images: {
    imageFilesCount: 5,
    filesWithDimensionsCount: 5,
    thumbnailCount: 5,
    thumbnailBlobBytes: 4096,
  },
  shareLinks: { total: 2, active: 1, revoked: 1, expired: 0, exhausted: 0 },
  audit: { total: 42 },
  cleanup: {
    fileItemSweeper: { enabled: false, intervalMinutes: 5, graceMinutes: 1440 },
    blobJanitor: { enabled: false, intervalMinutes: 5, graceMinutes: 1440 },
  },
  media: { imagesCount: 5, videosCount: 2, audioCount: 0, documentsCount: 1, otherCount: 0 },
  extraction: {
    pending: 0, completed: 5, skipped: 2, failed: 1,
    currentVersion: 2, atCurrentVersion: 5, belowCurrentVersion: 0,
    unsupportedFormatErrors: 1, ioErrors: 0, unexpectedErrors: 0, rawTruncatedErrors: 0,
  },
  derivatives: {
    smallThumbnailCount: 5, mediumPreviewCount: 2, videoPosterCount: 1,
    imagesMissingSmall: 0, imagesMissingMedium: 3, videosMissingPoster: 1,
  },
  userMetadata: {
    totalRows: 3, withTitle: 2, withDescription: 1, withTags: 2,
    withRating: 1, favorites: 2, withDateTakenOverride: 0, withLocationOverride: 0,
  },
  sensitiveAggregates: {
    blobsWithGps: 1, blobsWithRawDocument: 4, blobsWithBodySerial: 0, blobsWithLensSerial: 0,
    metadataUpdates: 7, metadataStripEvents: 1,
  },
};

describe('AdminStatsPage', () => {
  it('shows the phase-timing diagnostics banner when present', async () => {
    installFetchMock({
      'GET /api/admin/storage-stats': () =>
        jsonResponse({
          ...sampleStats,
          diagnostics: {
            totalMillis: 1234,
            coreMillis: 200,
            physicalScanMillis: 900,
            derivativeScanMillis: 100,
            metadataAggregateMillis: 34,
            cached: false,
            computedAt: '2026-06-06T10:00:00Z',
            ageSeconds: 0,
          },
        }),
    });
    render(
      <AuthedWrapper isAdmin>
        <AdminStatsPage />
      </AuthedWrapper>,
    );
    expect(await screen.findByText(/Calcolate in 1234 ms/)).toBeInTheDocument();
    expect(screen.getByText(/scansione fisica 900 ms/)).toBeInTheDocument();
  });

  it('offers an on-demand integrity check that requests the physical scan', async () => {
    const { default: userEvent } = await import('@testing-library/user-event');
    const mock = installFetchMock({
      'GET /api/admin/storage-stats': () => jsonResponse(sampleStats), // no diagnostics → scan not run
    });
    render(
      <AuthedWrapper isAdmin>
        <AdminStatsPage />
      </AuthedWrapper>,
    );
    const user = userEvent.setup();
    const runBtn = await screen.findByRole('button', { name: 'Esegui controllo integrità' });
    await user.click(runBtn);

    await vi.waitFor(() => {
      expect(mock.calls.some((c) => c.url.includes('physical=true'))).toBe(true);
    });
    // The default first load explicitly skips the scan.
    expect(mock.calls.some((c) => c.url.includes('physical=false'))).toBe(true);
  });

  it('does not break when diagnostics are absent', async () => {
    installFetchMock({
      'GET /api/admin/storage-stats': () => jsonResponse(sampleStats),
    });
    render(
      <AuthedWrapper isAdmin>
        <AdminStatsPage />
      </AuthedWrapper>,
    );
    expect(await screen.findByRole('region', { name: 'Utenti' })).toBeInTheDocument();
    expect(screen.queryByText(/Calcolate in/)).toBeNull();
  });

  it('renders stat cards from a mocked storage-stats response', async () => {
    installFetchMock({
      'GET /api/admin/storage-stats': () => jsonResponse(sampleStats),
      'GET /api/admin/media/previews/medium/status': () =>
        jsonResponse({ mediumPreviewMaxEdge: 1920, job: null }),
    });

    render(
      <AuthedWrapper isAdmin>
        <AdminStatsPage />
      </AuthedWrapper>,
    );

    // Wait for at least one card to appear (the loading state goes first).
    expect(
      await screen.findByRole('region', { name: 'Utenti' }),
    ).toBeInTheDocument();

    // Spot-check a few values render. Numbers use locale formatting so we
    // match on the raw digits the helper produces.
    expect(screen.getByText('42')).toBeInTheDocument(); // audit total
    expect(screen.getByText('File immagine')).toBeInTheDocument();
    expect(screen.getByText('Link di condivisione')).toBeInTheDocument();

    // Bytes pass through formatSize → "12.06 KiB"-ish. We don't pin the
    // exact unit string here to stay robust against helper tweaks; we just
    // verify the row label rendered.
    expect(screen.getByText('Byte fisici')).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Anteprime media' })).toBeInTheDocument();
    expect(await screen.findByText('Dimensione massima anteprima media: 1920 px')).toBeInTheDocument();
  });

  it('queues medium preview regeneration after confirmation', async () => {
    const { default: userEvent } = await import('@testing-library/user-event');
    const mock = installFetchMock({
      'GET /api/admin/storage-stats': () => jsonResponse(sampleStats),
      'GET /api/admin/media/previews/medium/status': () =>
        jsonResponse({ mediumPreviewMaxEdge: 1920, job: null }),
      'POST /api/admin/media/previews/medium/rebuild': () =>
        jsonResponse({ jobId: 'job-1', status: 'queued', mediumPreviewMaxEdge: 1920 }),
    });

    render(
      <AuthedWrapper isAdmin>
        <AdminStatsPage />
      </AuthedWrapper>,
    );

    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Rigenera anteprime medie' }));
    expect(screen.getByRole('dialog', { name: 'Rigenera anteprime medie' })).toBeInTheDocument();
    expect(screen.getByText(
      'Questa operazione cancellerà le anteprime medie esistenti e le rigenererà in background. Gli originali non verranno modificati.',
    )).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Cancella e rigenera' }));

    await screen.findByText('Stato rigenerazione: queued');
    expect(mock.calls.some((c) =>
      c.method === 'POST' && c.url === '/api/admin/media/previews/medium/rebuild')).toBe(true);
  });

  it('renders the admin-access-required message on 403', async () => {
    installFetchMock({
      'GET /api/admin/storage-stats': () => errorResponse(403),
    });

    render(
      <AuthedWrapper isAdmin={false}>
        <AdminStatsPage />
      </AuthedWrapper>,
    );

    expect(
      await screen.findByText('Accesso amministratore richiesto.'),
    ).toBeInTheDocument();
  });

  it('renders slice-64 media / metadata / derivatives / privacy diagnostics', async () => {
    installFetchMock({
      'GET /api/admin/storage-stats': () => jsonResponse(sampleStats),
    });

    render(
      <AuthedWrapper isAdmin>
        <AdminStatsPage />
      </AuthedWrapper>,
    );

    expect(
      await screen.findByRole('region', { name: 'Media' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Estrazione metadati' })).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Artefatti derivati' })).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Metadati utente' })).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Aggregati privacy' })).toBeInTheDocument();

    // Spot-check counts that come from the new blocks.
    expect(screen.getByText('Video senza poster')).toBeInTheDocument();
    expect(screen.getByText('Blob con dati GPS')).toBeInTheDocument();
    expect(screen.getByText('Rimozioni metadati (audit)')).toBeInTheDocument();

    // No raw / coord / serial / path strings ever render — the DTO carries
    // counts only, and the UI renders only those.
    const body = document.body.textContent ?? '';
    for (const needle of [
      'Latitude', 'Longitude', 'GpsLatitude', 'GpsLongitude',
      'BodySerialNumber', 'LensSerialNumber',
      'rawMetadataJson', 'StorageKey', 'storageKey',
      'BlobObjectId', 'objects/',
    ]) {
      expect(body).not.toContain(needle);
    }
  });

  it('warns when sweeper grace is greater than janitor grace', async () => {
    const stats = {
      ...sampleStats,
      cleanup: {
        fileItemSweeper: { enabled: true, intervalMinutes: 5, graceMinutes: 2880 },
        blobJanitor: { enabled: true, intervalMinutes: 5, graceMinutes: 1440 },
      },
    };
    installFetchMock({
      'GET /api/admin/storage-stats': () => jsonResponse(stats),
    });

    render(
      <AuthedWrapper isAdmin>
        <AdminStatsPage />
      </AuthedWrapper>,
    );

    const warning = await screen.findByRole('alert');
    expect(warning).toHaveTextContent(/grazia del FileItem sweeper/);
    expect(warning).toHaveTextContent(/grazia del Blob janitor/);
  });
});
