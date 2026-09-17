import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { I18nProvider } from '../i18n';
import {
  AuthedWrapper, emptyResponse, errorResponse, installFetchMock, jsonResponse,
} from '../test-utils';
import { PartyCrewWorkspacePage } from './PartyCrewWorkspacePage';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const PARTY_ID = 'p1';

const CO_ORGANIZER = [
  'party-crew.details.manage', 'party-crew.lifecycle.manage', 'party-crew.experience.manage',
  'party-crew.guests.read', 'party-crew.invitations.manage', 'party-crew.attendance.manage',
  'party-crew.contributions.configure', 'party-crew.contributions.moderate',
  'party-crew.activities.manage', 'party-crew.activities.control',
  'party-crew.screens.manage', 'party-crew.print.manage',
];

const DIRECTOR = [
  'party-crew.lifecycle.manage',
  'party-crew.contributions.configure', 'party-crew.contributions.moderate',
  'party-crew.activities.manage', 'party-crew.activities.control',
  'party-crew.screens.manage', 'party-crew.print.manage',
];

const session = (over: Record<string, unknown> = {}) => ({
  partyId: PARTY_ID,
  partyTitle: 'Compleanno di Lia',
  displayName: 'Marco',
  roleKey: 'co_organizer',
  capabilities: CO_ORGANIZER,
  ...over,
});

const party = (over: Record<string, unknown> = {}) => ({
  id: PARTY_ID, title: 'Compleanno di Lia', description: null, status: 'draft',
  eventStartsAt: null, liveStartedAt: null, liveEndedAt: null,
  guestAccessExpiresAt: null, libraryAccessExpiresAt: null,
  version: 1, createdAt: '2027-01-01T00:00:00Z', updatedAt: '2027-01-01T00:00:00Z',
  mediaSources: [{ albumId: 'a1', albumName: 'Album', role: 'main', sortOrder: 0 }],
  canChangeMainMediaSource: true,
  ...over,
});

const albumSettings = {
  albumId: 'a1', partyId: PARTY_ID, showOnTv: false, partyMode: true,
  partyUrl: '/party/tok', uploadEnabled: true, uploadUrl: '/party/uptok/upload',
  requireUploadApproval: false, requireMessageApproval: false,
  photoSlideSeconds: 9, maxVideoSlideSeconds: 60,
  maxPhotoUploadsPerParticipant: 0, maxVideoUploadsPerParticipant: 0,
  maxMessagesPerParticipant: 0, gameEnabled: false,
};

const counts = {
  partyId: PARTY_ID, partyStatus: 'draft', mailAvailable: true, shareAvailable: true,
  items: [], nextCursor: null,
  summary: {
    groups: 0, otherArrivals: 0,
    rsvp: {
      groups: 0, invited: 0, missingResponses: 0, attending: 0, declined: 0,
      expectedPeople: 0, unansweredGroups: 0,
    },
    attendance: {
      expectedPeople: 0, expectedArrived: 0, expectedMissing: 0,
      unexpectedKnownGuests: 0, otherArrivals: 0, totalArrivals: 0,
    },
  },
};

function mount(handlers: Record<string, () => Response> = {}) {
  const mock = installFetchMock({
    'GET /api/party-crew/session': () => jsonResponse(session()),
    'GET /api/party-crew/party': () => jsonResponse(party()),
    'GET /api/party-crew/album-settings': () => jsonResponse(albumSettings),
    'GET /api/party-crew/guest-content': () => jsonResponse([]),
    'POST /api/party-crew/guest-directory/query': () => jsonResponse(counts),
    'GET /api/party-crew/uploads': () =>
      jsonResponse({ albumId: 'a1', requireUploadApproval: false, items: [] }),
    'GET /api/party-crew/messages': () => jsonResponse({
      albumId: 'a1', isOwner: false, partyActive: true, requireMessageApproval: false, items: [],
    }),
    'GET /api/party-crew/devices': () => jsonResponse([]),
    ...handlers,
  });
  // The application's own providers, because the crew routes live inside them
  // exactly as every other route does. Nothing below reads a session: a crew
  // route is anonymous and never answers 401, so `invalidateAuth` is never
  // reached. The provider is here because the tree needs one, not because the
  // surface has one.
  render(
    <AuthedWrapper>
      <I18nProvider>
        <MemoryRouter initialEntries={[`/party/crew/${PARTY_ID}`]}>
          <Routes>
            <Route path="/party/crew/:partyId" element={<PartyCrewWorkspacePage />} />
          </Routes>
        </MemoryRouter>
      </I18nProvider>
    </AuthedWrapper>,
  );
  return mock;
}

