import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { PeoplePage } from './PeoplePage';
import { AuthedWrapper, installFetchMock, jsonResponse, emptyResponse, type MockHandler } from '../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const rep = { faceId: 'face-1', fileItemId: 'file-1', name: 'a.png', box: { x: 0.1, y: 0.1, width: 0.2, height: 0.2 } };

function group(faceCount: number): MockHandler {
  return () =>
    jsonResponse([
      { groupId: 'g-1', representative: rep, faceCount, confidence: 0.9, status: 'suggested' },
    ]);
}

function renderPeople(isAdmin = false, handlers: Record<string, MockHandler> = {}) {
  installFetchMock({
    'GET /api/people/suggested-groups': group(3),
    'GET /api/people': () => jsonResponse([]),
    ...handlers,
  });
  return render(
    <AuthedWrapper isAdmin={isAdmin}>
      <MemoryRouter initialEntries={['/people']}>
        <Routes>
          <Route path="/people" element={<PeoplePage />} />
          <Route path="/people/:personId" element={<div>detail</div>} />
        </Routes>
      </MemoryRouter>
    </AuthedWrapper>,
  );
}

it('renders tabs and suggested groups', async () => {
  renderPeople();
  expect(await screen.findByRole('heading', { name: 'Volti' })).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Gruppi suggeriti' })).toBeTruthy();
  expect(await screen.findByText('3 volti')).toBeTruthy();
  expect(screen.getByLabelText('Nome persona')).toBeTruthy();
});

it('hides the admin Settings tab from non-admins and shows it to admins', async () => {
  renderPeople(false);
  await screen.findByText('3 volti');
  expect(screen.queryByRole('button', { name: 'Impostazioni Face AI' })).toBeNull();
  cleanup();
  vi.unstubAllGlobals();

  renderPeople(true);
  await screen.findByText('3 volti');
  expect(screen.getByRole('button', { name: 'Impostazioni Face AI' })).toBeTruthy();
});

it('shows an empty state when there are no suggested groups', async () => {
  renderPeople(false, { 'GET /api/people/suggested-groups': () => jsonResponse([]) });
  expect(await screen.findByText('Nessun gruppo da mostrare.')).toBeTruthy();
});

it('switches to the People tab and lists named people', async () => {
  renderPeople(false, {
    'GET /api/people': () =>
      jsonResponse([{ personId: 'p-1', name: 'Alice', faceCount: 4, representative: rep }]),
  });
  await screen.findByText('3 volti');
  await userEvent.click(screen.getByRole('button', { name: 'Persone' }));
  expect(await screen.findByText('Alice')).toBeTruthy();
  expect(screen.getByText('4 volti')).toBeTruthy();
});

it('assigns a name to a suggested group', async () => {
  const mock = installFetchMock({
    'GET /api/people/suggested-groups': group(3),
    'GET /api/people': () => jsonResponse([]),
    'POST /api/people/groups/g-1/assign': () =>
      jsonResponse({ personId: 'p-1', name: 'Bob', faceCount: 3, representative: rep }),
  });
  render(
    <AuthedWrapper>
      <MemoryRouter initialEntries={['/people']}>
        <Routes>
          <Route path="/people" element={<PeoplePage />} />
        </Routes>
      </MemoryRouter>
    </AuthedWrapper>,
  );
  await screen.findByText('3 volti');
  await userEvent.type(screen.getByLabelText('Nome persona'), 'Bob');
  await userEvent.click(screen.getByRole('button', { name: 'Assegna' }));
  await waitFor(() =>
    expect(mock.calls.some((c) => c.method === 'POST' && c.url.includes('/groups/g-1/assign'))).toBe(true),
  );
});

it('populates the "aggiungi a persona esistente" dropdown on first entry (no tab bounce)', async () => {
  // Regression: the people list must load with the suggested groups so the group
  // cards' dropdown is populated immediately, without visiting the People tab first.
  renderPeople(false, {
    'GET /api/people': () =>
      jsonResponse([{ personId: 'p-1', name: 'Alice', faceCount: 4, representative: rep }]),
  });
  await screen.findByText('3 volti');
  const select = await screen.findByLabelText('Aggiungi a persona esistente');
  expect(select).toBeTruthy();
  expect(screen.getByRole('option', { name: 'Alice' })).toBeTruthy();
});

