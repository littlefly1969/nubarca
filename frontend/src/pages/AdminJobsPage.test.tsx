import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, within, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AdminJobsPage } from './AdminJobsPage';
import { AuthedWrapper, installFetchMock, jsonResponse, errorResponse } from '../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function job(overrides: Record<string, unknown> = {}) {
  return {
    id: 'job-1',
    type: 'storage.reconcile',
    status: 'running',
    priority: 100,
    attempts: 1,
    maxAttempts: 3,
    createdAt: '2026-06-09T10:00:00Z',
    availableAt: '2026-06-09T10:00:00Z',
    startedAt: '2026-06-09T10:00:01Z',
    completedAt: null,
    updatedAt: '2026-06-09T10:00:05Z',
    leaseUntil: '2026-06-09T10:02:00Z',
    heartbeatAt: '2026-06-09T10:00:05Z',
    cancellationRequested: false,
    progressCurrent: 3,
    progressTotal: 10,
    progressMessage: 'phase 2',
    lastErrorCode: null,
    lastErrorMessage: null,
    ...overrides,
  };
}

function page(items: ReturnType<typeof job>[], counts?: Record<string, number>) {
  return jsonResponse({
    items,
    page: 1,
    pageSize: 50,
    total: items.length,
    counts: { queued: 1, running: 1, succeeded: 2, failed: 1, cancelled: 0, ...counts },
  });
}

function p(name: string, kind: string, extra: Record<string, unknown> = {}) {
  return {
    name, kind, required: false, min: null, max: null,
    defaultBool: false, defaultInt: null, danger: false, ...extra,
  };
}

// A small, representative catalog: a media backfill (int + bools + a danger
// flag + a default-on dry-run), a single-blob command (required guid), and an
// AI command (text profile). Every field mirrors the backend AdminJobParamDto.
function catalog() {
  return jsonResponse({
    commands: [
      {
        key: 'media-video-hls-backfill', category: 'media', jobType: 'media.video.hls.backfill',
        available: true, disabledReason: null,
        params: [
          p('limit', 'int', { min: 1, max: 100000 }),
          p('retryFailed', 'bool'),
          p('force', 'bool', { danger: true }),
          p('dryRun', 'bool', { defaultBool: true }),
        ],
      },
      {
        key: 'media-video-hls-generate', category: 'media', jobType: 'media.video.hls.generate',
        available: true, disabledReason: null,
        params: [
          p('blobId', 'guid', { required: true }),
          p('force', 'bool', { danger: true }),
        ],
      },
      {
        // AI command: profile is a CHOICE preselected on the configured model.
        key: 'ai-photos-embeddings-backfill', category: 'ai', jobType: 'ai.photos.embeddings.backfill',
        available: true, disabledReason: null,
        params: [
          p('profileKey', 'choice', {
            defaultText: 'photo-siglip2-v2',
            options: [
              { value: 'det-image-embedding-v1', label: 'det-image-embedding-v1', recommended: false },
              { value: 'photo-siglip2-v2', label: 'photo-siglip2-v2', recommended: true },
            ],
          }),
          p('dryRun', 'bool', { defaultBool: true }),
        ],
      },
      {
        // Feature switched off server-side → shown disabled, run blocked.
        key: 'ai-tags-generate-backfill', category: 'ai', jobType: 'ai.tags.generate.backfill',
        available: false, disabledReason: 'feature-disabled',
        params: [p('dryRun', 'bool', { defaultBool: true })],
      },
    ],
  });
}

// Every AdminJobsPage render loads the catalog AND the pending counts.
const CATALOG = {
  'GET /api/admin/jobs/catalog': () => catalog(),
  'GET /api/admin/jobs/pending': () => jsonResponse({ 'media-video-hls-backfill': 4520 }),
};

function renderPage() {
  return render(<AuthedWrapper><AdminJobsPage /></AuthedWrapper>);
}

