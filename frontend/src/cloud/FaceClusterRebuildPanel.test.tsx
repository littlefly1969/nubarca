import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FaceClusterRebuildPanel } from './FaceClusterRebuildPanel';
import { AuthedWrapper, installFetchMock, jsonResponse, type MockHandler } from '../test-utils';

// "Ricalcola cluster volti" from the owner's side.
//
// Two things this panel must never do, and both are asserted rather than
// assumed: start the run without asking (it replaces a whole derived layer of
// the account's People and can take a while), and report an outcome the server
// has not reported — a status read that fails is not a failed clustering.

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const JOB = '11111111-2222-3333-4444-555555555555';

function status(state: string, extra: Record<string, unknown> = {}): MockHandler {
  return () =>
    jsonResponse({
      jobId: JOB,
      status: state,
      progressCurrent: null,
      progressTotal: null,
      progressMessage: null,
      createdAt: '2026-08-11T10:00:00Z',
      completedAt: null,
      lastErrorCode: null,
      ...extra,
    });
}

function renderPanel(handlers: Record<string, MockHandler>) {
  const mock = installFetchMock(handlers);
  render(<AuthedWrapper><FaceClusterRebuildPanel /></AuthedWrapper>);
  return mock;
}

describe('FaceClusterRebuildPanel', () => {
  it('explains what is rebuilt and what is preserved before anything happens', () => {
    const mock = renderPanel({});

    expect(screen.getByText(/Suggeriti/)).toBeTruthy();
    expect(screen.getByText(/non vengono modificati/)).toBeTruthy();
    expect(screen.getByTestId('face-cluster-start')).toBeTruthy();
    // Rendering the panel starts nothing.
    expect(mock.calls).toHaveLength(0);
  });

  it('asks for confirmation, and cancelling starts nothing', async () => {
    const mock = renderPanel({});

    await userEvent.click(screen.getByTestId('face-cluster-start'));
    expect(await screen.findByTestId('face-cluster-confirm')).toBeTruthy();
    expect(screen.getByText('Ricalcolare il cluster dei volti?')).toBeTruthy();

    await userEvent.click(screen.getByTestId('face-cluster-confirm-cancel'));

    await waitFor(() => expect(screen.queryByTestId('face-cluster-confirm')).toBeNull());
    expect(mock.calls.some((c) => c.method === 'POST')).toBe(false);
  });

  it('enqueues on confirmation and walks queued → running → done', async () => {
    let state = 'queued';
    const mock = renderPanel({
      'POST /api/people/cluster-rebuild': () =>
        jsonResponse({ jobId: JOB, status: 'queued', alreadyQueued: false }),
      [`GET /api/people/cluster-rebuild/${JOB}`]: (req) => status(state)(req),
    });

    await userEvent.click(screen.getByTestId('face-cluster-start'));
    await userEvent.click(await screen.findByTestId('face-cluster-confirm-run'));

    await waitFor(() =>
      expect(mock.calls.some((c) => c.method === 'POST' && c.url.endsWith('/api/people/cluster-rebuild')))
        .toBe(true),
    );
    expect(screen.getByTestId('face-cluster-status').textContent).toBe('In coda…');

    state = 'running';
    await waitFor(
      () => expect(screen.getByTestId('face-cluster-status').textContent).toBe('Ricalcolo in corso…'),
      { timeout: 8000 },
    );

    state = 'succeeded';
    await waitFor(
      () => expect(screen.getByTestId('face-cluster-status').textContent).toBe('Cluster ricalcolato.'),
      { timeout: 8000 },
    );
  }, 20000);

  it('reports a failed run with its safe error code, never a stack trace', async () => {
    renderPanel({
      'POST /api/people/cluster-rebuild': () =>
        jsonResponse({ jobId: JOB, status: 'queued', alreadyQueued: false }),
      [`GET /api/people/cluster-rebuild/${JOB}`]: status('failed', { lastErrorCode: 'TimeoutException' }),
    });

    await userEvent.click(screen.getByTestId('face-cluster-start'));
    await userEvent.click(await screen.findByTestId('face-cluster-confirm-run'));

    await waitFor(
      () => expect(screen.getByTestId('face-cluster-status').textContent)
        .toBe('Ricalcolo non completato (TimeoutException).'),
      { timeout: 8000 },
    );
  }, 20000);

  it('says so when the installation cannot cluster at all', async () => {
    renderPanel({
      'POST /api/people/cluster-rebuild': () =>
        new Response(JSON.stringify({ error: 'face-clustering-unavailable', reason: 'no-default-profile' }), {
          status: 409,
          headers: { 'content-type': 'application/json' },
        }),
    });

    await userEvent.click(screen.getByTestId('face-cluster-start'));
    await userEvent.click(await screen.findByTestId('face-cluster-confirm-run'));

    await waitFor(() =>
      expect(screen.getByTestId('face-cluster-status').textContent)
        .toBe('Il ricalcolo dei volti non è disponibile in questo ambiente.'),
    );
  });

  it('joins a run already in flight rather than starting a second one', async () => {
    const mock = renderPanel({
      'POST /api/people/cluster-rebuild': () =>
        jsonResponse({ jobId: JOB, status: 'running', alreadyQueued: true }),
      [`GET /api/people/cluster-rebuild/${JOB}`]: status('running'),
    });

    await userEvent.click(screen.getByTestId('face-cluster-start'));
    await userEvent.click(await screen.findByTestId('face-cluster-confirm-run'));

    await waitFor(() =>
      expect(screen.getByTestId('face-cluster-status').textContent).toBe('Ricalcolo in corso…'),
    );
    // One POST, whatever the server decided to do with it.
    expect(mock.calls.filter((c) => c.method === 'POST')).toHaveLength(1);
    // And the button is not offering a second run while this one is live.
    expect((screen.getByTestId('face-cluster-start') as HTMLButtonElement).disabled).toBe(true);
  });
});
