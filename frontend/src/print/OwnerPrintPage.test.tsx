import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../test-utils';
import { OwnerPrintPage } from './OwnerPrintPage';
import { popOutPlacement } from './popOut';
import { resetSubjectCutoutAvailabilityForTests } from '../subjectCutout/useSubjectCutoutAvailable';

// PRINTING FROM AN ALBUM through the party's own studio: the same steps, with
// the owner's printer, Piena or Cornice, their own line, the date and the mark.

afterEach(() => { cleanup(); vi.restoreAllMocks(); });

const ALBUM = 'A1';

function station(cutsStrips = false, paper: '10x15' | '13x18' = '10x15') {
  return {
    id: 's1', name: 'Sala', enabled: true, desiredState: 'running', status: 'online', lastSeenAt: null,
    agentVersion: '0.5.0', createdAt: '2026-01-01T00:00:00Z', revokedAt: null, queueCount: 0, currentJob: null,
    lastError: null,
    devices: [{
      id: 'd1', displayName: 'DNP DS-RX1HS', manufacturer: 'DNP', model: 'DS-RX1HS', adapterKind: 'cups',
      observedState: 'ready', lastSeenAt: '2026-01-01T00:00:00Z', supportsPhoto10x15: true, cutsStrips,
      loadedPaperSize: paper, papers: ['10x15', '13x18', '20x15'], mediaRemainingPrints: 120,
    }],
  };
}

function items(ids: string[]) {
  return ids.map((id) => ({
    fileItemId: id, name: `${id}.jpg`, mimeType: 'image/jpeg', sizeBytes: 1000,
    addedAt: '2026-01-01T00:00:00Z', thumbnailUrl: null,
  }));
}

function mount(path: string, { stations = [station()], extra = {} }: {
  stations?: unknown[]; extra?: Record<string, () => Response>;
} = {}) {
  const mock = installFetchMock({
    'GET /api/print/stations': () => jsonResponse(stations),
    'GET /api/print/shared-printers': () => jsonResponse([]),
    [`GET /api/albums/${ALBUM}`]: () => jsonResponse({ id: ALBUM, name: 'Vacanze 2026' }),
    [`GET /api/albums/${ALBUM}/items`]: () => jsonResponse(items(['f1', 'f2', 'f3', 'f4', 'f5', 'v1'])
      .map((it) => (it.fileItemId === 'v1' ? { ...it, mimeType: 'video/mp4' } : it))),
    'POST /api/print/photo-jobs': () => jsonResponse(
      { jobId: 'j1', shortCode: 'j1abcdef', state: 'ready', queueAhead: 0, mediaRemainingPrints: 120 }, 202),
    ...extra,
  });
  render(
    <MemoryRouter initialEntries={[path]}>
      <AuthedWrapper>
        <Routes><Route path="/print" element={<OwnerPrintPage />} /></Routes>
      </AuthedWrapper>
    </MemoryRouter>,
  );
  return mock;
}

const next = () => screen.getByRole('button', { name: 'Continua' });

function lastPost(calls: { url: string; method: string; body: string | null }[]) {
  const post = [...calls].reverse().find((c) => c.method === 'POST');
  return post ? JSON.parse(post.body ?? '{}') : null;
}

