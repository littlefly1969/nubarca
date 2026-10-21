import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AuthedWrapper, errorResponse, installFetchMock, jsonResponse } from '../test-utils';
import { MailRunPanel } from './MailRunPanel';
import { RUN_POLL_MS } from './useRunPolling';
import { archive, renderMail, run } from './mail.test.fixtures';

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

const SOURCE = {
  fileItemId: 'file-pst', name: 'casella.pst', sizeBytes: 271360,
  createdAt: '2026-10-01T08:00:00Z', alreadyImportedArchiveId: null,
};

describe('import management', () => {
  it('lists archives with counts and their latest run', async () => {
    installFetchMock({
      'GET /api/mail/archives': () => jsonResponse({ items: [archive()] }),
      'GET /api/mail/status': () => jsonResponse({ available: true, reason: 'ok' }),
      'GET /api/mail/sources': () => jsonResponse({ items: [] }),
    });
    renderMail('/mail?view=imports');

    const card = await screen.findByTestId('mail-archive');
    expect(within(card).getByRole('heading', { name: 'Ufficio 2019' })).toBeInTheDocument();
    expect(card).toHaveTextContent('messaggi');
    expect(card).toHaveTextContent('ufficio.pst');
    expect(within(card).getByTestId('mail-run')).toHaveAttribute('data-status', 'succeeded');
    expect(within(card).getByTestId('mail-run')).toHaveTextContent('Completata');
    expect(within(card).getByTestId('mail-archive-resume')).toHaveTextContent('Reimporta');
    // No sources: the owner is told to upload first, with a way to Files.
    expect(await screen.findByTestId('mail-sources-empty')).toHaveTextContent('Carica prima il .pst');
    expect(within(screen.getByTestId('mail-sources-empty')).getByRole('link')).toHaveAttribute('href', '/');
  });

  it('starts an import from a source and follows the new run', async () => {
    const started = run({ id: 'run-new', archiveId: 'arc-new', status: 'queued', phase: null, processedMessages: 0, totalMessages: 0 });
    let archives = [] as ReturnType<typeof archive>[];
    const mock = installFetchMock({
      'GET /api/mail/archives': () => jsonResponse({ items: archives }),
      'GET /api/mail/status': () => jsonResponse({ available: true, reason: 'ok' }),
      'GET /api/mail/sources': () => jsonResponse({ items: [SOURCE] }),
      'POST /api/mail/archives': () => {
        archives = [archive({ id: 'arc-new', displayName: 'casella', latestRun: started })];
        return jsonResponse({ archive: archives[0], run: started }, 202);
      },
    });
    renderMail('/mail?view=imports');

    await userEvent.click(await screen.findByRole('button', { name: 'Importa casella.pst' }));

    const post = mock.calls.find((c) => c.method === 'POST' && c.url === '/api/mail/archives');
    expect(JSON.parse(post!.body!)).toEqual({ fileItemId: 'file-pst' });
    expect(await screen.findByText('Importazione di «casella» avviata.')).toBeInTheDocument();
    const panel = await screen.findByTestId('mail-run');
    expect(panel).toHaveAttribute('data-status', 'queued');
    expect(within(panel).getByTestId('mail-run-cancel')).toBeInTheDocument();
  });

  it('attaches to the running import on 409 import_running', async () => {
    const running = run({ id: 'run-busy', archiveId: 'arc-1' });
    const mock = installFetchMock({
      'GET /api/mail/archives': () => jsonResponse({ items: [archive({ latestRun: null })] }),
      'GET /api/mail/status': () => jsonResponse({ available: true, reason: 'ok' }),
      'GET /api/mail/sources': () => jsonResponse({ items: [{ ...SOURCE, alreadyImportedArchiveId: 'arc-1' }] }),
      'POST /api/mail/archives': () => errorResponse(409, { error: 'import_running', runId: 'run-busy' }),
      'GET /api/mail/runs/run-busy': () => jsonResponse(running),
    });
    renderMail('/mail?view=imports');

    expect(await screen.findByText(/già importato/)).toBeInTheDocument();
    await userEvent.click(await screen.findByRole('button', { name: 'Importa casella.pst' }));

    expect(await screen.findByText(/già in corso/)).toBeInTheDocument();
    expect(mock.calls.some((c) => c.url === '/api/mail/runs/run-busy')).toBe(true);
    const panel = await screen.findByTestId('mail-run');
    expect(panel).toHaveAttribute('data-status', 'running');
    expect(panel).toHaveTextContent('40 di 100 messaggi (40%)');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('explains why import is unavailable and offers no source', async () => {
    const mock = installFetchMock({
      'GET /api/mail/archives': () => jsonResponse({ items: [archive()] }),
      'GET /api/mail/status': () => jsonResponse({ available: false, reason: 'importer_missing' }),
    });
    renderMail('/mail?view=imports');

    expect(await screen.findByTestId('mail-import-unavailable')).toHaveTextContent('manca il programma');
    expect(mock.calls.some((c) => c.url === '/api/mail/sources')).toBe(false);
    expect(screen.getByTestId('mail-archive-resume')).toBeDisabled();
  });

  it('resumes an interrupted import', async () => {
    const cancelled = run({ status: 'cancelled', phase: null, completedAt: '2026-10-01T10:30:00Z' });
    const resumed = run({ id: 'run-2', status: 'queued', phase: null });
    const mock = installFetchMock({
      'GET /api/mail/archives': () => jsonResponse({ items: [archive({ latestRun: cancelled })] }),
      'GET /api/mail/status': () => jsonResponse({ available: true, reason: 'ok' }),
      'GET /api/mail/sources': () => jsonResponse({ items: [] }),
      'POST /api/mail/archives/arc-1/import': () => jsonResponse(resumed, 202),
    });
    renderMail('/mail?view=imports');

    const button = await screen.findByTestId('mail-archive-resume');
    await waitFor(() => expect(button).toBeEnabled());
    expect(button).toHaveTextContent('Riprendi');
    await userEvent.click(button);

    expect(mock.calls.some((c) => c.method === 'POST' && c.url === '/api/mail/archives/arc-1/import')).toBe(true);
    await waitFor(() => expect(screen.getByTestId('mail-run')).toHaveAttribute('data-status', 'queued'));
  });

  it('says clearly when the source PST is gone (409 source_unavailable)', async () => {
    installFetchMock({
      'GET /api/mail/archives': () => jsonResponse({ items: [archive({ latestRun: run({ status: 'failed', phase: null }) })] }),
      'GET /api/mail/status': () => jsonResponse({ available: true, reason: 'ok' }),
      'GET /api/mail/sources': () => jsonResponse({ items: [] }),
      'POST /api/mail/archives/arc-1/import': () => errorResponse(409, { error: 'source_unavailable' }),
    });
    renderMail('/mail?view=imports');

    const button = await screen.findByTestId('mail-archive-resume');
    await waitFor(() => expect(button).toBeEnabled());
    await userEvent.click(button);
    expect(await screen.findByRole('alert')).toHaveTextContent('non è più disponibile o è stato modificato');
  });
});

describe('run progress panel', () => {
  function renderPanel(initial = run(), onTerminal = vi.fn()) {
    return {
      onTerminal,
      ...render(<AuthedWrapper><MailRunPanel run={initial} onTerminal={onTerminal} /></AuthedWrapper>),
    };
  }

  async function advance(ms: number) {
    await act(async () => { await vi.advanceTimersByTimeAsync(ms); });
  }

  it('polls every ~2 s while running and stops at a terminal status', async () => {
    vi.useFakeTimers();
    let n = 0;
    const mock = installFetchMock({
      'GET /api/mail/runs/run-1': () => {
        n += 1;
        return jsonResponse(n < 2
          ? run({ processedMessages: 70 })
          : run({ status: 'succeeded', phase: null, processedMessages: 100, completedAt: '2026-10-01T11:00:00Z' }));
      },
    });
    const { onTerminal } = renderPanel();
    const polls = () => mock.calls.filter((c) => c.url === '/api/mail/runs/run-1').length;

    expect(polls()).toBe(0);
    await advance(RUN_POLL_MS);
    expect(polls()).toBe(1);
    expect(screen.getByTestId('mail-run')).toHaveTextContent('70 di 100');
    await advance(RUN_POLL_MS);
    expect(polls()).toBe(2);
    expect(screen.getByTestId('mail-run')).toHaveAttribute('data-status', 'succeeded');
    expect(onTerminal).toHaveBeenCalledTimes(1);

    await advance(RUN_POLL_MS * 5);
    expect(polls()).toBe(2);
    expect(screen.queryByTestId('mail-run-cancel')).not.toBeInTheDocument();
  });

  it('stops polling when unmounted', async () => {
    vi.useFakeTimers();
    const mock = installFetchMock({ 'GET /api/mail/runs/run-1': () => jsonResponse(run()) });
    const { unmount } = renderPanel();
    await advance(RUN_POLL_MS);
    expect(mock.calls.length).toBe(1);
    unmount();
    await advance(RUN_POLL_MS * 10);
    expect(mock.calls.length).toBe(1);
  });

  it('backs off after a failed poll', async () => {
    vi.useFakeTimers();
    let fail = true;
    const mock = installFetchMock({
      'GET /api/mail/runs/run-1': () => (fail ? errorResponse(500) : jsonResponse(run())),
    });
    renderPanel();
    await advance(RUN_POLL_MS);
    expect(mock.calls.length).toBe(1);
    expect(screen.getByText(/riprovo tra poco/)).toBeInTheDocument();
    fail = false;
    // The next attempt waits twice as long.
    await advance(RUN_POLL_MS);
    expect(mock.calls.length).toBe(1);
    await advance(RUN_POLL_MS);
    expect(mock.calls.length).toBe(2);
  });

  it('does not poll a run that is already finished', async () => {
    vi.useFakeTimers();
    const mock = installFetchMock({});
    renderPanel(run({ status: 'partial', phase: null }));
    await advance(RUN_POLL_MS * 3);
    expect(mock.calls.length).toBe(0);
    expect(screen.getByTestId('mail-run')).toHaveTextContent('Completata con errori');
  });

  it('cancels with "Interrompi"', async () => {
    const mock = installFetchMock({
      'POST /api/mail/runs/run-1/cancel': () => jsonResponse(run({ cancellationRequested: true }), 202),
      'GET /api/mail/runs/run-1': () => jsonResponse(run({ cancellationRequested: true })),
    });
    renderPanel();
    await userEvent.click(screen.getByTestId('mail-run-cancel'));
    expect(mock.calls.some((c) => c.method === 'POST' && c.url === '/api/mail/runs/run-1/cancel')).toBe(true);
    await waitFor(() => expect(screen.getByTestId('mail-run-cancel')).toBeDisabled());
    expect(screen.getByTestId('mail-run')).toHaveTextContent('interruzione richiesta');
  });

  it('lists the failures with folder path, position and code', async () => {
    installFetchMock({
      'GET /api/mail/runs/run-1/failures': () => jsonResponse({
        items: [{ folderPath: ['Top', 'Posta in arrivo'], position: 17, code: 'corrupt', message: null }],
        nextCursor: null,
      }),
    });
    renderPanel(run({ status: 'partial', phase: null, failedMessages: 1 }));
    await userEvent.click(screen.getByTestId('mail-run-failures-toggle'));
    const table = await screen.findByRole('table');
    expect(table).toHaveTextContent('Top / Posta in arrivo');
    expect(table).toHaveTextContent('17');
    expect(table).toHaveTextContent('corrupt');
  });

  it('shows an incomplete scan with the unreadable folders and that it will be repeated', () => {
    renderPanel(run({
      status: 'partial', phase: null, failedMessages: 0, errorCode: 'scan_errors',
      errorMessage: '3 elementi del PST non sono leggibili.', completedAt: '2026-10-01T11:00:00Z',
      scanErrors: 3,
      scanIssues: [
        { scope: 'folder', code: 'corrupt', folderPath: ['Top', 'Progetti'] },
        { scope: 'message', code: 'corrupt', folderPath: null },
      ],
    }));
    expect(screen.getByTestId('mail-run-scan-errors')).toHaveTextContent('3');
    const issues = screen.getByTestId('mail-run-scan-issues');
    expect(issues).toHaveTextContent('la prossima importazione la ripeterà');
    expect(issues).toHaveTextContent('Top / Progetti');
    expect(issues).toHaveTextContent('Messaggio non identificato');
    expect(issues).toHaveTextContent('e altri 1');
    expect(screen.queryByTestId('mail-run-failures-toggle')).toBeNull();
  });

  it('shows no scan section for an exhaustive scan', () => {
    renderPanel(run({ status: 'succeeded', phase: null, failedMessages: 0, completedAt: '2026-10-01T11:00:00Z' }));
    expect(screen.queryByTestId('mail-run-scan-errors')).toBeNull();
    expect(screen.queryByTestId('mail-run-scan-issues')).toBeNull();
  });
});
