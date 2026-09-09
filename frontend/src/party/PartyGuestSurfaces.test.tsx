import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { installFetchMock, jsonResponse } from '../test-utils';
import { I18nProvider } from '../i18n';
import { PartyPage } from '../pages/PartyPage';

// One QR, three surfaces. What is checked here is that the SERVER's phase
// selects the surface and that the client asks for nothing the phase does not
// have — an invitation must not request album media, and a closed library must
// not produce a dead button.

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const TOKEN = 'tok-1';

const context = (over: Record<string, unknown> = {}) => ({
  title: 'Festa di Marta',
  phase: 'before',
  accessMode: 'full',
  eventStartsAt: '2027-06-12T18:30:00Z',
  albumName: null,
  itemCount: 0,
  coverUrl: null,
  content: [],
  capabilities: { contributionUrl: null, gameUrl: null, printUrl: null, faceSearch: false },
  library: { available: false, accessEndsAt: null },
  ...over,
});

const slot = (kind: string, content: Record<string, unknown>) => ({
  kind, enabled: true, visibleBefore: true, visibleLive: true, visibleAfter: true,
  content, version: 1,
});

function page() {
  return (
    <I18nProvider>
      <MemoryRouter initialEntries={[`/party/${TOKEN}`]}>
        <Routes>
          <Route path="/party/:token" element={<PartyPage />} />
        </Routes>
      </MemoryRouter>
    </I18nProvider>
  );
}

describe('the invitation', () => {
  it('reads as an invitation and asks for no album media', async () => {
    const mock = installFetchMock({
      [`GET /api/party/${TOKEN}`]: () => jsonResponse(context({
        content: [
          slot('invitation', { headline: 'Siamo felici di averti', message: 'In giardino' }),
          slot('location', { venueName: 'Villa Aurora', address: 'Via Roma 1', note: null }),
          slot('dress-code', { headline: 'Summer elegant', description: null }),
        ],
      })),
    });
    render(page());

    expect(await screen.findByTestId('party-before')).toBeInTheDocument();
    expect(screen.getByText('Festa di Marta')).toBeInTheDocument();
    expect(screen.getByText('Siamo felici di averti')).toBeInTheDocument();
    expect(screen.getByText('Villa Aurora')).toBeInTheDocument();
    expect(screen.getByText('Summer elegant')).toBeInTheDocument();

    // Not an empty album: no gallery, no deck, and — the part that matters —
    // no request for either.
    expect(screen.queryByTestId('party-grid')).not.toBeInTheDocument();
    expect(mock.calls.some((c) => c.url.includes('/items'))).toBe(false);
  });

  it('renders a composition rather than a broken frame with no cover', async () => {
    installFetchMock({ [`GET /api/party/${TOKEN}`]: () => jsonResponse(context()) });
    render(page());

    await screen.findByTestId('party-before');
    expect(document.querySelector('.party-invitation-cover--blank')).toBeInTheDocument();
    expect(document.querySelector('img.party-invitation-cover')).toBeNull();
  });

  it('renders content in the PRODUCT order, whatever order it is written in', async () => {
    installFetchMock({
      [`GET /api/party/${TOKEN}`]: () => jsonResponse(context({
        content: [
          slot('invitation', { headline: 'Benvenuti' }),
          slot('location', { venueName: 'Villa Aurora', address: 'Via Roma 1' }),
          slot('menu', { intro: 'Cena', sections: [] }),
        ],
      })),
    });
    render(page());

    const blocks = within(await screen.findByTestId('party-content'))
      .getAllByRole('generic', { hidden: true })
      .filter((el) => el.hasAttribute('data-content'));
    expect(blocks.map((el) => el.getAttribute('data-content')))
      .toEqual(['invitation', 'location', 'menu']);
  });

  it('shows nothing for a slot the host wrote nothing into', async () => {
    installFetchMock({
      [`GET /api/party/${TOKEN}`]: () => jsonResponse(context({
        content: [slot('invitation', { headline: null, message: null })],
      })),
    });
    render(page());

    await screen.findByTestId('party-before');
    // An optional field that is absent renders nothing, not an empty heading.
    expect(screen.queryByTestId('party-content')?.textContent ?? '').toBe('');
  });

  it('offers Home and Info in the dock, and never an album', async () => {
    installFetchMock({
      [`GET /api/party/${TOKEN}`]: () => jsonResponse(context({
        content: [slot('location', { venueName: 'Villa Aurora', address: 'Via Roma 1' })],
      })),
    });
    render(page());

    const dock = await screen.findByTestId('party-dock');
    expect(dock).toHaveAttribute('data-phase', 'before');
    expect(within(dock).getByTestId('party-dock-home')).toBeInTheDocument();
    expect(within(dock).getByTestId('party-dock-info')).toBeInTheDocument();
    // Absent, not disabled: there is no album to go to yet.
    expect(within(dock).queryByTestId('party-dock-album')).not.toBeInTheDocument();
    expect(within(dock).queryByTestId('party-dock-share')).not.toBeInTheDocument();
  });

  it('omits Info entirely when the host wrote nothing for this surface', async () => {
    installFetchMock({ [`GET /api/party/${TOKEN}`]: () => jsonResponse(context()) });
    render(page());

    const dock = await screen.findByTestId('party-dock');
    expect(within(dock).queryByTestId('party-dock-info')).not.toBeInTheDocument();
  });
});

