import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { StagingUploadPanel } from './StagingUploadPanel';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../test-utils';
import type { StagingSession } from '@nubarca/api-client';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function renderPage() {
  return render(
    <AuthedWrapper>
      <MemoryRouter>
        <StagingUploadPanel />
      </MemoryRouter>
    </AuthedWrapper>,
  );
}

// Tiny chunk size (4 bytes) so multi-chunk flows stay readable.
const CONFIG = {
  enabled: true,
  maxSessionBytes: 1024 * 1024,
  maxFileBytes: 1024,
  maxFilesPerSession: 100,
  chunkSizeBytes: 4,
  sessionTtlHours: 72,
};

function makeSession(overrides: Partial<StagingSession> = {}): StagingSession {
  return {
    sessionId: 's1',
    name: 'My upload',
    status: 'uploading',
    targetUserId: 'user-1',
    destinationFolderId: null,
    totalFiles: 1,
    totalBytes: 10,
    receivedFiles: 0,
    receivedBytes: 0,
    verifiedFiles: 0,
    failedFiles: 0,
    chunkSizeBytes: 4,
    createdAt: '2026-06-10T10:00:00Z',
    expiresAt: '2026-06-13T10:00:00Z',
    completedAt: null,
    lastErrorCode: null,
    lastErrorMessage: null,
    adminImportRunId: null,
    import: null,
    ...overrides,
  };
}

// A File with an explicit folder-relative path (what webkitdirectory yields).
function fileWithPath(relativePath: string, content: string, lastModified = 1_700_000_000_000): File {
  const name = relativePath.split('/').pop()!;
  const file = new File([content], name, { lastModified });
  Object.defineProperty(file, 'webkitRelativePath', { value: relativePath });
  return file;
}

const emptySessions = () => jsonResponse({ sessions: [], total: 0 });