describe('the party, run by somebody who is not its host', () => {
  it('is the same workspace, asking a different family of routes', async () => {
    const mock = mount();

    // The host's own product: the party's name, its state, its sections.
    expect(await screen.findByTestId('party-title')).toHaveTextContent('Compleanno di Lia');
    expect(screen.getByTestId('party-status')).toHaveAttribute('data-status', 'draft');
    expect(screen.getByTestId('party-tab-summary')).toBeInTheDocument();

    // And NOT ONE call to the host's routes. No party id, no album id, no
    // owner id was ever sent: the device cookie resolves all three.
    await waitFor(() => expect(mock.calls.length).toBeGreaterThan(3));
    for (const call of mock.calls) {
      expect(call.url).toMatch(/^\/api\/party-crew\//);
      expect(call.url).not.toContain('/api/parties/');
      expect(call.url).not.toContain('/api/albums/');
      expect(call.url).not.toContain(PARTY_ID);
    }
  });

  it('never offers the host’s own settings, at any role', async () => {
    mount();
    await screen.findByTestId('party-title');
    // Duplicating the party, tearing it down, and who else is helping: none of
    // it is delegable, so the section is not there to open.
    expect(screen.queryByTestId('party-tab-settings')).not.toBeInTheDocument();
  });

  it('gives a director the evening and not the guest list', async () => {
    mount({
      'GET /api/party-crew/session': () => jsonResponse(
        session({ roleKey: 'director', displayName: 'Sara', capabilities: DIRECTOR })),
    });

    await screen.findByTestId('party-title');
    expect(screen.getByTestId('party-tab-photos')).toBeInTheDocument();
    expect(screen.getByTestId('party-tab-activities')).toBeInTheDocument();
    expect(screen.getByTestId('party-tab-screens')).toBeInTheDocument();
    expect(screen.queryByTestId('party-tab-guests')).not.toBeInTheDocument();
    expect(screen.queryByTestId('party-tab-experience')).not.toBeInTheDocument();
  });

  it('never fetches a guest for a director, not even the counts', async () => {
    const mock = mount({
      'GET /api/party-crew/session': () => jsonResponse(
        session({ roleKey: 'director', capabilities: DIRECTOR })),
    });

    await screen.findByTestId('party-title');
    await waitFor(() => expect(mock.calls.length).toBeGreaterThan(2));
    // The workspace's counts-only query is still a guest query, and a role
    // without guests.read has no business making it.
    expect(mock.calls.some((call) => call.url.includes('guest-directory'))).toBe(false);
  });

  it('says who this device is, and offers the two things that are theirs', async () => {
    mount({
      'GET /api/party-crew/devices': () => jsonResponse([
        { grantId: 'g1', label: 'iPhone', pairedAt: '2027-06-01T10:00:00Z', lastUsedAt: null, isCurrent: true },
        { grantId: 'g2', label: 'Android', pairedAt: '2027-06-02T10:00:00Z', lastUsedAt: null, isCurrent: false },
      ]),
    });

    const identity = await screen.findByTestId('crew-identity');
    expect(identity).toHaveTextContent('Marco');
    expect(identity).toHaveTextContent('Co-organizzatore');

    await userEvent.click(identity.querySelector('summary')!);
    expect(await screen.findByTestId('crew-my-devices')).toBeInTheDocument();
    // The one they are holding cannot be dropped from the list — leaving is
    // its own button, and it says what it does.
    expect(screen.queryByTestId('crew-my-device-drop-g1')).not.toBeInTheDocument();
    expect(screen.getByTestId('crew-my-device-drop-g2')).toBeInTheDocument();
    expect(screen.getByTestId('crew-sign-out')).toBeInTheDocument();
  });

  it('says so plainly when this device no longer has access', async () => {
    mount({ 'GET /api/party-crew/session': () => errorResponse(404) });

    expect(await screen.findByText(/Non hai più accesso/)).toBeInTheDocument();
    expect(screen.getByText(/chiedi un nuovo link/)).toBeInTheDocument();
  });

  it('leaves, and does not pretend the party is still there afterwards', async () => {
    mount({ 'DELETE /api/party-crew/session': () => emptyResponse() });

    await userEvent.click((await screen.findByTestId('crew-identity')).querySelector('summary')!);
    await userEvent.click(await screen.findByTestId('crew-sign-out'));

    expect(await screen.findByText(/Questo dispositivo non gestisce più la festa/))
      .toBeInTheDocument();
  });
});