it('bulk-ignores a whole suggested group after confirmation', async () => {
  const mock = installFetchMock({
    'GET /api/people/suggested-groups': group(3),
    'GET /api/people': () => jsonResponse([]),
    'POST /api/people/groups/g-1/ignore': () => jsonResponse({ ignored: 3 }),
  });
  vi.spyOn(window, 'confirm').mockReturnValue(true);
  render(
    <AuthedWrapper>
      <MemoryRouter initialEntries={['/people']}>
        <Routes>
          <Route path="/people" element={<PeoplePage />} />
        </Routes>
      </MemoryRouter>
    </AuthedWrapper>,
  );
  await screen.findByText('3 volti');
  await userEvent.click(screen.getByRole('button', { name: 'Ignora gruppo' }));
  expect(window.confirm).toHaveBeenCalled();
  await waitFor(() =>
    expect(mock.calls.some((c) => c.method === 'POST' && c.url.includes('/groups/g-1/ignore'))).toBe(true),
  );
});

it('does not ignore the group when the confirmation is dismissed', async () => {
  const mock = installFetchMock({
    'GET /api/people/suggested-groups': group(3),
    'GET /api/people': () => jsonResponse([]),
    'POST /api/people/groups/g-1/ignore': () => jsonResponse({ ignored: 3 }),
  });
  vi.spyOn(window, 'confirm').mockReturnValue(false);
  render(
    <AuthedWrapper>
      <MemoryRouter initialEntries={['/people']}>
        <Routes>
          <Route path="/people" element={<PeoplePage />} />
        </Routes>
      </MemoryRouter>
    </AuthedWrapper>,
  );
  await screen.findByText('3 volti');
  await userEvent.click(screen.getByRole('button', { name: 'Ignora gruppo' }));
  expect(window.confirm).toHaveBeenCalled();
  expect(mock.calls.some((c) => c.method === 'POST' && c.url.includes('/groups/g-1/ignore'))).toBe(false);
});

it('lists ignored faces and restores one', async () => {
  const box = { x: 0.1, y: 0.1, width: 0.2, height: 0.2 };
  const mock = installFetchMock({
    'GET /api/people/suggested-groups': group(3),
    'GET /api/people': () => jsonResponse([]),
    'GET /api/people/ignored-faces': () =>
      jsonResponse({ items: [{ faceId: 'if-1', fileItemId: 'file-1', name: 'a.png', box }], nextCursor: null }),
    'DELETE /api/people/faces/if-1/ignore': () => emptyResponse(),
  });
  render(
    <AuthedWrapper>
      <MemoryRouter initialEntries={['/people']}>
        <Routes>
          <Route path="/people" element={<PeoplePage />} />
        </Routes>
      </MemoryRouter>
    </AuthedWrapper>,
  );
  await screen.findByText('3 volti');
  await userEvent.click(screen.getByRole('button', { name: 'Ignorati' }));
  expect(await screen.findByLabelText('Volto ignorato')).toBeTruthy();
  await userEvent.click(screen.getByRole('button', { name: 'Ripristina' }));
  await waitFor(() =>
    expect(mock.calls.some((c) => c.method === 'DELETE' && c.url.includes('/faces/if-1/ignore'))).toBe(true),
  );
  await waitFor(() => expect(screen.queryByLabelText('Volto ignorato')).toBeNull());
});

it('reviews the whole suggested group in the context viewer', async () => {
  const box = { x: 0.2, y: 0.2, width: 0.15, height: 0.15 };
  renderPeople(false, {
    // Clicking the group loads ALL its member faces, then opens the viewer on them.
    'GET /api/people/groups/g-1/faces': () =>
      jsonResponse([
        { faceId: 'face-1', fileItemId: 'file-1', name: 'a.png', box },
        { faceId: 'face-2', fileItemId: 'file-2', name: 'b.png', box },
      ]),
    'GET /api/people/faces/face-1/context': () =>
      jsonResponse({
        fileItemId: 'file-1', fileName: 'crowd.jpg', selectedFaceId: 'face-1', selectedBox: box,
        faces: [{ faceId: 'face-1', box }], personId: null, personName: null,
      }),
    'GET /api/people/faces/face-2/context': () =>
      jsonResponse({
        fileItemId: 'file-2', fileName: 'other.jpg', selectedFaceId: 'face-2', selectedBox: box,
        faces: [{ faceId: 'face-2', box }], personId: null, personName: null,
      }),
    'GET /api/people': () => jsonResponse([]),
  });
  await screen.findByText('3 volti');
  await userEvent.click(screen.getAllByRole('button', { name: 'Rivedi gruppo' })[0]);
  expect(await screen.findByRole('dialog', { name: 'Visualizzatore volto' })).toBeTruthy();
  expect(await screen.findByText('crowd.jpg')).toBeTruthy();
  // Group has 2 members → next/prev navigation is available (evaluate the group).
  expect(screen.getByRole('button', { name: 'Volto successivo' })).toBeTruthy();
});

