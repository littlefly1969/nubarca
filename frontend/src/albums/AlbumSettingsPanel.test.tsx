import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { AlbumSettingsPanel } from './AlbumSettingsPanel';
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

function renderPanel(overrides: Partial<Parameters<typeof AlbumSettingsPanel>[0]> = {}) {
  const props = {
    albumId: 'a1', album, party,
    onAlbumUpdated: vi.fn(), onPartyUpdated: vi.fn(), onDeleted: vi.fn(), onClose: vi.fn(),
    ...overrides,
  };
  render(
    <AuthedWrapper>
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

  // --- Party slideshow timing + per-participant quotas ---

  const activeParty = {
    ...party, partyMode: true, partyUrl: '/party/tok', showOnTv: true,
    uploadEnabled: true, uploadUrl: '/party/uptok/upload',
  };

  it('shows the four settings seeded with the server values', async () => {
    renderPanel({ party: activeParty });
    const section = screen.getByTestId('party-slideshow-settings');
    expect(section).toBeInTheDocument();
    expect(screen.getByLabelText(/Durata foto nello slideshow/i)).toHaveValue(9);
    expect(screen.getByLabelText(/Durata massima video nello slideshow/i)).toHaveValue(60);
    expect(screen.getByLabelText(/Massimo foto per partecipante/i)).toHaveValue(0);
    expect(screen.getByLabelText(/Massimo video per partecipante/i)).toHaveValue(0);
  });

  it('the settings section is absent while party mode is off', () => {
    renderPanel({ party });
    expect(screen.queryByTestId('party-slideshow-settings')).not.toBeInTheDocument();
  });

  it('refuses to save an out-of-range value and never calls the API', async () => {
    const fetchMock = installFetchMock({});
    renderPanel({ party: activeParty });
    const photo = screen.getByLabelText(/Durata foto nello slideshow/i);
    await userEvent.clear(photo);
    await userEvent.type(photo, '1'); // below the minimum of 3

    // Client validation mirrors the server ranges, so the round-trip is not
    // even attempted — but the server remains the validator.
    expect(screen.getByTestId('party-slideshow-save')).toBeDisabled();
    expect(fetchMock.calls.some((c) => c.url.includes('party-slideshow-settings'))).toBe(false);
  });

  it('saves the draft in ONE request and reports the returned values', async () => {
    const onPartyUpdated = vi.fn();
    const saved = {
      ...activeParty, photoSlideSeconds: 15, maxVideoSlideSeconds: 45,
      maxPhotoUploadsPerParticipant: 20, maxVideoUploadsPerParticipant: 5,
    };
    const fetchMock = installFetchMock({
      'PATCH /api/albums/a1/party-slideshow-settings': () => jsonResponse(saved),
    });
    renderPanel({ party: activeParty, onPartyUpdated });

    const photo = screen.getByLabelText(/Durata foto nello slideshow/i);
    await userEvent.clear(photo);
    await userEvent.type(photo, '15');
    const quota = screen.getByLabelText(/Massimo foto per partecipante/i);
    await userEvent.clear(quota);
    await userEvent.type(quota, '20');

    // A draft: nothing has been sent while typing.
    expect(fetchMock.calls.some((c) => c.url.includes('party-slideshow-settings'))).toBe(false);

    await userEvent.click(screen.getByTestId('party-slideshow-save'));
    expect(onPartyUpdated).toHaveBeenCalledWith(expect.objectContaining({
      photoSlideSeconds: 15, maxPhotoUploadsPerParticipant: 20,
    }));
    expect(await screen.findByText(/Impostazioni salvate/i)).toBeInTheDocument();
  });

  it('saving the settings never touches the party tokens or switches', async () => {
    const onPartyUpdated = vi.fn();
    installFetchMock({
      'PATCH /api/albums/a1/party-slideshow-settings': () => jsonResponse({
        ...activeParty, photoSlideSeconds: 12,
      }),
    });
    renderPanel({ party: activeParty, onPartyUpdated });
    const photo = screen.getByLabelText(/Durata foto nello slideshow/i);
    await userEvent.clear(photo);
    await userEvent.type(photo, '12');
    await userEvent.click(screen.getByTestId('party-slideshow-save'));

    // The dedicated endpoint returns the SAME urls/switches it was given: this
    // save cannot rotate a token or flip party/upload/approval as a side effect.
    const updated = onPartyUpdated.mock.calls[0][0];
    expect(updated.partyUrl).toBe(activeParty.partyUrl);
    expect(updated.uploadUrl).toBe(activeParty.uploadUrl);
    expect(updated.partyMode).toBe(true);
    expect(updated.uploadEnabled).toBe(true);
    expect(updated.requireUploadApproval).toBe(false);
  });

  it('surfaces a save failure without losing the draft', async () => {
    installFetchMock({
      'PATCH /api/albums/a1/party-slideshow-settings': () => new Response('', { status: 500 }),
    });
    renderPanel({ party: activeParty });
    const photo = screen.getByLabelText(/Durata foto nello slideshow/i);
    await userEvent.clear(photo);
    await userEvent.type(photo, '22');
    await userEvent.click(screen.getByTestId('party-slideshow-save'));

    expect(await screen.findByText(/Impossibile salvare le impostazioni/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/Durata foto nello slideshow/i)).toHaveValue(22);
  });

});
