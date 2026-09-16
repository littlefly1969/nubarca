/* eslint-disable */
// Review fixtures for the PUBLIC party — the surfaces a guest meets, rendered
// from the real components against mocked responses.
//
// The host's workspace has its own set (partyWorkspace.fixtures.tsx); these are
// the other half of the same journey, and they are measured by the same script.
// They are written to files whose names begin with `public-`, which is how
// `scripts/check-party-workspace-layout.mjs` knows to give them the whole
// viewport instead of the authenticated application shell: a guest page is the
// page, not a region inside one.
//
// What is covered, in the order a guest meets it:
//
//   the personal invitation, before the party and while it is on;
//   the public hub, before the party, during it, and after;
//   the contribution page and the greeting;
//   the game while a challenge is running.
//
// Run with:
//   PARTY_FIXTURE_DIR=/tmp/party npx vitest run --config vitest.fixtures.config.ts
//
// It contains no production data: every party, guest and photograph is invented
// here.

import { mkdirSync, writeFileSync } from 'node:fs';
import { afterEach, beforeEach, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { installFetchMock, jsonResponse } from '../../test-utils';
import { I18nProvider } from '../../i18n';
import { PartyPage } from '../../pages/PartyPage';
import { PartyInvitationPage } from '../../pages/PartyInvitationPage';
import { PartyUploadPage } from '../../pages/PartyUploadPage';
import { PartyGamePage } from '../../pages/PartyGamePage';

const OUT = process.env.PARTY_FIXTURE_DIR ?? '/tmp/party-fixtures';
const TOKEN = 'tok-1';

// The hub animates its cover and its face sweep. A still page is what gets
// measured, and it is also what a guest who asked for no motion sees.
beforeEach(() => {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query.includes('prefers-reduced-motion'),
    media: query,
    addEventListener: () => {},
    removeEventListener: () => {},
    addListener: () => {},
    removeListener: () => {},
    onchange: null,
    dispatchEvent: () => false,
  }));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  window.localStorage.clear();
  window.sessionStorage.clear();
});

const COVER = '/api/party/tok-1/media/f1/preview';

const slot = (kind: string, content: Record<string, string>, over: Record<string, unknown> = {}) => ({
  kind, enabled: true, visibleBefore: true, visibleLive: true, visibleAfter: false,
  content, version: 1, mediaPresentation: 'inline', mediaUrl: null, ...over,
});

const CONTENT = [
  slot('invitation', { headline: 'Vieni a festeggiare', message: 'Ci sarà da mangiare, da ballare e da ridere. Porta scarpe comode.' }),
  slot('location', { venueName: 'Cascina Rosa', address: 'Via dei Platani 14, Bergamo', note: 'Parcheggio nel cortile.' }),
  slot('dress-code', { headline: 'Elegante, ma non troppo', description: 'Un tocco di rosso, se ne hai voglia.' }),
  slot('menu', { intro: 'Cucina di stagione, e una torta enorme.' }),
];

const context = (over: Record<string, unknown> = {}) => ({
  title: 'Il compleanno di Marta',
  phase: 'live',
  accessMode: 'full',
  eventStartsAt: '2027-06-12T19:30:00Z',
  albumName: 'Marta 50',
  itemCount: 42,
  coverUrl: COVER,
  content: CONTENT,
  capabilities: {
    contributionUrl: '/party/upload-token/upload',
    gameUrl: `/party/${TOKEN}/game`,
    printUrl: `/party/print-token/print`,
    faceSearch: true,
  },
  library: { available: false, accessEndsAt: null },
  ...over,
});

const ITEMS = {
  albumName: 'Marta 50',
  items: Array.from({ length: 6 }, (_, i) => ({
    id: `f${i + 1}`, mediaType: 'image',
    thumbnailUrl: `/api/party/${TOKEN}/media/f${i + 1}/thumbnail`,
    previewUrl: `/api/party/${TOKEN}/media/f${i + 1}/preview`,
    downloadUrl: `/api/party/${TOKEN}/media/f${i + 1}/download`,
  })),
};

async function capture(name: string, waitFor: string) {
  await screen.findByTestId(waitFor);
  await new Promise((r) => setTimeout(r, 200));
  mkdirSync(OUT, { recursive: true });
  writeFileSync(`${OUT}/public-${name}.html`, document.body.innerHTML, 'utf8');
}

function mount(path: string, route: string, element: React.ReactNode) {
  render(
    <I18nProvider>
      <MemoryRouter initialEntries={[path]}>
        <Routes><Route path={route} element={element} /></Routes>
      </MemoryRouter>
    </I18nProvider>,
  );
}

/* ── The public hub ───────────────────────────────────────────────────────── */

it('public hub — before the party', async () => {
  installFetchMock({
    [`GET /api/party/${TOKEN}`]: () => jsonResponse(context({
      phase: 'before',
      capabilities: { contributionUrl: null, gameUrl: null, printUrl: null, faceSearch: false },
    })),
    [`GET /api/party/${TOKEN}/items`]: () => jsonResponse(ITEMS),
  });
  mount(`/party/${TOKEN}`, '/party/:token', <PartyPage />);
  await capture('hub-before', 'party-before');
});

it('public hub — during the party', async () => {
  installFetchMock({
    [`GET /api/party/${TOKEN}`]: () => jsonResponse(context()),
    [`GET /api/party/${TOKEN}/items`]: () => jsonResponse(ITEMS),
  });
  mount(`/party/${TOKEN}`, '/party/:token', <PartyPage />);
  await capture('hub-live', 'party-hub-cover');
});

