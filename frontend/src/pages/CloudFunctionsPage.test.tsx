import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { BrowserRouter, MemoryRouter, Route, Routes } from 'react-router';
import { CloudFunctionsPage } from './CloudFunctionsPage';
import { LegacyCloudToolRedirect } from '../cloud/LegacyCloudToolRedirect';
import { PERMISSIONS } from '@nubarca/api-client';
import { AuthedWrapper, installFetchMock, jsonResponse, emptyResponse } from '../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  // The back/forward test drives the real history; reset it for the next file.
  window.history.pushState({}, '', '/');
});

// Every tool panel loads its own data on mount, so the shared handler table
// covers all tools. A tool that is not selected never fetches.
const STAGING_CONFIG = {
  enabled: true,
  maxSessionBytes: 1024 * 1024,
  maxFileBytes: 1024,
  maxFilesPerSession: 100,
  chunkSizeBytes: 4,
  sessionTtlHours: 72,
};

const TV_DEVICE = {
  id: 's1',
  deviceLabel: 'Living room TV',
  userAgent: 'Mozilla/5.0 (SmartTV)',
  status: 'active',
  createdAt: '2026-07-01T10:00:00Z',
  lastSeenAt: '2026-07-05T10:00:00Z',
  expiresAt: '2026-08-01T10:00:00Z',
  revokedAt: null,
};

function toolHandlers(): Parameters<typeof installFetchMock>[0] {
  return {
    'GET /api/uploads/staging/config': () => jsonResponse(STAGING_CONFIG),
    'GET /api/uploads/staging/sessions': () => jsonResponse({ sessions: [], total: 0 }),
    'GET /api/tv-devices': () => jsonResponse([TV_DEVICE]),
    'GET /api/print/stations': () => jsonResponse([]),
    'GET /api/tv-personal/pin': () => jsonResponse({ configured: true, updatedAt: '2026-07-01T10:00:00Z' }),
    '* /api/photo-organizer/date-taken/dry-run': () => jsonResponse({
      summary: {
        candidateCount: 0, withDateCount: 0, missingDateCount: 0, toMoveCount: 0,
        alreadyOrganizedCount: 0, skippedMissingCount: 0, skippedConflictCount: 0,
        exactDuplicateRemovedCount: 0, foldersToCreateCount: 0, estimatedOperations: 0,
        bySource: {
          userOverride: 0, metadataOriginal: 0, metadataFallback: 0,
          fileCreatedFallback: 0, missing: 0,
        },
      },
      samples: [],
    }),
  };
}

function renderHub(initialEntries: string[] = ['/cloud-functions']) {
  return render(
    <AuthedWrapper>
      <MemoryRouter initialEntries={initialEntries}>
        <Routes>
          <Route path="/cloud-functions" element={<CloudFunctionsPage />} />
          {/* The real legacy aliases, so the redirect target is asserted for
              real rather than mocked. */}
          <Route path="/upload" element={<LegacyCloudToolRedirect tool="upload" />} />
          <Route path="/tv-devices" element={<LegacyCloudToolRedirect tool="tv-devices" />} />
          <Route path="/private" element={<div>private vault page</div>} />
        </Routes>
      </MemoryRouter>
    </AuthedWrapper>,
  );
}

function tabs() {
  return within(screen.getByTestId('cloud-tool-tabs'));
}

describe('Cloud Functions hub — tools', () => {
  it('offers all Cloud Functions as an accessible tablist', () => {
    installFetchMock(toolHandlers());
    renderHub();

    const tablist = screen.getByRole('tablist', { name: 'Strumenti delle funzioni cloud' });
    expect(tablist).toBeInTheDocument();
    const all = tabs().getAllByRole('tab');
    expect(all).toHaveLength(7);
    expect(all.map((t) => t.textContent)).toEqual([
      'Caricamento in blocco',
      'Organizza le foto per data',
      'Rimuovi duplicati multimediali esatti',
      'Scarica archivio foto',
      'Dispositivi TV',
      'Stazioni di stampa',
      'Ricalcola cluster volti',
    ]);
  });

  it('does not offer Private Vault as a Cloud Function any more', () => {
    installFetchMock(toolHandlers());
    renderHub();

    expect(screen.queryByTestId('cf-private-vault')).not.toBeInTheDocument();
    expect(screen.queryByText('Archivio privato')).not.toBeInTheDocument();
    // No control anywhere on the hub points at /private.
    const links = screen.queryAllByRole('link').map((a) => a.getAttribute('href'));
    expect(links).not.toContain('/private');
  });

  it('renders no grid of cards followed by a detached panel', () => {
    installFetchMock(toolHandlers());
    renderHub();
    expect(document.querySelector('.admin-grid')).toBeNull();
    expect(document.querySelector('.cloud-function-card')).toBeNull();
  });
});

