import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import type { ImageMediaItem } from '@nubarca/api-client';
import { containZoom } from '@nubarca/contracts';
import { OwnerPhotoPrintDialog } from './OwnerPhotoPrintDialog';
import { AuthedWrapper, errorResponse, installFetchMock, jsonResponse } from '../../test-utils';

/**
 * An owner's direct print: which printers are offered and why one is not, the
 * three numbers that must never be confused, the sheet as it will print —
 * framing, orientation, date — and one key per composition.
 */
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

const photo: ImageMediaItem = {
  id: 'f1', kind: 'image', name: 'IMG_1.jpg', title: null, displayName: 'Tramonto', mimeType: 'image/jpeg',
  sizeBytes: 1, width: 4000, height: 3000, createdAt: '2026-10-01T10:00:00Z', updatedAt: null,
  takenAt: null, favorite: false, rating: null, thumbnailUrl: '/api/files/f1/thumbnail?size=small',
  occurrenceCount: 1, hasDuplicates: false, hasGps: null,
};

function device(overrides: Record<string, unknown> = {}) {
  return {
    id: 'dev-1', displayName: 'DNP DS-RX1HS', manufacturer: 'DNP', model: 'DS-RX1HS', adapterKind: 'cups',
    observedState: 'ready', lastSeenAt: '2026-10-02T12:00:00Z', supportsPhoto10x15: true,
    loadedPaperSize: '10x15', papers: ['10x15', '20x15'], mediaRemainingPrints: 187,
    mediaRemainingObservedAt: '2026-10-02T12:32:00Z', ...overrides,
  };
}

function station(overrides: Record<string, unknown> = {}, devices = [device()]) {
  return {
    id: 'st-1', name: 'Sala', enabled: true, desiredState: 'running', status: 'online',
    lastSeenAt: '2026-10-02T12:00:00Z', agentVersion: '0.5.0', createdAt: '2026-10-01T10:00:00Z',
    revokedAt: null, devices, queueCount: 0, currentJob: null, lastError: null, ...overrides,
  };
}

function lent(overrides: Record<string, unknown> = {}) {
  return {
    shareId: 'sh-9', stationId: 'st-9', stationName: 'Casa di Mario', stationStatus: 'online',
    deviceId: 'dev-9', displayName: 'DS620', observedState: 'ready', ownerName: 'Mario',
    loadedPaperSize: '10x15', papers: ['10x15'], supportsPhoto10x15: true, cutsStrips: false,
    loadedPaperChangedBy: null, loadedPaperChangedAt: null, maxSheets: 12, usedSheets: 0,
    mediaRemainingPrints: 40, mediaRemainingObservedAt: '2026-10-02T12:32:00Z', ...overrides,
  };
}

const accepted = { jobId: 'j1', shortCode: 'abc12345', state: 'ready', queueAhead: 2, mediaRemainingPrints: 187 };

function mount(
  stations: unknown[] = [station()],
  shared: unknown[] = [],
  extra: Parameters<typeof installFetchMock>[0] = {},
  item: ImageMediaItem = photo,
) {
  const onClose = vi.fn();
  const mock = installFetchMock({
    'GET /api/print/stations': () => jsonResponse(stations),
    'GET /api/print/shared-printers': () => jsonResponse(shared),
    'POST /api/print/photo-jobs': () => jsonResponse(accepted, 202),
    ...extra,
  });
  render(
    <MemoryRouter>
      <AuthedWrapper>
        <OwnerPhotoPrintDialog item={item} onClose={onClose} />
      </AuthedWrapper>
    </MemoryRouter>,
  );
  return { mock, onClose };
}

const posts = (mock: ReturnType<typeof installFetchMock>) =>
  mock.calls.filter((c) => c.method === 'POST' && c.url === '/api/print/photo-jobs');
const lastBody = (mock: ReturnType<typeof installFetchMock>) => JSON.parse(posts(mock).at(-1)!.body!);
const keyOf = (call: { init?: RequestInit }) => new Headers(call.init?.headers).get('Idempotency-Key');

