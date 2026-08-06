import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { VideoFaceReviewTab } from './VideoFaceReviewTab';
import {
  AuthedWrapper,
  installFetchMock,
  emptyResponse,
  jsonResponse,
  fileMetadata,
  type MockHandler,
} from '../../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

// VFACE-02: the review queue. The behaviour under test is the CONTRACT — the
// model suggests, the owner decides, and a decided track leaves the queue.

const track = (id: string, name = 'clip.mp4') => ({
  trackId: id,
  fileItemId: `file-${id}`,
  name,
  startMilliseconds: 65_000,
  endMilliseconds: 92_000,
  representativeMilliseconds: 78_000,
  detectionCount: 12,
  qualityScore: 0.62,
});

function queue(items: unknown[]): MockHandler {
  return () => jsonResponse({ items, hasMore: false });
}

function suggestions(items: unknown[], unavailableReason: string | null = null): MockHandler {
  return () => jsonResponse({ threshold: 0.4, items, unavailableReason });
}

function renderTab(handlers: Record<string, MockHandler>) {
  const mock = installFetchMock(handlers);
  render(
    <AuthedWrapper>
      <VideoFaceReviewTab invalidateAuth={() => {}} />
    </AuthedWrapper>,
  );
  return mock;
}

it('lists undecided tracks with their video name and interval', async () => {
  renderTab({
    'GET /api/people/video-tracks/undecided': queue([track('t-1')]),
    'GET /api/people/video-tracks/t-1/suggestions': suggestions([]),
    'GET /api/people': () => jsonResponse([]),
  });

  expect(await screen.findByText('clip.mp4')).toBeTruthy();
  expect(await screen.findByText('1:05 – 1:32')).toBeTruthy();
});

it('shows a bounded suggestion as a similarity, not a certainty', async () => {
  renderTab({
    'GET /api/people/video-tracks/undecided': queue([track('t-1')]),
    'GET /api/people/video-tracks/t-1/suggestions': suggestions([
      { personId: 'p-1', name: 'Alice', similarity: 0.82, supportingEvidenceCount: 4 },
    ]),
    'GET /api/people': () => jsonResponse([]),
  });

  expect(await screen.findByRole('button', { name: 'Conferma Alice (82% simile)' })).toBeTruthy();
});

it('assigns the track when a suggestion is confirmed and drops it from the queue', async () => {
  const mock = renderTab({
    'GET /api/people/video-tracks/undecided': queue([track('t-1'), track('t-2', 'other.mp4')]),
    'GET /api/people/video-tracks/t-1/suggestions': suggestions([
      { personId: 'p-1', name: 'Alice', similarity: 0.9, supportingEvidenceCount: 2 },
    ]),
    'GET /api/people/video-tracks/t-2/suggestions': suggestions([]),
    'POST /api/people/video-tracks/t-1/assign': () => emptyResponse(204),
    'GET /api/people': () => jsonResponse([]),
  });

  fireEvent.click(await screen.findByRole('button', { name: /Conferma Alice/ }));

  await waitFor(() => expect(screen.queryByText('clip.mp4')).toBeNull());
  expect(screen.getByText('other.mp4')).toBeTruthy();

  const assign = mock.calls.find((c) => c.url.includes('/t-1/assign'));
  expect(assign).toBeTruthy();
  expect(JSON.parse(assign!.body ?? '{}')).toEqual({ personId: 'p-1' });
});

it('assigns to any existing person chosen from the list', async () => {
  const mock = renderTab({
    'GET /api/people/video-tracks/undecided': queue([track('t-1')]),
    'GET /api/people/video-tracks/t-1/suggestions': suggestions([]),
    'POST /api/people/video-tracks/t-1/assign': () => emptyResponse(204),
    'GET /api/people': () => jsonResponse([
      { personId: 'p-7', name: 'Bob', faceCount: 3, representative: null },
    ]),
  });

  const select = await screen.findByLabelText('Assegna questo volto a una persona');
  fireEvent.change(select, { target: { value: 'p-7' } });

  await waitFor(() => expect(screen.queryByText('clip.mp4')).toBeNull());
  expect(JSON.parse(
    mock.calls.find((c) => c.url.includes('/assign'))!.body ?? '{}',
  )).toEqual({ personId: 'p-7' });
});

it('ignores a track and drops it from the queue', async () => {
  const mock = renderTab({
    'GET /api/people/video-tracks/undecided': queue([track('t-1')]),
    'GET /api/people/video-tracks/t-1/suggestions': suggestions([]),
    'POST /api/people/video-tracks/t-1/ignore': () => emptyResponse(204),
    'GET /api/people': () => jsonResponse([]),
  });

  fireEvent.click(await screen.findByRole('button', { name: 'Ignora' }));

  await waitFor(() => expect(screen.queryByText('clip.mp4')).toBeNull());
  expect(mock.calls.some((c) => c.url.includes('/t-1/ignore'))).toBe(true);
});

it('never offers to create a new person from a track', async () => {
  renderTab({
    'GET /api/people/video-tracks/undecided': queue([track('t-1')]),
    'GET /api/people/video-tracks/t-1/suggestions': suggestions([]),
    'GET /api/people': () => jsonResponse([]),
  });

  await screen.findByText('clip.mp4');
  // No free-text name field exists here: naming people stays in the People flows.
  expect(screen.queryByRole('textbox')).toBeNull();
});

it('opens the player at the track timestamp', async () => {
  renderTab({
    'GET /api/people/video-tracks/undecided': queue([track('t-1')]),
    'GET /api/people/video-tracks/t-1/suggestions': suggestions([]),
    'GET /api/people': () => jsonResponse([]),
    'GET /api/files/file-t-1/metadata': () => jsonResponse(fileMetadata('file-t-1', 'clip.mp4')),
  });

  fireEvent.click(await screen.findByRole('button', { name: /clip\.mp4.*1:05/ }));

  await waitFor(() => expect(screen.getByRole('dialog')).toBeTruthy());
});

it('shows the loading, empty and error states', async () => {
  renderTab({
    'GET /api/people/video-tracks/undecided': queue([]),
    'GET /api/people': () => jsonResponse([]),
  });
  expect(await screen.findByText('Non c’è più nulla da rivedere.')).toBeTruthy();

  cleanup();
  vi.unstubAllGlobals();

  renderTab({
    'GET /api/people/video-tracks/undecided': () => new Response('boom', { status: 500 }),
    'GET /api/people': () => jsonResponse([]),
  });
  expect(await screen.findByText(/Impossibile caricare i volti dai video/)).toBeTruthy();
});

it('reports unavailable suggestions without blocking the decision', async () => {
  renderTab({
    'GET /api/people/video-tracks/undecided': queue([track('t-1')]),
    'GET /api/people/video-tracks/t-1/suggestions': () => new Response('boom', { status: 500 }),
    'POST /api/people/video-tracks/t-1/ignore': () => emptyResponse(204),
    'GET /api/people': () => jsonResponse([]),
  });

  expect(await screen.findByText('Suggerimenti non disponibili.')).toBeTruthy();
  // The owner can still decide.
  expect(screen.getByRole('button', { name: 'Ignora' })).toBeTruthy();
});