describe('Cloud Functions hub — URL state', () => {
  it('defaults to the Upload tool and renders it below the switcher', async () => {
    installFetchMock(toolHandlers());
    renderHub();

    expect(screen.getByTestId('cf-tool-upload')).toHaveAttribute('aria-selected', 'true');
    const panel = screen.getByTestId('cloud-tool-panel');
    expect(panel).toHaveAttribute('data-tool', 'upload');
    expect(panel).toHaveAttribute('role', 'tabpanel');
    expect(panel).toHaveAttribute('aria-labelledby', 'cloud-tool-tab-upload');
    // The COMPLETE upload tool, not a link to it.
    expect(await within(panel).findByTestId('staging-pick')).toBeInTheDocument();
  });

  it('selects the tool named in the URL', async () => {
    installFetchMock(toolHandlers());
    renderHub(['/cloud-functions?tool=tv-devices']);

    expect(screen.getByTestId('cf-tool-tv-devices')).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByTestId('cloud-tool-panel')).toHaveAttribute('data-tool', 'tv-devices');
    // TV Devices behaviour preserved: the real device list renders.
    expect(await screen.findByText('Living room TV')).toBeInTheDocument();
  });

  it('selects the archive tool from the URL', async () => {
    installFetchMock(toolHandlers());
    renderHub(['/cloud-functions?tool=archive']);
    expect(screen.getByTestId('cloud-tool-panel')).toHaveAttribute('data-tool', 'archive');
    expect(
      await screen.findByRole('button', { name: 'Crea sessione di esportazione' }),
    ).toBeInTheDocument();
  });

  it('falls back to the default tool for an invalid ?tool= value', async () => {
    installFetchMock(toolHandlers());
    renderHub(['/cloud-functions?tool=not-a-tool']);

    expect(screen.getByTestId('cf-tool-upload')).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByTestId('cloud-tool-panel')).toHaveAttribute('data-tool', 'upload');
    expect(await screen.findByTestId('staging-pick')).toBeInTheDocument();
  });

  it('falls back to the default tool for the removed Private Vault value', () => {
    installFetchMock(toolHandlers());
    renderHub(['/cloud-functions?tool=private']);
    expect(screen.getByTestId('cloud-tool-panel')).toHaveAttribute('data-tool', 'upload');
  });

  it('writes the selected tool to the URL and supports back/forward', async () => {
    installFetchMock(toolHandlers());
    // A REAL history is required here: MemoryRouter keeps its own stack, so
    // window.history.back() would not reach the router.
    window.history.pushState({}, '', '/cloud-functions');
    render(
      <AuthedWrapper>
        <BrowserRouter>
          <Routes>
            <Route path="/cloud-functions" element={<CloudFunctionsPage />} />
          </Routes>
        </BrowserRouter>
      </AuthedWrapper>,
    );
    const user = userEvent.setup();

    await user.click(screen.getByTestId('cf-tool-archive'));
    expect(window.location.search).toBe('?tool=archive');
    expect(screen.getByTestId('cloud-tool-panel')).toHaveAttribute('data-tool', 'archive');

    await user.click(screen.getByTestId('cf-tool-tv-devices'));
    expect(window.location.search).toBe('?tool=tv-devices');
    expect(screen.getByTestId('cloud-tool-panel')).toHaveAttribute('data-tool', 'tv-devices');

    // Back walks the tools that were visited (a push, not a replace).
    window.history.back();
    await waitFor(() =>
      expect(screen.getByTestId('cloud-tool-panel')).toHaveAttribute('data-tool', 'archive'));

    window.history.back();
    await waitFor(() =>
      expect(screen.getByTestId('cloud-tool-panel')).toHaveAttribute('data-tool', 'upload'));

    window.history.forward();
    await waitFor(() =>
      expect(screen.getByTestId('cloud-tool-panel')).toHaveAttribute('data-tool', 'archive'));
  });
});

