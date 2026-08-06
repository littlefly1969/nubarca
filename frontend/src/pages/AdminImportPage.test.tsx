import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { AdminImportPage } from './AdminImportPage';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../test-utils';
import type { AdminImportRunStatus } from '@nubarca/api-client';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function renderPage() {
  return render(
    <AuthedWrapper isAdmin>
      <MemoryRouter>
        <AdminImportPage />
      </MemoryRouter>
    </AuthedWrapper>,
  );
}

describe('AdminImportPage', () => {
  it('shows the disabled message when the feature is off', async () => {
    installFetchMock({
      'GET /api/admin/import/runs': () => jsonResponse({ runs: [], total: 0, limit: 25, offset: 0 }),
      'GET /api/admin/import/roots': () =>
        jsonResponse({ enabled: false, configured: false, roots: [] }),
    });
    renderPage();
    expect(await screen.findByText(/Server-side import is disabled/i)).toBeInTheDocument();
    expect(screen.getByText(/AdminImport__Enabled=true/)).toBeInTheDocument();
  });

  it('shows the no-roots message when enabled but unconfigured', async () => {
    installFetchMock({
      'GET /api/admin/import/runs': () => jsonResponse({ runs: [], total: 0, limit: 25, offset: 0 }),
      'GET /api/admin/import/roots': () =>
        jsonResponse({ enabled: true, configured: false, roots: [] }),
    });
    renderPage();
    expect(
      await screen.findByText(/enabled but no import roots are configured/i),
    ).toBeInTheDocument();
  });

  it('lists configured roots and browses directories after selecting one', async () => {
    installFetchMock({
      'GET /api/admin/import/runs': () => jsonResponse({ runs: [], total: 0, limit: 25, offset: 0 }),
      'GET /api/admin/import/roots': () =>
        jsonResponse({
          enabled: true,
          configured: true,
          roots: [{ rootId: 'abc123', label: 'incoming' }],
        }),
      'GET /api/admin/import/browse?rootId=abc123': () =>
        jsonResponse({
          rootId: 'abc123',
          relativePath: '',
          parentRelativePath: null,
          directories: [
            { name: 'photos', relativePath: 'photos', childDirectoryCount: 1, fileCount: 3 },
          ],
        }),
    });
    renderPage();

    const rootCard = await screen.findByRole('button', { name: 'incoming' });
    const user = userEvent.setup();
    await user.click(rootCard);

    // The directory browser renders the subdirectory and a select action.
    expect(await screen.findByText('📁 photos')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Select this directory' })).toBeInTheDocument();
  });

  it('walks the full wizard to a queued import run', async () => {
    installFetchMock({
      'GET /api/admin/import/runs': () => jsonResponse({ runs: [], total: 0, limit: 25, offset: 0 }),
      'GET /api/admin/import/roots': () =>
        jsonResponse({
          enabled: true,
          configured: true,
          roots: [{ rootId: 'abc123', label: 'incoming' }],
        }),
      'GET /api/admin/import/browse?rootId=abc123': () =>
        jsonResponse({
          rootId: 'abc123',
          relativePath: '',
          parentRelativePath: null,
          directories: [],
        }),
      'GET /api/admin/import/users': () =>
        jsonResponse([
          { id: 'user-2', email: 'target@example.com', displayName: 'Target', isAdmin: false, isActive: true },
        ]),
      'GET /api/admin/import/destination-folders?userId=user-2': () =>
        jsonResponse({ targetUserId: 'user-2', parentFolderId: null, folders: [] }),
      'POST /api/admin/import/preview': () =>
        jsonResponse({
          totalFiles: 5,
          totalDirectories: 2,
          totalBytes: 1024,
          skippedSymlinks: 0,
          skippedUnsupported: 0,
          unreadableCount: 0,
          truncated: false,
          warnings: [],
        }),
      'POST /api/admin/import/run': () =>
        jsonResponse({ importRunId: 'run-1', jobId: 'job-1', status: 'queued' }),
    });
    renderPage();

    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'incoming' }));
    await user.click(await screen.findByRole('button', { name: 'Select this directory' }));

    // Step 2: pick the target user.
    await user.click(await screen.findByRole('button', { name: /target@example.com/ }));

    // Step 3: use the library root as destination.
    await user.click(await screen.findByRole('button', { name: 'Use this folder' }));

    // Step 4: preview shows counts + confirmation copy.
    expect(await screen.findByText('5 files')).toBeInTheDocument();
    expect(screen.getByText(/Import 5 files/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Confirm & import' }));

    // Step 5: queued status + the worker note.
    expect(await screen.findByText(/Status: queued/)).toBeInTheDocument();
    expect(screen.getByText(/jobs run-once/)).toBeInTheDocument();
  });

  // ---- slice 82: runs history / detail / cancel / performance ----
  type RunOverrides = Partial<AdminImportRunStatus>;
  function baseRun(): AdminImportRunStatus {
    return {
      importRunId: 'run-1',
      jobId: 'job-1',
      status: 'running',
      cancelRequested: false,
      phase: null,
      rootId: 'abc123',
      sourceRelativePath: 'photos',
      targetUserId: 'user-2',
      targetUserEmail: 'target@example.com',
      destinationFolderId: null,
      scannedFiles: 3,
      pendingFiles: 0,
      importedFiles: 2,
      skippedFiles: 0,
      skippedPreviouslyDeletedFiles: 0,
      skippedAlreadyPresentFiles: 0,
      failedFiles: 0,
      conflictFiles: 1,
      alreadyImportedFiles: 0,
      cancelledFiles: 0,
      importedBytes: 2048,
      totalBytes: 4096,
      totalDirectories: 1,
      currentRelativePath: 'photos/a.jpg',
      error: null,
      createdAt: '2026-06-05T10:00:00Z',
      startedAt: '2026-06-05T10:00:01Z',
      completedAt: null,
      scanCompletedAt: null,
      metrics: {
        durationMillis: null, filesPerSecond: null, bytesPerSecond: null,
        conflictPercent: null, skippedPercent: null, failedPercent: null,
        averageImportedFileBytes: null,
      },
      timings: {
        readMillis: null, hashMillis: null, writeMillis: null, blobDbMillis: null,
        detectMillis: null, metadataMillis: null, fileItemMillis: null,
        thumbnailMillis: null, folderMillis: null, itemDbMillis: null,
      },
      conflictSamples: [],
    };
  }
  function makeRun(overrides: RunOverrides = {}) {
    return { ...baseRun(), ...overrides };
  }

  const disabledRoots = () =>
    jsonResponse({ enabled: false, configured: false, roots: [] });

  const emptyItems = () =>
    jsonResponse({ importRunId: 'run-1', items: [], total: 0, page: 1, pageSize: 25 });

  it('renders the import runs table', async () => {
    installFetchMock({
      'GET /api/admin/import/roots': disabledRoots,
      'GET /api/admin/import/runs': () =>
        jsonResponse({ runs: [makeRun()], total: 1, limit: 25, offset: 0 }),
    });
    renderPage();
    expect(await screen.findByText('photos')).toBeInTheDocument();
    expect(screen.getByText('target@example.com')).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Import runs' })).toBeInTheDocument();
  });

  it('opens a run detail with performance breakdown when a row is clicked', async () => {
    installFetchMock({
      'GET /api/admin/import/roots': disabledRoots,
      'GET /api/admin/import/runs': () =>
        jsonResponse({ runs: [makeRun()], total: 1, limit: 25, offset: 0 }),
      'GET /api/admin/import/runs/run-1/items': emptyItems,
      'GET /api/admin/import/runs/run-1': () =>
        jsonResponse(makeRun({
          status: 'succeeded',
          completedAt: '2026-06-05T10:00:05Z',
          metrics: {
            durationMillis: 4000, filesPerSecond: 0.5, bytesPerSecond: 512,
            conflictPercent: 25, skippedPercent: 0, failedPercent: 0,
            averageImportedFileBytes: 1024,
          },
          timings: {
            readMillis: 100, hashMillis: 400, writeMillis: 50, blobDbMillis: 20,
            detectMillis: 8, metadataMillis: 30, fileItemMillis: 10,
            thumbnailMillis: 5, folderMillis: 2, itemDbMillis: 4,
          },
        })),
    });
    renderPage();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: /Open run for target@example.com/ }));

    expect(await screen.findByRole('region', { name: 'Run detail' })).toBeInTheDocument();
    expect(await screen.findByText('Performance')).toBeInTheDocument();
    expect(screen.getByText('SHA-256')).toBeInTheDocument();
    expect(screen.getByText(/Duplicate files still spend time/)).toBeInTheDocument();
  });

  it('cancels a running run after confirmation', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);
    const mock = installFetchMock({
      'GET /api/admin/import/roots': disabledRoots,
      'GET /api/admin/import/runs': () =>
        jsonResponse({ runs: [makeRun()], total: 1, limit: 25, offset: 0 }),
      'GET /api/admin/import/runs/run-1/items': emptyItems,
      'GET /api/admin/import/runs/run-1': () => jsonResponse(makeRun()),
      'POST /api/admin/import/runs/run-1/cancel': () =>
        jsonResponse({ cancellationRequested: true, status: 'running' }),
    });
    renderPage();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: /Open run for target@example.com/ }));

    const cancelBtn = await screen.findByRole('button', { name: 'Cancel import' });
    await user.click(cancelBtn);

    expect(confirmSpy).toHaveBeenCalled();
    await vi.waitFor(() => {
      expect(mock.calls.some((c) => c.url.includes('/cancel') && c.method === 'POST')).toBe(true);
    });
    confirmSpy.mockRestore();
  });

  it('shows the throttle copy and configured values', async () => {
    installFetchMock({
      'GET /api/admin/import/roots': () =>
        jsonResponse({
          enabled: true,
          configured: true,
          roots: [{ rootId: 'abc123', label: 'incoming' }],
          throttle: {
            delayBetweenFilesMs: 25,
            maxBytesPerSecond: 0,
            maxRunMinutes: 30,
            yieldEveryFiles: 64,
          },
        }),
      'GET /api/admin/import/browse?rootId=abc123': () =>
        jsonResponse({ rootId: 'abc123', relativePath: '', parentRelativePath: null, directories: [] }),
      'GET /api/admin/import/runs': () =>
        jsonResponse({ runs: [], total: 0, limit: 25, offset: 0 }),
    });
    renderPage();
    expect(
      await screen.findByText(/Imports run in the background with low-priority throttling/),
    ).toBeInTheDocument();
    expect(screen.getByText(/25 ms\/file/)).toBeInTheDocument();
    expect(screen.getByText(/30 min\/slice/)).toBeInTheDocument();
  });

  it('shows the paused state with a resume-from-manifest note in the run detail', async () => {
    installFetchMock({
      'GET /api/admin/import/roots': disabledRoots,
      'GET /api/admin/import/runs': () =>
        jsonResponse({ runs: [makeRun({ status: 'paused' })], total: 1, limit: 25, offset: 0 }),
      'GET /api/admin/import/runs/run-1/items': emptyItems,
      'GET /api/admin/import/runs/run-1': () => jsonResponse(makeRun({ status: 'paused' })),
    });
    renderPage();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: /Open run for target@example.com/ }));
    expect(await screen.findByText(/will resume from\s+the saved manifest/)).toBeInTheDocument();
  });

  it('distinguishes resumed (already imported) from conflicts and shows safe samples', async () => {
    installFetchMock({
      'GET /api/admin/import/roots': disabledRoots,
      'GET /api/admin/import/runs': () =>
        jsonResponse({ runs: [makeRun({ status: 'running' })], total: 1, limit: 25, offset: 0 }),
      'GET /api/admin/import/runs/run-1/items': emptyItems,
      'GET /api/admin/import/runs/run-1': () =>
        jsonResponse(makeRun({
          status: 'paused',
          conflictFiles: 0,
          alreadyImportedFiles: 9132,
          conflictSamples: [
            { relativePath: '2009/Gita roma/P1000741.JPG', reason: 'already-imported-this-run' },
          ],
        })),
    });
    renderPage();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: /Open run for target@example.com/ }));

    expect(await screen.findByText(/Resumed \(already imported\): 9132/)).toBeInTheDocument();
    // The explanatory note clarifies these are not conflicts.
    expect(screen.getByText(/“Resumed” = files this run had already imported/)).toBeInTheDocument();
    // Samples render the safe relative path.
    await user.click(screen.getByText(/Conflict samples \(1\)/));
    expect(screen.getByText('2009/Gita roma/P1000741.JPG')).toBeInTheDocument();
  });

  it('renders "not available" for null metrics without breaking', async () => {
    installFetchMock({
      'GET /api/admin/import/roots': disabledRoots,
      'GET /api/admin/import/runs': () =>
        jsonResponse({ runs: [makeRun({ status: 'failed' })], total: 1, limit: 25, offset: 0 }),
      'GET /api/admin/import/runs/run-1/items': emptyItems,
      'GET /api/admin/import/runs/run-1': () => jsonResponse(makeRun({ status: 'failed' })),
    });
    renderPage();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: /Open run for target@example.com/ }));
    expect(await screen.findByRole('region', { name: 'Run detail' })).toBeInTheDocument();
    expect(screen.getByText(/Phase timings not available/)).toBeInTheDocument();
  });

  // ---- slice 92: manifest items, progress bar, derivatives ----

  it('shows the scanning phase and a manifest-derived progress bar', async () => {
    installFetchMock({
      'GET /api/admin/import/roots': disabledRoots,
      'GET /api/admin/import/runs': () =>
        jsonResponse({ runs: [makeRun()], total: 1, limit: 25, offset: 0 }),
      'GET /api/admin/import/runs/run-1/items': emptyItems,
      'GET /api/admin/import/runs/run-1': () =>
        jsonResponse(makeRun({
          status: 'running',
          phase: 'importing',
          scanCompletedAt: '2026-06-05T10:00:02Z',
          scannedFiles: 100,
          importedFiles: 40,
          conflictFiles: 10,
          pendingFiles: 50,
        })),
    });
    renderPage();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: /Open run for target@example.com/ }));

    // Phase + linked progress derived from the persisted manifest.
    expect(await screen.findByText(/Status: running — importing/)).toBeInTheDocument();
    const bar = await screen.findByRole('progressbar', { name: 'Import progress' });
    expect(bar).toHaveAttribute('aria-valuenow', '50'); // (40+10)/100
    expect(screen.getByText(/50\/100 files \(50%\)/)).toBeInTheDocument();
    expect(screen.getByText(/Pending: 50/)).toBeInTheDocument();
  });

  it('explains that derivatives are generated progressively after import', async () => {
    installFetchMock({
      'GET /api/admin/import/roots': disabledRoots,
      'GET /api/admin/import/runs': () =>
        jsonResponse({ runs: [makeRun()], total: 1, limit: 25, offset: 0 }),
      'GET /api/admin/import/runs/run-1/items': emptyItems,
      'GET /api/admin/import/runs/run-1': () => jsonResponse(makeRun()),
    });
    renderPage();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: /Open run for target@example.com/ }));
    expect(
      await screen.findByText(/video posters are generated/i),
    ).toBeInTheDocument();
    expect(screen.getByText(/may appear\s+progressively/)).toBeInTheDocument();
  });

  it('lists manifest items with status detail and supports the status filter', async () => {
    const mock = installFetchMock({
      'GET /api/admin/import/roots': disabledRoots,
      'GET /api/admin/import/runs': () =>
        jsonResponse({ runs: [makeRun({ status: 'partial' })], total: 1, limit: 25, offset: 0 }),
      'GET /api/admin/import/runs/run-1/items': () =>
        jsonResponse({
          importRunId: 'run-1',
          items: [
            {
              relativePath: 'a/ok.jpg', kind: 'file', sizeBytes: 1000, status: 'imported',
              failureCategory: null, failureMessage: null, conflictCategory: null,
              attempts: 1, sourceModifiedAt: null, completedAt: '2026-06-05T10:00:03Z',
            },
            {
              relativePath: 'a/resumed.jpg', kind: 'file', sizeBytes: 1000, status: 'imported',
              failureCategory: null, failureMessage: null,
              conflictCategory: 'already-imported-this-run',
              attempts: 2, sourceModifiedAt: null, completedAt: '2026-06-05T10:00:03Z',
            },
            {
              relativePath: 'a/dup.jpg', kind: 'file', sizeBytes: 1000, status: 'conflict',
              failureCategory: null, failureMessage: null, conflictCategory: 'preexisting',
              attempts: 1, sourceModifiedAt: null, completedAt: '2026-06-05T10:00:03Z',
            },
            {
              relativePath: 'a/broken.jpg', kind: 'file', sizeBytes: 1000, status: 'failed',
              failureCategory: 'source_changed',
              failureMessage: 'The source file changed after the scan; start a new run to rescan.',
              conflictCategory: null,
              attempts: 1, sourceModifiedAt: null, completedAt: '2026-06-05T10:00:03Z',
            },
            {
              relativePath: 'a/gone.jpg', kind: 'file', sizeBytes: 1000, status: 'skipped',
              failureCategory: 'source_missing', failureMessage: 'The source file no longer exists.',
              conflictCategory: null,
              attempts: 1, sourceModifiedAt: null, completedAt: '2026-06-05T10:00:03Z',
            },
          ],
          total: 5,
          page: 1,
          pageSize: 25,
        }),
      'GET /api/admin/import/runs/run-1': () => jsonResponse(makeRun({ status: 'partial' })),
    });
    renderPage();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: /Open run for target@example.com/ }));

    // Items table distinguishes failed / conflict / resumed / skipped.
    expect(await screen.findByText('Files (5)')).toBeInTheDocument();
    expect(screen.getByText('a/ok.jpg')).toBeInTheDocument();
    expect(screen.getByText('(resumed)')).toBeInTheDocument();
    expect(screen.getByText(/pre-existing file with this name/)).toBeInTheDocument();
    expect(screen.getByText(/source_changed — The source file changed/)).toBeInTheDocument();
    expect(screen.getByText(/source_missing/)).toBeInTheDocument();

    // The status filter triggers a filtered request.
    await user.selectOptions(screen.getByLabelText('Filter items by status'), 'failed');
    await vi.waitFor(() => {
      expect(mock.calls.some((c) => c.url.includes('/items') && c.url.includes('status=failed'))).toBe(true);
    });
  });

  it('enqueues the derivatives backfill job from a finished run detail', async () => {
    const mock = installFetchMock({
      'GET /api/admin/import/roots': disabledRoots,
      'GET /api/admin/import/runs': () =>
        jsonResponse({ runs: [makeRun({ status: 'succeeded' })], total: 1, limit: 25, offset: 0 }),
      'GET /api/admin/import/runs/run-1/items': emptyItems,
      'GET /api/admin/import/runs/run-1': () =>
        jsonResponse(makeRun({ status: 'succeeded', completedAt: '2026-06-05T10:00:05Z' })),
      'POST /api/admin/import/runs/run-1/enqueue-derivatives': () =>
        jsonResponse({ importRunId: 'run-1', jobId: 'job-9', jobStatus: 'queued' }),
    });
    renderPage();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: /Open run for target@example.com/ }));

    await user.click(await screen.findByRole('button', { name: 'Generate missing derivatives' }));
    expect(await screen.findByText(/Derivatives job queued/)).toBeInTheDocument();
    expect(mock.calls.some(
      (c) => c.url.includes('/enqueue-derivatives') && c.method === 'POST',
    )).toBe(true);
  });

  it('does not render sensitive internals in the run detail', async () => {
    installFetchMock({
      'GET /api/admin/import/roots': disabledRoots,
      'GET /api/admin/import/runs': () =>
        jsonResponse({ runs: [makeRun()], total: 1, limit: 25, offset: 0 }),
      'GET /api/admin/import/runs/run-1/items': emptyItems,
      'GET /api/admin/import/runs/run-1': () => jsonResponse(makeRun()),
    });
    const { container } = renderPage();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: /Open run for target@example.com/ }));
    await screen.findByRole('region', { name: 'Run detail' });

    for (const needle of ['storageKey', 'payloadJson', 'tokenHash', 'sha256', 'blobObjectId', 'fileItemId']) {
      expect(container.innerHTML.toLowerCase()).not.toContain(needle.toLowerCase());
    }
  });

  it('disables Confirm when the preview has no importable files', async () => {
    installFetchMock({
      'GET /api/admin/import/runs': () => jsonResponse({ runs: [], total: 0, limit: 25, offset: 0 }),
      'GET /api/admin/import/roots': () =>
        jsonResponse({ enabled: true, configured: true, roots: [{ rootId: 'abc123', label: 'incoming' }] }),
      'GET /api/admin/import/browse?rootId=abc123': () =>
        jsonResponse({ rootId: 'abc123', relativePath: '', parentRelativePath: null, directories: [] }),
      'GET /api/admin/import/users': () =>
        jsonResponse([
          { id: 'user-2', email: 'target@example.com', displayName: 'Target', isAdmin: false, isActive: true },
        ]),
      'GET /api/admin/import/destination-folders?userId=user-2': () =>
        jsonResponse({ targetUserId: 'user-2', parentFolderId: null, folders: [] }),
      'POST /api/admin/import/preview': () =>
        jsonResponse({
          totalFiles: 0,
          totalDirectories: 0,
          totalBytes: 0,
          skippedSymlinks: 0,
          skippedUnsupported: 0,
          unreadableCount: 0,
          truncated: false,
          warnings: ['No importable files were found in this directory.'],
        }),
    });
    renderPage();

    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'incoming' }));
    await user.click(await screen.findByRole('button', { name: 'Select this directory' }));
    await user.click(await screen.findByRole('button', { name: /target@example.com/ }));
    await user.click(await screen.findByRole('button', { name: 'Use this folder' }));

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Confirm & import' })).toBeDisabled();
    });
    expect(screen.getByText(/No importable files were found/)).toBeInTheDocument();
  });
});
