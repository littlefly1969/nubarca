import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { PlatesPage } from './PlatesPage';
import { AuthedWrapper, emptyResponse, installFetchMock, jsonResponse } from '../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const plate1 = {
  id: 'plate-1',
  originalFileName: 'targa-01.png',
  contentType: 'image/png',
  sizeBytes: 1234,
  width: 640,
  height: 480,
  status: 'uploaded',
  analysisStatus: 'not_started',
  platesCount: 0,
  createdAt: '2026-07-01T10:00:00Z',
  updatedAt: '2026-07-01T10:00:00Z',
  thumbnailUrl: '/api/plates/images/plate-1/thumbnail?size=small',
  previewUrl: '/api/plates/images/plate-1/preview',
};

function wrapper(children: React.ReactNode) {
  return (
    <AuthedWrapper>
      <MemoryRouter>{children}</MemoryRouter>
    </AuthedWrapper>
  );
}

describe('PlatesPage', () => {
  it('renders the heading and upload panel', async () => {
    installFetchMock({ 'GET /api/plates/images': () => jsonResponse([]) });
    render(wrapper(<PlatesPage />));

    expect(await screen.findByRole('heading', { name: 'Targhe' })).toBeInTheDocument();
    // The multi-image upload input is present.
    expect(screen.getByLabelText(/Scegli immagini/i)).toBeInTheDocument();
  });

  it('shows the empty state when there are no plates', async () => {
    installFetchMock({ 'GET /api/plates/images': () => jsonResponse([]) });
    render(wrapper(<PlatesPage />));

    expect(await screen.findByText(/Ancora nessuna targa/i)).toBeInTheDocument();
  });

  it('renders a grid of returned plate images', async () => {
    installFetchMock({ 'GET /api/plates/images': () => jsonResponse([plate1]) });
    render(wrapper(<PlatesPage />));

    expect(await screen.findByTestId('plate-grid')).toBeInTheDocument();
    expect(screen.getByText('targa-01.png')).toBeInTheDocument();
    // Grid tiles use the derived thumbnail URL, never an original.
    const img = screen.getByAltText('targa-01.png') as HTMLImageElement;
    expect(img.getAttribute('src')).toContain('/thumbnail');
  });

  it('shows an error state when the list request fails', async () => {
    installFetchMock({ 'GET /api/plates/images': () => jsonResponse({ error: 'boom' }, 500) });
    render(wrapper(<PlatesPage />));

    expect(await screen.findByText(/Impossibile caricare le targhe/i)).toBeInTheDocument();
  });

  it('uploads a selected image and refreshes the list', async () => {
    const user = userEvent.setup();
    let listCount = 0;
    installFetchMock({
      'GET /api/plates/images': () => jsonResponse(listCount++ === 0 ? [] : [plate1]),
      'POST /api/plates/images': () => jsonResponse(plate1, 201),
    });

    render(wrapper(<PlatesPage />));
    await screen.findByText(/Ancora nessuna targa/i);

    const file = new File([new Uint8Array([1, 2, 3])], 'targa-01.png', { type: 'image/png' });
    await user.upload(screen.getByLabelText(/Scegli immagini/i) as HTMLInputElement, file);

    // Per-file success pill + the refreshed grid both show up.
    expect(await screen.findByText(/caricata/i)).toBeInTheDocument();
    const grid = await screen.findByTestId('plate-grid');
    expect(within(grid).getByText('targa-01.png')).toBeInTheDocument();
  });

  it('deletes a plate after confirmation', async () => {
    const user = userEvent.setup();
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    let listCount = 0;
    const deleted = vi.fn();
    installFetchMock({
      'GET /api/plates/images': () => jsonResponse(listCount++ === 0 ? [plate1] : []),
      'DELETE /api/plates/images/plate-1': () => {
        deleted();
        return emptyResponse(204);
      },
    });

    render(wrapper(<PlatesPage />));
    await screen.findByText('targa-01.png');

    await user.click(screen.getByRole('button', { name: /Elimina targa targa-01\.png/i }));

    await waitFor(() => expect(deleted).toHaveBeenCalled());
    expect(await screen.findByText(/Ancora nessuna targa/i)).toBeInTheDocument();
  });

  it('does not render any blob/storage/owner internals', async () => {
    installFetchMock({ 'GET /api/plates/images': () => jsonResponse([plate1]) });
    const { container } = render(wrapper(<PlatesPage />));
    await screen.findByText('targa-01.png');

    const html = container.innerHTML.toLowerCase();
    for (const needle of ['storagekey', 'blobobjectid', 'owneruserid', 'sha256', 'logicalcontainerkey']) {
      expect(html).not.toContain(needle);
    }
  });
});
