import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PartyPrintSettings } from './PartyPrintSettings';
import { errorResponse, installFetchMock, jsonResponse } from '../test-utils';
import { I18nProvider } from '../i18n';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const ALBUM = 'album-1';

function settings(overrides: Record<string, unknown> = {}) {
  return {
    enabled: false,
    printStationId: null,
    printerDeviceId: null,
    photo: { enabled: false, maxPrints: 0, used: 0, remaining: 0, perGuest: 0 },
    strip: { enabled: false, maxPrints: 0, used: 0, remaining: 0, perGuest: 0 },
    footerText: null,
    footerMaxLength: 60,
    minBudget: 1,
    maxBudget: 500,
    ...overrides,
  };
}

function device(overrides: Record<string, unknown> = {}) {
  return {
    id: 'dev-1', displayName: 'DS620', manufacturer: 'DNP', model: 'DS620',
    adapterKind: 'cups', observedState: 'ready', lastSeenAt: '2026-01-01T00:00:00Z',
    supportsPhoto10x15: true, ...overrides,
  };
}

function station(overrides: Record<string, unknown> = {}) {
  return {
    id: 'st-1', name: 'Postazione sala', enabled: true, desiredState: 'running',
    status: 'online', lastSeenAt: '2026-01-01T00:00:00Z', agentVersion: '1.0',
    createdAt: '2026-01-01T00:00:00Z', revokedAt: null,
    devices: [device()], queueCount: 0, currentJob: null, lastError: null,
    ...overrides,
  };
}

function mount(
  loaded: unknown = settings(),
  stations: unknown[] = [station()],
  save?: () => Response,
) {
  return installFetchMock({
    [`GET /api/albums/${ALBUM}/party-print-settings`]: () => jsonResponse(loaded),
    'GET /api/print/stations': () => jsonResponse(stations),
    ...(save ? { [`PATCH /api/albums/${ALBUM}/party-print-settings`]: save } : {}),
  });
}

function view() {
  return render(<I18nProvider><PartyPrintSettings albumId={ALBUM} /></I18nProvider>);
}

