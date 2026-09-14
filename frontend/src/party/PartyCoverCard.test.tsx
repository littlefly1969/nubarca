import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Party } from '@nubarca/api-client';
import { I18nProvider } from '../i18n';
import { errorResponse, installFetchMock, jsonResponse } from '../test-utils';
import { PartyCoverCard } from './PartyCoverCard';

// The party's two covers, as the host chooses them. What is pinned here is that
// each card states BOTH covers to the server — its own as chosen, the other one
// exactly as the party holds it — and that "no photo" is a real choice.

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const PARTY = 'p1';
const ALBUM = 'a1';
const PUT_COVERS = `PUT /api/parties/${PARTY}/covers`;

const albumItems = [
  { fileItemId: 'f1', name: 'IMG_0001.jpg', thumbnailUrl: '/api/files/f1/thumbnail?size=small', sortOrder: 0 },
  { fileItemId: 'f2', name: 'IMG_0002.jpg', thumbnailUrl: '/api/files/f2/thumbnail?size=small', sortOrder: 1 },
];

function party(over: Partial<Party> = {}): Party {
  return {
    id: PARTY, title: 'Festa', description: null, status: 'published',
    eventStartsAt: null, liveStartedAt: null, liveEndedAt: null,
    guestAccessExpiresAt: null, libraryAccessExpiresAt: null, version: 3,
    createdAt: '2026-09-14T00:00:00Z', updatedAt: '2026-09-14T00:00:00Z',
    mediaSources: [], canChangeMainMediaSource: false,
    invitationCoverFileItemId: null, invitationCoverUrl: null,
    liveCoverFileItemId: null, liveCoverUrl: null,
    ...over,
  };
}

function mount(
  which: 'invitation' | 'live',
  initial: Party,
  extra: Parameters<typeof installFetchMock>[0] = {},
) {
  const onPartyUpdated = vi.fn();
  const mock = installFetchMock({
    [`GET /api/albums/${ALBUM}/items`]: () => jsonResponse(albumItems),
    [PUT_COVERS]: ({ body }) => jsonResponse({ ...initial, ...JSON.parse(body!), version: initial.version + 1 }),
    ...extra,
  });
  render(
    <I18nProvider>
      <PartyCoverCard party={initial} which={which} albumId={ALBUM} onPartyUpdated={onPartyUpdated} />
    </I18nProvider>,
  );
  return { mock, onPartyUpdated };
}

const sentBody = (mock: ReturnType<typeof installFetchMock>) =>
  JSON.parse(mock.calls.find((c) => c.method === 'PUT')!.body!);

describe('a party cover', () => {
  it('chooses the invitation cover from the album, and states the live one as it is', async () => {
    const { mock, onPartyUpdated } = mount('invitation', party({
      liveCoverFileItemId: 'live1', liveCoverUrl: '/api/files/live1/thumbnail?size=medium',
    }));
    const user = userEvent.setup();

    await user.click(screen.getByTestId('party-image-choose-cover-invitation'));
    const grid = await screen.findByTestId('party-image-grid');
    await user.click(within(grid).getByRole('button', { name: 'IMG_0002.jpg' }));
    await user.click(screen.getByTestId('party-cover-save-invitation'));

    await waitFor(() => expect(onPartyUpdated).toHaveBeenCalled());
    expect(sentBody(mock)).toEqual({
      invitationCoverFileItemId: 'f2', liveCoverFileItemId: 'live1', version: 3,
    });
  });

  it('removing the live cover hands the party back to the invitation cover', async () => {
    const { mock, onPartyUpdated } = mount('live', party({
      invitationCoverFileItemId: 'inv1', invitationCoverUrl: '/api/files/inv1/thumbnail?size=medium',
      liveCoverFileItemId: 'live1', liveCoverUrl: '/api/files/live1/thumbnail?size=medium',
    }));
    const user = userEvent.setup();

    await user.click(screen.getByTestId('party-image-remove-cover-live'));
    await user.click(screen.getByTestId('party-cover-save-live'));

    await waitFor(() => expect(onPartyUpdated).toHaveBeenCalled());
    expect(sentBody(mock)).toEqual({
      invitationCoverFileItemId: 'inv1', liveCoverFileItemId: null, version: 3,
    });
  });

  it('says so when the photograph cannot be used', async () => {
    mount('invitation', party({ invitationCoverFileItemId: 'f1', invitationCoverUrl: '/p' }), {
      [PUT_COVERS]: () => errorResponse(400, { error: 'invalid_media' }),
    });
    const user = userEvent.setup();

    await user.click(screen.getByTestId('party-cover-save-invitation'));
    expect(await screen.findByTestId('party-cover-invalid-invitation')).toBeInTheDocument();
  });
});
