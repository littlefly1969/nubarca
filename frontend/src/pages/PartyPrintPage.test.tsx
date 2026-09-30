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

/**
 * userEvent with its inter-event delay removed.
 *
 * Composing a strip is eight clicks — four photographs, plus the taps that
 * prove a fifth is refused — and with the default delay that walk sat right at
 * the 5s test timeout, so a loaded machine turned it red for no reason. The
 * delay buys nothing here: nothing in the studio is debounced or on a timer.
 */
const setup = () => userEvent.setup({ delay: null });

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
      { type: 'photo', enabled: true, remaining: 12, requiredPhotos: 1, remainingForYou: null },
      { type: 'twinStrip4', enabled: true, remaining: 5, requiredPhotos: 8, remainingForYou: null, cutByPrinter: true },
    ],
    paperSize: '10x15',
    photos: ['f1', 'f2', 'f3', 'f4', 'f5', 'f6', 'f7', 'f8', 'f9', 'f10'].map(photo),
    ...overrides,
  };
}

const accepted = {
  jobId: 'job-1', publicSequence: 12, product: 'photo', remainingForProduct: 11,
  queueAhead: 0,
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

type Product = 'photo' | 'grid4' | 'twinStrip4';

async function chooseFormat(user: ReturnType<typeof userEvent.setup>, type: Product) {
  await user.click(await screen.findByTestId(`party-print-format-${type}`));
}

async function pick(user: ReturnType<typeof userEvent.setup>, count: number) {
  const picks = screen.getAllByRole('button', { name: /Scegli questa foto/ });
  for (let i = 0; i < count; i += 1) await user.click(picks[i]);
}

const next = () => screen.getByRole('button', { name: 'Continua' });

/** Format → selection → (order) → framing → preview, for a ready-to-send sheet. */
async function compose(
  user: ReturnType<typeof userEvent.setup>, type: Product,
) {
  const count = type === 'twinStrip4' ? 8 : type === 'grid4' ? 4 : 1;
  await chooseFormat(user, type);
  await pick(user, count);
  await user.click(next());
  // Four photos and the twin strip are put in order before they are framed.
  if (type !== 'photo') await user.click(next());
  const frames = count;
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
    expect(screen.getByTestId('party-print-format-twinStrip4'))
      .toHaveTextContent('5 stampe disponibili');
  });

  it('does not render a product the host has turned off', async () => {
    mount(manifest({
      formats: [
        { type: 'photo', enabled: true, remaining: 3, requiredPhotos: 1, remainingForYou: null },
        { type: 'twinStrip4', enabled: false, remaining: 0, requiredPhotos: 8, remainingForYou: null },
      ],
    }));
    render(wrapper());
    await screen.findByTestId('party-print-format-photo');
    // Not a disabled card, not "coming soon": a capability that is off does not
    // exist on this page.
    expect(screen.queryByTestId('party-print-format-twinStrip4')).not.toBeInTheDocument();
  });

  it('shows an enabled product whose budget ran out, and refuses to start it', async () => {
    mount(manifest({
      formats: [
        { type: 'photo', enabled: true, remaining: 3, requiredPhotos: 1, remainingForYou: null },
        { type: 'twinStrip4', enabled: true, remaining: 0, requiredPhotos: 8, remainingForYou: null },
      ],
    }));
    render(wrapper());
    const strip = await screen.findByTestId('party-print-format-twinStrip4');
    // Guests watch each other collect strips, so "esaurito" is the honest
    // answer — but it cannot be startable.
    expect(strip).toHaveTextContent('Esaurito');
    expect(strip).toBeDisabled();
  });

  it('says printing is finished when every product is spent', async () => {
    mount(manifest({
      formats: [
        { type: 'photo', enabled: true, remaining: 0, requiredPhotos: 1, remainingForYou: null },
        { type: 'twinStrip4', enabled: true, remaining: 0, requiredPhotos: 8, remainingForYou: null },
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

  it('requires EIGHT DIFFERENT photos for a strip and will not take a ninth', async () => {
    const user = setup();
    mount();
    render(wrapper());
    await chooseFormat(user, 'twinStrip4');
    expect(next()).toBeDisabled();
    // Four would print the same strip twice: a strip needs its own four each.
    await pick(user, 4);
    expect(next()).toBeDisabled();
    await pick(user, 9);
    // Nine taps, eight slots: the ninth photograph is simply not taken.
    expect(screen.getByText('8 di 8')).toBeInTheDocument();
    expect(next()).toBeEnabled();
  });

  it('keeps Continue pinned at the bottom while the guest chooses from a long album', async () => {
    const user = setup();
    mount();
    render(wrapper());
    await chooseFormat(user, 'photo');
    // The invitation's own bar: reachable without scrolling to the album's end.
    const bar = screen.getByTestId('party-print-select-bar');
    expect(bar).toHaveClass('party-invitation-cta');
    expect(within(bar).getByRole('button', { name: 'Continua' })).toBeDisabled();
    await pick(user, 1);
    expect(within(bar).getByRole('button', { name: 'Continua' })).toBeEnabled();
  });

  it('numbers the chosen photographs in the order they were chosen', async () => {
    const user = setup();
    mount();
    render(wrapper());
    await chooseFormat(user, 'twinStrip4');
    const picks = screen.getAllByRole('button', { name: /Scegli questa foto/ });
    await user.click(picks[2]);
    await user.click(picks[0]);
    expect(within(picks[2]).getByText('1')).toBeInTheDocument();
    expect(within(picks[0]).getByText('2')).toBeInTheDocument();
  });

  it('renumbers the rest when a photograph is taken back out', async () => {
    const user = setup();
    mount();
    render(wrapper());
    await chooseFormat(user, 'twinStrip4');
    await pick(user, 3);
    const chosen = screen.getAllByRole('button', { name: /Togli dalla selezione/ });
    await user.click(chosen[0]);
    const remaining = screen.getAllByRole('button', { name: /Togli dalla selezione/ });
    expect(within(remaining[0]).getByText('1')).toBeInTheDocument();
    expect(within(remaining[1]).getByText('2')).toBeInTheDocument();
  });

  // --- Order --------------------------------------------------------------

  it('reorders a strip with BUTTONS, not only by dragging', async () => {
    const user = setup();
    const mock = mount(manifest(), {
      [`POST /api/party/${TOKEN}/print`]: () => jsonResponse(accepted, 202),
    });
    render(wrapper());
    await chooseFormat(user, 'twinStrip4');
    await pick(user, 8);
    await user.click(next());
    // Dragging is not reachable by keyboard, by screen reader, or by anyone who
    // cannot hold a press: the order has to be changeable without it.
    const down = screen.getAllByRole('button', { name: /Sposta giù/ });
    expect(down).toHaveLength(8);
    await user.click(down[0]);
    await user.click(next());
    for (let i = 0; i < 8; i += 1) await user.click(next());
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await waitFor(() => expect(lastPost(mock.calls)).not.toBeNull());
    expect(lastPost(mock.calls).slots.map((s: { itemId: string }) => s.itemId))
      .toEqual(['f2', 'f1', 'f3', 'f4', 'f5', 'f6', 'f7', 'f8']);
  });

  it('cannot move the first photograph up or the last one down', async () => {
    const user = setup();
    mount();
    render(wrapper());
    await chooseFormat(user, 'twinStrip4');
    await pick(user, 8);
    await user.click(next());
    expect(screen.getAllByRole('button', { name: /Sposta su/ })[0]).toBeDisabled();
    expect(screen.getAllByRole('button', { name: /Sposta giù/ })[7]).toBeDisabled();
  });

  // --- Framing ------------------------------------------------------------

  it('sends the whole photograph when the guest frames nothing', async () => {
    const user = setup();
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
    const user = setup();
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
    const user = setup();
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
    const user = setup();
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

  it('previews TWO different strips on one sheet, and no cut marks', async () => {
    const user = setup();
    mount();
    const { container } = render(wrapper());
    await compose(user, 'twinStrip4');
    // One 10x15 yields two keepsakes: eight slots, two strips — and no ticks,
    // because the printer cuts it and the printed sheet has none.
    expect(container.querySelectorAll('.party-print-slot')).toHaveLength(8);
    expect(container.querySelector('.party-print-cut')).toBeNull();
    // Nothing asks the guest to cut anything by hand.
    expect(screen.queryByText(/a mano|forbici/i)).not.toBeInTheDocument();
    expect(screen.getByText(/Due strisce da quattro foto/)).toBeInTheDocument();
    // Photographs 1–4 on the first strip, 5–8 on the second: not copies.
    const first = screen.getByTestId('party-print-strip-0').querySelector('img');
    const second = screen.getByTestId('party-print-strip-1').querySelector('img');
    expect(first?.getAttribute('src')).toContain('/f1/');
    expect(second?.getAttribute('src')).toContain('/f5/');
    // Two different strips are both announced.
    expect(screen.getByTestId('party-print-strip-1')).not.toHaveAttribute('aria-hidden');
  });

  it('puts the title on the untouched photo, with text and logo chosen apart', async () => {
    const user = setup();
    const mock = mount(manifest(), {
      [`POST /api/party/${TOKEN}/print`]: () => jsonResponse(accepted, 202),
    });
    const { container } = render(wrapper());
    await compose(user, 'photo');

    // The other looks carry none of the overlay's own choices.
    expect(screen.queryByRole('group', { name: 'Colore del testo' })).not.toBeInTheDocument();
    expect(screen.queryByRole('group', { name: 'Logo' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('radio', { name: 'Sulla foto' }));
    const sheet = screen.getByTestId('party-print-sheet');
    expect(sheet).toHaveAttribute('data-theme', 'overlay');
    expect(within(sheet).getByText('Beach Party')).toBeInTheDocument();
    expect(screen.getByTestId('party-print-overlay-symbol')).toBeInTheDocument();

    // Nothing lies over the whole photograph: the only support is the words'
    // own box, transparent at its top and never a solid colour at the foot.
    expect(container.querySelector('.party-print-overlay-scrim')).toBeNull();
    const support = screen.getByTestId('party-print-overlay-support');
    expect(support.style.background).toBe(
      'linear-gradient(180deg, rgba(10, 15, 26, 0) 0%, rgba(10, 15, 26, 0.22) 100%)');
    // The real number only exists once the print is sent: none is invented.
    expect(within(sheet).queryByText(/#\d/)).not.toBeInTheDocument();

    const text = screen.getByRole('group', { name: 'Colore del testo' });
    const logo = screen.getByRole('group', { name: 'Logo' });
    expect(within(text).getAllByRole('radio').map((r) => r.getAttribute('value')))
      .toEqual(['white', 'black', 'red']);
    expect(within(logo).getAllByRole('radio').map((r) => r.getAttribute('value')))
      .toEqual(['light', 'dark']);
    expect(within(text).getByRole('radio', { name: 'Bianco' })).toBeChecked();
    expect(within(logo).getByRole('radio', { name: 'Chiaro' })).toBeChecked();

    // Red text leaves the logo where it was, and the preview follows at once.
    await user.click(within(text).getByRole('radio', { name: 'Rosso' }));
    expect(sheet).toHaveAttribute('data-overlay-text', 'red');
    expect(sheet).toHaveAttribute('data-overlay-logo', 'light');
    expect(within(logo).getByRole('radio', { name: 'Chiaro' })).toBeChecked();
    // A dark logo leaves the text red.
    await user.click(within(logo).getByRole('radio', { name: 'Scuro' }));
    expect(sheet).toHaveAttribute('data-overlay-logo', 'dark');
    expect(sheet).toHaveAttribute('data-overlay-text', 'red');
    expect(within(text).getByRole('radio', { name: 'Rosso' })).toBeChecked();
    // The dark treatment is the brand's own dark mark, not the light one refilled.
    expect(screen.getByTestId('party-print-overlay-symbol'))
      .toHaveAttribute('src', '/brand/nubarca-mark-flat-on-light-256.png');
    expect(within(sheet).getByText('Beach Party').parentElement?.style.color)
      .toBe('rgb(209, 31, 46)');

    // Black text puts a white whisper under the words instead of a dark one.
    await user.click(within(text).getByRole('radio', { name: 'Nero' }));
    expect(support.style.background).toContain('rgba(245, 247, 251, 0.22)');

    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await waitFor(() => expect(lastPost(mock.calls)).not.toBeNull());
    expect(lastPost(mock.calls)).toMatchObject({
      theme: 'overlay', overlayText: 'black', overlayLogo: 'dark',
    });
  });

  // --- The title on the photo: the preview is the renderer's layout ----------

  async function onThePhoto(
    user: ReturnType<typeof userEvent.setup>, body: Record<string, unknown> = {},
  ) {
    mount(manifest(body));
    render(wrapper());
    await compose(user, 'photo');
    await user.click(screen.getByRole('radio', { name: 'Sulla foto' }));
    return screen.getByTestId('party-print-sheet');
  }

  it('starts the support just above the words, not at a fixed band of the photo', async () => {
    const user = setup();
    const sheet = await onThePhoto(user);
    const support = screen.getByTestId('party-print-overlay-support');
    // The support IS the words' box: anchored to the foot (CSS), it holds the
    // text and begins one padding above it — 2.5% of the short edge, which on
    // a portrait sheet is 2.5% of its width. No top of its own.
    expect(support.style.top).toBe('');
    expect(support.style.paddingTop).toBe('2.5%');
    expect(support.contains(within(sheet).getByText('Beach Party'))).toBe(true);
    expect(support.contains(within(sheet).getByText('Grazie di essere qui'))).toBe(true);

    // Turned landscape, the same padding is a smaller share of the wider sheet.
    await user.click(screen.getByRole('radio', { name: 'Orizzontale' }));
    expect(sheet).toHaveAttribute('data-orientation', 'landscape');
    expect(Number.parseFloat(support.style.paddingTop)).toBeCloseTo((2.5 * 1200) / 1800, 3);
  });

  it('prints the name as the paper will, and keeps the number its room', async () => {
    const user = setup();
    const sheet = await onThePhoto(user, {
      partyName: 'Il matrimonio di Giulia Rossi\ne Matteo Bianchi, finalmente insieme',
    });
    // Cut where the renderer cuts it, line break and all: never a longer name
    // than the one that will be printed.
    expect(within(sheet).getByText('Il matrimonio di Giulia Rossi e Matteo Bi…')).toBeInTheDocument();

    // The host's line shares the bottom line with the room kept for the widest
    // number a party reaches — kept, not written: no digit is on the page.
    const bottom = screen.getByTestId('party-print-overlay-bottom');
    expect(bottom).toHaveAttribute('data-number-room', '#9999');
    expect(bottom).toHaveTextContent(/^Grazie di essere qui$/);
    expect(within(sheet).queryByText(/#\d/)).not.toBeInTheDocument();
    // The name stands above that line, over the whole width.
    expect(bottom.contains(within(sheet).getByText(/^Il matrimonio/))).toBe(false);
  });

  it('puts the name on the bottom line, beside the number room, when there is no host\'s line', async () => {
    const user = setup();
    const sheet = await onThePhoto(user, { footerText: '  \n ' });
    const bottom = screen.getByTestId('party-print-overlay-bottom');
    // One line of words, so the support is one line tall.
    expect(bottom).toHaveTextContent(/^Beach Party$/);
    expect(sheet.querySelector('.party-print-overlay-line')).toBeNull();
    expect(bottom).toHaveAttribute('data-number-room', '#9999');
  });

  it('shrinks a long line, then lets the ellipsis take it, before it reaches the number', async () => {
    // jsdom lays nothing out, so the layout's answer is stated: the line has
    // 200px left beside the number's room and needs 300, the name 250 for 500.
    const measured: Record<string, [number, number]> = {
      'party-print-overlay-line': [200, 300],
      'party-print-overlay-name': [250, 500],
    };
    const size = (el: Element, i: 0 | 1) =>
      Object.entries(measured).find(([cls]) => el.classList.contains(cls))?.[1][i] ?? 0;
    Object.defineProperty(HTMLElement.prototype, 'clientWidth', {
      configurable: true, get() { return size(this, 0); },
    });
    Object.defineProperty(HTMLElement.prototype, 'scrollWidth', {
      configurable: true, get() { return size(this, 1); },
    });
    try {
      const user = setup();
      const sheet = await onThePhoto(user);
      // The line goes no smaller than 3/4 — the rest is the ellipsis's — and
      // the name as small as it must be, like FitLine.
      expect((sheet.querySelector('.party-print-overlay-line') as HTMLElement).style.fontSize)
        .toBe('calc(var(--line-size) * 0.75)');
      expect((sheet.querySelector('.party-print-overlay-name') as HTMLElement).style.fontSize)
        .toBe('calc(var(--title-size) * 0.5)');
    } finally {
      delete (HTMLElement.prototype as { clientWidth?: number }).clientWidth;
      delete (HTMLElement.prototype as { scrollWidth?: number }).scrollWidth;
    }
  });

  it('draws the renderer\'s halo, not a stronger one', async () => {
    const user = setup();
    await onThePhoto(user);
    const words = document.querySelector('.party-print-overlay-text') as HTMLElement;
    // A Gaussian of 0.6% of the short edge is a 1.2% blur radius; at 33%.
    expect(words.style.getPropertyValue('--halo')).toBe('rgb(10 15 26 / 33%)');
    expect(Number.parseFloat(words.style.getPropertyValue('--halo-blur'))).toBeCloseTo(1.2, 6);
  });

  it('keeps the overlay choices as real radios a keyboard can walk', async () => {
    const user = setup();
    mount();
    render(wrapper());
    await compose(user, 'photo');
    await user.click(screen.getByRole('radio', { name: 'Sulla foto' }));

    const text = screen.getByRole('group', { name: 'Colore del testo' });
    const white = within(text).getByRole('radio', { name: 'Bianco' });
    // One group, one name: arrow keys move the choice, Tab leaves the group.
    for (const radio of within(text).getAllByRole('radio')) {
      expect(radio).toHaveAttribute('name', 'party-print-overlay-text');
    }
    white.focus();
    expect(white).toHaveFocus();
    await user.keyboard('{ArrowRight}');
    expect(within(text).getByRole('radio', { name: 'Nero' })).toBeChecked();
    expect(screen.getByTestId('party-print-sheet')).toHaveAttribute('data-overlay-text', 'black');
    await user.tab();
    expect(within(screen.getByRole('group', { name: 'Logo' })).getByRole('radio', { name: 'Chiaro' }))
      .toHaveFocus();
  });

  it('sends no overlay colours with a framed look', async () => {
    const user = setup();
    const mock = mount(manifest(), {
      [`POST /api/party/${TOKEN}/print`]: () => jsonResponse(accepted, 202),
    });
    render(wrapper());
    await compose(user, 'photo');
    await user.click(screen.getByRole('radio', { name: 'Sulla foto' }));
    await user.click(within(screen.getByRole('group', { name: 'Colore del testo' }))
      .getByRole('radio', { name: 'Rosso' }));
    // Back to a framed look: the overlay's choices go away, and stay out of the job.
    await user.click(screen.getByRole('radio', { name: 'Notte' }));
    expect(screen.queryByRole('group', { name: 'Colore del testo' })).not.toBeInTheDocument();
    expect(screen.queryByTestId('party-print-overlay-support')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await waitFor(() => expect(lastPost(mock.calls)).not.toBeNull());
    const body = lastPost(mock.calls);
    expect(body.theme).toBe('midnight');
    expect(body).not.toHaveProperty('overlayText');
    expect(body).not.toHaveProperty('overlayLogo');
  });

  it('keeps the title-on-the-photo look off a strip', async () => {
    const user = setup();
    mount();
    render(wrapper());
    await compose(user, 'twinStrip4');
    expect(screen.queryByRole('radio', { name: 'Sulla foto' })).not.toBeInTheDocument();
    expect(screen.getByRole('radio', { name: 'Chiaro' })).toBeChecked();
    expect(screen.queryByRole('group', { name: 'Colore del testo' })).not.toBeInTheDocument();
    expect(screen.queryByRole('group', { name: 'Logo' })).not.toBeInTheDocument();
  });

  it('shows the brand\'s own light and dark marks, in their colours, for the symbol', async () => {
    const user = setup();
    mount();
    render(wrapper());
    await compose(user, 'photo');
    await user.click(screen.getByRole('radio', { name: 'Sulla foto' }));
    const symbol = screen.getByTestId('party-print-overlay-symbol');
    // Light: the mark made for dark grounds (Cloud White, Cyan, Electric Blue).
    expect(symbol).toHaveAttribute('src', '/brand/nubarca-mark-flat-on-dark-256.png');
    // No flat fill of any colour over an outline.
    expect(symbol.style.backgroundColor).toBe('');
    const logo = screen.getByRole('group', { name: 'Logo' });
    // Each choice shows the mark it will print.
    const swatch = (name: string) => within(logo).getByRole('radio', { name })
      .closest('label')?.querySelector('img');
    expect(swatch('Chiaro')).toHaveAttribute('src', '/brand/nubarca-mark-flat-on-dark-64.png');
    expect(swatch('Scuro')).toHaveAttribute('src', '/brand/nubarca-mark-flat-on-light-64.png');
    await user.click(within(logo).getByRole('radio', { name: 'Scuro' }));
    expect(symbol).toHaveAttribute('src', '/brand/nubarca-mark-flat-on-light-256.png');
  });

  it('gives a strip\'s wordmark the width the renderer gives it', async () => {
    const user = setup();
    mount();
    const { container } = render(wrapper());
    await compose(user, 'twinStrip4');
    const marks = container.querySelectorAll<HTMLImageElement>('.party-print-sheet-mark-strip');
    expect(marks).toHaveLength(2);
    for (const mark of marks) expect(mark.style.width).toBe('27%');
  });

  it('sizes the framing to the screen, not only to the column', async () => {
    const user = setup();
    mount();
    render(wrapper());
    await chooseFormat(user, 'photo');
    await pick(user, 1);
    await user.click(next());
    const frame = screen.getByTestId('party-print-crop');
    // The frame keeps its shape and is capped by the visible screen (the
    // CSS reads the shape from here), so on a phone its bottom is reachable.
    const stage = frame.parentElement as HTMLElement;
    expect(stage).toHaveClass('party-print-crop-stage');
    expect(Number(stage.style.getPropertyValue('--crop-aspect'))).toBeGreaterThan(0);
  });

  // --- Papers, four photos, the twin strip ---------------------------------

  const onPaper = (paperSize: string, types: Product[]) => manifest({
    paperSize,
    formats: types.map((type) => ({
      type, enabled: true, remaining: 5,
      requiredPhotos: type === 'twinStrip4' ? 8 : type === 'grid4' ? 4 : 1,
      remainingForYou: null, cutByPrinter: type === 'twinStrip4', paperSize,
    })),
  });

  it('offers what the loaded paper can make, named with the paper', async () => {
    // 10x15: all three, the twin strip among them.
    mount(onPaper('10x15', ['photo', 'grid4', 'twinStrip4']));
    const { unmount } = render(wrapper());
    expect(await screen.findByTestId('party-print-format-photo')).toHaveTextContent('Foto 10×15');
    expect(screen.getByTestId('party-print-format-grid4')).toHaveTextContent('4 foto su 10×15');
    expect(screen.getByTestId('party-print-format-twinStrip4')).toHaveTextContent('Due strisce da 4 foto');
    unmount();
    cleanup();

    // 13x18 and 20x15: a photo and four photos, and no twin strip.
    for (const paper of ['13x18', '20x15']) {
      mount(onPaper(paper, ['photo', 'grid4']));
      const view = render(wrapper());
      const label = paper.replace('x', '×');
      expect(await screen.findByTestId('party-print-format-photo')).toHaveTextContent(`Foto ${label}`);
      expect(screen.getByTestId('party-print-format-grid4')).toHaveTextContent(`4 foto su ${label}`);
      expect(screen.queryByTestId('party-print-format-twinStrip4')).not.toBeInTheDocument();
      expect(screen.getAllByRole('listitem')).toHaveLength(2);
      view.unmount();
      cleanup();
    }
  });

  it('composes four photos two by two, in the order chosen, on the loaded paper', async () => {
    const user = setup();
    const mock = mount(onPaper('20x15', ['photo', 'grid4']), {
      [`POST /api/party/${TOKEN}/print`]: () => jsonResponse({ ...accepted, product: 'grid4' }, 202),
    });
    render(wrapper());
    await chooseFormat(user, 'grid4');
    expect(screen.getByRole('heading', { name: 'Scegli 4 foto diverse' })).toBeInTheDocument();
    await pick(user, 4);
    await user.click(next());

    // Putting them in order says where each lands on the sheet.
    expect(screen.getByText('Posizione 1 · in alto a sinistra')).toBeInTheDocument();
    expect(screen.getByText('Posizione 2 · in alto a destra')).toBeInTheDocument();
    expect(screen.getByText('Posizione 3 · in basso a sinistra')).toBeInTheDocument();
    expect(screen.getByText('Posizione 4 · in basso a destra')).toBeInTheDocument();
    await user.click(next());
    // Each of the four is framed on its own.
    for (let i = 0; i < 4; i += 1) await user.click(next());

    // Two by two, lying as 20x15 is named, photographs 1–4 in reading order.
    const sheet = screen.getByTestId('party-print-sheet');
    expect(sheet).toHaveAttribute('data-layout', 'grid4');
    expect(sheet).toHaveAttribute('data-orientation', 'landscape');
    ['f1', 'f2', 'f3', 'f4'].forEach((id, i) => {
      expect(screen.getByTestId(`party-print-grid-${i}`).querySelector('img')?.getAttribute('src'))
        .toContain(`/${id}/`);
    });
    expect(screen.getByText('Così arriverà sul foglio 20×15.')).toBeInTheDocument();
    // Four photos never turn, and never take the title on the photo.
    expect(screen.queryByRole('group', { name: 'Come stamparla' })).not.toBeInTheDocument();
    expect(screen.queryByRole('radio', { name: 'Sulla foto' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await waitFor(() => expect(lastPost(mock.calls)).not.toBeNull());
    const body = lastPost(mock.calls);
    expect(body).toMatchObject({ product: 'grid4', paperSize: '20x15', theme: 'pure' });
    expect(body.slots.map((slot: { itemId: string }) => slot.itemId)).toEqual(['f1', 'f2', 'f3', 'f4']);
    expect(body).not.toHaveProperty('orientation');
  });

  it('shows the twin strip as the two strips of four it will be', async () => {
    const user = setup();
    mount();
    render(wrapper());
    await chooseFormat(user, 'twinStrip4');
    await pick(user, 8);
    await user.click(next());
    const first = screen.getByRole('region', { name: 'Prima striscia' });
    const second = screen.getByRole('region', { name: 'Seconda striscia' });
    expect(within(first).getAllByRole('listitem')).toHaveLength(4);
    expect(within(second).getAllByRole('listitem')).toHaveLength(4);
    expect(within(first).getByText('Posizione 1')).toBeInTheDocument();
    expect(within(second).getByText('Posizione 5')).toBeInTheDocument();
    // Moving the fourth down carries it onto the second strip.
    const fourth = within(first).getAllByRole('listitem')[3].querySelector('img')?.getAttribute('src');
    await user.click(screen.getByRole('button', { name: 'Sposta giù — Posizione 4' }));
    expect(within(screen.getByRole('region', { name: 'Seconda striscia' }))
      .getAllByRole('listitem')[0].querySelector('img')?.getAttribute('src')).toBe(fourth);
  });

  it('sends the paper the sheet was composed for', async () => {
    const user = setup();
    const mock = mount(onPaper('13x18', ['photo', 'grid4']), {
      [`POST /api/party/${TOKEN}/print`]: () => jsonResponse(accepted, 202),
    });
    render(wrapper());
    await compose(user, 'photo');
    expect(screen.getByText('Così arriverà sul foglio 13×18.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await waitFor(() => expect(lastPost(mock.calls)).not.toBeNull());
    expect(lastPost(mock.calls)).toMatchObject({ product: 'photo', paperSize: '13x18' });
  });

  it('starts again from what the new paper offers when the paper was changed', async () => {
    const user = setup();
    let paper = '10x15';
    mount(manifest(), {
      [`GET /api/party/${TOKEN}/print`]: () => jsonResponse(paper === '10x15'
        ? onPaper('10x15', ['photo', 'grid4', 'twinStrip4'])
        : onPaper('20x15', ['photo', 'grid4'])),
      [`POST /api/party/${TOKEN}/print`]: () => {
        // The operator put 20x15 in while this guest was composing.
        paper = '20x15';
        return errorResponse(409, { error: 'paper_changed' });
      },
    });
    render(wrapper());
    await compose(user, 'twinStrip4');
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    // Back at the products, saying why, with only what 20x15 can make.
    expect(await screen.findByRole('alert')).toHaveTextContent('Nella stampante è stata cambiata la carta');
    expect(await screen.findByText('Foto 20×15')).toBeInTheDocument();
    expect(screen.queryByTestId('party-print-format-twinStrip4')).not.toBeInTheDocument();
    // Choosing again puts the explanation away.
    await chooseFormat(user, 'photo');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('turns the sheet to follow a landscape photograph', async () => {
    const user = setup();
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
    const user = setup();
    mount();
    render(wrapper());
    await compose(user, 'photo');
    const sheet = screen.getByTestId('party-print-sheet');
    expect(within(sheet).getByText('Beach Party')).toBeInTheDocument();
    expect(within(sheet).getByText('Grazie di essere qui')).toBeInTheDocument();
    // The artwork, placed — never the product name set in a typeface.
    expect(within(sheet).getByRole('img', { name: 'NubArca' }))
      .toHaveAttribute('src', '/brand/nubarca-wordmark-on-light-480w.png');
  });

  it('takes the ON-DARK wordmark when the paper is dark', async () => {
    const user = setup();
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
    const user = setup();
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
    const user = setup();
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
    const user = setup();
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
    const user = setup();
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
    const user = setup();
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

  it('says how long the wait is, not just that there is one', async () => {
    const user = setup();
    mount(manifest(), {
      [`POST /api/party/${TOKEN}/print`]: () =>
        jsonResponse({ ...accepted, queueAhead: 3 }, 202),
    });
    render(wrapper());
    await compose(user, 'photo');
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    // A guest standing at the printer wants a number, not "in coda".
    expect(await screen.findByText('Ci sono 3 stampe prima della tua.')).toBeInTheDocument();
  });

  it('says outright when nobody is ahead', async () => {
    const user = setup();
    mount(manifest(), {
      [`POST /api/party/${TOKEN}/print`]: () => jsonResponse(accepted, 202),
    });
    render(wrapper());
    await compose(user, 'photo');
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    expect(await screen.findByText('Sei il prossimo.')).toBeInTheDocument();
  });

  it('says plainly when that was the last print of the format', async () => {
    const user = setup();
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

  it('says the sheet waits for the staff to change the paper', async () => {
    vi.useFakeTimers();
    mount(manifest(), {
      [`POST /api/party/${TOKEN}/print`]: () => jsonResponse(accepted, 202),
      [`GET /api/party/${TOKEN}/print/job-1`]: () => jsonResponse({
        jobId: 'job-1', state: 'waiting_paper', publicSequence: 12, product: 'photo',
      }),
    });
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
    await tick(4_500);
    // Not "in the queue" with no end: the printer has another paper in.
    expect(screen.getByText('In coda: lo staff deve cambiare la carta')).toBeInTheDocument();
  });

  it('says the printer has no sheets left for the party when a loan is used up', async () => {
    const user = setup();
    mount(manifest(), {
      [`POST /api/party/${TOKEN}/print`]: () => errorResponse(409, { error: 'share_exhausted' }),
    });
    render(wrapper());
    await compose(user, 'photo');
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    // Neither the guest's share nor the party's: the printer's.
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Questa stampante ha finito i fogli a disposizione. Chiedi allo staff.');
    expect(alert).not.toHaveTextContent(/tuoi ricordi|questo formato/);
  });

  it('says which refusal it was, in words a guest can act on', async () => {
    const user = setup();
    mount(manifest(), {
      [`POST /api/party/${TOKEN}/print`]: () => errorResponse(409, { error: 'budget_exhausted' }),
    });
    render(wrapper());
    await compose(user, 'photo');
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    expect(await screen.findByRole('alert'))
      .toHaveTextContent('Le stampe di questo formato sono appena finite.');
  });

  it('shows the guest THEIR remaining prints, not the party\u2019s', async () => {
    mount(manifest({
      formats: [
        { type: 'photo', enabled: true, remaining: 40, requiredPhotos: 1, remainingForYou: 2 },
        { type: 'twinStrip4', enabled: false, remaining: 0, requiredPhotos: 8, remainingForYou: null },
      ],
    }));
    render(wrapper());
    // Telling somebody allowed two that there are forty hides the rule from the
    // only person it applies to, and lets them find it out by being refused.
    const photo = await screen.findByTestId('party-print-format-photo');
    expect(photo).toHaveTextContent('2 stampe tue rimaste');
    expect(photo).not.toHaveTextContent('40');
  });

  it('still shows the party\u2019s number when it is the smaller one', async () => {
    mount(manifest({
      formats: [
        { type: 'photo', enabled: true, remaining: 1, requiredPhotos: 1, remainingForYou: 5 },
      ],
    }));
    render(wrapper());
    // Two ceilings apply and the smaller one is the truth.
    expect(await screen.findByTestId('party-print-format-photo'))
      .toHaveTextContent('1 stampa disponibile');
  });

  it('says whose allowance ran out, per format', async () => {
    mount(manifest({
      formats: [
        { type: 'photo', enabled: true, remaining: 40, requiredPhotos: 1, remainingForYou: 0 },
        { type: 'twinStrip4', enabled: true, remaining: 4, requiredPhotos: 8, remainingForYou: null },
      ],
    }));
    render(wrapper());
    // The party has 40 photo prints left; it is this guest who is done. Saying
    // "esaurito" would be a lie they see through when somebody else collects.
    expect(await screen.findByTestId('party-print-format-photo'))
      .toHaveTextContent('Hai finito le tue');
    expect(screen.getByTestId('party-print-format-twinStrip4'))
      .toHaveTextContent('4 stampe disponibili');
  });

  it('does not tell a guest the party is finished when it is their own share', async () => {
    mount(manifest({
      formats: [
        { type: 'photo', enabled: true, remaining: 40, requiredPhotos: 1, remainingForYou: 0 },
        { type: 'twinStrip4', enabled: true, remaining: 9, requiredPhotos: 8, remainingForYou: 0 },
      ],
    }));
    render(wrapper());
    // Both formats are closed to THIS guest while the party has plenty. The
    // wrong wording here sends them to complain to the host about a limit the
    // host set on purpose.
    expect(await screen.findByText('Hai stampato tutti i tuoi ricordi.')).toBeInTheDocument();
    expect(screen.queryByText(/Le stampe di questa festa sono finite/))
      .not.toBeInTheDocument();
  });

  it('does say the party is finished when it actually is', async () => {
    mount(manifest({
      formats: [
        { type: 'photo', enabled: true, remaining: 0, requiredPhotos: 1, remainingForYou: null },
        { type: 'twinStrip4', enabled: true, remaining: 0, requiredPhotos: 8, remainingForYou: null },
      ],
    }));
    render(wrapper());
    expect(await screen.findByText('Le stampe di questa festa sono finite.')).toBeInTheDocument();
  });

  it('defaults the sheet to the photograph\u2019s own orientation, and lets it be turned', async () => {
    const user = setup();
    const mock = mount(manifest(), {
      [`POST /api/party/${TOKEN}/print`]: () => jsonResponse(accepted, 202),
    });
    render(wrapper());
    await chooseFormat(user, 'photo');
    const picks = screen.getAllByRole('button', { name: /Scegli questa foto/ });
    setNatural(within(picks[0]).getByRole('presentation', { hidden: true }), 4000, 3000);
    await user.click(picks[0]);
    await user.click(next());
    await user.click(next());

    // A wide photograph starts on a landscape sheet, chosen for the guest.
    expect(screen.getByRole('radio', { name: 'Orizzontale' })).toBeChecked();
    expect(screen.getByTestId('party-print-sheet'))
      .toHaveAttribute('data-orientation', 'landscape');

    // And they can turn it: a portrait subject in a landscape frame is a choice
    // somebody may want, and the crop editor is what makes it work.
    await user.click(screen.getByRole('radio', { name: 'Verticale' }));
    expect(screen.getByTestId('party-print-sheet'))
      .toHaveAttribute('data-orientation', 'portrait');

    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await waitFor(() => expect(lastPost(mock.calls)).not.toBeNull());
    expect(lastPost(mock.calls).orientation).toBe('portrait');
  });

  it('sends no orientation at all when the guest left the default', async () => {
    const user = setup();
    const mock = mount(manifest(), {
      [`POST /api/party/${TOKEN}/print`]: () => jsonResponse(accepted, 202),
    });
    render(wrapper());
    await compose(user, 'photo');
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await waitFor(() => expect(lastPost(mock.calls)).not.toBeNull());
    // Absent, not "portrait": the server follows the photograph exactly as it
    // did before this choice existed.
    expect(lastPost(mock.calls)).not.toHaveProperty('orientation');
  });

  it('does not offer to turn a strip, because that is not a sheet it can turn', async () => {
    const user = setup();
    mount();
    render(wrapper());
    await compose(user, 'twinStrip4');
    // Two strips side by side IS the product; turning the sheet would destroy
    // it rather than reorient a picture.
    expect(screen.queryByRole('radio', { name: 'Orizzontale' })).not.toBeInTheDocument();
    expect(screen.getByRole('radio', { name: 'Chiaro' })).toBeInTheDocument();
  });

  it('does not blame the party when it is the guest\u2019s own share that is spent', async () => {
    const user = setup();
    mount(manifest(), {
      [`POST /api/party/${TOKEN}/print`]: () =>
        errorResponse(409, { error: 'guest_budget_exhausted' }),
    });
    render(wrapper());
    await compose(user, 'photo');
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    // Saying the party has run out is a lie the guest sees through the moment
    // somebody else collects a print.
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Hai già stampato tutti i tuoi ricordi.');
    expect(alert).not.toHaveTextContent(/festa/i);
  });

  it('never reveals a server reason it was not given', async () => {
    const user = setup();
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
    const user = setup();
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
    const user = setup();
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
    const user = setup();
    mount();
    render(wrapper());
    await chooseFormat(user, 'photo');
    // Ids from elsewhere match nothing this token serves, so there is no filter
    // to offer rather than one that would empty the gallery.
    expect(screen.queryByRole('button', { name: 'Solo le mie foto' })).not.toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: /Scegli questa foto/ })).toHaveLength(10);
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
