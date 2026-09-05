import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { PartyPrintPage } from './PartyPrintPage';
import { errorResponse, installFetchMock, jsonResponse } from '../test-utils';
import { I18nProvider } from '../i18n';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
  window.sessionStorage.clear();
  window.localStorage.clear();
});

const TOKEN = 'print-tok';

function wrapper(token = TOKEN) {
  return (
    <I18nProvider>
      <MemoryRouter initialEntries={[`/party/${token}/print`]}>
        <Routes>
          <Route path="/party/:token/print" element={<PartyPrintPage />} />
        </Routes>
      </MemoryRouter>
    </I18nProvider>
  );
}

function photo(id: string) {
  return {
    id,
    thumbnailUrl: `/api/party/${TOKEN}/print/media/${id}/thumbnail`,
    previewUrl: `/api/party/${TOKEN}/print/media/${id}/preview`,
  };
}

function manifest(overrides: Record<string, unknown> = {}) {
  return {
    partyName: 'Beach Party',
    footerText: 'Grazie di essere qui',
    formats: [
      { type: 'photo', enabled: true, remaining: 12, requiredPhotos: 1 },
      { type: 'strip4', enabled: true, remaining: 5, requiredPhotos: 4 },
    ],
    photos: ['f1', 'f2', 'f3', 'f4', 'f5'].map(photo),
    ...overrides,
  };
}

const accepted = {
  jobId: 'job-1', publicSequence: 12, product: 'photo', remainingForProduct: 11,
};

function mount(body: unknown = manifest(), extra: Record<string, ReturnType<typeof jsonResponse> | (() => Response)> = {}) {
  return installFetchMock({
    [`GET /api/party/${TOKEN}/print`]: () => jsonResponse(body),
    ...(extra as Record<string, () => Response>),
  });
}

/** jsdom never loads images, so a photograph's real shape is stated here. */
function setNatural(img: HTMLElement, width: number, height: number) {
  Object.defineProperty(img, 'naturalWidth', { value: width, configurable: true });
  Object.defineProperty(img, 'naturalHeight', { value: height, configurable: true });
  fireEvent.load(img);
}

async function chooseFormat(user: ReturnType<typeof userEvent.setup>, type: 'photo' | 'strip4') {
  await user.click(await screen.findByTestId(`party-print-format-${type}`));
}

async function pick(user: ReturnType<typeof userEvent.setup>, count: number) {
  const picks = screen.getAllByRole('button', { name: /Scegli questa foto/ });
  for (let i = 0; i < count; i += 1) await user.click(picks[i]);
}

const next = () => screen.getByRole('button', { name: 'Continua' });

/** Format → selection → (order) → framing → preview, for a ready-to-send sheet. */
async function compose(
  user: ReturnType<typeof userEvent.setup>, type: 'photo' | 'strip4',
) {
  await chooseFormat(user, type);
  await pick(user, type === 'strip4' ? 4 : 1);
  await user.click(next());
  if (type === 'strip4') await user.click(next());
  const frames = type === 'strip4' ? 4 : 1;
  for (let i = 0; i < frames; i += 1) await user.click(next());
}

function lastPost(calls: { url: string; method: string; body: string | null }[]) {
  const post = [...calls].reverse().find((c) => c.method === 'POST');
  return post ? JSON.parse(post.body ?? '{}') : null;
}