describe('PartyPrintSettings (owner panel)', () => {
  it('says plainly when there is no print station to print on', async () => {
    mount(settings(), []);
    view();
    // Not an empty select and a switch that cannot be turned on.
    expect(await screen.findByTestId('party-print-no-stations')).toBeInTheDocument();
    expect(screen.queryByLabelText(/Abilita la stampa/)).not.toBeInTheDocument();
  });

  it('offers each product its OWN budget, never a shared total', async () => {
    mount(settings({
      enabled: true, printStationId: 'st-1', printerDeviceId: 'dev-1',
      photo: { enabled: true, maxPrints: 40, used: 12, remaining: 28, perGuest: 2 },
      strip: { enabled: true, maxPrints: 10, used: 9, remaining: 1, perGuest: 1 },
    }));
    view();
    // 40 photos and 10 strips are two budgets. Nothing here may show 50.
    expect(await screen.findByTestId('party-print-photo-usage'))
      .toHaveTextContent('Usate 12 di 40 — ne restano 28');
    expect(screen.getByTestId('party-print-strip-usage'))
      .toHaveTextContent('Usate 9 di 10 — ne restano 1');
    expect(document.body.textContent).not.toContain('50');
  });

  it('only offers printers that can actually produce the sheet', async () => {
    mount(settings({ printStationId: 'st-1' }), [station({
      devices: [device(), device({ id: 'dev-2', displayName: 'LaserJet', supportsPhoto10x15: false })],
    })]);
    view();
    const printer = await screen.findByLabelText('Stampante');
    // Both products compose a 10x15 sheet, so a printer that cannot do that
    // size is never a choice a host can make.
    expect(within(printer).queryByText('LaserJet')).not.toBeInTheDocument();
    expect(within(printer).getByText('DS620')).toBeInTheDocument();
  });

  it('explains an empty printer list instead of leaving a dead select', async () => {
    mount(settings(), [station({ devices: [device({ supportsPhoto10x15: false })] })]);
    const user = userEvent.setup();
    view();
    await user.selectOptions(await screen.findByLabelText('Postazione di stampa'), 'st-1');
    expect(screen.getByTestId('party-print-no-printers')).toBeInTheDocument();
    expect(screen.getByLabelText('Stampante')).toBeDisabled();
  });

  it('forgets the chosen printer when the station changes', async () => {
    mount(settings({ printStationId: 'st-1', printerDeviceId: 'dev-1' }), [
      station(),
      station({ id: 'st-2', name: 'Postazione giardino', devices: [device({ id: 'dev-9', displayName: 'CP1500' })] }),
    ]);
    const user = userEvent.setup();
    view();
    const printer = await screen.findByLabelText('Stampante');
    expect(printer).toHaveValue('dev-1');
    // A printer belongs to a station: keeping the old one selected would aim
    // the party at a device the new station does not have.
    await user.selectOptions(screen.getByLabelText('Postazione di stampa'), 'st-2');
    expect(screen.getByLabelText('Stampante')).toHaveValue('');
  });

  it('saves the whole draft to the print endpoint, and to nothing else', async () => {
    const saved = settings({
      enabled: true, printStationId: 'st-1', printerDeviceId: 'dev-1',
      photo: { enabled: true, maxPrints: 25, used: 0, remaining: 25, perGuest: 3 },
      strip: { enabled: false, maxPrints: 0, used: 0, remaining: 0, perGuest: 0 },
      footerText: 'Auguri Anna',
    });
    const mock = mount(settings(), [station()], () => jsonResponse(saved));
    const user = userEvent.setup();
    view();

    await user.click(await screen.findByLabelText(/Abilita la stampa/));
    await user.selectOptions(screen.getByLabelText('Postazione di stampa'), 'st-1');
    await user.selectOptions(screen.getByLabelText('Stampante'), 'dev-1');
    await user.click(within(screen.getByTestId('party-print-photo')).getByRole('checkbox'));
    await user.type(screen.getByLabelText(/Foto 10×15 — Stampe massime/), '25');
    await user.type(screen.getByLabelText('Riga sulla stampa'), 'Auguri Anna');
    await user.click(screen.getByRole('button', { name: 'Salva impostazioni stampa' }));

    await screen.findByRole('status');
    const patch = mock.calls.find((c) => c.method === 'PATCH');
    expect(JSON.parse(patch!.body!)).toMatchObject({
      enabled: true, printStationId: 'st-1', printerDeviceId: 'dev-1',
      photoEnabled: true, photoMaxPrints: 25, stripEnabled: false,
      footerText: 'Auguri Anna',
    });
    // Saving a print budget must not touch party mode, the token, or moderation.
    expect(mock.calls.some((c) => c.url.includes('/party-settings'))).toBe(false);
  });

  it('offers a per-guest ceiling beside the party-wide budget', async () => {
    mount(settings({
      enabled: true, printStationId: 'st-1', printerDeviceId: 'dev-1',
      photo: { enabled: true, maxPrints: 40, used: 0, remaining: 40, perGuest: 2 },
    }));
    view();
    // A party budget alone is spent by whoever reaches the studio first; this
    // is the number that makes the paper last the evening.
    expect(await screen.findByLabelText(/Foto 10×15 — Stampe per ospite/))
      .toHaveValue(2);
    expect(screen.getByLabelText(/Foto 10×15 — Stampe massime/)).toHaveValue(40);
  });

  it('refuses to promise each guest more than the party has', async () => {
    mount(
      settings({ enabled: true, printStationId: 'st-1', printerDeviceId: 'dev-1' }),
      [station()],
      () => errorResponse(400, { error: 'photo_per_guest_above_budget' }),
    );
    const user = userEvent.setup();
    view();
    await user.click(await screen.findByRole('button', { name: 'Salva impostazioni stampa' }));
    expect(await screen.findByRole('alert'))
      .toHaveTextContent('Non puoi promettere a ogni ospite più foto di quante ne ha la festa.');
  });

  it('says why the server refused, in the host’s own words', async () => {
    const mock = mount(
      settings({
        enabled: true, printStationId: 'st-1', printerDeviceId: 'dev-1',
        photo: { enabled: true, maxPrints: 40, used: 12, remaining: 28, perGuest: 0 },
      }),
      [station()],
      () => errorResponse(400, { error: 'photo_budget_below_used' }),
    );
    const user = userEvent.setup();
    view();
    await user.click(await screen.findByRole('button', { name: 'Salva impostazioni stampa' }));
    // Twelve sheets already came out; the host is told that, not a code.
    expect(await screen.findByRole('alert'))
      .toHaveTextContent('Ci sono già più foto stampate di così.');
    expect(mock.calls.some((c) => c.method === 'PATCH')).toBe(true);
  });

  it('fills the range into the message when a budget is out of bounds', async () => {
    mount(
      settings({ enabled: true, printStationId: 'st-1', printerDeviceId: 'dev-1' }),
      [station()],
      () => errorResponse(400, { error: 'photo_budget_range' }),
    );
    const user = userEvent.setup();
    view();
    await user.click(await screen.findByRole('button', { name: 'Salva impostazioni stampa' }));
    expect(await screen.findByRole('alert'))
      .toHaveTextContent('Le stampe foto devono essere tra 1 e 500.');
  });

  it('never shows a server code it was not given a translation for', async () => {
    mount(
      settings({ enabled: true }),
      [station()],
      () => errorResponse(500, { error: 'Npgsql.PostgresException: boom' }),
    );
    const user = userEvent.setup();
    view();
    await user.click(await screen.findByRole('button', { name: 'Salva impostazioni stampa' }));
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Le impostazioni non sono state salvate.');
    expect(alert).not.toHaveTextContent(/Npgsql|boom/);
  });

  it('stays closed rather than showing a broken form', async () => {
    installFetchMock({
      [`GET /api/albums/${ALBUM}/party-print-settings`]: () => errorResponse(500),
      'GET /api/print/stations': () => jsonResponse([]),
    });
    const { container } = view();
    await waitFor(() => expect(container).toBeEmptyDOMElement());
  });
});