describe('Cloud Functions hub — keyboard', () => {
  it('moves between tools with the arrow keys and Home/End', async () => {
    installFetchMock(toolHandlers());
    renderHub();
    const user = userEvent.setup();

    const upload = screen.getByTestId('cf-tool-upload');
    // Roving tabindex: only the selected tab is in the tab order.
    expect(upload).toHaveAttribute('tabindex', '0');
    expect(screen.getByTestId('cf-tool-archive')).toHaveAttribute('tabindex', '-1');

    upload.focus();
    await user.keyboard('{ArrowRight}');
    expect(screen.getByTestId('cf-tool-organize')).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByTestId('cf-tool-organize')).toHaveFocus();

    await user.keyboard('{End}');
    expect(screen.getByTestId('cf-tool-face-cluster')).toHaveAttribute('aria-selected', 'true');

    await user.keyboard('{Home}');
    expect(screen.getByTestId('cf-tool-upload')).toHaveAttribute('aria-selected', 'true');
  });

  it('activates a tool with a plain click, keeping its panel below', async () => {
    installFetchMock(toolHandlers());
    renderHub();
    await userEvent.setup().click(screen.getByTestId('cf-tool-organize'));

    const panel = screen.getByTestId('cloud-tool-panel');
    expect(panel).toHaveAttribute('data-tool', 'organize');
    expect(within(panel).getByTestId('cf-organize')).toBeInTheDocument();
  });
});

describe('Cloud Functions hub — tool behaviour preserved', () => {
  it('opens the date organizer wizard from its tool panel', async () => {
    installFetchMock(toolHandlers());
    renderHub(['/cloud-functions?tool=organize']);
    await userEvent.setup().click(screen.getByTestId('cf-organize'));

    await waitFor(() => {
      const dialogs = screen.queryAllByRole('dialog');
      expect(dialogs.length + screen.queryAllByText(/organi/i).length).toBeGreaterThan(0);
    });
  });

  it('confirms exact-media cleanup and shows its aggregate result', async () => {
    vi.stubGlobal('confirm', () => true);
    installFetchMock({
      ...toolHandlers(),
      'POST /api/cloud-functions/media-duplicates/exact/runs': () => jsonResponse({
        runId: 'run-1', jobId: 'job-1', status: 'queued',
      }, 202),
      'GET /api/cloud-functions/media-duplicates/exact/runs/run-1': () => jsonResponse({
        runId: 'run-1', status: 'succeeded', duplicateGroupCount: 2,
        filesRemovedCount: 3, filesRetainedCount: 2, error: null,
        createdAt: '2026-08-09T10:00:00Z', startedAt: '2026-08-09T10:00:01Z',
        completedAt: '2026-08-09T10:00:02Z',
      }),
    });
    renderHub(['/cloud-functions?tool=dedupe']);

    await userEvent.setup().click(screen.getByTestId('cf-dedupe'));
    const result = await screen.findByTestId('cf-dedupe-status');
    expect(result).toHaveTextContent('Gruppi di duplicati esatti trovati2');
    expect(result).toHaveTextContent('File rimossi3');
    expect(result).toHaveTextContent('File conservati2');
  });

  it('creates an export session from the archive tool', async () => {
    installFetchMock({
      ...toolHandlers(),
      'POST /api/photo-exports': () => jsonResponse(
        { sessionId: 'sess-1', token: 'secret-token', status: 'pending', expiresAt: '2026-07-06T00:00:00Z' },
        201,
      ),
      'GET /api/photo-exports/sess-1': () => jsonResponse({
        sessionId: 'sess-1', status: 'ready', fileCount: 42, totalBytes: 1048576,
        errorSummary: null, createdAt: '2026-06-29T00:00:00Z',
        completedAt: '2026-06-29T00:01:00Z', expiresAt: '2026-07-06T00:00:00Z', manifestReady: true,
      }),
    });
    renderHub(['/cloud-functions?tool=archive']);
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Crea sessione di esportazione' }));
    await waitFor(() => expect(screen.getByTestId('export-status')).toHaveTextContent('ready'));
    expect(screen.getByText('42')).toBeTruthy();
  });

  it('revokes a TV device from the TV Devices tool', async () => {
    vi.stubGlobal('confirm', () => true);
    let revoked = false;
    let listCall = 0;
    installFetchMock({
      ...toolHandlers(),
      'GET /api/tv-devices': () => jsonResponse(
        listCall++ === 0
          ? [TV_DEVICE]
          : [{ ...TV_DEVICE, status: 'revoked', revokedAt: '2026-07-06T10:00:00Z' }],
      ),
      'DELETE /api/tv-devices/s1': () => { revoked = true; return emptyResponse(204); },
    });
    renderHub(['/cloud-functions?tool=tv-devices']);

    await userEvent.setup().click(await screen.findByRole('button', { name: 'Revoca' }));
    await waitFor(() => expect(revoked).toBe(true));
  });

  it('does not mount a tool that is not selected', async () => {
    installFetchMock(toolHandlers());
    const mock = installFetchMock(toolHandlers());
    renderHub(['/cloud-functions?tool=archive']);
    await screen.findByRole('button', { name: 'Crea sessione di esportazione' });

    // The upload and TV tools are inert while the archive tool is showing.
    expect(mock.calls.some((c) => c.url.includes('/api/uploads/staging'))).toBe(false);
    expect(mock.calls.some((c) => c.url.includes('/api/tv-devices'))).toBe(false);
  });
});

