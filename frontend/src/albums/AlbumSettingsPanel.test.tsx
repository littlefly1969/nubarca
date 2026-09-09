import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { AlbumSettingsPanel } from './AlbumSettingsPanel';
import { PERMISSIONS } from '@nubarca/api-client';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../test-utils';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const album = {
  id: 'a1', name: 'Trip', description: null, showOnTv: false,
  createdAt: '2025-01-01T00:00:00Z', updatedAt: '2025-01-01T00:00:00Z',
};
const party = {
  albumId: 'a1', showOnTv: false, partyMode: false, partyUrl: null,
  uploadEnabled: false, uploadUrl: null, requireUploadApproval: false,
  requireMessageApproval: false,
  photoSlideSeconds: 9, maxVideoSlideSeconds: 60,
  maxPhotoUploadsPerParticipant: 0, maxVideoUploadsPerParticipant: 0,
};

function renderPanel(
  overrides: Partial<Parameters<typeof AlbumSettingsPanel>[0]> = {},
  permissions?: readonly string[],
) {
  const props = {
    albumId: 'a1', album, party,
    onAlbumUpdated: vi.fn(), onPartyUpdated: vi.fn(), onDeleted: vi.fn(), onClose: vi.fn(),
    ...overrides,
  };
  render(
    <AuthedWrapper permissions={permissions}>
      <MemoryRouter><AlbumSettingsPanel {...props} /></MemoryRouter>
    </AuthedWrapper>,
  );
  return props;
}

describe('AlbumSettingsPanel', () => {
  it('is a modal dialog with rename, TV and delete controls', () => {
    renderPanel();
    const dialog = screen.getByRole('dialog');
    expect(dialog).toHaveAttribute('aria-modal', 'true');
    expect(screen.getByTestId('album-name')).toHaveValue('Trip');
    expect(screen.getByTestId('album-tv-toggle')).toBeInTheDocument();
    expect(screen.getByTestId('album-delete')).toBeInTheDocument();
  });

  it('Save is disabled until the name/description is dirty, then persists', async () => {
    const onAlbumUpdated = vi.fn();
    installFetchMock({ 'PATCH /api/albums/a1': () => jsonResponse({ ...album, name: 'Trip 2024' }) });
    renderPanel({ onAlbumUpdated });
    const save = screen.getByTestId('album-save');
    expect(save).toBeDisabled();
    await userEvent.type(screen.getByTestId('album-name'), ' 2024');
    expect(save).toBeEnabled();
    await userEvent.click(save);
    expect(onAlbumUpdated).toHaveBeenCalledWith(expect.objectContaining({ name: 'Trip 2024' }));
  });

  it('Escape closes the panel', async () => {
    const onClose = vi.fn();
    renderPanel({ onClose });
    screen.getByTestId('album-name').focus();
    await userEvent.keyboard('{Escape}');
    expect(onClose).toHaveBeenCalled();
  });

  // --- Party is a BRIDGE here now -----------------------------------------
  //
  // The slideshow numbers, the game and its deck, the upload switch and the
  // moderation links all moved to the party's own workspace, and their tests
  // moved with them. Two complete interfaces configuring one party would be two
  // places to change it and two places for them to disagree.

  const activeParty = {
    ...party, partyMode: true, partyUrl: '/party/tok', showOnTv: true,
    uploadEnabled: true, uploadUrl: '/party/uptok/upload',
  };

  it('offers one way in to the party this album belongs to', async () => {
    installFetchMock({
      'GET /api/parties': () => jsonResponse([{
        id: 'p1', title: 'Festa di Marta', status: 'live',
        eventStartsAt: null, liveStartedAt: null, liveEndedAt: null,
        updatedAt: '2027-01-01T00:00:00Z', mainAlbumId: 'a1', mainAlbumName: 'Trip',
      }]),
    });
    renderPanel({ party: activeParty });

    expect(await screen.findByTestId('album-party-open'))
      .toHaveAttribute('href', '/parties/p1');
    expect(screen.getByTestId('album-party-bridge')).toHaveTextContent('Festa di Marta');
    // And the party application itself is no longer here.
    expect(screen.queryByTestId('party-slideshow-settings')).not.toBeInTheDocument();
    expect(screen.queryByTestId('party-game-settings')).not.toBeInTheDocument();
    expect(screen.queryByTestId('album-party-upload')).not.toBeInTheDocument();
  });

  it('an unlinked album offers the compatibility entry point, once', async () => {
    installFetchMock({ 'GET /api/parties': () => jsonResponse([]) });
    renderPanel({ party });

    expect(await screen.findByTestId('album-party-use')).toBeInTheDocument();
    expect(screen.queryByTestId('album-party-open')).not.toBeInTheDocument();
  });

  it('Party is absent entirely without party.access', async () => {
    // Not a disabled switch: a door nobody may open is not drawn. The album's
    // own controls are untouched, because Show-on-TV is not a Party decision.
    const fetchMock = installFetchMock({});
    renderPanel({ party: activeParty }, [PERMISSIONS.tvManage]);

    expect(screen.queryByTestId('album-party-bridge')).not.toBeInTheDocument();
    expect(screen.getByTestId('album-tv-toggle')).toBeInTheDocument();
    expect(fetchMock.calls.some((c) => c.url.includes('/api/parties'))).toBe(false);
  });
});