describe('PartyPrintPage (public print studio)', () => {
  // --- What is on offer ---------------------------------------------------

  it('offers each product with its OWN remaining budget, never a shared total', async () => {
    mount();
    render(wrapper());
    // 12 and 5 are independent budgets. Nothing on this page may add them up:
    // spending a strip must not appear to consume a photo print.
    expect(await screen.findByTestId('party-print-format-photo'))
      .toHaveTextContent('12 stampe disponibili');
    expect(screen.getByTestId('party-print-format-strip4'))
      .toHaveTextContent('5 stampe disponibili');
  });

  it('does not render a product the host has turned off', async () => {
    mount(manifest({
      formats: [
        { type: 'photo', enabled: true, remaining: 3, requiredPhotos: 1 },
        { type: 'strip4', enabled: false, remaining: 0, requiredPhotos: 4 },
      ],
    }));
    render(wrapper());
    await screen.findByTestId('party-print-format-photo');
    // Not a disabled card, not "coming soon": a capability that is off does not
    // exist on this page.
    expect(screen.queryByTestId('party-print-format-strip4')).not.toBeInTheDocument();
  });

  it('shows an enabled product whose budget ran out, and refuses to start it', async () => {
    mount(manifest({
      formats: [
        { type: 'photo', enabled: true, remaining: 3, requiredPhotos: 1 },
        { type: 'strip4', enabled: true, remaining: 0, requiredPhotos: 4 },
      ],
    }));
    render(wrapper());
    const strip = await screen.findByTestId('party-print-format-strip4');
    // Guests watch each other collect strips, so "esaurito" is the honest
    // answer — but it cannot be startable.
    expect(strip).toHaveTextContent('Esaurito');
    expect(strip).toBeDisabled();
  });

  it('says printing is finished when every product is spent', async () => {
    mount(manifest({
      formats: [
        { type: 'photo', enabled: true, remaining: 0, requiredPhotos: 1 },
        { type: 'strip4', enabled: true, remaining: 0, requiredPhotos: 4 },
      ],
    }));
    render(wrapper());
    expect(await screen.findByText(/Le stampe di questa festa sono finite/))
      .toBeInTheDocument();
    expect(screen.queryByTestId('party-print-format-photo')).not.toBeInTheDocument();
  });

  it('shows the unavailable state when the print token no longer resolves', async () => {
    installFetchMock({ [`GET /api/party/${TOKEN}/print`]: () => errorResponse(404) });
    render(wrapper());
    expect(await screen.findByText('La stampa non è disponibile.')).toBeInTheDocument();
  });

  it('distinguishes a server failure from an unavailable capability', async () => {
    installFetchMock({ [`GET /api/party/${TOKEN}/print`]: () => errorResponse(500) });
    render(wrapper());
    expect(await screen.findByText(/Non riesco a caricare lo studio/)).toBeInTheDocument();
  });

  // --- Choosing -----------------------------------------------------------

  it('requires FOUR DIFFERENT photos for a strip and will not take a fifth', async () => {
    const user = userEvent.setup();
    mount();
    render(wrapper());
    await chooseFormat(user, 'strip4');
    expect(next()).toBeDisabled();
    await pick(user, 3);
    expect(next()).toBeDisabled();
    await pick(user, 5);
    // Five taps, four slots: the fifth photograph is simply not taken.
    expect(screen.getByText('4 di 4')).toBeInTheDocument();
    expect(next()).toBeEnabled();
  });

  it('numbers the chosen photographs in the order they were chosen', async () => {
    const user = userEvent.setup();
    mount();
    render(wrapper());
    await chooseFormat(user, 'strip4');
    const picks = screen.getAllByRole('button', { name: /Scegli questa foto/ });
    await user.click(picks[2]);
    await user.click(picks[0]);
    expect(within(picks[2]).getByText('1')).toBeInTheDocument();
    expect(within(picks[0]).getByText('2')).toBeInTheDocument();
  });

  it('renumbers the rest when a photograph is taken back out', async () => {
    const user = userEvent.setup();
    mount();
    render(wrapper());
    await chooseFormat(user, 'strip4');
    await pick(user, 3);
    const chosen = screen.getAllByRole('button', { name: /Togli dalla selezione/ });
    await user.click(chosen[0]);
    const remaining = screen.getAllByRole('button', { name: /Togli dalla selezione/ });
    expect(within(remaining[0]).getByText('1')).toBeInTheDocument();
    expect(within(remaining[1]).getByText('2')).toBeInTheDocument();
  });

  // --- Order --------------------------------------------------------------

  it('reorders a strip with BUTTONS, not only by dragging', async () => {
    const user = userEvent.setup();
    const mock = mount(manifest(), {
      [`POST /api/party/${TOKEN}/print`]: () => jsonResponse(accepted, 202),
    });
    render(wrapper());
    await chooseFormat(user, 'strip4');
    await pick(user, 4);
    await user.click(next());
    // Dragging is not reachable by keyboard, by screen reader, or by anyone who
    // cannot hold a press: the order has to be changeable without it.
    const down = screen.getAllByRole('button', { name: /Sposta giù/ });
    expect(down).toHaveLength(4);
    await user.click(down[0]);
    await user.click(next());
    for (let i = 0; i < 4; i += 1) await user.click(next());
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await waitFor(() => expect(lastPost(mock.calls)).not.toBeNull());
    expect(lastPost(mock.calls).slots.map((s: { itemId: string }) => s.itemId))
      .toEqual(['f2', 'f1', 'f3', 'f4']);
  });

  it('cannot move the first photograph up or the last one down', async () => {
    const user = userEvent.setup();
    mount();
    render(wrapper());
    await chooseFormat(user, 'strip4');
    await pick(user, 4);
    await user.click(next());
    expect(screen.getAllByRole('button', { name: /Sposta su/ })[0]).toBeDisabled();
    expect(screen.getAllByRole('button', { name: /Sposta giù/ })[3]).toBeDisabled();
  });

  // --- Framing ------------------------------------------------------------

  it('sends the whole photograph when the guest frames nothing', async () => {
    const user = userEvent.setup();
    const mock = mount(manifest(), {
      [`POST /api/party/${TOKEN}/print`]: () => jsonResponse(accepted, 202),
    });
    render(wrapper());
    await compose(user, 'photo');
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await waitFor(() => expect(lastPost(mock.calls)).not.toBeNull());
    expect(lastPost(mock.calls).slots).toEqual([
      { itemId: 'f1', cropX: 0, cropY: 0, cropWidth: 1, cropHeight: 1 },
    ]);
  });

  it('narrows the crop when the guest zooms in, and restores it on reset', async () => {
    const user = userEvent.setup();
    const mock = mount(manifest(), {
      [`POST /api/party/${TOKEN}/print`]: () => jsonResponse(accepted, 202),
    });
    render(wrapper());
    await chooseFormat(user, 'photo');
    await pick(user, 1);
    await user.click(next());

    const zoom = () => screen.getByRole('slider', { name: /Ingrandimento/ }) as HTMLInputElement;
    fireEvent.change(zoom(), { target: { value: '2' } });

    // Reset puts the whole photograph back, so framing is always undoable.
    await user.click(screen.getByRole('button', { name: 'Reimposta inquadratura' }));
    expect(zoom().value).toBe('1');

    fireEvent.change(zoom(), { target: { value: '2' } });
    await user.click(next());
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await waitFor(() => expect(lastPost(mock.calls)).not.toBeNull());
    const zoomed = lastPost(mock.calls).slots[0];
    expect(zoomed.cropWidth).toBeCloseTo(0.5, 5);
    expect(zoomed.cropX).toBeCloseTo(0.25, 5);
  });

  it('pans with the arrow keys, not only with a finger', async () => {
    const user = userEvent.setup();
    const mock = mount(manifest(), {
      [`POST /api/party/${TOKEN}/print`]: () => jsonResponse(accepted, 202),
    });
    render(wrapper());
    await chooseFormat(user, 'photo');
    await pick(user, 1);
    await user.click(next());
    fireEvent.change(screen.getByRole('slider', { name: /Ingrandimento/ }), {
      target: { value: '2' },
    });
    const frame = screen.getByTestId('party-print-crop');
    fireEvent.keyDown(frame, { key: 'ArrowRight' });
    fireEvent.keyDown(frame, { key: 'ArrowRight' });
    await user.click(next());
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await waitFor(() => expect(lastPost(mock.calls)).not.toBeNull());
    // Two nudges right of a half-width crop centred at 0.5.
    expect(lastPost(mock.calls).slots[0].cropX).toBeCloseTo(0.29, 5);
  });

  it('never lets framing walk off the edge of the photograph', async () => {
    const user = userEvent.setup();
    const mock = mount(manifest(), {
      [`POST /api/party/${TOKEN}/print`]: () => jsonResponse(accepted, 202),
    });
    render(wrapper());
    await chooseFormat(user, 'photo');
    await pick(user, 1);
    await user.click(next());
    fireEvent.change(screen.getByRole('slider', { name: /Ingrandimento/ }), {
      target: { value: '2' },
    });
    const frame = screen.getByTestId('party-print-crop');
    for (let i = 0; i < 60; i += 1) fireEvent.keyDown(frame, { key: 'ArrowRight' });
    await user.click(next());
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await waitFor(() => expect(lastPost(mock.calls)).not.toBeNull());
    const crop = lastPost(mock.calls).slots[0];
    // The server rejects a crop that leaves the image; the editor cannot make one.
    expect(crop.cropX + crop.cropWidth).toBeLessThanOrEqual(1);
    expect(crop.cropX).toBeGreaterThanOrEqual(0);
  });

  // --- The sheet ----------------------------------------------------------

  it('previews TWO identical strips on one sheet, with the cut marks', async () => {
    const user = userEvent.setup();
    mount();
    const { container } = render(wrapper());
    await compose(user, 'strip4');
    // One 10x15 yields two keepsakes: eight slots, two strips, two ticks.
    expect(container.querySelectorAll('.party-print-slot')).toHaveLength(8);
    expect(screen.getByTestId('party-print-strip-0')).toBeInTheDocument();
    expect(screen.getByTestId('party-print-strip-1')).toBeInTheDocument();
    expect(container.querySelectorAll('.party-print-cut')).toHaveLength(2);
    expect(screen.getByText(/Due strisce identiche/)).toBeInTheDocument();
    // Drawn twice, announced once: the caption is what says there are two,
    // rather than a screen reader reading the whole composition again.
    expect(screen.getByTestId('party-print-strip-1')).toHaveAttribute('aria-hidden', 'true');
    expect(screen.getByTestId('party-print-strip-0')).not.toHaveAttribute('aria-hidden');
  });

  it('turns the sheet to follow a landscape photograph', async () => {
    const user = userEvent.setup();
    mount();
    render(wrapper());
    await chooseFormat(user, 'photo');
    const picks = screen.getAllByRole('button', { name: /Scegli questa foto/ });
    setNatural(within(picks[0]).getByRole('presentation', { hidden: true }), 4000, 3000);
    await user.click(picks[0]);
    await user.click(next());
    await user.click(next());
    // A landscape picture prints on a landscape sheet, not on a portrait one
    // with white bars beside it — the same choice the renderer makes.
    expect(screen.getByTestId('party-print-sheet'))
      .toHaveAttribute('data-orientation', 'landscape');
  });

  it('puts the party name, the host line and the APPROVED wordmark on the sheet', async () => {
    const user = userEvent.setup();
    mount();
    render(wrapper());
    await compose(user, 'photo');
    const sheet = screen.getByTestId('party-print-sheet');
    expect(within(sheet).getByText('Beach Party')).toBeInTheDocument();
    expect(within(sheet).getByText('Grazie di essere qui')).toBeInTheDocument();
    // The artwork, placed — never the product name set in a typeface.
    expect(within(sheet).getByRole('img', { name: 'NubArca' }))
      .toHaveAttribute('src', '/brand/nubarca-wordmark-on-light.png');
  });

  it('takes the ON-DARK wordmark when the paper is dark', async () => {
    const user = userEvent.setup();
    mount();
    render(wrapper());
    await compose(user, 'photo');
    await user.click(screen.getByRole('radio', { name: 'Notte' }));
    const sheet = screen.getByTestId('party-print-sheet');
    expect(sheet).toHaveAttribute('data-theme', 'midnight');
    expect(within(sheet).getByRole('img', { name: 'NubArca' }))
      .toHaveAttribute('src', '/brand/nubarca-wordmark-on-dark-480w.png');
  });

  it('sends the theme the guest chose', async () => {
    const user = userEvent.setup();
    const mock = mount(manifest(), {
      [`POST /api/party/${TOKEN}/print`]: () => jsonResponse(accepted, 202),
    });
    render(wrapper());
    await compose(user, 'photo');
    await user.click(screen.getByRole('radio', { name: 'Festa' }));
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await waitFor(() => expect(lastPost(mock.calls)).not.toBeNull());
    expect(lastPost(mock.calls).theme).toBe('event');
  });

  // --- Sending ------------------------------------------------------------

  it('sends an Idempotency-Key, and REUSES it when the same sheet is retried', async () => {
    const user = userEvent.setup();
    let attempt = 0;
    const mock = mount(manifest(), {
      [`POST /api/party/${TOKEN}/print`]: () => {
        attempt += 1;
        return attempt === 1 ? errorResponse(503, { error: 'printer_unavailable' })
          : jsonResponse(accepted, 202);
      },
    });
    render(wrapper());
    await compose(user, 'photo');
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await screen.findByRole('alert');
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await screen.findByText('La tua stampa è in coda');

    const posts = mock.calls.filter((c) => c.method === 'POST');
    expect(posts).toHaveLength(2);
    const keyOf = (call: { init?: RequestInit }) =>
      (call.init?.headers as Record<string, string>)['Idempotency-Key'];
    expect(keyOf(posts[0])).toBeTruthy();
    // Printing is physical: a retry of the SAME sheet must never be able to
    // become a second sheet, so it carries the first attempt's key.
    expect(keyOf(posts[1])).toBe(keyOf(posts[0]));
  });

  it('retries the SAME sheet, not just the same key', async () => {
    const user = userEvent.setup();
    let attempt = 0;
    const mock = mount(manifest(), {
      [`POST /api/party/${TOKEN}/print`]: () => {
        attempt += 1;
        return attempt === 1 ? errorResponse(503, { error: 'printer_unavailable' })
          : jsonResponse(accepted, 202);
      },
    });
    render(wrapper());
    await compose(user, 'photo');
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await screen.findByRole('alert');

    // The photograph's real shape arrives between the two attempts. The server
    // has already decided about this key, so the second attempt must not be
    // asking it to print something else under it.
    const sheet = screen.getByTestId('party-print-sheet');
    setNatural(within(sheet).getAllByRole('presentation', { hidden: true })[0], 4000, 3000);
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await screen.findByText('La tua stampa è in coda');

    const posts = mock.calls.filter((c) => c.method === 'POST');
    expect(JSON.parse(posts[1].body!).slots).toEqual(JSON.parse(posts[0].body!).slots);
  });

  it('mints a NEW key once the composition changes', async () => {
    const user = userEvent.setup();
    const mock = mount(manifest(), {
      [`POST /api/party/${TOKEN}/print`]: () => errorResponse(503, { error: 'render_failed' }),
    });
    render(wrapper());
    await compose(user, 'photo');
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await screen.findByRole('alert');
    // A different sheet is a different print, and must not be deduplicated
    // against the one before it.
    await user.click(screen.getByRole('button', { name: 'Indietro' }));
    fireEvent.change(screen.getByRole('slider', { name: /Ingrandimento/ }), {
      target: { value: '2.5' },
    });
    await user.click(next());
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await waitFor(() => {
      expect(mock.calls.filter((c) => c.method === 'POST')).toHaveLength(2);
    });
    const posts = mock.calls.filter((c) => c.method === 'POST');
    const keyOf = (call: { init?: RequestInit }) =>
      (call.init?.headers as Record<string, string>)['Idempotency-Key'];
    expect(keyOf(posts[1])).not.toBe(keyOf(posts[0]));
  });

  it('gives the guest their queue number and what is left of that budget', async () => {
    const user = userEvent.setup();
    mount(manifest(), {
      [`POST /api/party/${TOKEN}/print`]: () => jsonResponse(accepted, 202),
    });
    render(wrapper());
    await compose(user, 'photo');
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    const sent = await screen.findByRole('status');
    expect(within(sent).getByText('12')).toBeInTheDocument();
    expect(within(sent).getByText('In preparazione')).toBeInTheDocument();
    expect(within(sent).getByText('Restano 11 stampe di questo formato')).toBeInTheDocument();
  });

  it('says plainly when that was the last print of the format', async () => {
    const user = userEvent.setup();
    mount(manifest(), {
      [`POST /api/party/${TOKEN}/print`]: () =>
        jsonResponse({ ...accepted, remainingForProduct: 0 }, 202),
    });
    render(wrapper());
    await compose(user, 'photo');
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await screen.findByRole('status');
    expect(screen.getByText('Era l’ultima stampa di questo formato.')).toBeInTheDocument();
    // With nothing left there is nothing to offer.
    expect(screen.queryByRole('button', { name: 'Stampa un altro ricordo' }))
      .not.toBeInTheDocument();
  });

  it('follows the print through the queue instead of going quiet after "sent"', async () => {
    // Driven with fireEvent rather than userEvent: the poll is on a timer, and
    // this test owns the clock.
    vi.useFakeTimers();
    mount(manifest(), {
      [`POST /api/party/${TOKEN}/print`]: () => jsonResponse(accepted, 202),
      [`GET /api/party/${TOKEN}/print/job-1`]: () => jsonResponse({
        jobId: 'job-1', state: 'printing', publicSequence: 12, product: 'photo',
      }),
    });
    // Testing Library's own waiting is built on the timers this test has
    // replaced, so every step is advanced explicitly instead.
    const tick = async (ms = 1) => {
      await act(async () => { await vi.advanceTimersByTimeAsync(ms); });
    };
    render(wrapper());
    await tick();
    fireEvent.click(screen.getByTestId('party-print-format-photo'));
    fireEvent.click(screen.getAllByRole('button', { name: /Scegli questa foto/ })[0]);
    fireEvent.click(next());
    fireEvent.click(next());
    fireEvent.click(screen.getByRole('button', { name: 'Stampa' }));
    await tick();
    expect(screen.getByText('In preparazione')).toBeInTheDocument();
    // The guest is standing at a printer: tell them what it is doing.
    await tick(4_500);
    expect(screen.getByText('In stampa')).toBeInTheDocument();
  });

  it('says which refusal it was, in words a guest can act on', async () => {
    const user = userEvent.setup();
    mount(manifest(), {
      [`POST /api/party/${TOKEN}/print`]: () => errorResponse(409, { error: 'budget_exhausted' }),
    });
    render(wrapper());
    await compose(user, 'photo');
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    expect(await screen.findByRole('alert'))
      .toHaveTextContent('Le stampe di questo formato sono appena finite.');
  });

  it('never reveals a server reason it was not given', async () => {
    const user = userEvent.setup();
    mount(manifest(), {
      [`POST /api/party/${TOKEN}/print`]: () =>
        errorResponse(500, { error: 'Npgsql.PostgresException: relation does not exist' }),
    });
    render(wrapper());
    await compose(user, 'photo');
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('La stampa non è stata inviata. Riprova.');
    expect(alert).not.toHaveTextContent(/Npgsql|relation/);
  });

  // --- What the studio may reach -----------------------------------------

  it('asks for derived media only, never an original or a download', async () => {
    const user = userEvent.setup();
    const mock = mount(manifest(), {
      [`POST /api/party/${TOKEN}/print`]: () => jsonResponse(accepted, 202),
    });
    const { container } = render(wrapper());
    await compose(user, 'photo');
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await screen.findByRole('status');
    const urls = [
      ...mock.calls.map((c) => c.url),
      ...Array.from(container.querySelectorAll('img'), (img) => img.getAttribute('src') ?? ''),
    ];
    // The sheet is composed server-side from the original; the browser only ever
    // sees stripped thumbnails and previews.
    expect(urls.some((url) => /\/download|\/content|\/original/.test(url))).toBe(false);
  });

  // --- Carrying a face search across --------------------------------------

  it('offers the guest their own photographs when they searched on the hub', async () => {
    window.sessionStorage.setItem('nubarca.party.faceFilter', JSON.stringify(['f2', 'f4']));
    const user = userEvent.setup();
    mount();
    render(wrapper());
    await chooseFormat(user, 'photo');
    await user.click(screen.getByRole('button', { name: 'Solo le mie foto' }));
    // The hub's face search does not have to be run twice to print from it.
    expect(screen.getAllByRole('button', { name: /Scegli questa foto/ })).toHaveLength(2);
  });

  it('ignores a face search left behind by a different party', async () => {
    window.sessionStorage.setItem(
      'nubarca.party.faceFilter', JSON.stringify(['other-1', 'other-2']));
    const user = userEvent.setup();
    mount();
    render(wrapper());
    await chooseFormat(user, 'photo');
    // Ids from elsewhere match nothing this token serves, so there is no filter
    // to offer rather than one that would empty the gallery.
    expect(screen.queryByRole('button', { name: 'Solo le mie foto' })).not.toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: /Scegli questa foto/ })).toHaveLength(5);
  });

  // --- The way out --------------------------------------------------------

  it('offers the way back the hub left behind, and nothing when opened cold', async () => {
    mount();
    const { unmount } = render(wrapper());
    await screen.findByTestId('party-print-format-photo');
    // A print token cannot address the album, so an exit is only offered when
    // the hub itself left its path in this tab.
    expect(screen.queryByRole('link', { name: /Torna alla festa/ })).not.toBeInTheDocument();
    unmount();

    window.sessionStorage.setItem('nubarca.party.home', '/party/view-tok');
    render(wrapper());
    expect(await screen.findByRole('link', { name: /Torna alla festa/ }))
      .toHaveAttribute('href', '/party/view-tok');
  });

  it('refuses a remembered path that is not a party page', async () => {
    window.sessionStorage.setItem('nubarca.party.home', 'https://elsewhere.example/steal');
    mount();
    render(wrapper());
    await screen.findByTestId('party-print-format-photo');
    expect(screen.queryByRole('link', { name: /Torna alla festa/ })).not.toBeInTheDocument();
  });
});
