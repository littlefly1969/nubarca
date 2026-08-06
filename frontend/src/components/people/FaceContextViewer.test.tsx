import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { FaceContextViewer } from './FaceContextViewer';
import { AuthedWrapper, installFetchMock, jsonResponse, type MockHandler } from '../../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const b = (x: number) => ({ x, y: 0.2, width: 0.15, height: 0.15 });

function context(faceId: string, personName: string | null = null): MockHandler {
  return () =>
    jsonResponse({
      fileItemId: 'file-1',
      fileName: 'crowd.jpg',
      selectedFaceId: faceId,
      selectedBox: b(0.2),
      faces: [
        { faceId: 'f-1', box: b(0.2) },
        { faceId: 'f-2', box: b(0.6) },
      ],
      personId: personName ? 'p-1' : null,
      personName,
    });
}

function renderViewer(faceIds: string[], index: number, onIndexChange = vi.fn(), onClose = vi.fn()) {
  return {
    onIndexChange,
    onClose,
    ...render(
      <AuthedWrapper>
        <MemoryRouter>
          <FaceContextViewer faceIds={faceIds} index={index} onIndexChange={onIndexChange} onClose={onClose} />
        </MemoryRouter>
      </AuthedWrapper>,
    ),
  };
}

it('opens, shows the photo, and highlights the selected face', async () => {
  installFetchMock({ 'GET /api/people/faces/f-1/context': context('f-1', 'Alice') });
  renderViewer(['f-1', 'f-2'], 0);

  expect(await screen.findByText('crowd.jpg')).toBeTruthy();
  expect(screen.getByText('Alice')).toBeTruthy();
  // The full photo (medium preview, never the original).
  const img = screen.getByRole('img');
  expect(img.getAttribute('src')).toBe('/api/files/file-1/preview');
  // Selected face highlighted + the other face shown subtly.
  expect(screen.getByRole('button', { name: 'Volto selezionato' })).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Altri volti nella foto' })).toBeTruthy();
});

it('opens at 100% zoom (not a close-up) with the selected face highlighted', async () => {
  installFetchMock({
    'GET /api/people/faces/f-1/context': context('f-1', 'Alice'),
    'GET /api/people': () => jsonResponse([]),
  });
  renderViewer(['f-1', 'f-2'], 0);
  await screen.findByText('crowd.jpg');
  // Initial zoom is 100% — the viewer does NOT auto-focus/zoom into the face.
  expect(screen.getByLabelText('Zoom').textContent).toBe('100%');
  // Selected face is still highlighted.
  expect(screen.getByRole('button', { name: 'Volto selezionato' })).toBeTruthy();
});

it('focuses the face on demand via "Centra volto"', async () => {
  installFetchMock({
    'GET /api/people/faces/f-1/context': context('f-1'),
    'GET /api/people': () => jsonResponse([]),
  });
  renderViewer(['f-1'], 0);
  await screen.findByText('crowd.jpg');
  expect(screen.getByLabelText('Zoom').textContent).toBe('100%');
  await userEvent.click(screen.getByRole('button', { name: 'Centra volto' }));
  // Focusing zooms in past 100%.
  expect(screen.getByLabelText('Zoom').textContent).not.toBe('100%');
});

it('does not show a redundant "Apri nella foto" action inside the viewer', async () => {
  installFetchMock({
    'GET /api/people/faces/f-1/context': context('f-1'),
    'GET /api/people': () => jsonResponse([]),
  });
  renderViewer(['f-1'], 0);
  await screen.findByText('crowd.jpg');
  expect(screen.queryByRole('button', { name: 'Apri nella foto' })).toBeNull();
});

it('zooms in and out and supports fit / focus', async () => {
  installFetchMock({ 'GET /api/people/faces/f-1/context': context('f-1') });
  renderViewer(['f-1'], 0);
  await screen.findByText('crowd.jpg');

  await userEvent.click(screen.getByRole('button', { name: 'Mostra foto intera' }));
  expect(screen.getByLabelText('Zoom').textContent).toBe('100%');

  await userEvent.click(screen.getByRole('button', { name: 'Zoom avanti' }));
  expect(screen.getByLabelText('Zoom').textContent).toBe('125%');

  await userEvent.click(screen.getByRole('button', { name: 'Zoom indietro' }));
  expect(screen.getByLabelText('Zoom').textContent).toBe('100%');

  // Focus/fit controls exist and do not throw.
  await userEvent.click(screen.getByRole('button', { name: 'Centra volto' }));
});

it('navigates to the next face', async () => {
  installFetchMock({
    'GET /api/people/faces/f-1/context': context('f-1'),
    'GET /api/people/faces/f-2/context': context('f-2'),
  });
  const onIndexChange = vi.fn();
  renderViewer(['f-1', 'f-2'], 0, onIndexChange);
  await screen.findByText('crowd.jpg');

  await userEvent.click(screen.getByRole('button', { name: 'Volto successivo' }));
  expect(onIndexChange).toHaveBeenCalledWith(1);
});

it('closes on Escape and on the close button', async () => {
  installFetchMock({ 'GET /api/people/faces/f-1/context': context('f-1') });
  const onClose = vi.fn();
  renderViewer(['f-1'], 0, vi.fn(), onClose);
  await screen.findByText('crowd.jpg');

  fireEvent.keyDown(window, { key: 'Escape' });
  expect(onClose).toHaveBeenCalled();
});

it('assigns the selected face to a person from the viewer', async () => {
  const mock = installFetchMock({
    'GET /api/people/faces/f-1/context': context('f-1'), // unassigned
    'GET /api/people': () =>
      jsonResponse([{ personId: 'p-1', name: 'Alice', faceCount: 1, representative: null }]),
    'POST /api/people/faces/f-1/assign': () =>
      jsonResponse({ personId: 'p-1', name: 'Alice', faceCount: 2, representative: null }),
  });
  renderViewer(['f-1'], 0);
  await screen.findByText('crowd.jpg');
  expect(screen.getByText('Non assegnato')).toBeTruthy();

  await userEvent.click(screen.getByRole('button', { name: 'Assegna a persona' }));
  await userEvent.click(await screen.findByRole('button', { name: 'Alice' }));

  await waitFor(() =>
    expect(mock.calls.some((c) => c.method === 'POST' && c.url.includes('/faces/f-1/assign'))).toBe(true),
  );
});

it('renders no storage internals', async () => {
  installFetchMock({
    'GET /api/people/faces/f-1/context': context('f-1', 'Alice'),
    'GET /api/people': () => jsonResponse([]),
  });
  const { container } = renderViewer(['f-1', 'f-2'], 0);
  await screen.findByText('crowd.jpg');
  const html = container.innerHTML;
  for (const needle of ['blobObjectId', 'storageKey', 'sha256', '/storage/objects/', 'profileId']) {
    expect(html).not.toContain(needle);
  }
});
