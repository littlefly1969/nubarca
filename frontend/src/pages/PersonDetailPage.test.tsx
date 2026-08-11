import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
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
      items: available
        ? [{ faceId: 'f-2', fileItemId: 'file-2', name: 'b.png', box, score: 0.8, assignedPersonId: null, assignedPersonName: null }]
        : [],
      nextCursor: null,
      hasMore: false,
      unavailableReason: available ? null : 'vector-backend-unavailable',
    });
}

// A proposal that already belongs to another person: kept on purpose, so it must
// be labelled and offer a MOVE rather than a plain add.
function similarAssignedElsewhere(): MockHandler {
  return () =>
    jsonResponse({
      profileAvailable: true,
      threshold: 0.35,
      items: [
        { faceId: 'f-2', fileItemId: 'file-2', name: 'b.png', box, score: 0.8, assignedPersonId: null, assignedPersonName: null },
        { faceId: 'f-3', fileItemId: 'file-3', name: 'c.png', box, score: 0.7, assignedPersonId: 'p-2', assignedPersonName: 'Maria' },
      ],
      nextCursor: null,
      hasMore: false,
      unavailableReason: null,
    });
}

// The persisted reference template (person_face_references), in slot order.
function referenceFaceRows(count: number) {
  return Array.from({ length: count }, (_, i) => ({
    faceId: `ref-${i}`,
    fileItemId: `file-ref-${i}`,
    name: `ref${i}.png`,
    box,
    ordinal: i,
  }));
}
function referenceFaces(count: number): MockHandler {
  return () => jsonResponse(referenceFaceRows(count));
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

// The page is ordered for the frequent job: see the template, run the search,
// judge the suggestions — without scrolling past the collection the person
// already has. Asserted on real DOM position, so a CSS `order:` trick (which
// leaves keyboard and screen-reader traversal in the old sequence) would fail.
it('puts reference faces and the similar-face search before the assigned faces', async () => {
  renderDetail({
    'GET /api/people/p-1': person('Alice'),
    'GET /api/people/p-1/photos': photos(),
    'GET /api/people/p-1/videos': videos(),
    'GET /api/people/p-1/reference-faces': referenceFaces(4),
    'GET /api/people/p-1/similar-faces': similar(true),
  });

  const references = await screen.findByText('Volti di riferimento · 4/6');
  const search = await screen.findByRole('heading', { name: 'Cerca volti simili' });
  const suggestion = await screen.findByText(/80%/);
  const assigned = await screen.findByText('Foto (1)');

  const precedes = (a: Node, b: Node) =>
    Boolean(a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING);

  expect(precedes(references, search)).toBe(true);
  expect(precedes(search, suggestion)).toBe(true);
  expect(precedes(suggestion, assigned)).toBe(true);
});

// ---- reference faces panel -------------------------------------------------

it('shows the persisted reference faces with their count and slot order', async () => {
  renderDetail({
    'GET /api/people/p-1': person('Alice'),
    'GET /api/people/p-1/photos': photos(),
    'GET /api/people/p-1/videos': videos(),
    'GET /api/people/p-1/reference-faces': referenceFaces(4),
    'GET /api/people/p-1/similar-faces': similar(true),
  });

  expect(await screen.findByText('Volti di riferimento · 4/6')).toBeTruthy();
  const panel = screen.getByLabelText('Volti di riferimento');
  // One thumbnail per persisted slot, numbered from ordinal + 1, in order.
  const slots = within(panel).getAllByText(/^#\d$/).map((el) => el.textContent);
  expect(slots).toEqual(['#1', '#2', '#3', '#4']);
});

// ---- a correction moves the WHOLE page ------------------------------------
//
// Removing / moving / ignoring a face changes what this person IS: the backend
// reselects the reference template from what is left, and the suggestions come
// from that template. Reloading only the photos left "6/6" on screen next to a
// reference the owner had just disowned, and kept offering matches computed
// from evidence that no longer existed.

it('refetches the references and the suggestions after a face is removed', async () => {
  let referenceCount = 4;
  const mock = installFetchMock({
    'GET /api/people/p-1': person('Alice'),
    'GET /api/people/p-1/photos': photos(),
    'GET /api/people/p-1/videos': videos(),
    'GET /api/people/p-1/reference-faces': () => jsonResponse(referenceFaceRows(referenceCount)),
    'GET /api/people/p-1/similar-faces': similar(true),
    'DELETE /api/people/p-1/faces/f-1': () => { referenceCount = 3; return emptyResponse(); },
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

  expect(await screen.findByText('Volti di riferimento · 4/6')).toBeTruthy();
  const similarBefore = mock.calls.filter((c) => c.url.includes('/similar-faces')).length;

  await userEvent.click(await screen.findByRole('button', { name: 'Rimuovi volto' }));

  // The panel adopts the rebuilt set — it does not stay on the old count until
  // the page is reloaded.
  expect(await screen.findByText('Volti di riferimento · 3/6')).toBeTruthy();
  // …and the suggestions are asked for again, because the template changed.
  await waitFor(() =>
    expect(mock.calls.filter((c) => c.url.includes('/similar-faces')).length)
      .toBeGreaterThan(similarBefore),
  );
  // No full page reload was needed to see any of it.
  expect(screen.getByRole('heading', { name: 'Alice' })).toBeTruthy();
});

it('recomputes the reference set on demand and adopts the new one', async () => {
  let referenceCount = 6;
  const mock = installFetchMock({
    'GET /api/people/p-1': person('Alice'),
    'GET /api/people/p-1/photos': photos(),
    'GET /api/people/p-1/videos': videos(),
    'GET /api/people/p-1/reference-faces': () => jsonResponse(referenceFaceRows(referenceCount)),
    'GET /api/people/p-1/similar-faces': similar(true),
    // The selector decides four references are enough: 4/6 is a correct answer,
    // not a failure to reach six.
    'POST /api/people/p-1/reference-faces/rebuild': () => {
      referenceCount = 4;
      return jsonResponse(referenceFaceRows(4));
    },
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

  expect(await screen.findByText('Volti di riferimento · 6/6')).toBeTruthy();
  const similarBefore = mock.calls.filter((c) => c.url.includes('/similar-faces')).length;

  await userEvent.click(screen.getByRole('button', { name: 'Ricalcola riferimenti' }));

  await waitFor(() =>
    expect(mock.calls.some((c) =>
      c.method === 'POST' && c.url.endsWith('/api/people/p-1/reference-faces/rebuild'))).toBe(true),
  );
  expect(await screen.findByText('Volti di riferimento · 4/6')).toBeTruthy();
  await waitFor(() =>
    expect(mock.calls.filter((c) => c.url.includes('/similar-faces')).length)
      .toBeGreaterThan(similarBefore),
  );
});

it('marks the recompute action busy while the request is in flight', async () => {
  let release: (() => void) | null = null;
  const pending = new Promise<void>((resolve) => { release = resolve; });
  installFetchMock({
    'GET /api/people/p-1': person('Alice'),
    'GET /api/people/p-1/photos': photos(),
    'GET /api/people/p-1/videos': videos(),
    'GET /api/people/p-1/reference-faces': referenceFaces(6),
    'GET /api/people/p-1/similar-faces': similar(true),
    'POST /api/people/p-1/reference-faces/rebuild': async () => {
      await pending;
      return jsonResponse(referenceFaceRows(4));
    },
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

  await screen.findByText('Volti di riferimento · 6/6');
  await userEvent.click(screen.getByRole('button', { name: 'Ricalcola riferimenti' }));

  const busy = await screen.findByRole('button', { name: 'Ricalcolo…' });
  expect((busy as HTMLButtonElement).disabled).toBe(true);

  release!();
  expect(await screen.findByRole('button', { name: 'Ricalcola riferimenti' })).toBeTruthy();
});

it('explains the empty reference set instead of erroring, and does not search', async () => {
  const mock = installFetchMock({
    'GET /api/people/p-1': person('Alice'),
    'GET /api/people/p-1/photos': photos(),
    'GET /api/people/p-1/videos': videos(),
    'GET /api/people/p-1/reference-faces': referenceFaces(0),
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

  expect(await screen.findByText('Volti di riferimento · 0/6')).toBeTruthy();
  expect(screen.getByText(/Volti di riferimento non ancora generati/)).toBeTruthy();
  expect(screen.getByText(/Verranno scelti alla prima ricerca di volti simili/)).toBeTruthy();
  // No error surface, and the panel itself never triggers a reference build.
  expect(screen.queryByRole('alert')).toBeNull();
  const refCalls = mock.calls.filter((c) => c.url.includes('/reference-faces'));
  expect(refCalls.every((c) => c.method === 'GET')).toBe(true);
});

it('refetches the reference faces after a successful similar-face search', async () => {
  let refCall = 0;
  const mock = installFetchMock({
    'GET /api/people/p-1': person('Alice'),
    'GET /api/people/p-1/photos': photos(),
    'GET /api/people/p-1/videos': videos(),
    // First read is pre-bootstrap; the search builds the set, so the next read
    // sees it — the panel must go from 0/6 to 5/6 with no page refresh.
    'GET /api/people/p-1/reference-faces': () => {
      refCall += 1;
      return jsonResponse(referenceFaceRows(refCall === 1 ? 0 : 5));
    },
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

  expect(await screen.findByText('Volti di riferimento · 5/6')).toBeTruthy();
  expect(mock.calls.filter((c) => c.url.includes('/reference-faces')).length).toBeGreaterThan(1);
});

it('labels a proposal already assigned to another person and offers a move', async () => {
  const mock = installFetchMock({
    'GET /api/people/p-1': person('Alice'),
    'GET /api/people/p-1/photos': photos(),
    'GET /api/people/p-1/videos': videos(),
    'GET /api/people/p-1/similar-faces': similarAssignedElsewhere(),
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

  // The assigned candidate names its current person…
  expect(await screen.findByText('Già assegnato a: Maria')).toBeTruthy();
  // …and its action says it MOVES the face, never a plain "Aggiungi".
  const move = screen.getByRole('button', { name: 'Sposta qui' });
  expect(move).toBeTruthy();
  // The free candidate keeps the ordinary add action.
  expect(screen.getByRole('button', { name: 'Aggiungi' })).toBeTruthy();

  // The move goes through the same one-person-per-face assign endpoint.
  await userEvent.click(move);
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
