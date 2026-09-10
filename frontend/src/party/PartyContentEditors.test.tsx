import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { PartyGuestContentSlot } from '@nubarca/api-client';
import { I18nProvider } from '../i18n';
import { errorResponse, installFetchMock, jsonResponse } from '../test-utils';
import { PartyContentCard } from './PartyContentEditors';

// A slot's one photograph, as the HOST chooses it. What is pinned here is that
// the photograph is a reference to one of the host's ordinary files: chosen from
// the party's album or uploaded through the ordinary library upload, saved with
// the rest of the card — and never, by any path, added to the album.

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const PARTY = 'p1';
const ALBUM = 'a1';
const PUT_MENU = `PUT /api/parties/${PARTY}/guest-content/menu`;

function slot(over: Partial<PartyGuestContentSlot> = {}): PartyGuestContentSlot {
  return {
    kind: 'menu', enabled: true, visibleBefore: true, visibleLive: true, visibleAfter: false,
    content: { intro: 'Cena in giardino', sections: [] }, version: 1,
    mediaFileItemId: null, mediaUrl: null, ...over,
  };
}

const albumItems = [
  { fileItemId: 'f1', name: 'IMG_0001.jpg', thumbnailUrl: '/api/files/f1/thumbnail?size=small', sortOrder: 0 },
  { fileItemId: 'f2', name: 'IMG_0002.jpg', thumbnailUrl: '/api/files/f2/thumbnail?size=small', sortOrder: 1 },
];

function mount(
  initial: PartyGuestContentSlot,
  extra: Parameters<typeof installFetchMock>[0] = {},
  albumId: string | null = ALBUM,
) {
  const onSaved = vi.fn();
  const mock = installFetchMock({
    [`GET /api/albums/${ALBUM}/items`]: () => jsonResponse(albumItems),
    // The server answers with the slot as it now stands.
    [PUT_MENU]: ({ body }) => {
      const sent = JSON.parse(body!);
      return jsonResponse({
        ...initial, ...sent, version: initial.version + 1,
        mediaUrl: sent.mediaFileItemId ? `/api/files/${sent.mediaFileItemId}/thumbnail?size=medium` : null,
      });
    },
    ...extra,
  });
  render(
    <I18nProvider>
      <PartyContentCard
        slot={initial} partyId={PARTY} albumId={albumId}
        phases={['before', 'live']} onSaved={onSaved}
      />
    </I18nProvider>,
  );
  return { mock, onSaved };
}

const sentBody = (mock: ReturnType<typeof installFetchMock>) =>
  JSON.parse(mock.calls.find((c) => c.method === 'PUT')!.body!);

const addedToAnAlbum = (mock: ReturnType<typeof installFetchMock>) =>
  mock.calls.some((c) => c.method === 'POST' && /\/api\/albums\/[^/]+\/items/.test(c.url));

describe('a slot’s photograph', () => {
  it('is chosen from the party album and saved with the rest of the card', async () => {
    const { mock, onSaved } = mount(slot());
    const user = userEvent.setup();

    await user.click(screen.getByTestId('party-image-choose-menu'));
    const grid = await screen.findByTestId('party-image-grid');
    await user.click(within(grid).getByRole('button', { name: 'IMG_0002.jpg' }));

    expect(screen.getByTestId('party-image-preview-menu'))
      .toHaveAttribute('src', '/api/files/f2/thumbnail?size=small');

    await user.click(screen.getByTestId('party-content-save-menu'));
    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    const body = sentBody(mock);
    expect(body.mediaFileItemId).toBe('f2');
    // The words travel with it, and the version is the SLOT's own.
    expect(body.content.intro).toBe('Cena in giardino');
    expect(body.version).toBe(1);
    expect(addedToAnAlbum(mock)).toBe(false);
  });

  it('is removed as an edit of the slot', async () => {
    const { mock, onSaved } = mount(slot({
      mediaFileItemId: 'f1', mediaUrl: '/api/files/f1/thumbnail?size=medium',
    }));
    const user = userEvent.setup();

    expect(screen.getByTestId('party-image-preview-menu')).toBeInTheDocument();
    await user.click(screen.getByTestId('party-image-remove-menu'));
    expect(screen.queryByTestId('party-image-preview-menu')).not.toBeInTheDocument();
    expect(screen.queryByTestId('party-image-remove-menu')).not.toBeInTheDocument();

    await user.click(screen.getByTestId('party-content-save-menu'));
    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect(sentBody(mock).mediaFileItemId).toBeNull();
  });

  it('is uploaded through the ordinary library upload and filed in no album', async () => {
    const { mock, onSaved } = mount(slot(), {
      'POST /api/files': () => jsonResponse({
        id: 'new1', name: 'menu.jpg', mimeType: 'image/jpeg', sizeBytes: 10,
        createdAt: '2026-09-10T10:00:00Z',
      }),
    });
    const user = userEvent.setup();

    fireEvent.change(screen.getByTestId('party-image-upload-menu-input'), {
      target: { files: [new File(['x'], 'menu.jpg', { type: 'image/jpeg' })] },
    });
    expect(await screen.findByTestId('party-image-preview-menu'))
      .toHaveAttribute('src', '/api/files/new1/thumbnail?size=small');

    await user.click(screen.getByTestId('party-content-save-menu'));
    await waitFor(() => expect(onSaved).toHaveBeenCalled());

    const upload = mock.calls.find((c) => c.method === 'POST' && c.url === '/api/files');
    expect(upload?.body).toBe('[FormData]');
    expect(sentBody(mock).mediaFileItemId).toBe('new1');
    // THE condition of this slice: a Party reference never implies album
    // membership, so nothing on this path asks to add the file to an album.
    expect(addedToAnAlbum(mock)).toBe(false);
  });

  it('says so when the chosen photograph is no longer available', () => {
    mount(slot({ mediaFileItemId: 'gone', mediaUrl: null }));

    expect(screen.getByTestId('party-image-unavailable-menu')).toBeInTheDocument();
    expect(screen.queryByTestId('party-image-preview-menu')).not.toBeInTheDocument();
    // And it can be taken away.
    expect(screen.getByTestId('party-image-remove-menu')).toBeInTheDocument();
  });

  it('reports a photograph the server refused rather than failing silently', async () => {
    mount(slot(), {
      [PUT_MENU]: () => errorResponse(400, { error: 'invalid_media' }),
    });
    const user = userEvent.setup();

    await user.click(screen.getByTestId('party-image-choose-menu'));
    await user.click(await screen.findByRole('button', { name: 'IMG_0001.jpg' }));
    await user.click(screen.getByTestId('party-content-save-menu'));

    expect(await screen.findByRole('alert')).toHaveTextContent(/non può essere usata/i);
  });

  it('explains a library name clash instead of dropping the upload', async () => {
    mount(slot(), { 'POST /api/files': () => errorResponse(409) });

    fireEvent.change(screen.getByTestId('party-image-upload-menu-input'), {
      target: { files: [new File(['x'], 'menu.jpg', { type: 'image/jpeg' })] },
    });

    expect(await screen.findByRole('alert')).toHaveTextContent(/già un file con questo nome/i);
  });

  it('offers only an upload when the party has no album yet', () => {
    mount(slot(), {}, null);

    expect(screen.queryByTestId('party-image-choose-menu')).not.toBeInTheDocument();
    expect(screen.getByTestId('party-image-upload-menu')).toBeInTheDocument();
  });
});
