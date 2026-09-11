import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useLocation, useNavigate } from 'react-router';
import { PartyPage } from './PartyPage';
import { installFetchMock, jsonResponse } from '../test-utils';
import { I18nProvider } from '../i18n';

// The poster, as a guest actually meets it: a row that opens a picture whole,
// and a Back that closes the picture rather than leaving the party.
//
// The history behaviour is an ACCEPTANCE criterion, not polish. On a phone the
// system Back gesture is how people dismiss things, and a full-screen image
// that swallowed it — or that exited the party when pressed — would be the
// difference between a feature and a complaint. So it is pinned here from all
// three directions: opened from the UI, arrived at by URL, and invented.

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

/**
 * Reports the current location, and offers the one thing jsdom cannot give us.
 *
 * `window.history.back()` does not drive a MemoryRouter — it keeps its own
 * in-memory stack — so pressing the browser's or Android's Back is simulated by
 * the router's own `navigate(-1)`, which is precisely what a history pop is.
 */
function LocationProbe() {
  const location = useLocation();
  const navigate = useNavigate();
  return (
    <>
      <span data-testid="location">{`${location.pathname}${location.search}`}</span>
      <button type="button" data-testid="press-back" onClick={() => navigate(-1)}>back</button>
    </>
  );
}

function wrapper(entries: string[] = ['/party/tok-1']) {
  return (
    <I18nProvider>
      <MemoryRouter initialEntries={entries}>
        <LocationProbe />
        <Routes>
          <Route path="/party/:token" element={<PartyPage />} />
        </Routes>
      </MemoryRouter>
    </I18nProvider>
  );
}

const MENU_MEDIA = '/api/party/tok-1/content/menu/media?v=3';

function slot(over: Record<string, unknown> = {}) {
  return {
    kind: 'menu',
    enabled: true,
    visibleBefore: true,
    visibleLive: true,
    visibleAfter: false,
    content: { intro: 'Cena in giardino', sections: [] },
    version: 3,
    mediaUrl: MENU_MEDIA,
    mediaPresentation: 'poster',
    ...over,
  };
}

function context(over: Record<string, unknown> = {}) {
  return {
    title: 'Beach Party',
    phase: 'live',
    accessMode: 'full',
    eventStartsAt: null,
    albumName: 'Beach Party',
    itemCount: 1,
    coverUrl: '/api/party/tok-1/media/f1/preview',
    content: [slot()],
    capabilities: {
      contributionUrl: null, gameUrl: null, printUrl: null, faceSearch: false,
    },
    library: { available: false, accessEndsAt: null },
    ...over,
  };
}

const items = {
  albumName: 'Beach Party',
  items: [{
    id: 'f1',
    mediaType: 'image',
    thumbnailUrl: '/api/party/tok-1/media/f1/thumbnail',
    previewUrl: '/api/party/tok-1/media/f1/preview',
    downloadUrl: '/api/party/tok-1/media/f1/download',
  }],
};

function mock(ctx: Record<string, unknown> = context()) {
  return installFetchMock({
    'GET /api/party/tok-1': () => jsonResponse(ctx),
    'GET /api/party/tok-1/items': () => jsonResponse(items),
  });
}

