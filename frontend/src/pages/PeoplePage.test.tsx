import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
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

it('filters the People tab by name without asking the server', async () => {
  let listCalls = 0;
  renderPeople(false, {
    'GET /api/people': () => {
      listCalls += 1;
      return jsonResponse([
        { personId: 'p-1', name: 'Marco', faceCount: 1, representative: null },
        { personId: 'p-2', name: 'Maria', faceCount: 1, representative: null },
        { personId: 'p-3', name: 'Gianmarco', faceCount: 1, representative: null },
        { personId: 'p-4', name: 'Lucia', faceCount: 1, representative: null },
      ]);
    },
  });
  await screen.findByText('3 volti');
  await userEvent.click(screen.getByRole('button', { name: 'Persone' }));
  await screen.findByText('Marco');

  const before = listCalls;
  await userEvent.type(screen.getByLabelText('Cerca persona'), 'mar');

  // Contains, not startsWith: Gianmarco has to survive.
  await waitFor(() => expect(screen.queryByText('Lucia')).toBeNull());
  expect(screen.getByText('Marco')).toBeTruthy();
  expect(screen.getByText('Maria')).toBeTruthy();
  expect(screen.getByText('Gianmarco')).toBeTruthy();

  // The whole list was already loaded, so typing must cost no request.
  expect(listCalls).toBe(before);
});