/** The preview's picture, loaded at its real shape (the derivative is auto-oriented). */
function loadPreview(width = 4000, height = 3000) {
  const img = screen.getByTestId('owner-print-frame').querySelector('img')!;
  Object.defineProperty(img, 'naturalWidth', { value: width, configurable: true });
  Object.defineProperty(img, 'naturalHeight', { value: height, configurable: true });
  fireEvent.load(img);
}

describe('OwnerPhotoPrintDialog — printers', () => {
  it('says there is no printer, and where to set one up, rather than offering a dead button', async () => {
    mount([], []);
    const empty = await screen.findByTestId('owner-print-empty');
    expect(empty).toHaveTextContent('Nessuna stampante disponibile');
    expect(within(empty).getByRole('link', { name: 'Apri Print Stations' }))
      .toHaveAttribute('href', '/cloud-functions?tool=print-stations');
    expect(screen.queryByTestId('owner-print-send')).not.toBeInTheDocument();
  });

  it('chooses the only usable printer for you, and shows its paper and the prints left on its media', async () => {
    mount();
    const radio = await screen.findByRole('radio', { name: /DNP DS-RX1HS/ });
    expect(radio).toBeChecked();
    expect(screen.getByTestId('owner-print-paper')).toHaveTextContent('10×15');
    expect(screen.getByTestId('owner-print-media-value')).toHaveTextContent(/^187$/);
    expect(screen.getByTestId('owner-print-media')).toHaveTextContent('Stima fornita dalla stampante');
    // Your own printer has no loan, so no quota line at all.
    expect(screen.queryByTestId('owner-print-quota')).not.toBeInTheDocument();
  });

  it('with several printers, waits for a choice', async () => {
    mount([station()], [lent()]);
    await screen.findByRole('radio', { name: /DNP DS-RX1HS/ });
    expect(screen.getByRole('radio', { name: /DNP DS-RX1HS/ })).not.toBeChecked();
    expect(screen.getByRole('radio', { name: /DS620/ })).not.toBeChecked();
    expect(screen.getByTestId('owner-print-send')).toBeDisabled();
  });

  it('keeps a lent printer\'s quota apart from the prints on its media', async () => {
    const user = userEvent.setup();
    mount([], [lent({ maxSheets: 12, usedSheets: 0 })]);
    const radio = await screen.findByRole('radio', { name: /DS620/ });
    expect(radio).toBeChecked();
    expect(screen.getByRole('radio', { name: /condivisa da Mario/ })).toBe(radio);
    expect(screen.getByTestId('owner-print-media-value')).toHaveTextContent(/^40$/);
    expect(screen.getByTestId('owner-print-quota')).toHaveTextContent('Restano 12 fogli');
    expect(screen.getByTestId('owner-print-facts')).toHaveTextContent('Quota condivisa');
    // Never "12 prints available": the loan is not the media.
    expect(screen.getByTestId('owner-print-facts')).not.toHaveTextContent(/Stampe residue\s*12/);
    await user.click(screen.getByTestId('owner-print-send'));
    await screen.findByTestId('owner-print-sent');
  });

  it('shows an offline printer, disabled, saying why', async () => {
    mount([station({ status: 'offline' })]);
    const row = await screen.findByTestId('owner-print-printer-dev-1');
    expect(within(row).getByRole('radio')).toBeDisabled();
    expect(row).toHaveTextContent('Offline: ora non può stampare');
    // The count it last reported is not shown as current.
    expect(row).toHaveTextContent(/Stampe residue: ultimo dato 187/);
  });

  it('shows a printer that cannot print its loaded paper, disabled', async () => {
    mount([station({}, [device({ loadedPaperSize: '13x18', papers: ['10x15'] })])]);
    const row = await screen.findByTestId('owner-print-printer-dev-1');
    expect(within(row).getByRole('radio')).toBeDisabled();
    expect(row).toHaveTextContent('Non stampa la carta caricata');
  });

  it('a count of zero is a warning, never a lock', async () => {
    const user = userEvent.setup();
    const { mock } = mount([station({}, [device({ mediaRemainingPrints: 0 })])]);
    await screen.findByRole('radio', { name: /DNP DS-RX1HS/ });
    expect(screen.getByTestId('owner-print-media')).toHaveTextContent('La stampante segnala che il supporto è esaurito');
    await user.click(screen.getByTestId('owner-print-send'));
    await screen.findByTestId('owner-print-sent');
    expect(posts(mock)).toHaveLength(1);
  });
});