describe('a Party content poster', () => {
  it('opens full screen from its row and pushes a history entry', async () => {
    const user = userEvent.setup();
    mock();
    render(wrapper());

    await user.click(await screen.findByTestId('party-poster-open-menu'));

    const viewer = await screen.findByTestId('party-image-viewer');
    expect(viewer.querySelector('img')).toHaveAttribute('src', MENU_MEDIA);
    // The URL is what Back will consume.
    expect(screen.getByTestId('location')).toHaveTextContent('/party/tok-1?poster=menu');
  });

  it('has no download, ever', async () => {
    const user = userEvent.setup();
    mock();
    render(wrapper());
    await user.click(await screen.findByTestId('party-poster-open-menu'));

    const viewer = await screen.findByTestId('party-image-viewer');
    // A Party reference authorizes LOOKING. Not bytes, not an original.
    expect(within(viewer).queryByRole('link', { name: /scarica|download/i }))
      .not.toBeInTheDocument();
  });

  it('closes on Back and leaves the guest in the party', async () => {
    const user = userEvent.setup();
    mock();
    render(wrapper());
    await user.click(await screen.findByTestId('party-poster-open-menu'));
    await screen.findByTestId('party-image-viewer');

    await user.click(screen.getByTestId('press-back'));

    await waitFor(() => {
      expect(screen.queryByTestId('party-image-viewer')).not.toBeInTheDocument();
    });
    // Still on the party, not off it.
    expect(screen.getByTestId('location')).toHaveTextContent('/party/tok-1');
    expect(screen.getByTestId('party-poster-open-menu')).toBeInTheDocument();
  });

  it('Close behaves exactly like Back when the poster was opened here', async () => {
    const user = userEvent.setup();
    mock();
    render(wrapper());
    await user.click(await screen.findByTestId('party-poster-open-menu'));

    await user.click(await screen.findByTestId('party-viewer-close'));

    await waitFor(() => {
      expect(screen.queryByTestId('party-image-viewer')).not.toBeInTheDocument();
    });
    expect(screen.getByTestId('location')).toHaveTextContent('/party/tok-1');
  });

  it('opens straight from a deep link, and Close does not leave NubArca', async () => {
    // Someone who arrived on ?poster=menu has no entry of ours to pop, so Close
    // rewrites the URL in place rather than navigating out of the application.
    const user = userEvent.setup();
    mock();
    render(wrapper(['/party/tok-1?poster=menu']));

    await screen.findByTestId('party-image-viewer');
    await user.click(screen.getByTestId('party-viewer-close'));

    await waitFor(() => {
      expect(screen.queryByTestId('party-image-viewer')).not.toBeInTheDocument();
    });
    expect(screen.getByTestId('location')).toHaveTextContent('/party/tok-1');
    expect(screen.getByTestId('party-poster-open-menu')).toBeInTheDocument();
  });

  it('an invented poster value opens nothing at all', async () => {
    // A query string is not authority. The kind is resolved against the
    // server-authorized context or it resolves to nothing.
    mock();
    render(wrapper(['/party/tok-1?poster=definitely-not-a-kind']));

    await screen.findByTestId('party-poster-open-menu');
    expect(screen.queryByTestId('party-image-viewer')).not.toBeInTheDocument();
    await waitFor(() => {
      expect(screen.getByTestId('location')).toHaveTextContent('/party/tok-1');
    });
  });

  it('a kind that is real but INLINE opens nothing', async () => {
    mock(context({ content: [slot({ mediaPresentation: 'inline' })] }));
    render(wrapper(['/party/tok-1?poster=menu']));

    await screen.findByTestId('party-content');
    expect(screen.queryByTestId('party-image-viewer')).not.toBeInTheDocument();
  });

  it('a poster whose photograph is gone opens nothing', async () => {
    mock(context({ content: [slot({ mediaUrl: null })] }));
    render(wrapper(['/party/tok-1?poster=menu']));

    await screen.findByTestId('party-content');
    expect(screen.queryByTestId('party-image-viewer')).not.toBeInTheDocument();
    // And no dead row is offered either.
    expect(screen.queryByTestId('party-poster-open-menu')).not.toBeInTheDocument();
  });

  it('closes itself when the poster stops being available while it is open', async () => {
    // Polling keeps the context fresh. A slot the host disables mid-viewing
    // must not keep showing stale authority.
    const user = userEvent.setup();
    let current = context();
    installFetchMock({
      'GET /api/party/tok-1': () => jsonResponse(current),
      'GET /api/party/tok-1/items': () => jsonResponse(items),
    });
    render(wrapper());
    await user.click(await screen.findByTestId('party-poster-open-menu'));
    await screen.findByTestId('party-image-viewer');

    current = context({ content: [slot({ mediaUrl: null })] });

    await waitFor(
      () => { expect(screen.queryByTestId('party-image-viewer')).not.toBeInTheDocument(); },
      { timeout: 20_000 },
    );
    expect(screen.getByTestId('location')).toHaveTextContent('/party/tok-1');
  }, 25_000);
});

// The overlay is common to the three surfaces, and these are the tests that
// would have caught it not being. The poster rows on Before and After updated
// the URL and then showed nothing at all, because the viewer was mounted only
// inside the Live branch — below two early returns it never reached.