describe('StagingUploadPanel', () => {
  it('shows the disabled message when staging is off', async () => {
    installFetchMock({
      'GET /api/uploads/staging/config': () => jsonResponse({ ...CONFIG, enabled: false }),
    });
    renderPage();
    expect(await screen.findByText(/caricamenti in staging sono disabilitati/)).toBeInTheDocument();
    expect(screen.getByText(/Staging__Enabled=true/)).toBeInTheDocument();
  });

  it('explains the standby/resume limitations up front', async () => {
    installFetchMock({
      'GET /api/uploads/staging/config': () => jsonResponse(CONFIG),
      'GET /api/uploads/staging/sessions': emptySessions,
    });
    renderPage();
    expect(await screen.findByText(/non può proseguire mentre il browser/)).toBeInTheDocument();
    expect(screen.getByText(/potrebbe essere necessario riselezionare/)).toBeInTheDocument();
  });

  it('builds a preflight summary from selected files and lists rejected paths', async () => {
    installFetchMock({
      'GET /api/uploads/staging/config': () => jsonResponse(CONFIG),
      'GET /api/uploads/staging/sessions': emptySessions,
    });
    renderPage();
    const input = await screen.findByTestId('staging-folder-input');

    const user = userEvent.setup();
    await user.upload(input as HTMLInputElement, [
      fileWithPath('photos/a.txt', '0123456789'),
      fileWithPath('photos/big.txt', 'x'.repeat(20)),
      fileWithPath('../evil.txt', 'haxx'),
    ]);

    expect(await screen.findByText('2 file')).toBeInTheDocument(); // evil.txt rejected
    expect(screen.getByText(/1 percorsi rifiutati/)).toBeInTheDocument();
    expect(screen.getByText('../evil.txt')).toBeInTheDocument();
    expect(screen.getByText(/path traversal/)).toBeInTheDocument();
    // Largest files listed in the preflight.
    expect(screen.getByText('File più grandi')).toBeInTheDocument();
  });

  it('uploads every missing chunk with progress and reaches 100%', async () => {
    const putUrls: string[] = [];
    const mock = installFetchMock({
      'GET /api/uploads/staging/config': () => jsonResponse(CONFIG),
      'GET /api/uploads/staging/sessions': emptySessions,
      'POST /api/uploads/staging/sessions': () => jsonResponse(makeSession({ status: 'draft' })),
      'POST /api/uploads/staging/sessions/s1/manifest': () =>
        jsonResponse({ sessionId: 's1', status: 'manifest_received', totalFiles: 1, totalBytes: 10, chunkSizeBytes: 4, alreadyCompleteFiles: 0 }),
      'GET /api/uploads/staging/sessions/s1/missing': () =>
        jsonResponse({
          sessionId: 's1',
          chunkSizeBytes: 4,
          items: [{
            itemId: 'i1', ordinal: 1, relativePath: 'photos/a.txt', sizeBytes: 10,
            lastModifiedAt: new Date(1_700_000_000_000).toISOString(),
            missingChunks: [0, 1, 2],
          }],
          nextAfterOrdinal: 1,
          hasMore: false,
        }),
      'PUT /api/uploads/staging/sessions/s1/items/i1/chunks/0': (req) => {
        putUrls.push(req.url);
        return jsonResponse({ itemId: 'i1', chunkIndex: 0, alreadyReceived: false, itemStatus: 'uploading', receivedChunkCount: 1, expectedChunkCount: 3 });
      },
      'PUT /api/uploads/staging/sessions/s1/items/i1/chunks/1': (req) => {
        putUrls.push(req.url);
        return jsonResponse({ itemId: 'i1', chunkIndex: 1, alreadyReceived: false, itemStatus: 'uploading', receivedChunkCount: 2, expectedChunkCount: 3 });
      },
      'PUT /api/uploads/staging/sessions/s1/items/i1/chunks/2': (req) => {
        putUrls.push(req.url);
        return jsonResponse({ itemId: 'i1', chunkIndex: 2, alreadyReceived: false, itemStatus: 'uploaded', receivedChunkCount: 3, expectedChunkCount: 3 });
      },
      'GET /api/uploads/staging/sessions/s1': () =>
        jsonResponse(makeSession({ receivedFiles: 1, receivedBytes: 10 })),
    });
    renderPage();

    const user = userEvent.setup();
    await user.upload(
      (await screen.findByTestId('staging-files-input')) as HTMLInputElement,
      [fileWithPath('photos/a.txt', '0123456789')],
    );
    await user.click(await screen.findByRole('button', { name: 'Avvia caricamento in staging' }));

    const bar = await screen.findByRole('progressbar', { name: 'Avanzamento caricamento' });
    await waitFor(() => expect(bar).toHaveAttribute('aria-valuenow', '100'));
    expect(putUrls).toHaveLength(3);
    expect(mock.calls.some((c) => c.url.endsWith('/manifest') && c.method === 'POST')).toBe(true);
  });

  it('retries a transient chunk failure and completes', async () => {
    let attempts = 0;
    installFetchMock({
      'GET /api/uploads/staging/config': () => jsonResponse(CONFIG),
      'GET /api/uploads/staging/sessions': emptySessions,
      'POST /api/uploads/staging/sessions': () => jsonResponse(makeSession({ status: 'draft' })),
      'POST /api/uploads/staging/sessions/s1/manifest': () =>
        jsonResponse({ sessionId: 's1', status: 'manifest_received', totalFiles: 1, totalBytes: 4, chunkSizeBytes: 4, alreadyCompleteFiles: 0 }),
      'GET /api/uploads/staging/sessions/s1/missing': () =>
        jsonResponse({
          sessionId: 's1',
          chunkSizeBytes: 4,
          items: [{ itemId: 'i1', ordinal: 1, relativePath: 'a.txt', sizeBytes: 4, lastModifiedAt: null, missingChunks: [0] }],
          nextAfterOrdinal: 1,
          hasMore: false,
        }),
      'PUT /api/uploads/staging/sessions/s1/items/i1/chunks/0': () => {
        attempts++;
        if (attempts === 1) return new Response(null, { status: 500 });
        return jsonResponse({ itemId: 'i1', chunkIndex: 0, alreadyReceived: false, itemStatus: 'uploaded', receivedChunkCount: 1, expectedChunkCount: 1 });
      },
      'GET /api/uploads/staging/sessions/s1': () =>
        jsonResponse(makeSession({ receivedFiles: 1, receivedBytes: 4 })),
    });
    renderPage();

    const user = userEvent.setup();
    await user.upload(
      (await screen.findByTestId('staging-files-input')) as HTMLInputElement,
      [fileWithPath('a.txt', 'DATA')],
    );
    await user.click(await screen.findByRole('button', { name: 'Avvia caricamento in staging' }));

    await waitFor(() => expect(attempts).toBe(2), { timeout: 5000 });
    const bar = await screen.findByRole('progressbar', { name: 'Avanzamento caricamento' });
    await waitFor(() => expect(bar).toHaveAttribute('aria-valuenow', '100'));
    // The transient failure was retried, not surfaced as a failed chunk.
    expect(screen.queryByText(/chunk\(s\) failed/)).not.toBeInTheDocument();
  });

  it('resumes a session after reselecting files, uploading only the missing chunks', async () => {
    const putUrls: string[] = [];
    installFetchMock({
      'GET /api/uploads/staging/config': () => jsonResponse(CONFIG),
      'GET /api/uploads/staging/sessions': () =>
        jsonResponse({ sessions: [makeSession({ status: 'uploading', receivedFiles: 0, receivedBytes: 8 })], total: 1 }),
      'GET /api/uploads/staging/sessions/s1/missing': () =>
        jsonResponse({
          sessionId: 's1',
          chunkSizeBytes: 4,
          // The server already has chunks 0 and 2 — only 1 is missing.
          items: [{
            itemId: 'i1', ordinal: 1, relativePath: 'photos/a.txt', sizeBytes: 10,
            lastModifiedAt: new Date(1_700_000_000_000).toISOString(),
            missingChunks: [1],
          }],
          nextAfterOrdinal: 1,
          hasMore: false,
        }),
      'PUT /api/uploads/staging/sessions/s1/items/i1/chunks/1': (req) => {
        putUrls.push(req.url);
        return jsonResponse({ itemId: 'i1', chunkIndex: 1, alreadyReceived: false, itemStatus: 'uploaded', receivedChunkCount: 3, expectedChunkCount: 3 });
      },
      'GET /api/uploads/staging/sessions/s1': () =>
        jsonResponse(makeSession({ receivedFiles: 1, receivedBytes: 10 })),
    });
    renderPage();

    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Riprendi' }));
    expect(await screen.findByTestId('staging-reselect')).toBeInTheDocument();
    expect(screen.getByText(/caricherà solo quelli mancanti/)).toBeInTheDocument();

    await user.upload(
      screen.getByTestId('staging-reselect-input') as HTMLInputElement,
      [fileWithPath('photos/a.txt', '0123456789')],
    );
    await user.click(screen.getByRole('button', { name: 'Riprendi caricamento' }));

    await waitFor(() => expect(putUrls).toHaveLength(1));
    expect(putUrls[0]).toContain('/chunks/1');
  });

  it('skips reselected files that no longer match the manifest', async () => {
    installFetchMock({
      'GET /api/uploads/staging/config': () => jsonResponse(CONFIG),
      'GET /api/uploads/staging/sessions': () =>
        jsonResponse({ sessions: [makeSession({ status: 'uploading' })], total: 1 }),
      'GET /api/uploads/staging/sessions/s1/missing': () =>
        jsonResponse({
          sessionId: 's1',
          chunkSizeBytes: 4,
          items: [{ itemId: 'i1', ordinal: 1, relativePath: 'photos/a.txt', sizeBytes: 10, lastModifiedAt: null, missingChunks: [0, 1, 2] }],
          nextAfterOrdinal: 1,
          hasMore: false,
        }),
      'GET /api/uploads/staging/sessions/s1': () => jsonResponse(makeSession()),
    });
    renderPage();

    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Riprendi' }));
    // Same path but DIFFERENT size → unmatched, requires a decision.
    await user.upload(
      (await screen.findByTestId('staging-reselect-input')) as HTMLInputElement,
      [fileWithPath('photos/a.txt', 'short')],
    );
    await user.click(screen.getByRole('button', { name: 'Riprendi caricamento' }));

    expect(await screen.findByText(/non corrispondono agli originali/)).toBeInTheDocument();
  });

  it('verify and import follow the session state and show import progress', async () => {
    let detailStatus = 'uploading';
    let importBlock: StagingSession['import'] = null;
    installFetchMock({
      'GET /api/uploads/staging/config': () => jsonResponse(CONFIG),
      'GET /api/uploads/staging/sessions': () =>
        jsonResponse({ sessions: [makeSession({ status: 'uploading' })], total: 1 }),
      'GET /api/uploads/staging/sessions/s1': () =>
        jsonResponse(makeSession({ status: detailStatus, import: importBlock })),
      'POST /api/uploads/staging/sessions/s1/verify': () => {
        detailStatus = 'ready_to_import';
        return jsonResponse({ sessionId: 's1', status: 'ready_to_import', verifiedFiles: 1, incompleteFiles: 0, corruptFiles: 0, readyToImport: true });
      },
      'POST /api/uploads/staging/sessions/s1/import': () => {
        detailStatus = 'importing';
        importBlock = {
          status: 'running', phase: 'importing', importedFiles: 1, pendingFiles: 0,
          failedFiles: 0, conflictFiles: 0, skippedFiles: 0,
          skippedPreviouslyDeletedFiles: 0, skippedAlreadyPresentFiles: 0, importedBytes: 10,
        };
        return jsonResponse({ sessionId: 's1', status: 'importing', adminImportRunId: 'run-1', jobId: 'job-1' });
      },
    });
    renderPage();

    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Riprendi' }));

    // uploading → Verify available, Start import not yet.
    expect(await screen.findByRole('button', { name: 'Verifica caricamento' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Avvia importazione' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Verifica caricamento' }));
    expect(await screen.findByText(/1 verificati, 0 incompleti/)).toBeInTheDocument();

    // ready_to_import → Start import appears; clicking it shows progress.
    await user.click(await screen.findByRole('button', { name: 'Avvia importazione' }));
    expect(await screen.findByText(/Stato: importing — importing/)).toBeInTheDocument();
    expect(await screen.findByText(/1 importati, 0 in attesa/)).toBeInTheDocument();
  });

  it('does not render sensitive internals', async () => {
    installFetchMock({
      'GET /api/uploads/staging/config': () => jsonResponse(CONFIG),
      'GET /api/uploads/staging/sessions': () =>
        jsonResponse({ sessions: [makeSession()], total: 1 }),
      'GET /api/uploads/staging/sessions/s1': () => jsonResponse(makeSession()),
    });
    renderPage();
    await screen.findByText('Caricamenti in staging recenti');

    const text = document.body.textContent ?? '';
    for (const needle of ['storageKey', 'payloadJson', 'tokenHash', 'sha256', 'blobObjectId', '/var/lib']) {
      expect(text.toLowerCase()).not.toContain(needle.toLowerCase());
    }
  });

  it('renders the two import options with both defaulting ON', async () => {
    installFetchMock({
      'GET /api/uploads/staging/config': () => jsonResponse(CONFIG),
      'GET /api/uploads/staging/sessions': emptySessions,
    });
    renderPage();

    expect(await screen.findByText('Salta i file che ho già eliminato')).toBeInTheDocument();
    expect(screen.getByText('Salta i file già presenti nella mia libreria')).toBeInTheDocument();
    expect(screen.getByText(/corrispondenza esatta del contenuto/)).toBeInTheDocument();

    const deleted = screen.getByTestId('skip-previously-deleted') as HTMLInputElement;
    const existing = screen.getByTestId('skip-existing-content') as HTMLInputElement;
    expect(deleted.checked).toBe(true);
    expect(existing.checked).toBe(true);
  });

  it('sends the chosen import options when starting an upload', async () => {
    let createdBody: Record<string, unknown> | null = null;
    installFetchMock({
      'GET /api/uploads/staging/config': () => jsonResponse(CONFIG),
      'GET /api/uploads/staging/sessions': emptySessions,
      'POST /api/uploads/staging/sessions': (req) => {
        createdBody = JSON.parse((req?.body as string) ?? '{}') as Record<string, unknown>;
        return jsonResponse(makeSession({ status: 'draft' }));
      },
      'POST /api/uploads/staging/sessions/s1/manifest': () =>
        jsonResponse({ sessionId: 's1', status: 'manifest_received', totalFiles: 1, totalBytes: 2, chunkSizeBytes: 4, alreadyCompleteFiles: 0 }),
      'GET /api/uploads/staging/sessions/s1': () => jsonResponse(makeSession({ status: 'manifest_received' })),
      'GET /api/uploads/staging/sessions/s1/missing': () =>
        jsonResponse({ sessionId: 's1', chunkSizeBytes: 4, items: [], nextAfterOrdinal: null, hasMore: false }),
    });
    renderPage();
    await screen.findByText('Salta i file che ho già eliminato');

    // Turn OFF "already in library", leave "previously deleted" ON.
    await userEvent.click(screen.getByTestId('skip-existing-content'));
    await userEvent.upload(
      screen.getByTestId('staging-files-input'),
      [fileWithPath('a.txt', 'hi')],
    );
    await userEvent.click(screen.getByRole('button', { name: /Avvia caricamento in staging/ }));

    await waitFor(() => expect(createdBody).not.toBeNull());
    expect(createdBody!.skipPreviouslyDeleted).toBe(true);
    expect(createdBody!.skipExistingContent).toBe(false);
  });

  it('shows the import summary breakdown (deleted / already-present) with safe counts', async () => {
    const importBlock = {
      status: 'succeeded', phase: null, importedFiles: 3, pendingFiles: 0,
      failedFiles: 1, conflictFiles: 0, skippedFiles: 0,
      skippedPreviouslyDeletedFiles: 5, skippedAlreadyPresentFiles: 2, importedBytes: 30,
    };
    installFetchMock({
      'GET /api/uploads/staging/config': () => jsonResponse(CONFIG),
      'GET /api/uploads/staging/sessions': () =>
        jsonResponse({ sessions: [makeSession({ status: 'imported', import: importBlock })], total: 1 }),
      'GET /api/uploads/staging/sessions/s1': () =>
        jsonResponse(makeSession({ status: 'imported', import: importBlock })),
    });
    renderPage();

    await userEvent.click(await screen.findByRole('button', { name: /^Apri$/ }));

    const summary = await screen.findByTestId('staging-import-summary');
    expect(summary).toHaveTextContent('3 importati');
    expect(summary).toHaveTextContent('5 saltati (già eliminati)');
    expect(summary).toHaveTextContent('2 saltati (già in libreria)');
    expect(summary).toHaveTextContent('1 non riusciti');
  });
});
