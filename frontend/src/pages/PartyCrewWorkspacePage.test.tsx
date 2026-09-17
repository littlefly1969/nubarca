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
  'party-crew.lifecycle.manage', 'party-crew.contributions.moderate',
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
    [`GET /api/party-crew/parties/${PARTY_ID}/session`]: () => jsonResponse(session()),
    [`GET /api/party-crew/parties/${PARTY_ID}/party`]: () => jsonResponse(party()),
    [`GET /api/party-crew/parties/${PARTY_ID}/album-settings`]: () => jsonResponse(albumSettings),
    [`GET /api/party-crew/parties/${PARTY_ID}/guest-content`]: () => jsonResponse([]),
    [`POST /api/party-crew/parties/${PARTY_ID}/guest-directory/query`]: () => jsonResponse(counts),
    [`GET /api/party-crew/parties/${PARTY_ID}/uploads`]: () =>
      jsonResponse({ albumId: 'a1', requireUploadApproval: false, items: [] }),
    [`GET /api/party-crew/parties/${PARTY_ID}/messages`]: () => jsonResponse({
      albumId: 'a1', isOwner: false, partyActive: true, requireMessageApproval: false, items: [],
    }),
    [`GET /api/party-crew/parties/${PARTY_ID}/devices`]: () => jsonResponse([]),
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
      expect(call.url).toMatch(/^\/api\/party-crew\/parties\//);
      // The host's own families are never touched, and no album or owner id is
      // ever sent. The PARTY id is: it says which of this browser's
      // assignments the request is about, and the server checks it against a
      // grant this device actually holds.
      expect(call.url).not.toContain('/api/parties/');
      expect(call.url).not.toContain('/api/albums/');
      expect(call.url).toContain(`/api/party-crew/parties/${PARTY_ID}/`);
    }
  });

  it('opens the party the URL names, and says so when this browser has no grant for it', async () => {
    // THE URL IS THE SELECTOR. One browser may hold several assignments, so
    // the page asks for the party in the address bar — and a party this device
    // has no grant for is told so, never quietly swapped for one it does have.
    const OTHER = 'p2';
    const mock = installFetchMock({
      [`GET /api/party-crew/parties/${OTHER}/session`]: () => errorResponse(404),
    });
    render(
      <AuthedWrapper>
        <I18nProvider>
          <MemoryRouter initialEntries={[`/party/crew/${OTHER}`]}>
            <Routes>
              <Route path="/party/crew/:partyId" element={<PartyCrewWorkspacePage />} />
            </Routes>
          </MemoryRouter>
        </I18nProvider>
      </AuthedWrapper>,
    );

    expect(await screen.findByText(/Non hai più accesso/)).toBeInTheDocument();
    // It asked about the party in the URL and nothing else.
    expect(mock.calls.every((call) => call.url.includes(`/parties/${OTHER}/`))).toBe(true);
    expect(mock.calls.some((call) => call.url.includes(`/parties/${PARTY_ID}/`))).toBe(false);
  });

  it('carries the party of the URL into every request the workspace makes', async () => {
    const mock = mount();
    await screen.findByTestId('party-title');
    await waitFor(() => expect(mock.calls.length).toBeGreaterThan(3));

    // Not one request went anywhere but this party's own family.
    for (const call of mock.calls) {
      expect(call.url.startsWith(`/api/party-crew/parties/${PARTY_ID}/`)).toBe(true);
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
      [`GET /api/party-crew/parties/${PARTY_ID}/session`]: () => jsonResponse(
        session({ roleKey: 'director', displayName: 'Sara', capabilities: DIRECTOR })),
    });

    await screen.findByTestId('party-title');
    expect(screen.getByTestId('party-tab-photos')).toBeInTheDocument();
    expect(screen.getByTestId('party-tab-activities')).toBeInTheDocument();
    expect(screen.getByTestId('party-tab-screens')).toBeInTheDocument();
    expect(screen.queryByTestId('party-tab-guests')).not.toBeInTheDocument();
    expect(screen.queryByTestId('party-tab-experience')).not.toBeInTheDocument();
  });

  it('gives a director the numbers and never a person', async () => {
    const mock = mount({
      [`GET /api/party-crew/parties/${PARTY_ID}/session`]: () => jsonResponse(
        session({ roleKey: 'director', capabilities: DIRECTOR })),
    });

    await screen.findByTestId('party-title');
    await waitFor(() => expect(mock.calls.length).toBeGreaterThan(2));

    // Somebody running the evening needs to know how many are expected and how
    // many arrived — so the counts ARE read, from a route that answers the
    // summary and nothing else.
    expect(mock.calls.some((call) => call.url.endsWith('/guest-counts'))).toBe(true);
    // And the directory, which returns people, is never asked.
    expect(mock.calls.some((call) => call.url.includes('guest-directory'))).toBe(false);
  });

  it('shows a director no way to configure what it only moderates', async () => {
    mount({
      [`GET /api/party-crew/parties/${PARTY_ID}/session`]: () => jsonResponse(
        session({ roleKey: 'director', capabilities: DIRECTOR })),
    });

    await screen.findByTestId('party-title');
    await userEvent.click(screen.getByTestId('party-tab-photos'));

    // The QUEUE is theirs — it is what they are there for.
    expect(await screen.findByTestId('party-photos-queue')).toBeInTheDocument();
    // The SWITCH is not. A control that answers 404 is worse than one that is
    // not drawn, so the role does not grow to fit the surface: the surface
    // reads the role.
    expect(screen.queryByTestId('party-photos-uploads')).not.toBeInTheDocument();
  });

  it('never shows a collaborator the venue’s printers', async () => {
    const mock = mount({
      [`GET /api/party-crew/parties/${PARTY_ID}/session`]: () => jsonResponse(
        session({ roleKey: 'director', capabilities: DIRECTOR })),
      [`GET /api/party-crew/parties/${PARTY_ID}/print-settings`]: () => jsonResponse({
        albumId: 'a1', enabled: false, printStationId: null, printerDeviceId: null,
        photo: { enabled: false, maxPrints: 0, perGuest: 0, used: 0 },
        strip: { enabled: false, maxPrints: 0, perGuest: 0, used: 0 },
        footerText: '',
      }),
    });

    await screen.findByTestId('party-title');
    await userEvent.click(screen.getByTestId('party-tab-screens'));
    expect(await screen.findByTestId('party-print-crew-station')).toBeInTheDocument();

    // Enumerating the installation's hardware is the host administering their
    // own equipment. The request is never made — not made and refused.
    expect(mock.calls.some((call) => call.url.includes('print-stations'))).toBe(false);
    expect(mock.calls.some((call) => call.url.includes('/api/print/'))).toBe(false);
  });

  it('says who this device is, and offers the two things that are theirs', async () => {
    mount({
      [`GET /api/party-crew/parties/${PARTY_ID}/devices`]: () => jsonResponse([
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
    expect(screen.getByTestId('crew-leave-party')).toBeInTheDocument();
    expect(screen.getByTestId('crew-disconnect-device')).toBeInTheDocument();
  });

  it('says so plainly when this device no longer has access', async () => {
    mount({ [`GET /api/party-crew/parties/${PARTY_ID}/session`]: () => errorResponse(404) });

    expect(await screen.findByText(/Non hai più accesso/)).toBeInTheDocument();
    expect(screen.getByText(/chiedi un nuovo link/)).toBeInTheDocument();
  });

  it('unmounts the party when this device leaves it', async () => {
    mount({ [`DELETE /api/party-crew/parties/${PARTY_ID}/session`]: () => emptyResponse() });

    // The party is on screen, with its data.
    expect(await screen.findByTestId('party-title')).toBeInTheDocument();

    await userEvent.click((await screen.findByTestId('crew-identity')).querySelector('summary')!);
    await userEvent.click(await screen.findByTestId('crew-leave-party'));

    expect(await screen.findByText(/non gestisce più questa festa/)).toBeInTheDocument();
    // AND THE WORKSPACE IS GONE. Hiding a menu would leave every name, arrival
    // and photograph this person had loaded sitting in the DOM of a session
    // they just ended.
    expect(screen.queryByTestId('party-title')).not.toBeInTheDocument();
    expect(screen.queryByTestId('party-tab-summary')).not.toBeInTheDocument();
  });

  it('unmounts the party when this browser disconnects entirely', async () => {
    mount({ 'DELETE /api/party-crew/device': () => emptyResponse() });

    await screen.findByTestId('party-title');
    await userEvent.click((await screen.findByTestId('crew-identity')).querySelector('summary')!);
    await userEvent.click(await screen.findByTestId('crew-disconnect-device'));

    expect(await screen.findByText(/non gestisce più nessuna festa/)).toBeInTheDocument();
    expect(screen.queryByTestId('party-title')).not.toBeInTheDocument();
  });
});