describe('the poster opens on EVERY surface, not only Live', () => {
  it('opens from the invitation, before the party', async () => {
    const user = userEvent.setup();
    mock(context({
      phase: 'before',
      albumName: null,
      itemCount: 0,
      content: [slot({
        kind: 'invitation',
        content: { headline: 'Ci siamo' },
        visibleBefore: true,
        visibleLive: false,
        mediaUrl: '/api/party/tok-1/content/invitation/media?v=3',
      })],
    }));
    render(wrapper());

    await user.click(await screen.findByTestId('party-poster-open-invitation'));

    const viewer = await screen.findByTestId('party-image-viewer');
    expect(viewer.querySelector('img'))
      .toHaveAttribute('src', '/api/party/tok-1/content/invitation/media?v=3');
    expect(screen.getByTestId('location')).toHaveTextContent('?poster=invitation');
    // Still no download on the invitation either.
    expect(within(viewer).queryByRole('link')).not.toBeInTheDocument();
  });

  it('opens a menu poster on the invitation too', async () => {
    const user = userEvent.setup();
    mock(context({
      phase: 'before',
      albumName: null,
      itemCount: 0,
      content: [slot({ visibleBefore: true, visibleLive: false })],
    }));
    render(wrapper());

    await user.click(await screen.findByTestId('party-poster-open-menu'));

    expect((await screen.findByTestId('party-image-viewer')).querySelector('img'))
      .toHaveAttribute('src', MENU_MEDIA);
  });

  it('opens the thank-you from the memories surface', async () => {
    const user = userEvent.setup();
    mock(context({
      phase: 'after',
      content: [slot({
        kind: 'thank-you',
        content: { headline: 'Grazie!' },
        visibleBefore: false,
        visibleLive: false,
        visibleAfter: true,
        mediaUrl: '/api/party/tok-1/content/thank-you/media?v=3',
      })],
      library: { available: false, accessEndsAt: null },
    }));
    render(wrapper());

    await user.click(await screen.findByTestId('party-poster-open-thank-you'));

    expect((await screen.findByTestId('party-image-viewer')).querySelector('img'))
      .toHaveAttribute('src', '/api/party/tok-1/content/thank-you/media?v=3');
    expect(screen.getByTestId('location')).toHaveTextContent('?poster=thank-you');
  });

  it('Back closes the poster and keeps the invitation on screen', async () => {
    const user = userEvent.setup();
    mock(context({
      phase: 'before',
      albumName: null,
      itemCount: 0,
      content: [slot({ visibleBefore: true, visibleLive: false })],
    }));
    render(wrapper());
    await user.click(await screen.findByTestId('party-poster-open-menu'));
    await screen.findByTestId('party-image-viewer');

    await user.click(screen.getByTestId('press-back'));

    await waitFor(() => {
      expect(screen.queryByTestId('party-image-viewer')).not.toBeInTheDocument();
    });
    expect(screen.getByTestId('location')).toHaveTextContent('/party/tok-1');
    // The invitation, not the party, and not a blank page.
    expect(screen.getByTestId('party-before')).toBeInTheDocument();
  });
});

describe('the Info dock follows what is actually presentable', () => {
  it('offers Info when a poster can still be opened', async () => {
    mock(context({
      phase: 'before',
      albumName: null,
      itemCount: 0,
      content: [slot({ visibleBefore: true, visibleLive: false })],
    }));
    render(wrapper());
    await screen.findByTestId('party-poster-open-menu');
    expect(screen.getByTestId('party-dock-info')).toBeInTheDocument();
  });

  it('offers no Info when the only slot is a poster with nothing to show', async () => {
    // The section renders nothing — correctly — so a dock entry would scroll to
    // an empty container. `content.length > 0` could not tell the difference.
    mock(context({
      phase: 'before',
      albumName: null,
      itemCount: 0,
      content: [slot({ visibleBefore: true, visibleLive: false, mediaUrl: null })],
    }));
    render(wrapper());
    await screen.findByTestId('party-before');
    expect(screen.queryByTestId('party-dock-info')).not.toBeInTheDocument();
  });

  it('still offers Info for an inline slot with no photograph', async () => {
    // An inline slot stands on its words; its photograph was always optional.
    mock(context({
      phase: 'before',
      albumName: null,
      itemCount: 0,
      content: [slot({
        visibleBefore: true, visibleLive: false,
        mediaPresentation: 'inline', mediaUrl: null,
      })],
    }));
    render(wrapper());
    await screen.findByTestId('party-before');
    expect(screen.getByTestId('party-dock-info')).toBeInTheDocument();
  });
});

describe('the gallery viewer keeps what it had', () => {
  it('still offers the download the server sent', async () => {
    // The two callers share one viewer; they do not share authority. The
    // gallery's download survives exactly where it was allowed before.
    const user = userEvent.setup();
    mock();
    render(wrapper());

    await screen.findByTestId('party-grid');
    await user.click(screen.getByRole('button', { name: 'Apri foto' }));

    const viewer = await screen.findByTestId('party-image-viewer');
    expect(viewer.querySelector('img'))
      .toHaveAttribute('src', '/api/party/tok-1/media/f1/preview');
    expect(within(viewer).getByRole('link')).toHaveAttribute(
      'href', '/api/party/tok-1/media/f1/download');
  });
});
