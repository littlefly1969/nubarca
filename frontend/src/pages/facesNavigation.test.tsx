// UX-02 §6: the Faces tab is navigable state, and a person detail returns to
// the tab that opened it.
//
// The defect these cover: the tab lived in useState, so a refresh dropped it,
// it could not be linked, Back/Forward ignored it, and opening a named person
// and pressing the visible Back action landed on the default landing tab
// instead of "Persone".
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { RouterProvider, createMemoryRouter, useLocation } from 'react-router';
import { PeoplePage } from './PeoplePage';
import { PersonDetailPage } from './PersonDetailPage';
import {
  DEFAULT_FACES_TAB,
  FACES_FALLBACK_RETURN,
  FACES_TABS,
  facesTabPath,
  resolveFacesReturn,
  resolveFacesTab,
} from './facesTabs';
import {
  AuthedWrapper, emptyResponse, installFetchMock, jsonResponse, type MockHandler,
} from '../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const PERSON = {
  personId: 'p-1',
  name: 'Ada',
  faceCount: 2,
  representative: {
    faceId: 'face-1',
    fileItemId: 'file-1',
    name: 'a.png',
    box: { x: 0.1, y: 0.1, width: 0.2, height: 0.2 },
  },
};

/** Reports the live location so a test can assert the URL, not just the view. */
function LocationProbe() {
  const location = useLocation();
  return (
    <div data-testid="location">{`${location.pathname}${location.search}`}</div>
  );
}

function currentLocation(): string {
  return screen.getByTestId('location').textContent ?? '';
}

function renderFaces({
  entries = ['/people'],
  isAdmin = false,
  handlers = {},
}: {
  entries?: string[];
  isAdmin?: boolean;
  handlers?: Record<string, MockHandler>;
} = {}) {
  installFetchMock({
    'GET /api/people/suggested-groups': () => jsonResponse([]),
    'GET /api/people': () => jsonResponse([PERSON]),
    'GET /api/people/p-1': () => jsonResponse(PERSON),
    'GET /api/people/p-1/photos': () => jsonResponse([]),
    'GET /api/people/p-1/videos': () => jsonResponse([]),
    'GET /api/people/p-1/similar-faces': () => jsonResponse({ items: [], profileAvailable: false }),
    'DELETE /api/people/p-1': () => emptyResponse(),
    ...handlers,
  });
  // createMemoryRouter, not <MemoryRouter>: only the router object exposes
  // navigate(-1), which is how a BROWSER Back press is simulated. <MemoryRouter>
  // keeps its history private, and window.history.back() would move the jsdom
  // history the router is not listening to — a test that silently proves
  // nothing.
  const router = createMemoryRouter(
    [
      {
        path: '/people',
        element: <><LocationProbe /><PeoplePage /></>,
      },
      {
        path: '/people/:personId',
        element: <><LocationProbe /><PersonDetailPage /></>,
      },
    ],
    { initialEntries: entries },
  );
  const view = render(
    <AuthedWrapper isAdmin={isAdmin}>
      <RouterProvider router={router} />
    </AuthedWrapper>,
  );
  return { ...view, router };
}

// --- the pure contract ---------------------------------------------------

describe('resolveFacesTab', () => {
  it('accepts every tab in the contract', () => {
    for (const tab of FACES_TABS) {
      expect(resolveFacesTab(tab, true), tab).toBe(tab);
    }
  });

  it('falls back for an absent or unknown value instead of erroring', () => {
    for (const raw of [null, undefined, '', 'nope', 'PEOPLE', '../etc']) {
      expect(resolveFacesTab(raw, true), String(raw)).toBe(DEFAULT_FACES_TAB);
    }
  });

  it('keeps the admin-only Settings tab away from a non-admin', () => {
    expect(resolveFacesTab('settings', false)).toBe(DEFAULT_FACES_TAB);
    expect(resolveFacesTab('settings', true)).toBe('settings');
  });
});

describe('resolveFacesReturn', () => {
  it('uses the tab the person was opened from', () => {
    expect(resolveFacesReturn({ facesReturn: '/people?tab=people' })).toBe('/people?tab=people');
  });

  it('falls back to the named-people tab without state', () => {
    for (const state of [null, undefined, {}, { facesReturn: 42 }]) {
      expect(resolveFacesReturn(state)).toBe(FACES_FALLBACK_RETURN);
    }
    expect(FACES_FALLBACK_RETURN).toBe('/people?tab=people');
  });

  it('refuses a return location that leaves the application', () => {
    // Router state is attacker-influenceable in principle; a return target is
    // a navigation, so it must stay in-app.
    for (const hostile of ['https://evil.example/people', '//evil.example/people', '/admin']) {
      expect(resolveFacesReturn({ facesReturn: hostile }), hostile).toBe(FACES_FALLBACK_RETURN);
    }
  });
});

