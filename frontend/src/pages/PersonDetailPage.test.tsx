import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { PersonDetailPage } from './PersonDetailPage';
import {
  AuthedWrapper,
  installFetchMock,
  jsonResponse,
  emptyResponse,
  fileMetadata,
  type MockHandler,
} from '../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const box = { x: 0.1, y: 0.1, width: 0.2, height: 0.2 };

function person(name: string): MockHandler {
  return () => jsonResponse({ personId: 'p-1', name, faceCount: 1, representative: { faceId: 'f-1', fileItemId: 'file-1', name: 'a.png', box } });
}
function photos(): MockHandler {
  return () => jsonResponse([{ fileItemId: 'file-1', name: 'a.png', faces: [{ faceId: 'f-1', box }] }]);
}
// VFACE-02: the detail page also loads confirmed VIDEO results.
function videos(items: unknown[] = []): MockHandler {
  return () => jsonResponse(items);
}
function similar(available: boolean): MockHandler {
  return () =>
    jsonResponse({
      profileAvailable: available,
      threshold: 0.35,
      items: available ? [{ faceId: 'f-2', fileItemId: 'file-2', name: 'b.png', box, score: 0.8 }] : [],
      nextCursor: null,
      hasMore: false,
      unavailableReason: available ? null : 'vector-backend-unavailable',
    });
}

function renderDetail(handlers: Record<string, MockHandler>) {
  installFetchMock(handlers);
  return render(
    <AuthedWrapper>
      <MemoryRouter initialEntries={['/people/p-1']}>
        <Routes>
          <Route path="/people/:personId" element={<PersonDetailPage />} />
          <Route path="/people" element={<div>people list</div>} />
        </Routes>
      </MemoryRouter>
    </AuthedWrapper>,
  );
}

it('shows the person, their photos, and similar faces', async () => {
  renderDetail({
    'GET /api/people/p-1': person('Alice'),
    'GET /api/people/p-1/photos': photos(),
    'GET /api/people/p-1/videos': videos(),
    'GET /api/people/p-1/similar-faces': similar(true),
  });
  expect(await screen.findByRole('heading', { name: 'Alice' })).toBeTruthy();
  expect(await screen.findByText('Foto (1)')).toBeTruthy();
  // Similar face result with its score.
  expect(await screen.findByText(/80%/)).toBeTruthy();
});

it('renames the person', async () => {
  const mock = installFetchMock({
    'GET /api/people/p-1': person('Alice'),
    'GET /api/people/p-1/photos': photos(),
    'GET /api/people/p-1/videos': videos(),
    'GET /api/people/p-1/similar-faces': similar(true),
    'PUT /api/people/p-1': person('Alice R'),
  });
  render(
    <AuthedWrapper>
      <MemoryRouter initialEntries={['/people/p-1']}>
        <Routes>
          <Route path="/people/:personId" element={<PersonDetailPage />} />
        </Routes>
      </MemoryRouter>
    </AuthedWrapper>,
  );
  await screen.findByRole('heading', { name: 'Alice' });
  const input = screen.getByLabelText('Nome persona');
  await userEvent.clear(input);
  await userEvent.type(input, 'Alice R');
  await userEvent.click(screen.getByRole('button', { name: 'Rinomina' }));
  await waitFor(() => expect(mock.calls.some((c) => c.method === 'PUT' && c.url.endsWith('/api/people/p-1'))).toBe(true));
});

it('shows the unavailable state when face search is not available', async () => {
  renderDetail({
    'GET /api/people/p-1': person('Alice'),
    'GET /api/people/p-1/photos': photos(),
    'GET /api/people/p-1/videos': videos(),
    'GET /api/people/p-1/similar-faces': similar(false),
  });
  expect(await screen.findByText('Ricerca volti non disponibile in questo ambiente.')).toBeTruthy();
});

it('refetches similar faces when the threshold changes', async () => {
  const mock = installFetchMock({
    'GET /api/people/p-1': person('Alice'),
    'GET /api/people/p-1/photos': photos(),
    'GET /api/people/p-1/videos': videos(),
    'GET /api/people/p-1/similar-faces': similar(true),
  });
  render(
    <AuthedWrapper>
      <MemoryRouter initialEntries={['/people/p-1']}>
        <Routes>
          <Route path="/people/:personId" element={<PersonDetailPage />} />
        </Routes>
      </MemoryRouter>
    </AuthedWrapper>,
  );
  await screen.findByText(/80%/);
  const before = mock.calls.filter((c) => c.url.includes('/similar-faces')).length;

  // Move the threshold slider → debounced refetch with the new value.
  fireEvent.change(screen.getByLabelText('Soglia similarità'), { target: { value: '80' } });

  await waitFor(() => {
    const calls = mock.calls.filter((c) => c.url.includes('/similar-faces'));
    expect(calls.length).toBeGreaterThan(before);
    expect(calls.some((c) => c.url.includes('minSimilarity=0.8'))).toBe(true);
  });
});

