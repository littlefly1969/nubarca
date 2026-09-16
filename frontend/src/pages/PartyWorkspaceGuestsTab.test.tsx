import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../test-utils';
import { PartyWorkspacePage } from './PartyWorkspacePage';

// The guest console lives IN the party's workspace — its own section, between
// what the guests will see and the photographs they will take — not in an
// application of its own. The section travels in the URL, because what is
// inside it does too.

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

const emptyDirectory = {
  partyId: PARTY_ID, partyStatus: 'draft', mailAvailable: true, shareAvailable: true,
  summary: {
    groups: 0,
    otherArrivals: 0,
    rsvp: {
      groups: 0, invited: 0, missingResponses: 0, attending: 0, declined: 0, expectedPeople: 0, unansweredGroups: 0,
    },
    attendance: {
      expectedPeople: 0, expectedArrived: 0, expectedMissing: 0,
      unexpectedKnownGuests: 0, otherArrivals: 0, totalArrivals: 0,
    },
  },
  items: [],
  nextCursor: null,
};

function renderWorkspace(at = '') {
  const mock = installFetchMock({
    [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(party),
    [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse({
      albumId: ALBUM_ID, partyId: PARTY_ID, showOnTv: false, partyMode: false, partyUrl: null,
      uploadEnabled: false, uploadUrl: null, requireUploadApproval: false, requireMessageApproval: false,
      photoSlideSeconds: 9, maxVideoSlideSeconds: 60, maxPhotoUploadsPerParticipant: 0,
      maxVideoUploadsPerParticipant: 0, maxMessagesPerParticipant: 0, gameEnabled: false,
    }),
    [`GET /api/parties/${PARTY_ID}/guest-content`]: () => jsonResponse([]),
    [`POST /api/parties/${PARTY_ID}/guest-directory/query`]: () => jsonResponse(emptyDirectory),
  });
  render(
    <AuthedWrapper>
      <MemoryRouter initialEntries={[`/parties/${PARTY_ID}${at}`]}>
        <Routes>
          <Route path="/parties/:partyId" element={<PartyWorkspacePage />} />
        </Routes>
      </MemoryRouter>
    </AuthedWrapper>,
  );
  return mock;
}

/** Every directory read this page made, as the bodies it actually sent. */
function directoryQueries(mock: { calls: { url: string; body: string | null | FormData }[] }) {
  return mock.calls
    .filter((c) => c.url.includes('/guest-directory'))
    .map((c) => JSON.parse(String(c.body)) as { take?: number | null });
}

it('asks the guest list for NUMBERS, and for names only when the host opens it', async () => {
  const mock = renderWorkspace();

  await screen.findByTestId('party-tab-guests');
  // "Ospiti", not "Invitati": a party may be open and invite nobody at all.
  expect(screen.getByTestId('party-tab-guests')).toHaveTextContent('Ospiti');

  // The summary needs the counts, and asks for exactly those: `take: 0` is the
  // totals alone. No card, no person, no name reaches a page that is not the
  // console — which is what keeps the guest list out of every other surface.
  const beforeOpening = directoryQueries(mock);
  expect(beforeOpening.length).toBeGreaterThan(0);
  expect(beforeOpening.every((q) => q.take === 0)).toBe(true);

  await userEvent.click(screen.getByTestId('party-tab-guests'));

  // With no invitation this is an OPEN party, and the console says so rather
  // than asking for a guest list it does not need.
  expect(await screen.findByTestId('guest-open')).toBeInTheDocument();
  expect(screen.getByTestId('party-tab-guests')).toHaveAttribute('aria-selected', 'true');
  expect(screen.queryByTestId('guest-metrics')).not.toBeInTheDocument();

  // Now it reads a PAGE — still in pages, never the whole guest list.
  expect(directoryQueries(mock).some((q) => q.take !== 0)).toBe(true);
  expect(mock.calls.some((c) => c.url.includes('/guest-list'))).toBe(false);
});

it('keeps the open section in the URL, so a reload comes back to it', async () => {
  renderWorkspace('?section=guests');

  expect(await screen.findByTestId('guest-open')).toBeInTheDocument();
  expect(screen.getByTestId('party-tab-guests')).toHaveAttribute('aria-selected', 'true');
});

it('still honours a link made when the sections were tabs', async () => {
  renderWorkspace('?tab=guests');

  expect(await screen.findByTestId('guest-open')).toBeInTheDocument();
  expect(screen.getByTestId('party-tab-guests')).toHaveAttribute('aria-selected', 'true');
});