it('says so when a name filter matches nobody', async () => {
  renderPeople(false, {
    'GET /api/people': () => jsonResponse([
      { personId: 'p-1', name: 'Marco', faceCount: 1, representative: null },
    ]),
  });
  await screen.findByText('3 volti');
  await userEvent.click(screen.getByRole('button', { name: 'Persone' }));
  await screen.findByText('Marco');

  await userEvent.type(screen.getByLabelText('Cerca persona'), 'zzz');
  await waitFor(() => expect(screen.getByText('Nessuna persona corrisponde a «zzz».')).toBeTruthy());
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

// ---- "Ignora volto" from inside the viewer --------------------------------
//
// The action used to persist and then leave the screen exactly as it was: the
// card stayed in the grid, and the viewer stayed on a face that is no longer a
// candidate — only a page reload made the change visible. What must happen now
// is the whole thing, without one.

it('drops an ignored face from the grid and moves the viewer to the next one', async () => {
  const box = { x: 0.2, y: 0.2, width: 0.15, height: 0.15 };
  const face = (id: string, file: string, name: string, ignored = false) => () =>
    jsonResponse({
      fileItemId: file, fileName: name, selectedFaceId: id, selectedBox: box,
      faces: [{ faceId: id, box }], personId: null, personName: null, isIgnored: ignored,
    });
  const mock = installFetchMock({
    'GET /api/people/suggested-groups': group(3),
    'GET /api/people': () => jsonResponse([]),
    'GET /api/people/unassigned-faces': () =>
      jsonResponse({
        items: [
          { faceId: 'u-1', fileItemId: 'file-1', name: 'a.png', box, hasEmbedding: true, detectionScore: 0.9 },
          { faceId: 'u-2', fileItemId: 'file-2', name: 'b.png', box, hasEmbedding: true, detectionScore: 0.8 },
        ],
        nextCursor: null,
        profileAvailable: true,
      }),
    'GET /api/people/faces/u-1/context': face('u-1', 'file-1', 'first.jpg'),
    'GET /api/people/faces/u-2/context': face('u-2', 'file-2', 'second.jpg'),
    'POST /api/people/faces/u-1/ignore': () => emptyResponse(),
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
  await userEvent.click(screen.getByRole('button', { name: 'Volti non assegnati' }));
  expect((await screen.findAllByLabelText('Volto non assegnato')).length).toBe(2);

  // Open the first face in the context viewer and ignore it from there.
  await userEvent.click(screen.getAllByLabelText('Volto non assegnato')[0]);
  expect(await screen.findByText('first.jpg')).toBeTruthy();
  // Scoped to the viewer: every unassigned card carries the same trigger.
  const viewer = screen.getByRole('dialog', { name: 'Visualizzatore volto' });
  await userEvent.click(within(viewer).getByRole('button', { name: 'Assegna persona' }));
  await userEvent.click(await screen.findByRole('button', { name: 'Ignora volto' }));

  await waitFor(() =>
    expect(mock.calls.some((c) => c.method === 'POST' && c.url.includes('/faces/u-1/ignore'))).toBe(true),
  );

  // The viewer moved to the face that took its place — it did not stay on the
  // one that was just dismissed.
  expect(await screen.findByText('second.jpg')).toBeTruthy();
  // The source grid lost the card, with no page reload.
  await waitFor(() => expect(screen.getAllByLabelText('Volto non assegnato').length).toBe(1));
  // And it is not asked for again.
  const contextCalls = mock.calls.filter((c) => c.url.includes('/faces/u-1/context')).length;
  expect(contextCalls).toBe(1);
});

it('closes the viewer when the ignored face was the only one left', async () => {
  const box = { x: 0.2, y: 0.2, width: 0.15, height: 0.15 };
  installFetchMock({
    'GET /api/people/suggested-groups': group(3),
    'GET /api/people': () => jsonResponse([]),
    'GET /api/people/unassigned-faces': () =>
      jsonResponse({
        items: [{ faceId: 'u-1', fileItemId: 'file-1', name: 'a.png', box, hasEmbedding: true, detectionScore: 0.9 }],
        nextCursor: null,
        profileAvailable: true,
      }),
    'GET /api/people/faces/u-1/context': () =>
      jsonResponse({
        fileItemId: 'file-1', fileName: 'only.jpg', selectedFaceId: 'u-1', selectedBox: box,
        faces: [{ faceId: 'u-1', box }], personId: null, personName: null, isIgnored: false,
      }),
    'POST /api/people/faces/u-1/ignore': () => emptyResponse(),
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
  await userEvent.click(screen.getByRole('button', { name: 'Volti non assegnati' }));
  await userEvent.click(await screen.findByLabelText('Volto non assegnato'));
  await screen.findByText('only.jpg');
  const viewer = screen.getByRole('dialog', { name: 'Visualizzatore volto' });
  await userEvent.click(within(viewer).getByRole('button', { name: 'Assegna persona' }));
  await userEvent.click(await screen.findByRole('button', { name: 'Ignora volto' }));

  await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Visualizzatore volto' })).toBeNull());
  await waitFor(() => expect(screen.queryByLabelText('Volto non assegnato')).toBeNull());
});

it('offers Restore, not Ignore, on a face opened from the Ignorati tab', async () => {
  const box = { x: 0.2, y: 0.2, width: 0.15, height: 0.15 };
  const mock = installFetchMock({
    'GET /api/people/suggested-groups': group(3),
    'GET /api/people': () => jsonResponse([]),
    'GET /api/people/ignored-faces': () =>
      jsonResponse({ items: [{ faceId: 'if-1', fileItemId: 'file-1', name: 'a.png', box }], nextCursor: null }),
    'GET /api/people/faces/if-1/context': () =>
      jsonResponse({
        fileItemId: 'file-1', fileName: 'dismissed.jpg', selectedFaceId: 'if-1', selectedBox: box,
        faces: [{ faceId: 'if-1', box }], personId: null, personName: null, isIgnored: true,
      }),
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
  await userEvent.click(await screen.findByLabelText('Volto ignorato'));
  await screen.findByText('dismissed.jpg');
  await userEvent.click(screen.getByRole('button', { name: 'Assegna persona' }));

  // An "Ignora volto" here would be an action that does nothing.
  const dialog = await screen.findByRole('dialog', { name: 'Assegna a persona' });
  expect(within(dialog).queryByRole('button', { name: 'Ignora volto' })).toBeNull();
  await userEvent.click(within(dialog).getByRole('button', { name: 'Ripristina' }));

  await waitFor(() =>
    expect(mock.calls.some((c) => c.method === 'DELETE' && c.url.includes('/faces/if-1/ignore'))).toBe(true),
  );
  // Restored → it leaves the recovery tab, and the viewer has nothing to show.
  await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Visualizzatore volto' })).toBeNull());
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
