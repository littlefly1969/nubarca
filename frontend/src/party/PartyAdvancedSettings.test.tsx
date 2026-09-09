import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import type { AlbumPartyStatus } from '@nubarca/api-client';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../test-utils';
import { PartySlideshowSettings } from './PartyAdvancedSettings';

// The party's numbers, MOVED here from AlbumSettingsPanel with the behaviour
// they always had — a draft that is saved explicitly, validated against the
// shared ranges, on an endpoint that cannot touch a token as a side effect.
// These tests moved with the code they describe.

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const party: AlbumPartyStatus = {
  albumId: 'a1', showOnTv: true, partyMode: true, partyUrl: '/party/tok',
  uploadEnabled: true, uploadUrl: '/party/uptok/upload',
  requireUploadApproval: false, requireMessageApproval: false,
  photoSlideSeconds: 9, maxVideoSlideSeconds: 60,
  maxPhotoUploadsPerParticipant: 0, maxVideoUploadsPerParticipant: 0,
  maxMessagesPerParticipant: 0,
};

function renderSettings(onUpdated = vi.fn()) {
  render(
    <AuthedWrapper>
      <MemoryRouter>
        <PartySlideshowSettings albumId="a1" party={party} onUpdated={onUpdated} />
      </MemoryRouter>
    </AuthedWrapper>,
  );
  return onUpdated;
}

describe('PartySlideshowSettings', () => {
  it('shows the settings seeded with the server values', () => {
    renderSettings();
    expect(screen.getByTestId('party-slideshow-settings')).toBeInTheDocument();
    expect(screen.getByLabelText(/Durata foto nello slideshow/i)).toHaveValue(9);
    expect(screen.getByLabelText(/Durata massima video nello slideshow/i)).toHaveValue(60);
    expect(screen.getByLabelText(/Massimo foto per partecipante/i)).toHaveValue(0);
    expect(screen.getByLabelText(/Massimo video per partecipante/i)).toHaveValue(0);
  });

  it('refuses to save an out-of-range value and never calls the API', async () => {
    const fetchMock = installFetchMock({});
    renderSettings();
    const photo = screen.getByLabelText(/Durata foto nello slideshow/i);
    await userEvent.clear(photo);
    await userEvent.type(photo, '1'); // below the minimum of 3

    // Client validation mirrors the server ranges, so the round-trip is not
    // even attempted — but the server remains the validator.
    expect(screen.getByTestId('party-slideshow-save')).toBeDisabled();
    expect(fetchMock.calls.some((c) => c.url.includes('party-slideshow-settings'))).toBe(false);
  });

  it('saves the draft in ONE request and reports the returned values', async () => {
    const saved = {
      ...party, photoSlideSeconds: 15, maxVideoSlideSeconds: 45,
      maxPhotoUploadsPerParticipant: 20, maxVideoUploadsPerParticipant: 5,
    };
    const fetchMock = installFetchMock({
      'PATCH /api/albums/a1/party-slideshow-settings': () => jsonResponse(saved),
    });
    const onUpdated = renderSettings();

    const photo = screen.getByLabelText(/Durata foto nello slideshow/i);
    await userEvent.clear(photo);
    await userEvent.type(photo, '15');
    const quota = screen.getByLabelText(/Massimo foto per partecipante/i);
    await userEvent.clear(quota);
    await userEvent.type(quota, '20');

    // A draft: nothing has been sent while typing, because every keystroke on
    // the way to "15" is a real setting a television would adopt.
    expect(fetchMock.calls.some((c) => c.url.includes('party-slideshow-settings'))).toBe(false);

    await userEvent.click(screen.getByTestId('party-slideshow-save'));
    expect(onUpdated).toHaveBeenCalledWith(expect.objectContaining({
      photoSlideSeconds: 15, maxPhotoUploadsPerParticipant: 20,
    }));
    expect(await screen.findByText(/Impostazioni salvate/i)).toBeInTheDocument();
  });

  it('saving the settings never touches the party tokens or switches', async () => {
    installFetchMock({
      'PATCH /api/albums/a1/party-slideshow-settings': () => jsonResponse({
        ...party, photoSlideSeconds: 12,
      }),
    });
    const onUpdated = renderSettings();
    const photo = screen.getByLabelText(/Durata foto nello slideshow/i);
    await userEvent.clear(photo);
    await userEvent.type(photo, '12');
    await userEvent.click(screen.getByTestId('party-slideshow-save'));

    // The dedicated endpoint returns the SAME urls/switches it was given: this
    // save cannot rotate a token or flip party/upload/approval as a side effect.
    const updated = onUpdated.mock.calls[0][0];
    expect(updated.partyUrl).toBe(party.partyUrl);
    expect(updated.uploadUrl).toBe(party.uploadUrl);
    expect(updated.partyMode).toBe(true);
    expect(updated.uploadEnabled).toBe(true);
    expect(updated.requireUploadApproval).toBe(false);
  });

  it('surfaces a save failure without losing the draft', async () => {
    installFetchMock({
      'PATCH /api/albums/a1/party-slideshow-settings': () => new Response('', { status: 500 }),
    });
    renderSettings();
    const photo = screen.getByLabelText(/Durata foto nello slideshow/i);
    await userEvent.clear(photo);
    await userEvent.type(photo, '22');
    await userEvent.click(screen.getByTestId('party-slideshow-save'));

    expect(await screen.findByText(/Impossibile salvare le impostazioni/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/Durata foto nello slideshow/i)).toHaveValue(22);
  });
});