it('offers "Associa cluster a persona…" on a suggested group and opens the dialog', async () => {
  renderPeople(false, {
    'GET /api/people': () =>
      jsonResponse([{ personId: 'p-1', name: 'Alice', faceCount: 4, representative: rep }]),
  });
  await screen.findByText('3 volti');
  const mergeBtn = screen.getByRole('button', { name: 'Associa cluster a persona…' });
  expect(mergeBtn).toBeTruthy();
  await userEvent.click(mergeBtn);
  expect(await screen.findByRole('dialog', { name: 'Associa cluster a persona' })).toBeTruthy();
  expect(screen.getByText('Volti nel gruppo:')).toBeTruthy();
});

it('renders the admin Face AI settings panel in the Settings tab', async () => {
  renderPeople(true, {
    'GET /api/admin/ai/face-settings': () =>
      jsonResponse({
        aiEnabled: true,
        faceDetectionEnabled: false,
        faceEmbeddingsEnabled: false,
        faceClusteringEnabled: false,
        activeFaceProfileKey: 'face-insightface-antelopev2-v1',
        modelDirConfigured: false,
        onnxIntraOpThreads: null,
        maxConcurrency: 1,
        thresholds: {
          clusterSimilarityThreshold: 0.4,
          candidateSimilarityThreshold: 0.3,
          searchDefaultSimilarityThreshold: 0.35,
          searchMinSimilarity: 0.2,
          searchMaxSimilarity: 0.95,
          maxFacesPerImage: 50,
          knnLouvainResolution: 1.0,
        },
        models: [],
        clustering: {
          mode: 'pgvector_knn',
          knnNeighbors: 40,
          knnEfSearch: 100,
          knnMinSimilarity: 0.4,
          knnCandidateSimilarity: 0.3,
          knnMaxEligibleFacesPerRun: 100000,
          knnMaxClusterSize: 300,
          exactMaxFacesToCluster: 4000,
        },
      }),
  });
  await screen.findByText('3 volti');
  await userEvent.click(screen.getByRole('button', { name: 'Impostazioni Face AI' }));
  expect(await screen.findByLabelText('Soglia clustering')).toBeTruthy();
  // The new Louvain resolution knob is editable.
  expect(screen.getByLabelText('Risoluzione Louvain (γ)')).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Salva' })).toBeTruthy();
  // Read-only advanced clustering params are surfaced.
  expect(await screen.findByText(/pgvector kNN/)).toBeTruthy();
  expect(screen.getByText(/ef_search:/)).toBeTruthy();
});

// ---- VFACE-02: faces-in-videos review tab ---------------------------------

it('opens the faces-in-videos tab and reviews a track', async () => {
  renderPeople(false, {
    'GET /api/people/video-tracks/undecided': () => jsonResponse({
      items: [{
        trackId: 't-1',
        fileItemId: 'file-9',
        name: 'clip.mp4',
        startMilliseconds: 65_000,
        endMilliseconds: 92_000,
        representativeMilliseconds: 78_000,
        detectionCount: 12,
        qualityScore: 0.62,
      }],
      hasMore: false,
    }),
    'GET /api/people/video-tracks/t-1/suggestions': () => jsonResponse({
      threshold: 0.4,
      items: [{ personId: 'p-1', name: 'Alice', similarity: 0.88, supportingEvidenceCount: 3 }],
      unavailableReason: null,
    }),
    'POST /api/people/video-tracks/t-1/assign': () => emptyResponse(204),
  });

  await userEvent.click(await screen.findByRole('button', { name: 'Volti nei video' }));

  expect(await screen.findByText('clip.mp4')).toBeTruthy();
  expect(await screen.findByText('1:05 – 1:32')).toBeTruthy();

  await userEvent.click(await screen.findByRole('button', { name: /Conferma Alice/ }));
  await waitFor(() => expect(screen.queryByText('clip.mp4')).toBeNull());
});