describe('OwnerPhotoPrintDialog — the sheet', () => {
  it('follows the photograph\'s orientation, and can be turned', async () => {
    const user = userEvent.setup();
    const { mock } = mount();
    await screen.findByRole('radio', { name: /DNP DS-RX1HS/ });
    expect(screen.getByTestId('owner-print-orientation-landscape')).toBeChecked();
    // A derivative that is really standing (EXIF-rotated) turns the sheet too.
    loadPreview(3000, 4000);
    expect(screen.getByTestId('owner-print-orientation-portrait')).toBeChecked();
    await user.click(screen.getByTestId('owner-print-orientation-landscape'));
    expect(screen.getByTestId('owner-print-preview')).toHaveAttribute('data-orientation', 'landscape');
    // Once turned by hand, the photograph no longer turns it back.
    loadPreview(3000, 4000);
    expect(screen.getByTestId('owner-print-orientation-landscape')).toBeChecked();
    await user.click(screen.getByTestId('owner-print-send'));
    await waitFor(() => expect(posts(mock)).toHaveLength(1));
    expect(lastBody(mock)).toMatchObject({
      fileItemId: 'f1', printStationId: 'st-1', printerDeviceId: 'dev-1', expectedPaperSize: '10x15',
      orientation: 'landscape', includeDate: false,
    });
  });

  it('frames by drag, zoom in and out, Adatta, Riempi and Centra — and sends what it shows', async () => {
    const user = userEvent.setup();
    const { mock } = mount();
    await screen.findByRole('radio', { name: /DNP DS-RX1HS/ });
    loadPreview(4000, 3000);
    const frame = screen.getByTestId('owner-print-frame');
    const zoom = screen.getByTestId('owner-print-framing-zoom') as HTMLInputElement;

    // 4:3 on a 3:2 sheet: zoomed in it overflows, a drag moves it.
    fireEvent.change(zoom, { target: { value: '2' } });
    vi.spyOn(frame, 'getBoundingClientRect').mockReturnValue({
      x: 0, y: 0, width: 300, height: 200, top: 0, left: 0, right: 300, bottom: 200, toJSON: () => ({}),
    });
    fireEvent.pointerDown(frame, { clientX: 200, clientY: 100, pointerId: 1 });
    fireEvent.pointerMove(frame, { clientX: 140, clientY: 100, pointerId: 1 });
    fireEvent.pointerUp(frame, { pointerId: 1 });
    await user.click(screen.getByTestId('owner-print-send'));
    await waitFor(() => expect(posts(mock)).toHaveLength(1));
    const dragged = lastBody(mock).placement;
    expect(dragged.zoom).toBe(2);
    expect(dragged.centerX).toBeGreaterThan(0.5);
  });

  it('Adatta prints the whole photograph on white; Riempi and Centra are the other two', async () => {
    const user = userEvent.setup();
    mount();
    await screen.findByRole('radio', { name: /DNP DS-RX1HS/ });
    loadPreview(4000, 3000);
    const frame = screen.getByTestId('owner-print-frame');
    const img = frame.querySelector('img')!;

    await user.click(screen.getByTestId('owner-print-framing-fit'));
    // Full height, white beside it.
    expect(parseFloat(img.style.height)).toBeCloseTo(100, 6);
    expect(parseFloat(img.style.width)).toBeCloseTo((100 * (4 / 3)) / 1.5, 6);
    expect(frame.style.background).toBe('rgb(255, 255, 255)');

    await user.click(screen.getByTestId('owner-print-framing-fill'));
    expect(parseFloat(img.style.width)).toBeCloseTo(100, 6);

    fireEvent.change(screen.getByTestId('owner-print-framing-zoom'), { target: { value: '3' } });
    fireEvent.keyDown(frame, { key: 'ArrowRight' });
    await user.click(screen.getByTestId('owner-print-framing-center'));
    expect(parseFloat(img.style.left)).toBeCloseTo(-100, 6);
  });

  it('sends the whole photograph\'s zoom when fitted', async () => {
    const user = userEvent.setup();
    const { mock } = mount();
    await screen.findByRole('radio', { name: /DNP DS-RX1HS/ });
    loadPreview(4000, 3000);
    await user.click(screen.getByTestId('owner-print-framing-fit'));
    await user.click(screen.getByTestId('owner-print-send'));
    await waitFor(() => expect(posts(mock)).toHaveLength(1));
    expect(lastBody(mock).placement).toEqual({ centerX: 0.5, centerY: 0.5, zoom: containZoom(4 / 3, 1.5) });
  });
});