describe('the memories', () => {
  it('thanks the guests in the host’s words, or the product’s', async () => {
    installFetchMock({
      [`GET /api/party/${TOKEN}`]: () => jsonResponse(context({
        phase: 'after',
        library: { available: true, accessEndsAt: '2027-07-20T00:00:00Z' },
        content: [slot('thank-you', { headline: 'Che serata!', message: 'Grazie di cuore' })],
      })),
      [`GET /api/party/${TOKEN}/items`]: () => jsonResponse({ albumName: 'Album', items: [] }),
    });
    render(page());

    expect(await screen.findByTestId('party-after')).toBeInTheDocument();
    expect(screen.getByText('Che serata!')).toBeInTheDocument();
    expect(screen.getByTestId('party-memories-cta')).toBeInTheDocument();
    // The deadline is stated rather than left for the guest to discover.
    expect(screen.getByText(/disponibili fino al/i)).toBeInTheDocument();
  });

  it('falls back to a product thank-you when the host wrote none', async () => {
    installFetchMock({
      [`GET /api/party/${TOKEN}`]: () => jsonResponse(context({
        phase: 'after', library: { available: true, accessEndsAt: null },
      })),
      [`GET /api/party/${TOKEN}/items`]: () => jsonResponse({ albumName: 'Album', items: [] }),
    });
    render(page());

    expect(await screen.findByText(/Grazie per aver festeggiato con noi/i)).toBeInTheDocument();
  });

  it('never offers a dead button once the memories have closed', async () => {
    const mock = installFetchMock({
      [`GET /api/party/${TOKEN}`]: () => jsonResponse(context({
        phase: 'after', library: { available: false, accessEndsAt: null },
      })),
    });
    render(page());

    await screen.findByTestId('party-after');
    expect(screen.queryByTestId('party-memories-cta')).not.toBeInTheDocument();
    expect(screen.getByTestId('party-memories-closed')).toBeInTheDocument();
    // And no request for an album that is no longer there.
    expect(mock.calls.some((c) => c.url.includes('/items'))).toBe(false);
  });

  it('is the greeting and the photographs, and nothing else, in library-only', async () => {
    installFetchMock({
      [`GET /api/party/${TOKEN}`]: () => jsonResponse(context({
        phase: 'after',
        accessMode: 'library-only',
        library: { available: true, accessEndsAt: '2027-07-20T00:00:00Z' },
        // Even were the server to send them, a library-only visit is not a
        // visit to the party: the informational slots stay out of it.
        content: [slot('location', { venueName: 'Villa Aurora', address: 'Via Roma 1' })],
      })),
      [`GET /api/party/${TOKEN}/items`]: () => jsonResponse({ albumName: 'Album', items: [] }),
    });
    render(page());

    const after = await screen.findByTestId('party-after');
    expect(after).toHaveAttribute('data-access', 'library-only');
    expect(screen.queryByText('Villa Aurora')).not.toBeInTheDocument();
    expect(screen.getByTestId('party-memories-cta')).toBeInTheDocument();

    const dock = await screen.findByTestId('party-dock');
    // Memories and the album. No capability deck, no info, no contribution.
    expect(within(dock).queryByTestId('party-dock-info')).not.toBeInTheDocument();
    expect(within(dock).queryByTestId('party-dock-share')).not.toBeInTheDocument();
    expect(screen.queryByTestId('party-capability-print')).not.toBeInTheDocument();
  });

  it('opens the album from the memories', async () => {
    installFetchMock({
      [`GET /api/party/${TOKEN}`]: () => jsonResponse(context({
        phase: 'after',
        albumName: 'Album della festa',
        library: { available: true, accessEndsAt: null },
      })),
      [`GET /api/party/${TOKEN}/items`]: () => jsonResponse({
        albumName: 'Album della festa',
        items: [{
          id: 'f1', mediaType: 'image',
          thumbnailUrl: `/api/party/${TOKEN}/media/f1/thumbnail`,
          previewUrl: `/api/party/${TOKEN}/media/f1/preview`,
          downloadUrl: `/api/party/${TOKEN}/media/f1/download`,
        }],
      }),
    });
    render(page());

    await userEvent.click(await screen.findByTestId('party-memories-cta'));
    expect(await screen.findByTestId('party-grid')).toBeInTheDocument();
  });
});