describe('OwnerPrintPage', () => {
  it.each([
    { path: `/print?album=${ALBUM}&files=f1`, width: 4000, height: 3000, orientation: 'landscape' },
    { path: `/print?album=${ALBUM}&files=f1`, width: 3000, height: 4000, orientation: 'portrait' },
    { path: '/print?files=f1', width: 4000, height: 3000, orientation: 'landscape' },
    { path: '/print?files=f1', width: 3000, height: 4000, orientation: 'portrait' },
  ])('sends the automatic $orientation shown in the preview without requiring a toggle', async ({ path, width, height, orientation }) => {
    const user = userEvent.setup();
    const mock = mount(path);
    await user.click(await screen.findByTestId('party-print-format-photo'));
    await user.click(next());
    const img = screen.getByTestId('party-print-crop').querySelector('img')!;
    Object.defineProperties(img, {
      naturalWidth: { value: width }, naturalHeight: { value: height },
    });
    fireEvent.load(img);
    await user.click(next());
    expect(screen.getByTestId('party-print-sheet')).toHaveAttribute('data-orientation', orientation);
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await waitFor(() => expect(lastPost(mock.calls)?.orientation).toBe(orientation));
  });

  it('holds an adapted photograph to the new frame minimum when the sheet is turned', async () => {
    const user = userEvent.setup();
    const mock = mount(`/print?album=${ALBUM}&files=f1`);
    await user.click(await screen.findByTestId('party-print-format-photo'));
    await user.click(next());
    const img = screen.getByTestId('party-print-crop').querySelector('img')!;
    Object.defineProperties(img, {
      naturalWidth: { value: 4000 }, naturalHeight: { value: 3000 },
    });
    fireEvent.load(img);
    await user.click(screen.getByRole('radio', { name: 'Verticale' }));
    await user.click(screen.getByTestId('party-print-framing-fit'));
    expect(screen.getByTestId('party-print-framing-zoom')).toHaveAttribute('aria-valuetext', '0.50×');
    await user.click(screen.getByRole('radio', { name: 'Orizzontale' }));
    expect(screen.getByTestId('party-print-framing-zoom')).toHaveAttribute('aria-valuetext', '0.89×');
    await user.click(next());
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await waitFor(() => expect(lastPost(mock.calls)?.orientation).toBe('landscape'));
    expect(lastPost(mock.calls).photos[0].placement.zoom).toBeCloseTo(8 / 9);
  });

  it.each([
    { width: 4000, height: 3000, to: 'Verticale', orientation: 'portrait', framed: false },
    { width: 3000, height: 4000, to: 'Orizzontale', orientation: 'landscape', framed: false },
    { width: 4000, height: 3000, to: 'Verticale', orientation: 'portrait', framed: true },
    { width: 3000, height: 4000, to: 'Orizzontale', orientation: 'landscape', framed: true },
  ])('reframes on one click to $orientation, keeping zoom and focal point (framed: $framed)', async ({ width, height, to, orientation, framed }) => {
    const user = userEvent.setup();
    const mock = mount(`/print?album=${ALBUM}&files=f1`);
    await user.click(await screen.findByTestId('party-print-format-photo'));
    await user.click(next());
    const img = screen.getByTestId('party-print-crop').querySelector('img')!;
    Object.defineProperties(img, {
      naturalWidth: { value: width }, naturalHeight: { value: height },
    });
    fireEvent.load(img);
    if (framed) {
      await user.click(next());
      await user.click(screen.getByRole('radio', { name: 'Cornice' }));
      await user.click(screen.getByRole('button', { name: 'Indietro' }));
    }
    const frame = screen.getByTestId('party-print-crop');
    const radio = screen.getByRole('radio', { name: to });
    // Orientation is available before touching the framing editor.
    expect(radio.compareDocumentPosition(frame) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    const oldAspect = Number.parseFloat(frame.style.aspectRatio);
    fireEvent.change(screen.getByTestId('party-print-framing-zoom'), { target: { value: '2' } });
    fireEvent.keyDown(frame, { key: 'ArrowRight' });
    await user.click(radio);
    expect(radio).toBeChecked();
    const newAspect = Number.parseFloat(frame.style.aspectRatio);
    expect(newAspect).not.toBe(oldAspect);
    expect(orientation === 'portrait' ? newAspect < 1 : newAspect > 1).toBe(true);
    expect(screen.getByTestId('party-print-framing-zoom')).toHaveValue('2');
    const cropStyle = frame.querySelector('img')!.getAttribute('style');
    await user.click(next());
    const sheet = screen.getByTestId('party-print-sheet');
    expect(sheet).toHaveAttribute('data-orientation', orientation);
    expect(sheet.querySelector('img.party-print-framed')!.getAttribute('style')).toBe(cropStyle);
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await waitFor(() => expect(lastPost(mock.calls)?.orientation).toBe(orientation));
    expect(lastPost(mock.calls).photos[0].placement).toEqual({ centerX: 0.52, centerY: 0.5, zoom: 2 });
  });

  it('opens on the album, with the printer, and offers what its paper can make — the strip only from a printer that cuts', async () => {
    mount(`/print?album=${ALBUM}`);
    expect(await screen.findByRole('heading', { level: 1 })).toHaveTextContent('Vacanze 2026');
    expect(screen.getByTestId('owner-print-printer-d1')).toHaveTextContent('DNP DS-RX1HS');
    expect(screen.getByTestId('party-print-format-photo')).toHaveTextContent('Foto 10×15');
    expect(screen.getByTestId('party-print-format-grid4')).toBeInTheDocument();
    expect(screen.queryByTestId('party-print-format-twinStrip4')).toBeNull();

    // The album's photographs only — not its video.
    await userEvent.setup().click(screen.getByTestId('party-print-format-grid4'));
    expect(screen.getAllByRole('button', { name: /Scegli questa foto/ })).toHaveLength(5);
  });

  it('offers both strips when the printer cuts them', async () => {
    mount(`/print?album=${ALBUM}`, { stations: [station(true)] });
    expect(await screen.findByTestId('party-print-format-twinStrip4')).toHaveTextContent('Due strisce diverse');
    expect(screen.getByTestId('party-print-format-twinStrip4-4')).toHaveTextContent('Due strisce uguali');
  });

  it('frames one photograph in Cornice, with the owner\'s line, the date and the mark, and sends exactly that', async () => {
    const user = userEvent.setup();
    const mock = mount(`/print?album=${ALBUM}&files=f2`, {
      extra: { 'GET /api/print/photo-jobs/date': () => jsonResponse({ date: '2019-07-01', source: 'embedded' }) },
    });
    await user.click(await screen.findByTestId('party-print-format-photo'));
    // The photograph selected in the album is already chosen.
    expect(screen.getByRole('button', { name: /Togli dalla selezione/ })).toBeInTheDocument();
    await user.click(next());
    await user.click(next());

    // Piena first, as a direct print always was; Cornice gives the band.
    expect(screen.getByRole('radio', { name: 'Piena' })).toBeChecked();
    expect(screen.queryByTestId('owner-print-caption')).toBeNull();
    await user.click(screen.getByRole('radio', { name: 'Cornice' }));
    await user.type(screen.getByTestId('owner-print-caption'), 'Estate al lago');
    await user.click(screen.getByTestId('owner-print-brand'));
    await user.click(screen.getByTestId('owner-print-date'));

    const sheet = screen.getByTestId('party-print-sheet');
    expect(sheet).toHaveTextContent('Estate al lago');
    expect(await screen.findByTestId('print-sheet-mark')).toHaveTextContent('01/07/2019');
    expect(sheet.querySelector('.party-print-sheet-mark')).not.toBeNull();

    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await waitFor(() => expect(lastPost(mock.calls)).not.toBeNull());
    const body = lastPost(mock.calls);
    expect(body).toMatchObject({
      printStationId: 's1', printerDeviceId: 'd1', expectedPaperSize: '10x15',
      layout: 'photo', style: 'framed', caption: 'Estate al lago', brand: true,
      includeDate: true, dateLocale: 'it',
    });
    expect(body.photos).toEqual([{ fileItemId: 'f2', placement: { centerX: 0.5, centerY: 0.5, zoom: 1 } }]);
    expect(['portrait', 'landscape']).toContain(body.orientation);
    expect(await screen.findByTestId('owner-print-sent')).toHaveTextContent('Inviata alla stampa');
  });

  it('sends four photographs in the order they were chosen, framed, with no orientation of their own', async () => {
    const user = userEvent.setup();
    const mock = mount(`/print?album=${ALBUM}&files=f3,f1,f4,f2`);
    await user.click(await screen.findByTestId('party-print-format-grid4'));
    await user.click(next());
    await user.click(next());
    for (let i = 0; i < 4; i += 1) await user.click(next());

    // Four photographs are always framed: no Piena to choose, the line is offered.
    expect(screen.queryByRole('radio', { name: 'Piena' })).toBeNull();
    expect(screen.getByTestId('owner-print-caption')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await waitFor(() => expect(lastPost(mock.calls)).not.toBeNull());
    const body = lastPost(mock.calls);
    expect(body.layout).toBe('grid4');
    expect(body.style).toBe('framed');
    expect(body).not.toHaveProperty('orientation');
    expect(body).not.toHaveProperty('caption');
    expect(body.photos.map((p: { fileItemId: string }) => p.fileItemId)).toEqual(['f3', 'f1', 'f4', 'f2']);
  });

  it('will not send a line the band cannot hold', async () => {
    const user = userEvent.setup();
    mount(`/print?album=${ALBUM}&files=f1`);
    await user.click(await screen.findByTestId('party-print-format-photo'));
    await user.click(next());
    await user.click(next());
    await user.click(screen.getByRole('radio', { name: 'Cornice' }));
    await user.type(screen.getByTestId('owner-print-caption'), 'a'.repeat(41));
    expect(screen.getByText(/Al massimo 40 caratteri/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Stampa' })).toBeDisabled();
  });

  it('says so when the loan has no sheets left', async () => {
    const user = userEvent.setup();
    mount(`/print?album=${ALBUM}&files=f1`, {
      extra: { 'POST /api/print/photo-jobs': () => jsonResponse({ error: 'share_exhausted' }, 409) },
    });
    await user.click(await screen.findByTestId('party-print-format-photo'));
    await user.click(next());
    await user.click(next());
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('I fogli della condivisione sono finiti');
  });

  it('prints the photographs selected in the library, and only those', async () => {
    const user = userEvent.setup();
    mount('/print?files=f9,f8');
    expect(await screen.findByRole('heading', { level: 1 })).toHaveTextContent('Stampa foto');
    await user.click(screen.getByTestId('party-print-format-photo'));
    expect(screen.getAllByRole('button', { name: /Scegli questa foto|Togli dalla selezione/ })).toHaveLength(2);
    expect(screen.getByText('Torna alla libreria')).toBeInTheDocument();
  });

  it('composes from an album\'s selection only, and offers no format it cannot fill', async () => {
    const user = userEvent.setup();
    mount(`/print?album=${ALBUM}&files=f4,f2,f5&videos=2`);
    expect(await screen.findByTestId('owner-print-videos-skipped')).toHaveTextContent('2 video della selezione non vengono stampati');
    expect(screen.getByTestId('party-print-format-grid4')).toBeDisabled();
    expect(screen.getByTestId('party-print-format-grid4')).toHaveTextContent('Servono 4 foto, ne hai 3');
    await user.click(screen.getByTestId('party-print-format-photo'));
    // Three to choose one from: nothing is chosen for the person.
    expect(screen.getAllByRole('button', { name: /Scegli questa foto/ })).toHaveLength(3);
    expect(screen.queryByRole('button', { name: /Togli dalla selezione/ })).toBeNull();
  });

  it('stays on the same photographs to print another', async () => {
    const user = userEvent.setup();
    mount(`/print?album=${ALBUM}&files=f4,f2,f5`);
    await user.click(await screen.findByTestId('party-print-format-photo'));
    await user.click(screen.getAllByRole('button', { name: /Scegli questa foto/ })[1]);
    await user.click(next());
    await user.click(next());
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await user.click(await screen.findByRole('button', { name: /Stampa un/ }));
    await user.click(await screen.findByTestId('party-print-format-photo'));
    expect(screen.getAllByRole('button', { name: /Scegli questa foto/ })).toHaveLength(3);
  });

  it('turns the sheet only where it is framed: the preview shows the choice', async () => {
    const user = userEvent.setup();
    mount(`/print?album=${ALBUM}&files=f1`);
    await user.click(await screen.findByTestId('party-print-format-photo'));
    await user.click(next());
    await user.click(screen.getByRole('radio', { name: 'Verticale' }));
    await user.click(next());
    expect(screen.getByTestId('party-print-sheet')).toHaveAttribute('data-orientation', 'portrait');
    expect(screen.queryByRole('radio', { name: 'Verticale' })).toBeNull();
    expect(screen.queryByRole('radio', { name: 'Orizzontale' })).toBeNull();
    // Back to the framing, the choice is there to change.
    await user.click(screen.getByRole('button', { name: 'Indietro' }));
    expect(screen.getByRole('radio', { name: 'Verticale' })).toBeChecked();
  });

  it('keeps the sheet\'s choices on the way back, and starts them again for another format', async () => {
    const user = userEvent.setup();
    mount(`/print?album=${ALBUM}&files=f1,f2,f3,f4`);
    const toPreview = async () => {
      await user.click(screen.getAllByRole('button', { name: /Scegli questa foto/ })[0]);
      await user.click(next());
      await user.click(next());
    };
    const backToFormats = async () => {
      for (let i = 0; i < 3; i += 1) await user.click(screen.getByRole('button', { name: 'Indietro' }));
    };
    await user.click(await screen.findByTestId('party-print-format-photo'));
    await toPreview();
    await user.click(screen.getByRole('radio', { name: 'Cornice' }));
    await user.type(screen.getByTestId('owner-print-caption'), 'Estate al lago');
    await user.click(screen.getByTestId('owner-print-brand'));

    // Same format again: everything is as it was left.
    await backToFormats();
    await user.click(screen.getByTestId('party-print-format-photo'));
    await toPreview();
    expect(screen.getByRole('radio', { name: 'Cornice' })).toBeChecked();
    expect(screen.getByTestId('owner-print-caption')).toHaveValue('Estate al lago');
    expect(screen.getByTestId('owner-print-brand')).toBeChecked();

    // Another format: the sheet's choices start again.
    await backToFormats();
    await user.click(screen.getByTestId('party-print-format-grid4'));
    await user.click(next());
    await user.click(next());
    for (let i = 0; i < 4; i += 1) await user.click(next());
    expect(screen.getByTestId('owner-print-caption')).toHaveValue('');
    expect(screen.getByTestId('owner-print-brand')).not.toBeChecked();
  });

  it('starts the next print from the defaults', async () => {
    const user = userEvent.setup();
    mount(`/print?album=${ALBUM}&files=f1`);
    await user.click(await screen.findByTestId('party-print-format-photo'));
    await user.click(next());
    await user.click(next());
    await user.click(screen.getByRole('radio', { name: 'Cornice' }));
    await user.type(screen.getByTestId('owner-print-caption'), 'Estate al lago');
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await user.click(await screen.findByRole('button', { name: /Stampa un/ }));

    await user.click(await screen.findByTestId('party-print-format-photo'));
    await user.click(next());
    await user.click(next());
    expect(screen.getByRole('radio', { name: 'Piena' })).toBeChecked();
    expect(screen.queryByTestId('owner-print-caption')).toBeNull();
    await user.click(screen.getByRole('radio', { name: 'Cornice' }));
    expect(screen.getByTestId('owner-print-caption')).toHaveValue('');
  });

  it('says a simulated printer is simulated where it is chosen', async () => {
    const simulated = station();
    simulated.devices[0] = { ...simulated.devices[0], simulated: true } as typeof simulated.devices[0];
    mount(`/print?album=${ALBUM}&files=f1`, { stations: [simulated] });
    expect(await screen.findByTestId('owner-print-printer-d1')).toHaveTextContent('Simulata');
  });

  it('says there is no printer to use rather than offering a studio that cannot print', async () => {
    mount(`/print?album=${ALBUM}`, { stations: [] });
    expect(await screen.findByTestId('owner-print-empty')).toHaveTextContent('Nessuna stampante disponibile');
    expect(screen.queryByTestId('party-print-format-photo')).toBeNull();
  });
});

describe('OwnerPrintPage — Esce dalla foto', () => {
  const REGION = { x: 0.25, y: 0.1, width: 0.5, height: 0.8 };

  beforeEach(() => {
    resetSubjectCutoutAvailabilityForTests();
    vi.stubGlobal('URL', Object.assign(URL, {
      createObjectURL: vi.fn(() => 'blob:subject'),
      revokeObjectURL: vi.fn(),
    }));
  });

  afterEach(() => { vi.unstubAllGlobals(); });

  const cutout = () => new Response(new Uint8Array([137, 80, 78, 71]), {
    status: 200,
    headers: {
      'content-type': 'image/png',
      'X-Subject-Cutout-Region': `${REGION.x},${REGION.y},${REGION.width},${REGION.height}`,
    },
  });

  function printPost(calls: { url: string; method: string; body: string | null }[]) {
    const post = [...calls].reverse().find((c) => c.method === 'POST' && c.url.endsWith('/api/print/photo-jobs'));
    return post ? JSON.parse(post.body ?? '{}') : null;
  }

  async function toPreview(user: ReturnType<typeof userEvent.setup>) {
    await user.click(await screen.findByTestId('party-print-format-photo'));
    await user.click(next());
    const img = screen.getByTestId('party-print-crop').querySelector('img')!;
    Object.defineProperties(img, { naturalWidth: { value: 3000 }, naturalHeight: { value: 4000 } });
    fireEvent.load(img);
    await user.click(next());
  }

  it('lifts the subject, proposes the framing that breaks it out, and sends exactly that', async () => {
    const user = userEvent.setup();
    const mock = mount(`/print?album=${ALBUM}&files=f2`, {
      extra: {
        'GET /api/subject-cutout': () => jsonResponse({ available: true }),
        'POST /api/files/f2/subject-cutout': cutout,
      },
    });
    await toPreview(user);
    await user.click(await screen.findByRole('radio', { name: 'Esce dalla foto' }));

    // The subject, laid over the photograph and shown above the frame.
    const layer = await screen.findByTestId('print-popout-layer');
    expect(layer.querySelector('img')).toHaveAttribute('src', 'blob:subject');
    expect(screen.getByTestId('owner-print-popout-note')).toHaveTextContent('esce dal bordo superiore');
    // Words go in its band, as on a framed sheet.
    expect(screen.getByTestId('owner-print-caption')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await waitFor(() => expect(printPost(mock.calls)).not.toBeNull());
    const body = printPost(mock.calls);
    expect(body).toMatchObject({ layout: 'photo', style: 'popOut', orientation: 'portrait' });
    const proposed = popOutPlacement(3000 / 4000, '10x15', true, REGION);
    expect(body.photos[0].placement.zoom).toBeCloseTo(proposed.zoom);
    expect(body.photos[0].placement.centerY).toBeCloseTo(proposed.centerY);
    expect(body.photos[0].placement.zoom).toBeGreaterThan(1);
  });

  it('lets the owner tap the one person who breaks out, and prints that touch', async () => {
    const user = userEvent.setup();
    const RIGHT = { x: 0.55, y: 0.1, width: 0.2, height: 0.8 };
    let lifts = 0;
    const mock = mount(`/print?album=${ALBUM}&files=f2`, {
      extra: {
        'GET /api/subject-cutout': () => jsonResponse({ available: true, pointSelection: true }),
        'POST /api/files/f2/subject-cutout': () => {
          lifts += 1;
          if (lifts !== 2) return cutout();
          return new Response(new Uint8Array([137, 80, 78, 71]), {
            status: 200,
            headers: {
              'content-type': 'image/png',
              'X-Subject-Cutout-Region': `${RIGHT.x},${RIGHT.y},${RIGHT.width},${RIGHT.height}`,
            },
          });
        },
      },
    });
    await toPreview(user);
    await user.click(await screen.findByRole('radio', { name: 'Esce dalla foto' }));

    // Every subject first; a touch on the right of the cutout picks that person.
    const image = await screen.findByTestId('owner-print-subject-pick-image');
    Object.defineProperty(image, 'getBoundingClientRect', {
      value: () => ({ left: 0, top: 0, width: 100, height: 100, right: 100, bottom: 100, x: 0, y: 0, toJSON: () => ({}) }),
    });
    fireEvent.click(image, { clientX: 80, clientY: 50 });

    await waitFor(() => expect(lifts).toBe(2));
    const touch = [...mock.calls].reverse().find((c) => c.url.endsWith('/api/files/f2/subject-cutout'));
    const point = JSON.parse(touch!.body ?? '{}').point;
    expect(point.x).toBeCloseTo(0.25 + 0.8 * 0.5);
    expect(point.y).toBeCloseTo(0.1 + 0.5 * 0.8);
    expect(await screen.findByText(/Esce solo la persona che hai toccato/)).toBeInTheDocument();

    // The framing follows the person, and the print carries the same touch.
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await waitFor(() => expect(printPost(mock.calls)).not.toBeNull());
    const body = printPost(mock.calls);
    expect(body.subject.x).toBeCloseTo(point.x);
    expect(body.subject.y).toBeCloseTo(point.y);
    expect(body.photos[0].placement.centerX).toBeCloseTo(popOutPlacement(3000 / 4000, '10x15', true, RIGHT).centerX);
  });

  it('goes back to every subject, and prints none of the touch', async () => {
    const user = userEvent.setup();
    const mock = mount(`/print?album=${ALBUM}&files=f2`, {
      extra: {
        'GET /api/subject-cutout': () => jsonResponse({ available: true, pointSelection: true }),
        'POST /api/files/f2/subject-cutout': cutout,
      },
    });
    await toPreview(user);
    await user.click(await screen.findByRole('radio', { name: 'Esce dalla foto' }));
    const image = await screen.findByTestId('owner-print-subject-pick-image');
    Object.defineProperty(image, 'getBoundingClientRect', {
      value: () => ({ left: 0, top: 0, width: 100, height: 100, right: 100, bottom: 100, x: 0, y: 0, toJSON: () => ({}) }),
    });
    fireEvent.click(image, { clientX: 20, clientY: 50 });
    await user.click(await screen.findByRole('button', { name: 'Tutte le persone' }));
    await screen.findByText(/Tocca qui chi deve uscire/);

    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await waitFor(() => expect(printPost(mock.calls)).not.toBeNull());
    expect(printPost(mock.calls).subject).toBeUndefined();
  });

  it('offers no touch where the server cannot select one person', async () => {
    const user = userEvent.setup();
    mount(`/print?album=${ALBUM}&files=f2`, {
      extra: {
        'GET /api/subject-cutout': () => jsonResponse({ available: true, pointSelection: false }),
        'POST /api/files/f2/subject-cutout': cutout,
      },
    });
    await toPreview(user);
    await user.click(await screen.findByRole('radio', { name: 'Esce dalla foto' }));
    await screen.findByTestId('print-popout-layer');
    expect(screen.queryByTestId('owner-print-subject-pick')).toBeNull();
  });

  it('says so, and will not print, when the photograph has no subject', async () => {
    const user = userEvent.setup();
    mount(`/print?album=${ALBUM}&files=f2`, {
      extra: {
        'GET /api/subject-cutout': () => jsonResponse({ available: true }),
        'POST /api/files/f2/subject-cutout': () => jsonResponse({ code: 'no_subject' }, 422),
      },
    });
    await toPreview(user);
    await user.click(await screen.findByRole('radio', { name: 'Esce dalla foto' }));
    expect(await screen.findByTestId('owner-print-popout-note')).toHaveTextContent('non trovo un soggetto');
    expect(screen.getByRole('button', { name: 'Stampa' })).toBeDisabled();
    expect(screen.queryByTestId('print-popout-layer')).toBeNull();
  });

  it('is not offered where subject cutout is off', async () => {
    const user = userEvent.setup();
    mount(`/print?album=${ALBUM}&files=f2`, {
      extra: { 'GET /api/subject-cutout': () => jsonResponse({ available: false }) },
    });
    await toPreview(user);
    expect(screen.getByRole('radio', { name: 'Cornice' })).toBeInTheDocument();
    expect(screen.queryByRole('radio', { name: 'Esce dalla foto' })).toBeNull();
  });
});
