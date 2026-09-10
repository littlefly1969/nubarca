import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
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

// A slot's one photograph. The SERVER decides whether there is one and where it
// lives — an address on this token, never a file id — so what is checked here is
// the rendering: where it goes, that it is drawn once, that it never becomes a
// download, and that a picture which fails to load leaves no hole.
describe('a slot’s photograph', () => {
  const MENU_MEDIA = `/api/party/${TOKEN}/content/menu/media?v=2`;
  const withMedia = (kind: string, content: Record<string, unknown>, mediaUrl: string | null) =>
    ({ ...slot(kind, content), mediaUrl });

  it('puts the menu in a card with its photograph on top', async () => {
    installFetchMock({
      [`GET /api/party/${TOKEN}`]: () => jsonResponse(context({
        content: [withMedia('menu', {
          intro: 'Cena in giardino',
          sections: [{ title: 'Antipasti', items: ['Bruschetta'] }],
        }, MENU_MEDIA)],
      })),
    });
    render(page());

    await screen.findByTestId('party-before');
    const menu = document.querySelector<HTMLElement>('[data-content="menu"]')!;
    const image = within(menu).getByTestId('party-content-media');
    expect(image).toHaveAttribute('src', MENU_MEDIA);
    // The picture first, then the menu itself.
    expect(menu.firstElementChild).toBe(image);
    expect(within(menu).getByText('Antipasti')).toBeInTheDocument();
    expect(within(menu).getByText('Bruschetta')).toBeInTheDocument();
  });

  it('keeps a menu without a photograph a perfectly good menu', async () => {
    installFetchMock({
      [`GET /api/party/${TOKEN}`]: () => jsonResponse(context({
        content: [slot('menu', { intro: 'Cena', sections: [{ title: 'Primi', items: ['Risotto'] }] })],
      })),
    });
    render(page());

    await screen.findByTestId('party-before');
    const menu = document.querySelector<HTMLElement>('[data-content="menu"]')!;
    expect(menu.querySelector('img')).toBeNull();
    expect(within(menu).getByText('Risotto')).toBeInTheDocument();
  });

  it('draws another slot’s photograph only when it has one', async () => {
    installFetchMock({
      [`GET /api/party/${TOKEN}`]: () => jsonResponse(context({
        content: [
          withMedia('location', { venueName: 'Villa Aurora', address: 'Via Roma 1' },
            `/api/party/${TOKEN}/content/location/media?v=1`),
          slot('info', { title: 'Parcheggio', body: 'In fondo alla via' }),
        ],
      })),
    });
    render(page());

    await screen.findByTestId('party-before');
    expect(document.querySelector('[data-content="location"] img'))
      .toHaveAttribute('src', `/api/party/${TOKEN}/content/location/media?v=1`);
    expect(document.querySelector('[data-content="info"] img')).toBeNull();
  });

  it('draws the invitation’s own photograph once, as the hero', async () => {
    const INVITATION_MEDIA = `/api/party/${TOKEN}/content/invitation/media?v=3`;
    installFetchMock({
      [`GET /api/party/${TOKEN}`]: () => jsonResponse(context({
        // The server has already put the invitation's photograph ahead of the
        // album cover; the page follows it and does not draw it twice.
        coverUrl: INVITATION_MEDIA,
        content: [withMedia('invitation', { headline: 'Vieni!' }, INVITATION_MEDIA)],
      })),
    });
    render(page());

    expect(await screen.findByTestId('party-invitation-hero')).toHaveAttribute('src', INVITATION_MEDIA);
    expect(document.querySelectorAll(`img[src="${INVITATION_MEDIA}"]`)).toHaveLength(1);
    expect(screen.getByText('Vieni!')).toBeInTheDocument();
  });

  it('shows the album’s chosen cover when the invitation has no photograph of its own', async () => {
    const COVER = `/api/party/${TOKEN}/media/f1/preview`;
    installFetchMock({
      [`GET /api/party/${TOKEN}`]: () => jsonResponse(context({
        coverUrl: COVER,
        content: [slot('invitation', { headline: 'Vieni!' })],
      })),
    });
    render(page());

    expect(await screen.findByTestId('party-invitation-hero')).toHaveAttribute('src', COVER);
    expect(screen.queryByTestId('party-content-media')).not.toBeInTheDocument();
  });

  it('leaves the words and no broken frame when a photograph cannot be loaded', async () => {
    installFetchMock({
      [`GET /api/party/${TOKEN}`]: () => jsonResponse(context({
        coverUrl: `/api/party/${TOKEN}/content/invitation/media?v=1`,
        content: [withMedia('menu', { intro: 'Cena in giardino', sections: [] }, MENU_MEDIA)],
      })),
    });
    render(page());

    // Sent to Trash between the page loading and the picture arriving.
    fireEvent.error(await screen.findByTestId('party-content-media'));
    expect(screen.queryByTestId('party-content-media')).not.toBeInTheDocument();
    expect(screen.getByText('Cena in giardino')).toBeInTheDocument();

    fireEvent.error(screen.getByTestId('party-invitation-hero'));
    expect(screen.queryByTestId('party-invitation-hero')).not.toBeInTheDocument();
    expect(document.querySelector('.party-invitation-cover--blank')).toBeInTheDocument();
  });

  it('never turns a content photograph into a download', async () => {
    installFetchMock({
      [`GET /api/party/${TOKEN}`]: () => jsonResponse(context({
        content: [
          withMedia('location', { venueName: 'Villa Aurora', address: 'Via Roma 1' },
            `/api/party/${TOKEN}/content/location/media?v=1`),
          withMedia('menu', { intro: 'Cena', sections: [] }, MENU_MEDIA),
        ],
      })),
    });
    render(page());

    const content = await screen.findByTestId('party-content');
    expect(content.querySelector('a[download]')).toBeNull();
    // The only link is the maps link built from the address — nothing points
    // at a photograph, and there is no button to save one.
    const hrefs = Array.from(content.querySelectorAll('a')).map((a) => a.getAttribute('href') ?? '');
    expect(hrefs.some((href) => href.includes('/content/'))).toBe(false);
    expect(within(content).queryByRole('button')).toBeNull();
  });

  it('gives the thank-you its photograph afterwards', async () => {
    const THANKS = `/api/party/${TOKEN}/content/thank-you/media?v=1`;
    installFetchMock({
      [`GET /api/party/${TOKEN}`]: () => jsonResponse(context({
        phase: 'after',
        library: { available: false, accessEndsAt: null },
        content: [withMedia('thank-you', { headline: 'Che serata!' }, THANKS)],
      })),
    });
    render(page());

    const after = await screen.findByTestId('party-after');
    expect(within(after).getByTestId('party-content-media')).toHaveAttribute('src', THANKS);
    expect(within(after).getByText('Che serata!')).toBeInTheDocument();
  });
});
