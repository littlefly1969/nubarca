/* eslint-disable */
// Review fixtures for the Party workspace — the REAL components, the real data.
//
// jsdom has no layout engine, so the unit suite proves structure and behaviour
// and can say nothing about whether a phone at 320px scrolls sideways, whether
// a thumb can hit a control, or whether two sticky panels cover each other.
// That is what `scripts/check-party-workspace-layout.mjs` measures — in a real
// browser, with the real stylesheets.
//
// It needs real markup to measure, and hand-writing a copy of what the
// components emit would be a second product free to drift from the first. So
// this renders the ACTUAL workspace against mocked API responses, exactly as
// the tests do, and writes each state's `body.innerHTML` to a directory the
// browser script then opens.
//
// It is a dev/QA tool, not a CI job, and is deliberately outside the suite's
// include pattern (`*.test.tsx`). Run it with:
//
//   npx vitest run --include 'src/**/*.fixtures.tsx'
//
// It contains no production data: every party, guest and photograph below is
// invented here.

import { mkdirSync, writeFileSync } from 'node:fs';
import { afterEach, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../../test-utils';
import { PartyWorkspacePage } from '../../pages/PartyWorkspacePage';
import { PartiesPage } from '../../pages/PartiesPage';

const OUT = process.env.PARTY_FIXTURE_DIR ?? '/tmp/party-fixtures';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const PARTY_ID = 'p1';
const ALBUM_ID = 'a1';

const party = (over: Record<string, unknown> = {}) => ({
  id: PARTY_ID, title: 'Il compleanno di Marta', description: 'Cinquant’anni, e si festeggia.',
  status: 'draft', eventStartsAt: '2027-06-12T19:30:00Z', liveStartedAt: null, liveEndedAt: null,
  guestAccessExpiresAt: null, libraryAccessExpiresAt: null,
  version: 3, createdAt: '2027-01-01T00:00:00Z', updatedAt: '2027-01-01T00:00:00Z',
  mediaSources: [{ albumId: ALBUM_ID, albumName: 'Marta 50', role: 'main', sortOrder: 0 }],
  canChangeMainMediaSource: true,
  ...over,
});

const albumParty = (over: Record<string, unknown> = {}) => ({
  albumId: ALBUM_ID, partyId: PARTY_ID, showOnTv: true, partyMode: true,
  partyUrl: '/party/Hq7Fn2', uploadEnabled: true, uploadUrl: '/party/Kd3Pa9/upload',
  requireUploadApproval: true, requireMessageApproval: false,
  photoSlideSeconds: 9, maxVideoSlideSeconds: 60,
  maxPhotoUploadsPerParticipant: 20, maxVideoUploadsPerParticipant: 5,
  maxMessagesPerParticipant: 3, gameEnabled: true, priorityVotingEnabled: false,
  minChallengeIntervalSeconds: 300, maxChallengeIntervalSeconds: 540,
  votesPerGuest: 3, maxChallengesPerSession: null,
  ...over,
});

const slot = (kind: string, over: Record<string, unknown> = {}) => ({
  kind, enabled: false, visibleBefore: true, visibleLive: true, visibleAfter: false,
  content: {}, version: 1, mediaPresentation: 'inline', mediaFileItemId: null, mediaUrl: null,
  ...over,
});

const SLOTS = [
  slot('invitation', {
    enabled: true, visibleLive: false,
    content: { headline: 'Vieni a festeggiare', message: 'Ci sarà da mangiare, da ballare e da ridere.' },
  }),
  slot('location', { enabled: true, content: { venueName: 'Cascina Rosa', address: 'Via dei Platani 14' } }),
  slot('dress-code'),
  slot('menu', { enabled: true, content: { intro: 'Cucina di stagione, e una torta enorme.' } }),
  slot('info'),
  slot('thank-you', { visibleBefore: false, visibleLive: false, visibleAfter: true }),
];

const counts = (over: { groups?: number; rsvp?: Record<string, number>; attendance?: Record<string, number> } = {}) => ({
  partyId: PARTY_ID, partyStatus: 'draft', mailAvailable: true, shareAvailable: true,
  items: [], nextCursor: null,
  summary: {
    groups: over.groups ?? 18,
    otherArrivals: 0,
    rsvp: {
      groups: over.groups ?? 18, invited: 46, missingResponses: 9, attending: 31,
      declined: 6, expectedPeople: 31, unansweredGroups: 4, ...over.rsvp,
    },
    attendance: {
      expectedPeople: 31, expectedArrived: 22, expectedMissing: 9,
      unexpectedKnownGuests: 1, otherArrivals: 3, totalArrivals: 26, ...over.attendance,
    },
  },
});

const UPLOADS = {
  albumId: ALBUM_ID, requireUploadApproval: true,
  items: [
    { fileItemId: 'f1', name: 'IMG_2201.jpg', status: 'pending', uploadedAt: '2027-06-12T21:14:00Z', thumbnailUrl: '' },
    { fileItemId: 'f2', name: 'IMG_2202.jpg', status: 'pending', uploadedAt: '2027-06-12T21:16:00Z', thumbnailUrl: '' },
    { fileItemId: 'f3', name: 'IMG_2190.jpg', status: 'approved', uploadedAt: '2027-06-12T20:41:00Z', thumbnailUrl: '' },
  ],
};

const MESSAGES = {
  albumId: ALBUM_ID, isOwner: true, partyActive: true, requireMessageApproval: false,
  items: [
    { id: 'm1', displayName: 'Chiara', text: 'Auguri! Sei bellissima stasera.', status: 'visible', isHero: false, createdAt: '2027-06-12T21:05:00Z' },
    { id: 'm2', displayName: null, text: 'Cinquanta portati benissimo.', status: 'pending', isHero: false, createdAt: '2027-06-12T21:20:00Z' },
  ],
};


/** One page of the guest directory, so the console has cards to draw. */
const GROUPS = [
  {
    kind: 'group', groupId: 'g1', label: 'Famiglia Rossi', version: 2,
    maxAdditionalGuests: 1, additionalGuestsUsed: 1,
    people: [
      { guestId: 'p1', name: 'Mario', isAdditionalGuest: false, rsvpStatus: 'attending', checkedInAt: '2027-06-12T20:10:00Z', checkInSource: 'host', matched: false },
      { guestId: 'p2', name: 'Laura', isAdditionalGuest: false, rsvpStatus: 'attending', checkedInAt: null, checkInSource: null, matched: false },
      { guestId: 'p3', name: '+1', isAdditionalGuest: true, rsvpStatus: 'pending', checkedInAt: null, checkInSource: null, matched: false },
    ],
    counts: { attending: 2, pending: 1, declined: 0, arrived: 1 },
    invitation: { state: 'sent', lastAttemptChannel: 'whatsapp', lastAttemptKind: 'invitation', lastAttemptStatus: 'shared', lastAttemptAt: '2027-06-01T10:12:00Z' },
    whatsappDirect: true, canSend: true, canRemind: true, canShare: true,
  },
  {
    kind: 'group', groupId: 'g2', label: 'Chiara e Paolo', version: 1,
    maxAdditionalGuests: 0, additionalGuestsUsed: 0,
    people: [
      { guestId: 'p4', name: 'Chiara', isAdditionalGuest: false, rsvpStatus: 'pending', checkedInAt: null, checkInSource: null, matched: false },
      { guestId: 'p5', name: 'Paolo', isAdditionalGuest: false, rsvpStatus: 'declined', checkedInAt: null, checkInSource: null, matched: false },
    ],
    counts: { attending: 0, pending: 1, declined: 1, arrived: 0 },
    invitation: { state: 'not_sent', lastAttemptChannel: null, lastAttemptKind: null, lastAttemptStatus: null, lastAttemptAt: null },
    whatsappDirect: false, canSend: true, canRemind: false, canShare: true,
  },
  {
    kind: 'other', id: 'o1', name: 'Il collega di Marta', checkedInAt: '2027-06-12T21:02:00Z', version: 1,
  },
];

function base(extra: Record<string, () => Response> = {}) {
  return installFetchMock({
    [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(party()),
    [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
    [`GET /api/parties/${PARTY_ID}/guest-content`]: () => jsonResponse(SLOTS),
    [`POST /api/parties/${PARTY_ID}/guest-directory/query`]: () => jsonResponse(counts()),
    [`GET /api/albums/${ALBUM_ID}/party-uploads`]: () => jsonResponse(UPLOADS),
    [`GET /api/albums/${ALBUM_ID}/party-messages`]: () => jsonResponse(MESSAGES),
    [`GET /api/albums/${ALBUM_ID}/party-print-settings`]: () => jsonResponse({
      albumId: ALBUM_ID, enabled: false, printStationId: null, printerDeviceId: null,
      photo: { enabled: false, maxPrints: 0, perGuest: 0, used: 0 },
      strip: { enabled: false, maxPrints: 0, perGuest: 0, used: 0 },
      footerText: '',
    }),
    'GET /api/print/stations': () => jsonResponse([]),
    [`GET /api/albums/${ALBUM_ID}/party-challenges`]: () => jsonResponse({ albumId: ALBUM_ID, challenges: [] }),
    'GET /api/albums': () => jsonResponse([]),
    ...extra,
  });
}

async function capture(name: string, waitFor: string) {
  await screen.findByTestId(waitFor);
  // Let the workspace's later reads (counts, queues) settle before the snapshot.
  await new Promise((r) => setTimeout(r, 200));
  mkdirSync(OUT, { recursive: true });
  writeFileSync(`${OUT}/${name}.html`, document.body.innerHTML, 'utf8');
}

function mountWorkspace(search: string, over: Record<string, () => Response> = {}) {
  base(over);
  render(
    <AuthedWrapper>
      <MemoryRouter initialEntries={[`/parties/${PARTY_ID}${search}`]}>
        <Routes>
          <Route path="/parties/:partyId" element={<PartyWorkspacePage />} />
        </Routes>
      </MemoryRouter>
    </AuthedWrapper>,
  );
}

it('draft — summary', async () => {
  mountWorkspace('?section=summary', {
    [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(party({ status: 'draft' })),
    [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty({ partyMode: false, partyUrl: null })),
    [`POST /api/parties/${PARTY_ID}/guest-directory/query`]: () => jsonResponse(counts({ groups: 0 })),
  });
  await capture('draft-summary', 'party-next');
});

it('published — summary', async () => {
  mountWorkspace('?section=summary', {
    [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(party({ status: 'published' })),
  });
  await capture('published-summary', 'party-next');
});

it('live — console', async () => {
  mountWorkspace('', {
    [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(party({ status: 'live', liveStartedAt: '2027-06-12T19:40:00Z' })),
  });
  await capture('live-console', 'party-live-arrivals');
});

it('live — open party with no guest list', async () => {
  mountWorkspace('', {
    [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(party({ status: 'live' })),
    [`POST /api/parties/${PARTY_ID}/guest-directory/query`]: () =>
      jsonResponse(counts({ groups: 0, attendance: { totalArrivals: 84 } })),
  });
  await capture('live-open-party', 'party-live-arrivals');
});

it('ended — summary', async () => {
  mountWorkspace('?section=summary', {
    [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(party({ status: 'ended', libraryAccessExpiresAt: '2027-09-01T00:00:00Z' })),
  });
  await capture('ended-summary', 'party-next');
});

it('experience', async () => {
  mountWorkspace('?section=experience', {
    [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(party({ status: 'published' })),
  });
  await capture('experience', 'party-content-invitation');
});

it('photos', async () => {
  mountWorkspace('?section=photos', {
    [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(party({ status: 'live' })),
  });
  await capture('photos', 'party-contributions');
});

it('activities', async () => {
  mountWorkspace('?section=activities', {
    [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(party({ status: 'published' })),
  });
  await capture('activities', 'party-activities-messages');
});

it('screens', async () => {
  mountWorkspace('?section=screens', {
    [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(party({ status: 'published' })),
  });
  await capture('screens', 'party-screens-tv');
});

it('settings', async () => {
  mountWorkspace('?section=settings', {
    [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(party({ status: 'published' })),
  });
  await capture('settings', 'party-settings-form');
});

it('empty draft — nothing set up yet', async () => {
  mountWorkspace('?section=summary', {
    [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(party({
      status: 'draft', eventStartsAt: null, mediaSources: [],
    })),
    [`POST /api/parties/${PARTY_ID}/guest-directory/query`]: () => jsonResponse(counts({ groups: 0 })),
  });
  await capture('empty-draft', 'party-next');
});

// ONE CAVEAT, and it belongs to these two only. The guest list is VIRTUALIZED:
// its rows are absolutely positioned at offsets the virtualizer computes from
// measured card heights, and jsdom measures every card as zero — so the
// snapshot freezes the estimated offsets and the cards overlap in the picture.
// Per-element measurements (overflow, target sizes, gutters) are unaffected and
// are what the script asserts; the vertical stacking in these two screenshots
// is an artifact of the capture, not of the product.
it('guests — a populated console', async () => {
  mountWorkspace('?section=guests', {
    [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(party({ status: 'published' })),
    [`POST /api/parties/${PARTY_ID}/guest-directory/query`]: () =>
      jsonResponse({ ...counts(), partyStatus: 'published', items: GROUPS }),
  });
  await capture('guests', 'party-guests');
});

it('guests — an open party with no list at all', async () => {
  mountWorkspace('?section=guests', {
    [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(party({ status: 'published' })),
    [`POST /api/parties/${PARTY_ID}/guest-directory/query`]: () =>
      jsonResponse({ ...counts({ groups: 0 }), partyStatus: 'published' }),
  });
  await capture('guests-open', 'party-guests');
});

it('parties list', async () => {
  installFetchMock({
    'GET /api/parties': () => jsonResponse([
      { id: 'a', title: 'Il compleanno di Marta', status: 'live', eventStartsAt: '2027-06-12T19:30:00Z', liveStartedAt: '2027-06-12T19:40:00Z', liveEndedAt: null, updatedAt: '2027-06-12T19:40:00Z', mainAlbumId: ALBUM_ID, mainAlbumName: 'Marta 50' },
      { id: 'b', title: 'Laurea di Giulio', status: 'published', eventStartsAt: '2027-07-04T18:00:00Z', liveStartedAt: null, liveEndedAt: null, updatedAt: '2027-06-01T00:00:00Z', mainAlbumId: 'a2', mainAlbumName: 'Giulio' },
      { id: 'c', title: 'Cena di settembre', status: 'draft', eventStartsAt: null, liveStartedAt: null, liveEndedAt: null, updatedAt: '2027-05-20T00:00:00Z', mainAlbumId: null, mainAlbumName: null },
      { id: 'd', title: 'Capodanno 2027', status: 'ended', eventStartsAt: '2026-12-31T22:00:00Z', liveStartedAt: null, liveEndedAt: '2027-01-01T04:00:00Z', updatedAt: '2027-01-01T04:00:00Z', mainAlbumId: 'a3', mainAlbumName: 'Capodanno' },
    ]),
  });
  render(
    <AuthedWrapper>
      <MemoryRouter initialEntries={['/parties']}>
        <Routes><Route path="/parties" element={<PartiesPage />} /></Routes>
      </MemoryRouter>
    </AuthedWrapper>,
  );
  await capture('parties-list', 'parties-page');
});