// --- the URL contract ----------------------------------------------------

describe('Faces tab URL contract', () => {
  it('normalizes a bare /people to the default tab', async () => {
    renderFaces({ entries: ['/people'] });
    await waitFor(() => expect(currentLocation()).toBe(facesTabPath(DEFAULT_FACES_TAB)));
  });

  it('opens the tab named in the URL — a refresh keeps it', async () => {
    renderFaces({ entries: ['/people?tab=people'] });
    // The named-people grid, not the default suggested-groups view.
    expect(await screen.findByRole('link', { name: /Ada/ })).toBeInTheDocument();
    expect(currentLocation()).toBe('/people?tab=people');
  });

  it('falls back safely for an invalid tab and rewrites the URL', async () => {
    renderFaces({ entries: ['/people?tab=not-a-tab'] });
    await waitFor(() => expect(currentLocation()).toBe(facesTabPath(DEFAULT_FACES_TAB)));
  });

  it('does not honour ?tab=settings for a non-admin', async () => {
    renderFaces({ entries: ['/people?tab=settings'], isAdmin: false });
    await waitFor(() => expect(currentLocation()).toBe(facesTabPath(DEFAULT_FACES_TAB)));
    expect(screen.queryByRole('button', { name: 'Impostazioni Face AI' })).toBeNull();
  });

  it('honours ?tab=settings for an admin', async () => {
    renderFaces({ entries: ['/people?tab=settings'], isAdmin: true });
    await waitFor(() => expect(currentLocation()).toBe('/people?tab=settings'));
  });

  it('writes the tab to the URL when the user picks one', async () => {
    const user = userEvent.setup();
    renderFaces({ entries: ['/people'] });
    await waitFor(() => expect(currentLocation()).toBe(facesTabPath(DEFAULT_FACES_TAB)));

    await user.click(screen.getByRole('button', { name: 'Persone' }));
    await waitFor(() => expect(currentLocation()).toBe('/people?tab=people'));
  });

  it('makes a tab change a Back stop, and normalization not one', async () => {
    const user = userEvent.setup();
    const { router } = renderFaces({ entries: ['/people'] });
    await waitFor(() => expect(currentLocation()).toBe(facesTabPath(DEFAULT_FACES_TAB)));

    await user.click(screen.getByRole('button', { name: 'Persone' }));
    await waitFor(() => expect(currentLocation()).toBe('/people?tab=people'));

    // Back returns to the normalized default, NOT to the un-normalized
    // /people the user never chose.
    await act(async () => { await router.navigate(-1); });
    await waitFor(() => expect(currentLocation()).toBe(facesTabPath(DEFAULT_FACES_TAB)));
  });
});

// --- person detail return ------------------------------------------------

describe('person detail return navigation', () => {
  it('returns to the named People tab from the visible Back action', async () => {
    const user = userEvent.setup();
    renderFaces({ entries: ['/people?tab=people'] });

    await user.click(await screen.findByRole('link', { name: /Ada/ }));
    await waitFor(() => expect(currentLocation()).toBe('/people/p-1'));

    await user.click(await screen.findByRole('link', { name: /Torna alle persone/ }));
    await waitFor(() => expect(currentLocation()).toBe('/people?tab=people'));
    expect(await screen.findByRole('link', { name: /Ada/ })).toBeInTheDocument();
  });

  it('restores the named People tab on browser Back too', async () => {
    const user = userEvent.setup();
    const { router } = renderFaces({ entries: ['/people?tab=people'] });

    await user.click(await screen.findByRole('link', { name: /Ada/ }));
    await waitFor(() => expect(currentLocation()).toBe('/people/p-1'));

    await act(async () => { await router.navigate(-1); });
    await waitFor(() => expect(currentLocation()).toBe('/people?tab=people'));
    expect(await screen.findByRole('link', { name: /Ada/ })).toBeInTheDocument();
  });

  it('falls back to the named tab when the person URL was opened directly', async () => {
    // A bookmark, a new tab, or a pasted link: no router state and no useful
    // history entry, which is why Back is a link and not navigate(-1).
    const user = userEvent.setup();
    renderFaces({ entries: ['/people/p-1'] });

    await user.click(await screen.findByRole('link', { name: /Torna alle persone/ }));
    await waitFor(() => expect(currentLocation()).toBe('/people?tab=people'));
  });

  it('returns to the named tab after archiving the person', async () => {
    const user = userEvent.setup();
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    renderFaces({ entries: ['/people?tab=people'] });

    await user.click(await screen.findByRole('link', { name: /Ada/ }));
    await waitFor(() => expect(currentLocation()).toBe('/people/p-1'));

    await user.click(await screen.findByRole('button', { name: 'Rimuovi persona' }));
    await waitFor(() => expect(currentLocation()).toBe('/people?tab=people'));
  });
});
