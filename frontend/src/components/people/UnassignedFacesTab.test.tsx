import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { UnassignedFacesTab } from './UnassignedFacesTab';
import { installFetchMock, jsonResponse } from '../../test-utils';
import { I18nProvider } from '../../i18n';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const box = { x: 0.1, y: 0.1, width: 0.2, height: 0.2 };

function renderTab(handlers = {}) {
  const mock = installFetchMock({
    'GET /api/people/unassigned-faces': () =>
      jsonResponse({
        items: [
          { faceId: 'uf-1', fileItemId: 'file-1', name: 'a.png', box, hasEmbedding: true, detectionScore: 0.9 },
          { faceId: 'uf-2', fileItemId: 'file-2', name: 'b.png', box, hasEmbedding: false, detectionScore: 0.8 },
        ],
        nextCursor: null,
        profileAvailable: true,
      }),
    'GET /api/people': () => jsonResponse([{ personId: 'p-1', name: 'Alice', faceCount: 2, representative: null }]),
    ...handlers,
  });
  render(
    <I18nProvider>
      <MemoryRouter>
        <UnassignedFacesTab onOpenFace={() => {}} invalidateAuth={() => {}} />
      </MemoryRouter>
    </I18nProvider>,
  );
  return mock;
}

it('loads and lists unassigned faces', async () => {
  renderTab();
  await waitFor(() => expect(screen.getAllByLabelText('Volto non assegnato').length).toBe(2));
  // Each face offers an assign action.
  expect(screen.getAllByRole('button', { name: 'Assegna a persona' }).length).toBe(2);
});

it('assigns an unassigned face and removes it from the pool', async () => {
  const mock = renderTab({
    'POST /api/people/faces/uf-1/assign': () =>
      jsonResponse({ personId: 'p-1', name: 'Alice', faceCount: 3, representative: null }),
  });
  await waitFor(() => expect(screen.getAllByLabelText('Volto non assegnato').length).toBe(2));

  // Open the first face's assign menu and pick Alice.
  await userEvent.click(screen.getAllByRole('button', { name: 'Assegna a persona' })[0]);
  await userEvent.click(screen.getByRole('button', { name: 'Alice' }));

  await waitFor(() =>
    expect(mock.calls.some((c) => c.method === 'POST' && c.url.includes('/faces/uf-1/assign'))).toBe(true),
  );
  // The assigned face is dropped from the pool → only one remains.
  await waitFor(() => expect(screen.getAllByLabelText('Volto non assegnato').length).toBe(1));
});

it('shows an empty state when there are no unassigned faces', async () => {
  renderTab({
    'GET /api/people/unassigned-faces': () =>
      jsonResponse({ items: [], nextCursor: null, profileAvailable: true }),
  });
  expect(await screen.findByText('Nessun volto non assegnato.')).toBeTruthy();
});