describe('AdminJobsPage — queue status', () => {
  it('renders status counters and the jobs table', async () => {
    installFetchMock({ ...CATALOG, 'GET /api/admin/jobs': () => page([job()]) });
    renderPage();

    const counters = await screen.findByLabelText('Contatori stato processi');
    expect(within(counters).getByText(/Completati/)).toHaveTextContent('2');
    expect(within(counters).getByText(/In esecuzione/)).toHaveTextContent('1');
    expect(screen.getByLabelText('Processi in background')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'storage.reconcile' })).toBeInTheDocument();
    expect(screen.getByText(/3\/10 phase 2/)).toBeInTheDocument();
  });

  it('shows the empty state when there are no jobs', async () => {
    installFetchMock({ ...CATALOG, 'GET /api/admin/jobs': () => page([]) });
    renderPage();
    expect(await screen.findByText('Nessun processo in background.')).toBeInTheDocument();
  });

  it('opens the detail drawer with a progress bar when a job is clicked', async () => {
    installFetchMock({ ...CATALOG, 'GET /api/admin/jobs': () => page([job()]) });
    renderPage();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'storage.reconcile' }));

    const dialog = await screen.findByRole('dialog', { name: 'Processo: storage.reconcile' });
    expect(within(dialog).getByRole('progressbar')).toHaveAttribute('aria-valuenow', '30');
    expect(within(dialog).getByText('phase 2', { exact: false })).toBeInTheDocument();
  });

  it('cancels a running job after confirmation and calls the endpoint', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);
    const mock = installFetchMock({
      ...CATALOG,
      'GET /api/admin/jobs': () => page([job()]),
      'POST /api/admin/jobs/job-1/cancel': () => jsonResponse(job({ cancellationRequested: true })),
    });
    renderPage();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'storage.reconcile' }));
    const dialog = await screen.findByRole('dialog', { name: 'Processo: storage.reconcile' });

    await user.click(within(dialog).getByRole('button', { name: 'Richiedi annullamento' }));

    expect(confirmSpy).toHaveBeenCalled();
    await vi.waitFor(() => {
      expect(mock.calls.some((c) => c.method === 'POST' && c.url.includes('/api/admin/jobs/job-1/cancel'))).toBe(true);
    });
  });

  it('does not show an active cancel button for terminal jobs', async () => {
    installFetchMock({ ...CATALOG, 'GET /api/admin/jobs': () => page([job({ status: 'succeeded', completedAt: '2026-06-09T10:01:00Z' })]) });
    renderPage();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'storage.reconcile' }));
    const dialog = await screen.findByRole('dialog', { name: 'Processo: storage.reconcile' });

    expect(within(dialog).queryByRole('button', { name: 'Richiedi annullamento' })).toBeNull();
    expect(within(dialog).getByText(/terminato e non può essere annullato/)).toBeInTheDocument();
  });

  it('renders an error state safely when the list fails', async () => {
    installFetchMock({ ...CATALOG, 'GET /api/admin/jobs': () => errorResponse(500) });
    renderPage();
    expect(await screen.findByText('Impossibile caricare i processi. Riprova.')).toBeInTheDocument();
  });

  it('lists import and media-derivative jobs side by side', async () => {
    installFetchMock({
      ...CATALOG,
      'GET /api/admin/jobs': () => page([
        job({ id: 'job-i', type: 'admin.import', progressMessage: 'photos/2009' }),
        job({
          id: 'job-d', type: 'media.derivatives.backfill', status: 'queued',
          progressCurrent: null, progressTotal: null, progressMessage: null,
        }),
      ]),
    });
    renderPage();
    expect(await screen.findByRole('button', { name: 'admin.import' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'media.derivatives.backfill' })).toBeInTheDocument();
  });

  it('never renders payload or sensitive-looking strings', async () => {
    installFetchMock({
      ...CATALOG,
      'GET /api/admin/jobs': () => page([job({ lastErrorCode: 'IOException', lastErrorMessage: 'disk full' })]),
    });
    renderPage();
    await screen.findByRole('button', { name: 'storage.reconcile' });
    const text = document.body.textContent ?? '';
    for (const needle of ['payload', 'StorageKey', 'sha256', 'BlobId', 'worker-host', 'TokenHash']) {
      expect(text.toLowerCase()).not.toContain(needle.toLowerCase());
    }
  });
});