describe('legacy Cloud Functions routes', () => {
  it('/upload redirects to the canonical Upload tool', async () => {
    installFetchMock(toolHandlers());
    renderHub(['/upload']);

    expect(screen.getByTestId('cloud-tool-panel')).toHaveAttribute('data-tool', 'upload');
    expect(screen.getByTestId('cf-tool-upload')).toHaveAttribute('aria-selected', 'true');
    expect(await screen.findByTestId('staging-pick')).toBeInTheDocument();
  });

  it('/tv-devices redirects to the canonical TV Devices tool', async () => {
    installFetchMock(toolHandlers());
    renderHub(['/tv-devices']);

    expect(screen.getByTestId('cloud-tool-panel')).toHaveAttribute('data-tool', 'tv-devices');
    expect(screen.getByTestId('cf-tool-tv-devices')).toHaveAttribute('aria-selected', 'true');
    expect(await screen.findByText('Living room TV')).toBeInTheDocument();
  });
});

// Reaching the hub is one authority; a tool may need another. Everything the
// page computes — which tabs exist, which one a deep link opens, where an arrow
// key lands — has to work on the VISIBLE list, or a user is shown a door that
// answers 403, or an arrow focuses nothing.
describe('Cloud Functions hub — permission gating', () => {
  function renderAs(permissions: readonly string[], initialEntries = ['/cloud-functions']) {
    installFetchMock(toolHandlers());
    return render(
      <AuthedWrapper permissions={permissions}>
        <MemoryRouter initialEntries={initialEntries}>
          <Routes>
            <Route path="/cloud-functions" element={<CloudFunctionsPage />} />
          </Routes>
        </MemoryRouter>
      </AuthedWrapper>,
    );
  }

  const WITHOUT = [PERMISSIONS.cloudFunctionsAccess, PERMISSIONS.tvManage];
  const WITH = [...WITHOUT, PERMISSIONS.peopleAccess, PERMISSIONS.peopleClusterRebuild];

  it('offers the face-cluster tool to a user who holds the permission', () => {
    renderAs(WITH);
    expect(screen.getByTestId('cf-tool-face-cluster')).toBeInTheDocument();
  });

  it('does not render the tool at all without the permission', () => {
    renderAs(WITHOUT);
    expect(screen.queryByTestId('cf-tool-face-cluster')).toBeNull();
    // The other tools are untouched — this gates one tool, not the hub.
    expect(screen.getByTestId('cf-tool-upload')).toBeInTheDocument();
    expect(tabs().getAllByRole('tab')).toHaveLength(6);
  });

  it('falls back safely when an unauthorized deep link names the tool', () => {
    renderAs(WITHOUT, ['/cloud-functions?tool=face-cluster']);

    // Not the protected panel, not even for a frame.
    expect(screen.queryByTestId('face-cluster-rebuild')).toBeNull();
    expect(screen.getByTestId('cloud-tool-panel')).toHaveAttribute('data-tool', 'upload');
  });

  it('honours the same deep link for a user who may use it', async () => {
    renderAs(WITH, ['/cloud-functions?tool=face-cluster']);

    expect(screen.getByTestId('cloud-tool-panel')).toHaveAttribute('data-tool', 'face-cluster');
    expect(await screen.findByTestId('face-cluster-rebuild')).toBeInTheDocument();
  });

  it('walks only the visible tabs with the keyboard', async () => {
    renderAs(WITHOUT);
    const user = userEvent.setup();

    // End must land on the last VISIBLE tool. Computed from the full catalogue
    // it would land on a hidden index and focus nothing.
    screen.getByTestId('cf-tool-upload').focus();
    await user.keyboard('{End}');
    expect(screen.getByTestId('cf-tool-print-stations')).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByTestId('cf-tool-print-stations')).toHaveFocus();

    // …and wrapping forward from the last one returns to the first.
    await user.keyboard('{ArrowRight}');
    expect(screen.getByTestId('cf-tool-upload')).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByTestId('cf-tool-upload')).toHaveFocus();
  });
});
