/* eslint-disable */
// Review fixtures for PARTY CREW — the real components, measured in a browser.
//
// The same tool as `partyWorkspace.fixtures.tsx`, for the three screens the
// workspace's own fixtures cannot reach: the code screen, the device-limit
// screen and the collaborator's shell. They are the ones a person meets on a
// phone at a party, in a hurry, so they are exactly the ones worth measuring —
// jsdom has no layout engine and can say nothing about a 44px target or a page
// that scrolls sideways at 320px.
//
//   PARTY_FIXTURE_DIR=/tmp/party npx vitest run --config vitest.fixtures.config.ts
//   node scripts/check-party-workspace-layout.mjs
//
// Invented data only: no party, person or address below is real.

import { mkdirSync, writeFileSync } from 'node:fs';
import { afterEach, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../../test-utils';
import { PartyCrewPairingPage } from '../../pages/PartyCrewPairingPage';
import { PartyCrewWorkspacePage } from '../../pages/PartyCrewWorkspacePage';
import { PartyCrewPanel } from '../workspace/PartyCrewPanel';

const OUT = process.env.PARTY_FIXTURE_DIR ?? '/tmp/party-fixtures';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const PARTY_ID = 'p1';
const CREW = `/api/parties/${PARTY_ID}/crew`;

const CO_ORGANIZER = [
  'party-crew.details.manage', 'party-crew.lifecycle.manage', 'party-crew.experience.manage',
  'party-crew.guests.read', 'party-crew.invitations.manage', 'party-crew.attendance.manage',
  'party-crew.contributions.configure', 'party-crew.contributions.moderate',
  'party-crew.activities.manage', 'party-crew.activities.control',
  'party-crew.screens.manage', 'party-crew.print.manage',
];

const devices = [
  { grantId: 'g1', label: 'iPhone · Safari', pairedAt: '2027-06-01T10:00:00Z', lastUsedAt: '2027-06-11T22:40:00Z', isCurrent: true },
  { grantId: 'g2', label: 'Android · Chrome', pairedAt: '2027-06-02T10:00:00Z', lastUsedAt: '2027-06-10T18:05:00Z', isCurrent: false },
];

async function capture(name: string, waitFor: string) {
  await screen.findByTestId(waitFor);
  await new Promise((r) => setTimeout(r, 200));
  mkdirSync(OUT, { recursive: true });
  writeFileSync(`${OUT}/${name}.html`, document.body.innerHTML, 'utf8');
}

function mountPairing(at: string, handlers: Record<string, () => Response>) {
  installFetchMock(handlers);
  render(
    <AuthedWrapper>
      <MemoryRouter initialEntries={[at]}>
        <Routes>
          <Route path="/party/crew/verify" element={<PartyCrewPairingPage />} />
          <Route path="/party/crew/devices" element={<PartyCrewPairingPage />} />
          <Route path="/party/crew/:partyId" element={<div />} />
        </Routes>
      </MemoryRouter>
    </AuthedWrapper>,
  );
}

it('crew — the code screen', async () => {
  mountPairing('/party/crew/verify', {
    'GET /api/party-crew/auth/challenge': () => jsonResponse({
      partyTitle: 'Il compleanno di Marta',
      roleKey: 'co_organizer',
      maskedEmail: 'l••••@example.com',
      expiresAt: '2027-06-12T18:10:00Z',
    }),
  });
  await capture('crew-code', 'crew-code');
});

it('crew — two devices already', async () => {
  mountPairing('/party/crew/devices', {
    'GET /api/party-crew/auth/devices': () => jsonResponse(
      devices.map((d) => ({ ...d, isCurrent: false }))),
  });
  await capture('crew-device-limit', 'crew-limit-devices');
});

it('crew — the collaborator’s party', { timeout: 20_000 }, async () => {
  installFetchMock({
    'GET /api/party-crew/session': () => jsonResponse({
      partyId: PARTY_ID, partyTitle: 'Il compleanno di Marta',
      displayName: 'Marco Bianchi', roleKey: 'co_organizer', capabilities: CO_ORGANIZER,
    }),
    'GET /api/party-crew/party': () => jsonResponse({
      id: PARTY_ID, title: 'Il compleanno di Marta',
      description: 'Cinquant’anni, e si festeggia.',
      status: 'live', eventStartsAt: '2027-06-12T19:30:00Z',
      liveStartedAt: '2027-06-12T19:35:00Z', liveEndedAt: null,
      guestAccessExpiresAt: null, libraryAccessExpiresAt: null,
      version: 3, createdAt: '2027-01-01T00:00:00Z', updatedAt: '2027-01-01T00:00:00Z',
      mediaSources: [{ albumId: 'a1', albumName: 'Marta 50', role: 'main', sortOrder: 0 }],
      canChangeMainMediaSource: false,
    }),
    'GET /api/party-crew/album-settings': () => jsonResponse({
      albumId: 'a1', partyId: PARTY_ID, showOnTv: true, partyMode: true,
      partyUrl: '/party/Hq7Fn2', uploadEnabled: true, uploadUrl: '/party/Kd3Pa9/upload',
      requireUploadApproval: true, requireMessageApproval: false,
      photoSlideSeconds: 9, maxVideoSlideSeconds: 60,
      maxPhotoUploadsPerParticipant: 20, maxVideoUploadsPerParticipant: 5,
      maxMessagesPerParticipant: 3, gameEnabled: true,
    }),
    'GET /api/party-crew/guest-content': () => jsonResponse([]),
    'POST /api/party-crew/guest-directory/query': () => jsonResponse({
      partyId: PARTY_ID, partyStatus: 'live', mailAvailable: true, shareAvailable: true,
      items: [], nextCursor: null,
      summary: {
        groups: 12, otherArrivals: 3,
        rsvp: {
          groups: 12, invited: 12, missingResponses: 2, attending: 28, declined: 4,
          expectedPeople: 28, unansweredGroups: 2,
        },
        attendance: {
          expectedPeople: 28, expectedArrived: 21, expectedMissing: 7,
          unexpectedKnownGuests: 1, otherArrivals: 3, totalArrivals: 24,
        },
      },
    }),
    'GET /api/party-crew/uploads': () =>
      jsonResponse({ albumId: 'a1', requireUploadApproval: true, items: [] }),
    'GET /api/party-crew/messages': () => jsonResponse({
      albumId: 'a1', isOwner: false, partyActive: true, requireMessageApproval: false, items: [],
    }),
    'GET /api/party-crew/devices': () => jsonResponse(devices),
  });
  render(
    <AuthedWrapper>
      <MemoryRouter initialEntries={[`/party/crew/${PARTY_ID}`]}>
        <Routes>
          <Route path="/party/crew/:partyId" element={<PartyCrewWorkspacePage />} />
        </Routes>
      </MemoryRouter>
    </AuthedWrapper>,
  );
  await capture('crew-workspace', 'party-title');
});

it('crew — the host’s collaborators, with their devices', async () => {
  installFetchMock({
    [`GET ${CREW}`]: () => jsonResponse({
      mailAvailable: true,
      assignableRoles: ['co_organizer', 'director'],
      collaborators: [
        {
          id: 'c1', displayName: 'Marco Bianchi', email: 'marco.bianchi@example.com',
          roleKey: 'co_organizer', version: 2, createdAt: '2027-06-01T10:00:00Z',
          capabilities: CO_ORGANIZER, activeDevices: 2, maxDevices: 2,
          hasPendingInvite: false, inviteExpiresAt: null, devices,
        },
        {
          id: 'c2', displayName: 'Sara', email: 'sara@example.com',
          roleKey: 'director', version: 1, createdAt: '2027-06-03T10:00:00Z',
          capabilities: [], activeDevices: 0, maxDevices: 2,
          hasPendingInvite: true, inviteExpiresAt: '2027-06-04T10:00:00Z', devices: [],
        },
      ],
    }),
  });
  // Inside the `.pw` shell, because that is where it lives: the workspace's
  // design tokens (`--pw-tap` and the rest) are declared on it, so a panel
  // measured outside one is not the panel the host sees.
  render(
    <AuthedWrapper>
      <main className="pw"><PartyCrewPanel partyId={PARTY_ID} /></main>
    </AuthedWrapper>,
  );
  await capture('crew-owner-panel', 'party-crew-list');
});

it('crew — adding somebody', async () => {
  installFetchMock({
    [`GET ${CREW}`]: () => jsonResponse({
      collaborators: [], mailAvailable: true, assignableRoles: ['co_organizer', 'director'],
    }),
  });
  // Inside the `.pw` shell, because that is where it lives: the workspace's
  // design tokens (`--pw-tap` and the rest) are declared on it, so a panel
  // measured outside one is not the panel the host sees.
  render(
    <AuthedWrapper>
      <main className="pw"><PartyCrewPanel partyId={PARTY_ID} /></main>
    </AuthedWrapper>,
  );
  await userEvent.click(await screen.findByTestId('party-crew-add'));
  await capture('crew-owner-form', 'party-crew-new');
});
