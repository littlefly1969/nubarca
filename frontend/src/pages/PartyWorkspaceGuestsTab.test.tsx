import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../test-utils';
import { PartyWorkspacePage } from './PartyWorkspacePage';

// The guest list lives IN the party's workspace — its own tab, between writing
// the invitation and running the evening — not in an application of its own.

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const PARTY_ID = 'p1';
const ALBUM_ID = 'a1';

const party = {
  id: PARTY_ID, title: 'Festa di Marta', description: null, status: 'draft',
  eventStartsAt: null, liveStartedAt: null, liveEndedAt: null,
  guestAccessExpiresAt: null, libraryAccessExpiresAt: null,
  version: 1, createdAt: '2027-01-01T00:00:00Z', updatedAt: '2027-01-01T00:00:00Z',
  mediaSources: [{ albumId: ALBUM_ID, albumName: 'Album di Marta', role: 'main', sortOrder: 0 }],
  canChangeMainMediaSource: true,
};

it('offers the guest list as a tab between Before and Live, and opens it', async () => {
  const mock = installFetchMock({
    [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(party),
    [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse({
      albumId: ALBUM_ID, partyId: PARTY_ID, showOnTv: false, partyMode: false, partyUrl: null,
      uploadEnabled: false, uploadUrl: null, requireUploadApproval: false, requireMessageApproval: false,
      photoSlideSeconds: 9, maxVideoSlideSeconds: 60, maxPhotoUploadsPerParticipant: 0,
      maxVideoUploadsPerParticipant: 0, maxMessagesPerParticipant: 0, gameEnabled: false,
    }),
    [`GET /api/parties/${PARTY_ID}/guest-content`]: () => jsonResponse([]),
    [`GET /api/parties/${PARTY_ID}/guest-list`]: () => jsonResponse({
      partyId: PARTY_ID, partyStatus: 'draft', mailAvailable: true,
      summary: { groups: 0, invited: 0, missingResponses: 0, attending: 0, declined: 0, expectedPeople: 0, unansweredGroups: 0 },
      groups: [], questions: [],
    }),
  });
  render(
    <AuthedWrapper>
      <MemoryRouter initialEntries={[`/parties/${PARTY_ID}`]}>
        <Routes>
          <Route path="/parties/:partyId" element={<PartyWorkspacePage />} />
        </Routes>
      </MemoryRouter>
    </AuthedWrapper>,
  );

  const tabs = await screen.findAllByRole('tab');
  expect(tabs.map((tab) => tab.id)).toEqual([
    'party-tab-overview', 'party-tab-before', 'party-tab-guests', 'party-tab-live', 'party-tab-after', 'party-tab-photos',
  ]);
  // "Ospiti", not "Invitati": a party may be open and invite nobody at all.
  expect(screen.getByTestId('party-tab-guests')).toHaveTextContent('Ospiti');
  // Nothing is asked of the guest list until the host opens it.
  expect(mock.calls.some((c) => c.url.endsWith('/guest-list'))).toBe(false);

  await userEvent.click(screen.getByTestId('party-tab-guests'));

  // With no invitation this is an OPEN party, and the tab says so rather than
  // asking for a guest list it does not need — which stays on offer, folded.
  expect(await screen.findByTestId('party-guests-open')).toBeInTheDocument();
  expect(screen.getByTestId('party-tab-guests')).toHaveAttribute('aria-selected', 'true');
  expect(screen.queryByTestId('party-guests-metrics')).not.toBeInTheDocument();
  expect(screen.getByTestId('party-guests-manage')).toHaveTextContent('Lista invitati (facoltativa)');
  expect(screen.getByTestId('party-guests-empty')).toBeInTheDocument();
});