describe('the party moving under a guest who is reading', () => {
  it('offers the party rather than taking the page away', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      let phase = 'before';
      installFetchMock({
        [`GET /api/party/${TOKEN}`]: () => jsonResponse(context({
          phase,
          albumName: phase === 'live' ? 'Album' : null,
          capabilities: {
            contributionUrl: null, gameUrl: null, printUrl: null, faceSearch: false,
          },
        })),
        [`GET /api/party/${TOKEN}/items`]: () => jsonResponse({
          albumName: 'Album',
          items: [{
            id: 'f1', mediaType: 'image',
            thumbnailUrl: `/api/party/${TOKEN}/media/f1/thumbnail`,
            previewUrl: `/api/party/${TOKEN}/media/f1/preview`,
            downloadUrl: `/api/party/${TOKEN}/media/f1/download`,
          }],
        }),
      });
      render(page());

      expect(await screen.findByTestId('party-before')).toBeInTheDocument();

      phase = 'live';
      await vi.advanceTimersByTimeAsync(20_000);

      // The invitation is STILL on screen: nobody is reading a page that
      // rearranges itself under them.
      expect(await screen.findByTestId('party-moved-live')).toBeInTheDocument();
      expect(screen.getByTestId('party-before')).toBeInTheDocument();

      await userEvent.click(screen.getByTestId('party-phase-enter'));
      expect(await screen.findByTestId('party-grid')).toBeInTheDocument();
      expect(screen.queryByTestId('party-before')).not.toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });
});

describe('unavailable', () => {
  it('is one generic answer, whatever closed it', async () => {
    installFetchMock({
      [`GET /api/party/${TOKEN}`]: () => new Response('', { status: 404 }),
    });
    render(page());

    expect(await screen.findByText(/non .* disponibile/i)).toBeInTheDocument();
    expect(screen.queryByTestId('party-before')).not.toBeInTheDocument();
    expect(screen.queryByTestId('party-after')).not.toBeInTheDocument();
  });
});