describe('AdminJobsPage — commands console', () => {
  function card(title: string): HTMLElement {
    const heading = screen.getByText(title);
    const el = heading.closest('.admin-console-card');
    if (!el) throw new Error(`card not found for ${title}`);
    return el as HTMLElement;
  }

  it('renders command cards grouped by category from the catalog', async () => {
    installFetchMock({ ...CATALOG, 'GET /api/admin/jobs': () => page([]) });
    renderPage();
    // Category headings + a representative command title from each.
    expect(await screen.findByText('Media (anteprime, poster, video)')).toBeInTheDocument();
    expect(screen.getByText('Intelligenza artificiale')).toBeInTheDocument();
    expect(screen.getByText('Prepara i video (HLS)')).toBeInTheDocument();
    expect(screen.getByText('Indicizza foto (embedding)')).toBeInTheDocument();
  });

  it('shows the backlog badge for a command with pending work', async () => {
    installFetchMock({ ...CATALOG, 'GET /api/admin/jobs': () => page([]) });
    renderPage();
    await screen.findByText('Prepara i video (HLS)');
    expect(await within(card('Prepara i video (HLS)')).findByText('4520 da elaborare')).toBeInTheDocument();
  });

  it('marks a switched-off command as disabled and blocks running it', async () => {
    installFetchMock({ ...CATALOG, 'GET /api/admin/jobs': () => page([]) });
    renderPage();
    await screen.findByText('Genera tag AI');
    const c = card('Genera tag AI');

    expect(within(c).getByText('disattivato')).toBeInTheDocument();
    expect(within(c).getByText(/funzione è disattivata/)).toBeInTheDocument();
    // Both labels are irrelevant — the action must simply be unavailable.
    expect(within(c).getByRole('button', { name: /Esegui|Simula/ })).toBeDisabled();
  });

  it('offers the model as a select preselected on the recommended profile', async () => {
    const mock = installFetchMock({
      ...CATALOG,
      'GET /api/admin/jobs': () => page([]),
      'POST /api/admin/jobs/enqueue': () =>
        jsonResponse({ jobId: 'j-ai', jobType: 'ai.photos.embeddings.backfill' }),
    });
    renderPage();
    const user = userEvent.setup();
    await screen.findByText('Indicizza foto (embedding)');
    const c = card('Indicizza foto (embedding)');

    await user.click(within(c).getByRole('button', { name: /Parametri/ }));
    const select = within(c).getByLabelText('Profilo') as HTMLSelectElement;
    // Preselected on the configured production model, marked as recommended.
    expect(select.value).toBe('photo-siglip2-v2');
    expect(within(select).getByText('photo-siglip2-v2 — consigliato')).toBeInTheDocument();

    // Dry-run defaults on → the action reads "Simula" and says so.
    expect(within(c).getByRole('button', { name: 'Simula' })).toBeInTheDocument();
    expect(within(c).getByText(/conta soltanto/)).toBeInTheDocument();

    await user.click(within(c).getByRole('button', { name: 'Simula' }));
    await waitFor(() => {
      const call = mock.calls.find((x) => x.method === 'POST' && x.url.includes('/enqueue'));
      expect(call).toBeTruthy();
      const body = JSON.parse(call!.body ?? '{}');
      expect(body.params).toMatchObject({ profileKey: 'photo-siglip2-v2', dryRun: true });
    });
  });

  it('switches the action back to Esegui when dry-run is unchecked', async () => {
    installFetchMock({ ...CATALOG, 'GET /api/admin/jobs': () => page([]) });
    renderPage();
    const user = userEvent.setup();
    await screen.findByText('Prepara i video (HLS)');
    const c = card('Prepara i video (HLS)');

    await user.click(within(c).getByRole('button', { name: /Parametri/ }));
    expect(within(c).getByRole('button', { name: 'Simula' })).toBeInTheDocument();
    await user.click(within(c).getByLabelText(/Simulazione/));
    expect(within(c).getByRole('button', { name: 'Esegui' })).toBeInTheDocument();
  });

  it('enqueues a command with its parameters and shows feedback', async () => {
    const mock = installFetchMock({
      ...CATALOG,
      'GET /api/admin/jobs': () => page([]),
      'POST /api/admin/jobs/enqueue': () =>
        jsonResponse({ jobId: 'j-9', jobType: 'media.video.hls.backfill' }),
    });
    renderPage();
    const user = userEvent.setup();

    await screen.findByText('Prepara i video (HLS)');
    const c = card('Prepara i video (HLS)');
    // Open the params, set a limit, run.
    await user.click(within(c).getByRole('button', { name: /Parametri/ }));
    await user.type(within(c).getByLabelText('Limite'), '5');
    // Dry-run is preselected → the action is labelled "Simula".
    await user.click(within(c).getByRole('button', { name: 'Simula' }));

    await waitFor(() => {
      const call = mock.calls.find((x) => x.method === 'POST' && x.url.includes('/api/admin/jobs/enqueue'));
      expect(call).toBeTruthy();
      const body = JSON.parse(call!.body ?? '{}');
      expect(body.command).toBe('media-video-hls-backfill');
      // dry-run defaults ON; the limit we typed is sent; danger force stays false.
      expect(body.params).toMatchObject({ limit: 5, dryRun: true, force: false, retryFailed: false });
    });
    expect(await within(c).findByText(/Accodato ✓/)).toBeInTheDocument();
  });

  it('requires confirmation before a destructive flag is enqueued', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false);
    const mock = installFetchMock({
      ...CATALOG,
      'GET /api/admin/jobs': () => page([]),
      'POST /api/admin/jobs/enqueue': () =>
        jsonResponse({ jobId: 'j-1', jobType: 'media.video.hls.backfill' }),
    });
    renderPage();
    const user = userEvent.setup();

    await screen.findByText('Prepara i video (HLS)');
    const c = card('Prepara i video (HLS)');
    await user.click(within(c).getByRole('button', { name: /Parametri/ }));
    await user.click(within(c).getByLabelText(/Forza/));
    await user.click(within(c).getByRole('button', { name: 'Simula' }));

    // Confirm declined → no enqueue call.
    expect(confirmSpy).toHaveBeenCalled();
    expect(mock.calls.some((x) => x.method === 'POST' && x.url.includes('/enqueue'))).toBe(false);
  });

  it('keeps Run disabled until a required parameter is provided', async () => {
    installFetchMock({ ...CATALOG, 'GET /api/admin/jobs': () => page([]) });
    renderPage();
    const user = userEvent.setup();

    await screen.findByText('Prepara un singolo video (HLS)');
    const c = card('Prepara un singolo video (HLS)');
    expect(within(c).getByRole('button', { name: 'Esegui' })).toBeDisabled();

    await user.click(within(c).getByRole('button', { name: /Parametri/ }));
    await user.type(within(c).getByLabelText(/ID blob/), '11111111-1111-1111-1111-111111111111');
    expect(within(c).getByRole('button', { name: 'Esegui' })).toBeEnabled();
  });
});