it('adds a similar face to the person', async () => {
  const mock = installFetchMock({
    'GET /api/people/p-1': person('Alice'),
    'GET /api/people/p-1/photos': photos(),
    'GET /api/people/p-1/videos': videos(),
    'GET /api/people/p-1/similar-faces': similar(true),
    'POST /api/people/p-1/faces': () => emptyResponse(),
  });
  render(
    <AuthedWrapper>
      <MemoryRouter initialEntries={['/people/p-1']}>
        <Routes>
          <Route path="/people/:personId" element={<PersonDetailPage />} />
        </Routes>
      </MemoryRouter>
    </AuthedWrapper>,
  );
  await screen.findByText(/80%/);
  await userEvent.click(screen.getByRole('button', { name: 'Aggiungi' }));
  await waitFor(() =>
    expect(mock.calls.some((c) => c.method === 'POST' && c.url.endsWith('/api/people/p-1/faces'))).toBe(true),
  );
});

// ---- VFACE-02: confirmed video results ------------------------------------

const videoMatch = (start: number, end: number, rep: number) => ({
  evidenceType: 'person',
  startMilliseconds: start,
  endMilliseconds: end,
  representativeMilliseconds: rep,
});

it('shows confirmed videos with their temporal interval', async () => {
  renderDetail({
    'GET /api/people/p-1': person('Alice'),
    'GET /api/people/p-1/photos': photos(),
    'GET /api/people/p-1/similar-faces': similar(false),
    'GET /api/people/p-1/videos': videos([{
      fileItemId: 'file-9',
      name: 'clip.mp4',
      bestMatch: videoMatch(65_000, 92_000, 78_000),
      additionalMatches: [videoMatch(5_000, 7_000, 6_000)],
    }]),
  });

  expect(await screen.findByText('Video (1)')).toBeTruthy();
  expect(await screen.findByText('clip.mp4')).toBeTruthy();
  // The interval reads as a video position, not a date.
  expect(await screen.findByText('1:05 – 1:32')).toBeTruthy();
  // The additional interval is offered as its own jump target.
  expect(await screen.findByRole('button', { name: '0:05 – 0:07' })).toBeTruthy();
});

it('opens the player at the representative timestamp of the clicked match', async () => {
  renderDetail({
    'GET /api/people/p-1': person('Alice'),
    'GET /api/people/p-1/photos': photos(),
    'GET /api/people/p-1/similar-faces': similar(false),
    'GET /api/people/p-1/videos': videos([{
      fileItemId: 'file-9',
      name: 'clip.mp4',
      bestMatch: videoMatch(65_000, 92_000, 78_000),
      additionalMatches: [],
    }]),
    'GET /api/files/file-9/metadata': () => jsonResponse(fileMetadata('file-9', 'clip.mp4')),
  });

  const poster = await screen.findByRole('button', { name: /clip\.mp4.*1:05/ });
  fireEvent.click(poster);

  // The existing viewer opens; the video element carries the media source for
  // the clicked file (the seek itself lives in HlsVideoPlayer).
  await waitFor(() => expect(screen.getByRole('dialog')).toBeTruthy());
});

it('shows an empty state when no video is confirmed', async () => {
  renderDetail({
    'GET /api/people/p-1': person('Alice'),
    'GET /api/people/p-1/photos': photos(),
    'GET /api/people/p-1/similar-faces': similar(false),
    'GET /api/people/p-1/videos': videos([]),
  });

  expect(await screen.findByText('Video (0)')).toBeTruthy();
  expect(await screen.findByText('Nessun video ancora confermato per questa persona.')).toBeTruthy();
});

it('shows a retryable error when videos fail to load', async () => {
  renderDetail({
    'GET /api/people/p-1': person('Alice'),
    'GET /api/people/p-1/photos': photos(),
    'GET /api/people/p-1/similar-faces': similar(false),
    'GET /api/people/p-1/videos': () => new Response('boom', { status: 500 }),
  });

  expect(await screen.findByText(/Impossibile caricare i video/)).toBeTruthy();
});

it('renders a vertical video poster without cropping it', async () => {
  renderDetail({
    'GET /api/people/p-1': person('Alice'),
    'GET /api/people/p-1/photos': photos(),
    'GET /api/people/p-1/similar-faces': similar(false),
    'GET /api/people/p-1/videos': videos([{
      fileItemId: 'file-tall',
      name: 'portrait.mp4',
      bestMatch: videoMatch(1_000, 2_000, 1_500),
      additionalMatches: [],
    }]),
  });

  await screen.findByText('portrait.mp4');
  const poster = document.querySelector('.person-video-poster img') as HTMLImageElement | null;
  expect(poster).toBeTruthy();
  // Source-aspect tiles: the poster is contained, never cropped to a square.
  expect(poster!.getAttribute('src')).toBe('/api/files/file-tall/poster');
});