describe('OwnerPhotoPrintDialog — the date', () => {
  const dateRoute = (date: string, source: string) => ({
    'GET /api/print/photo-jobs/date': () => jsonResponse({ date, source }),
  });

  it('prints no date unless asked, and does not even read it', async () => {
    const user = userEvent.setup();
    const { mock } = mount();
    await screen.findByRole('radio', { name: /DNP DS-RX1HS/ });
    expect(screen.getByTestId('owner-print-date')).not.toBeChecked();
    expect(screen.queryByTestId('owner-print-date-text')).not.toBeInTheDocument();
    await user.click(screen.getByTestId('owner-print-send'));
    await waitFor(() => expect(posts(mock)).toHaveLength(1));
    expect(lastBody(mock).includeDate).toBe(false);
    expect(lastBody(mock)).not.toHaveProperty('dateLocale');
    expect(mock.calls.some((c) => c.url.startsWith('/api/print/photo-jobs/date'))).toBe(false);
  });

  it('shows the camera\'s date on the photograph in the print\'s own format, and sends the language and zone', async () => {
    const user = userEvent.setup();
    const { mock } = mount(undefined, undefined, dateRoute('2024-07-14', 'embedded'));
    await screen.findByRole('radio', { name: /DNP DS-RX1HS/ });
    await user.click(screen.getByTestId('owner-print-date'));
    expect(await screen.findByTestId('owner-print-date-text')).toHaveTextContent(/^14\/07\/2024$/);
    expect(screen.getByTestId('owner-print-date-note')).toHaveTextContent('Data della foto: 14/07/2024');
    const read = mock.calls.find((c) => c.url.startsWith('/api/print/photo-jobs/date'))!;
    expect(new URL(read.url, 'http://x').searchParams.get('fileItemId')).toBe('f1');
    expect(new URL(read.url, 'http://x').searchParams.get('timeZone')).toBeTruthy();

    await user.click(screen.getByTestId('owner-print-send'));
    await waitFor(() => expect(posts(mock)).toHaveLength(1));
    expect(lastBody(mock)).toMatchObject({ includeDate: true, dateLocale: 'it' });
    expect(typeof lastBody(mock).timeZone).toBe('string');
  });

  it('a date the owner corrected is the photograph\'s date', async () => {
    const user = userEvent.setup();
    mount(undefined, undefined, dateRoute('2023-12-25', 'user'));
    await screen.findByRole('radio', { name: /DNP DS-RX1HS/ });
    await user.click(screen.getByTestId('owner-print-date'));
    expect(await screen.findByTestId('owner-print-date-note')).toHaveTextContent('Data della foto: 25/12/2023');
  });

  it('a photograph with no date says today\'s will be used', async () => {
    const user = userEvent.setup();
    mount(undefined, undefined, dateRoute('2026-10-02', 'today'));
    await screen.findByRole('radio', { name: /DNP DS-RX1HS/ });
    await user.click(screen.getByTestId('owner-print-date'));
    expect(await screen.findByTestId('owner-print-date-note'))
      .toHaveTextContent('Nessuna data della foto: verrà usata la data di oggi (02/10/2026)');
  });

  it('puts the date on the photograph, not on the white band', async () => {
    const user = userEvent.setup();
    mount(undefined, undefined, dateRoute('2024-07-14', 'embedded'));
    await screen.findByRole('radio', { name: /DNP DS-RX1HS/ });
    loadPreview(4000, 3000);
    await user.click(screen.getByTestId('owner-print-framing-fit'));
    await user.click(screen.getByTestId('owner-print-date'));
    const text = await screen.findByTestId('owner-print-date-text');
    // The photograph ends short of the right edge: the date is inset from IT.
    const band = (1 - (4 / 3) / 1.5) / 2;
    const offset = Number(/calc\(([\d.]+)%/.exec(text.style.right)?.[1]);
    expect(offset).toBeCloseTo(band * 100, 3);
    // On a filled sheet it sits at the sheet's own edge, inset only.
    await user.click(screen.getByTestId('owner-print-framing-fill'));
    expect(Number(/calc\(([\d.]+)%/.exec(text.style.right)?.[1])).toBeCloseTo(0, 6);
  });
});

describe('OwnerPhotoPrintDialog — sending', () => {
  it('a retry of the same composition keeps its key; a changed one gets a new key', async () => {
    const user = userEvent.setup();
    let attempt = 0;
    const { mock } = mount(undefined, undefined, {
      'POST /api/print/photo-jobs': () => {
        attempt += 1;
        if (attempt === 1) throw new TypeError('network');
        return jsonResponse(accepted, 202);
      },
    });
    await screen.findByRole('radio', { name: /DNP DS-RX1HS/ });
    await user.click(screen.getByTestId('owner-print-send'));
    await screen.findByTestId('owner-print-error');
    await user.click(screen.getByTestId('owner-print-send'));
    await screen.findByTestId('owner-print-sent');
    const [first, second] = posts(mock);
    expect(keyOf(first)).toBeTruthy();
    expect(keyOf(second)).toBe(keyOf(first));
  });

  it('a different composition after a failure is a different print', async () => {
    const user = userEvent.setup();
    let attempt = 0;
    const { mock } = mount(undefined, undefined, {
      'POST /api/print/photo-jobs': () => {
        attempt += 1;
        if (attempt === 1) throw new TypeError('network');
        return jsonResponse(accepted, 202);
      },
    });
    await screen.findByRole('radio', { name: /DNP DS-RX1HS/ });
    await user.click(screen.getByTestId('owner-print-send'));
    await screen.findByTestId('owner-print-error');
    fireEvent.change(screen.getByTestId('owner-print-framing-zoom'), { target: { value: '2' } });
    await user.click(screen.getByTestId('owner-print-send'));
    await screen.findByTestId('owner-print-sent');
    const [first, second] = posts(mock);
    expect(keyOf(second)).not.toBe(keyOf(first));
  });

  it('says it is queued, how many are ahead, and its code', async () => {
    const user = userEvent.setup();
    mount();
    await screen.findByRole('radio', { name: /DNP DS-RX1HS/ });
    await user.click(screen.getByTestId('owner-print-send'));
    const sent = await screen.findByTestId('owner-print-sent');
    expect(sent).toHaveTextContent('Inviata alla stampa');
    expect(sent).toHaveTextContent('In coda: 2 stampe prima di questa');
    expect(sent).toHaveTextContent('Codice abc12345');
  });

  it('when the paper changed, reads the printer again, keeps the photo and the date, and re-frames for the new paper', async () => {
    const user = userEvent.setup();
    let reads = 0;
    const { mock } = mount(undefined, undefined, {
      'GET /api/print/stations': () => {
        reads += 1;
        return jsonResponse([station({}, [device({ loadedPaperSize: reads === 1 ? '10x15' : '20x15' })])]);
      },
      'GET /api/print/photo-jobs/date': () => jsonResponse({ date: '2024-07-14', source: 'embedded' }),
      'POST /api/print/photo-jobs': () => errorResponse(409, { error: 'paper_changed' }),
    });
    await screen.findByRole('radio', { name: /DNP DS-RX1HS/ });
    await user.click(screen.getByTestId('owner-print-date'));
    await screen.findByTestId('owner-print-date-text');
    const before = screen.getByTestId('owner-print-frame').style.aspectRatio;

    await user.click(screen.getByTestId('owner-print-send'));
    expect(await screen.findByTestId('owner-print-error')).toHaveTextContent('La carta è cambiata');
    await waitFor(() => expect(screen.getByTestId('owner-print-paper')).toHaveTextContent('20×15'));
    expect(screen.getByRole('radio', { name: /DNP DS-RX1HS/ })).toBeChecked();
    expect(screen.getByTestId('owner-print-date')).toBeChecked();
    expect(screen.getByTestId('owner-print-date-text')).toBeInTheDocument();
    expect(screen.getByTestId('owner-print-frame').style.aspectRatio).not.toBe(before);
    expect(posts(mock)).toHaveLength(1);
  });
});