it('public hub — after the party', async () => {
  installFetchMock({
    [`GET /api/party/${TOKEN}`]: () => jsonResponse(context({
      phase: 'after',
      capabilities: { contributionUrl: null, gameUrl: null, printUrl: null, faceSearch: true },
      library: { available: true, accessEndsAt: '2027-09-01T00:00:00Z' },
    })),
    [`GET /api/party/${TOKEN}/items`]: () => jsonResponse(ITEMS),
  });
  mount(`/party/${TOKEN}`, '/party/:token', <PartyPage />);
  await capture('hub-after', 'party-after');
});

/* ── The personal invitation ──────────────────────────────────────────────── */

const INVITE = 'inv-1';
const INVITE_URL = `/api/party-invitations/${INVITE}`;

const person = (id: string, name: string, status = 'pending', over: Record<string, unknown> = {}) => ({
  id, name, isAdditionalGuest: false, status, dietaryNotes: null, ...over,
});

const invitation = (over: { party?: Record<string, unknown>; invitation?: Record<string, unknown> } = {}) => ({
  party: {
    title: 'Il compleanno di Marta', description: null,
    eventStartsAt: '2027-06-12T19:30:00Z', phase: 'before',
    coverUrl: COVER, content: CONTENT,
    ...over.party,
  },
  invitation: {
    label: 'Famiglia Rossi', version: 3, canRespond: true, canCheckIn: false,
    maxAdditionalGuests: 1, additionalGuestsUsed: 0,
    guests: [person('m', 'Mario'), person('l', 'Laura')],
    questions: [
      { id: 'q-menu', prompt: 'Carne o pesce?', kind: 'single_choice', required: true, options: ['Carne', 'Pesce'], answer: null },
    ],
    ...over.invitation,
  },
});

it('invitation — before the party', async () => {
  installFetchMock({ [`GET ${INVITE_URL}`]: () => jsonResponse(invitation()) });
  mount(`/party/invite/${INVITE}`, '/party/invite/:token', <PartyInvitationPage />);
  await capture('invitation-before', 'party-rsvp-open');
});

it('invitation — the reply, once it has been sent', async () => {
  installFetchMock({
    [`GET ${INVITE_URL}`]: () => jsonResponse(invitation({
      invitation: {
        guests: [
          person('m', 'Mario', 'attending', { dietaryNotes: 'Senza glutine' }),
          person('l', 'Laura', 'declined'),
        ],
        questions: [
          { id: 'q-menu', prompt: 'Carne o pesce?', kind: 'single_choice', required: true, options: ['Carne', 'Pesce'], answer: 'Pesce' },
        ],
      },
    })),
  });
  mount(`/party/invite/${INVITE}`, '/party/invite/:token', <PartyInvitationPage />);
  await capture('invitation-answered', 'party-rsvp');
});

it('invitation — the reply sheet, open', async () => {
  installFetchMock({ [`GET ${INVITE_URL}`]: () => jsonResponse(invitation()) });
  mount(`/party/invite/${INVITE}`, '/party/invite/:token', <PartyInvitationPage />);
  await screen.findByTestId('party-rsvp-open');
  (await import('@testing-library/react')).fireEvent.click(screen.getByTestId('party-rsvp-open'));
  await capture('invitation-sheet', 'party-rsvp-sheet');
});

it('invitation — while the party is live', async () => {
  installFetchMock({
    [`GET ${INVITE_URL}`]: () => jsonResponse(invitation({
      party: { phase: 'live', partyUrl: `/party/${TOKEN}` },
      invitation: {
        canRespond: false, canCheckIn: true,
        guests: [
          person('m', 'Mario', 'attending', { checkedInAt: '2027-06-12T20:10:00Z' }),
          person('l', 'Laura', 'attending', { checkedInAt: null }),
        ],
      },
    })),
  });
  mount(`/party/invite/${INVITE}`, '/party/invite/:token', <PartyInvitationPage />);
  await capture('invitation-live', 'party-checkin');
});

/* ── Contributing ─────────────────────────────────────────────────────────── */

it('contribution — photo or greeting', async () => {
  installFetchMock({
    'GET /api/party/uptok-1': () => jsonResponse(context()),
  });
  mount('/party/uptok-1/upload', '/party/:token/upload', <PartyUploadPage />);
  await capture('contribution', 'party-mode-media');
});

/* ── The game ─────────────────────────────────────────────────────────────── */

it('game — a challenge is running', async () => {
  installFetchMock({
    [`POST /api/party/${TOKEN}/game/join`]: () => jsonResponse({ participantId: 'part-1' }),
    [`GET /api/party/${TOKEN}/game`]: () => jsonResponse({
      status: 'live',
      phase: 'running',
      version: 4,
      roundNumber: 2,
      totalChallenges: 8,
      playedRounds: 1,
      guestsPresent: 37,
      currentChallenge: {
        id: 'c1', title: 'Il festeggiato deve ballare il tango',
        body: 'Trenta secondi, con chi vuole lui.',
        mediaUrl: null, durationSeconds: 45,
      },
      nextChallenge: null,
      voting: null,
      myVote: null,
      deadlineAt: null,
      displaySeenSecondsAgo: 2,
      tvUrl: null,
      availableCommands: [],
    }),
  });
  mount(`/party/${TOKEN}/game`, '/party/:token/game', <PartyGamePage />);
  await capture('game', 'party-game-page');
});
