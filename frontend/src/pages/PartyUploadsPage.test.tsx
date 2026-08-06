import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { PartyUploadsPage } from './PartyUploadsPage';
import { AuthedWrapper, emptyResponse, installFetchMock, jsonResponse } from '../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const approved = {
  fileItemId: 'file-1', name: 'guest.jpg', mediaType: 'image', status: 'approved',
  thumbnailUrl: '/api/files/file-1/thumbnail', uploadedAt: '2026-07-06T10:00:00Z', moderatedAt: null,
};
const pending = {
  fileItemId: 'file-2', name: 'wait.jpg', mediaType: 'image', status: 'pending',
  thumbnailUrl: '/api/files/file-2/thumbnail', uploadedAt: '2026-07-06T11:00:00Z', moderatedAt: null,
};
const removedFromAlbum = {
  fileItemId: 'file-3', name: 'gone.jpg', mediaType: 'image', status: 'removed_from_album',
  thumbnailUrl: '/api/files/file-3/thumbnail', uploadedAt: '2026-07-06T12:00:00Z', moderatedAt: null,
};

function listResponse(items: unknown[], requireUploadApproval = false) {
  return { albumId: 'album-1', requireUploadApproval, items };
}

function wrapper(albumId = 'album-1') {
  return (
    <AuthedWrapper>
      <MemoryRouter initialEntries={[`/albums/${albumId}/party-uploads`]}>
        <Routes>
          <Route path="/albums/:albumId/party-uploads" element={<PartyUploadsPage />} />
          <Route path="/albums" element={<div>albums list</div>} />
          <Route path="/albums/:albumId" element={<div>album detail</div>} />
        </Routes>
      </MemoryRouter>
    </AuthedWrapper>
  );
}

describe('PartyUploadsPage', () => {
  it('renders guest uploads with status and the approval toggle (default off)', async () => {
    installFetchMock({
      'GET /api/albums/album-1/party-uploads': () => jsonResponse(listResponse([approved])),
    });
    render(wrapper());

    expect(await screen.findByText('guest.jpg')).toBeInTheDocument();
    // The per-row status badge (scoped to the row, not the section heading).
    expect(within(screen.getByTestId('party-upload-row')).getByText('Visibile')).toBeInTheDocument();
    const toggle = screen.getByRole('checkbox', { name: /Richiedi approvazione per i caricamenti party/i });
    expect(toggle).not.toBeChecked();
    // No face/person/original-download UI.
    expect(screen.queryByText(/person|face|original/i)).not.toBeInTheDocument();
  });

  it('shows the empty state when there are no guest uploads', async () => {
    installFetchMock({
      'GET /api/albums/album-1/party-uploads': () => jsonResponse(listResponse([])),
    });
    render(wrapper());
    expect(await screen.findByTestId('party-uploads-empty')).toBeInTheDocument();
  });

  it('hides a visible upload after confirmation and reloads', async () => {
    const user = userEvent.setup();
    vi.stubGlobal('confirm', () => true);
    let call = 0;
    const mock = installFetchMock({
      'GET /api/albums/album-1/party-uploads': () =>
        jsonResponse(listResponse(call++ === 0 ? [approved] : [{ ...approved, status: 'hidden' }])),
      'POST /api/albums/album-1/party-uploads/file-1/hide': () => emptyResponse(204),
    });

    render(wrapper());
    await user.click(await screen.findByRole('button', { name: /Nascondi guest.jpg/i }));

    await waitFor(() => {
      expect(screen.getByText('Nascosto')).toBeInTheDocument();
    });
    expect(mock.calls.some((c) => c.url.includes('/party-uploads/file-1/hide') && c.method === 'POST')).toBe(true);
  });

  it('does not hide when the confirmation is dismissed', async () => {
    const user = userEvent.setup();
    vi.stubGlobal('confirm', () => false);
    const mock = installFetchMock({
      'GET /api/albums/album-1/party-uploads': () => jsonResponse(listResponse([approved])),
      'POST /api/albums/album-1/party-uploads/file-1/hide': () => emptyResponse(204),
    });

    render(wrapper());
    await user.click(await screen.findByRole('button', { name: /Nascondi guest.jpg/i }));
    expect(mock.calls.some((c) => c.url.includes('/hide'))).toBe(false);
  });

  it('approves a pending upload when approval mode is enabled', async () => {
    const user = userEvent.setup();
    let call = 0;
    const mock = installFetchMock({
      'GET /api/albums/album-1/party-uploads': () =>
        jsonResponse(call++ === 0
          ? listResponse([pending], true)
          : listResponse([{ ...pending, status: 'approved' }], true)),
      'POST /api/albums/album-1/party-uploads/file-2/approve': () => emptyResponse(204),
    });

    render(wrapper());
    expect(await screen.findByTestId('party-uploads-pending')).toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: /Richiedi approvazione per i caricamenti party/i })).toBeChecked();

    await user.click(screen.getByRole('button', { name: /Approva wait.jpg/i }));
    await waitFor(() => {
      expect(mock.calls.some((c) => c.url.includes('/party-uploads/file-2/approve'))).toBe(true);
    });
  });

  it('enables approval mode via the toggle (confirmed)', async () => {
    const user = userEvent.setup();
    vi.stubGlobal('confirm', () => true);
    let patchBody: string | null = null;
    const mock = installFetchMock({
      'GET /api/albums/album-1/party-uploads': () => jsonResponse(listResponse([approved])),
      'PATCH /api/albums/album-1/party-settings': (req) => {
        patchBody = req.body;
        return jsonResponse({
          albumId: 'album-1', showOnTv: true, partyMode: true, partyUrl: '/party/t',
          uploadEnabled: true, uploadUrl: '/party/u/upload', requireUploadApproval: true,
        });
      },
      'GET /api/albums/album-1/party-settings': () => jsonResponse({
        albumId: 'album-1', showOnTv: true, partyMode: true, partyUrl: '/party/t',
        uploadEnabled: true, uploadUrl: '/party/u/upload', requireUploadApproval: true,
      }),
    });

    render(wrapper());
    await user.click(await screen.findByRole('checkbox', { name: /Richiedi approvazione per i caricamenti party/i }));

    await waitFor(() => {
      expect(mock.calls.some((c) => c.url.includes('/party-settings') && c.method === 'PATCH')).toBe(true);
    });
    expect(patchBody).toContain('"requireUploadApproval":true');
  });

  it('shows removed-from-album uploads and restores them explicitly', async () => {
    const user = userEvent.setup();
    vi.stubGlobal('confirm', () => true);
    let call = 0;
    const mock = installFetchMock({
      'GET /api/albums/album-1/party-uploads': () =>
        jsonResponse(call++ === 0
          ? listResponse([removedFromAlbum])
          : listResponse([{ ...removedFromAlbum, status: 'approved' }])),
      'POST /api/albums/album-1/party-uploads/file-3/restore': () => emptyResponse(204),
    });

    render(wrapper());

    expect(await screen.findByText('Rimosso dall’album')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: /Ripristina gone.jpg nell.album/i }));

    await waitFor(() => {
      expect(mock.calls.some((c) => c.url.includes('/party-uploads/file-3/restore'))).toBe(true);
    });
    expect(await screen.findByText('Visibile')).toBeInTheDocument();
  });
});
