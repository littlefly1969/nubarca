import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PartyPrintSettings } from './PartyPrintSettings';
import { errorResponse, installFetchMock, jsonResponse } from '../test-utils';
import { I18nProvider } from '../i18n';
import { crewPartyApi, PartyApiProvider } from '../party/workspace/partyApi';
import { CREW_CAPABILITIES } from '../party/crew/crewModel';

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
  it('says plainly when there is no printer to print on', async () => {
    mount(settings(), []);
    view();
    // Not an empty picker and a switch that cannot be turned on.
    expect(await screen.findByTestId('party-print-no-stations')).toBeInTheDocument();
    expect(screen.queryByTestId('party-print-enabled')).not.toBeInTheDocument();
  });

  it('says plainly when a station exists but nothing on it can print 10×15', async () => {
    mount(settings(), [station({ devices: [device({ supportsPhoto10x15: false })] })]);
    view();
    // A station that contributes no usable printer contributes NOTHING: one
    // honest sentence rather than a station picker above a dead printer picker.
    expect(await screen.findByTestId('party-print-no-stations')).toBeInTheDocument();
    expect(screen.queryByTestId('party-print-choices')).not.toBeInTheDocument();
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
    // Both products compose a 10x15 sheet, so a printer that cannot do that
    // size is never a choice a host can make — it is absent, not disabled.
    expect(await screen.findByTestId('party-print-option-dev-1')).toBeInTheDocument();
    expect(screen.queryByTestId('party-print-option-dev-2')).not.toBeInTheDocument();
    expect(screen.queryByText('LaserJet')).not.toBeInTheDocument();
  });

  it('is a real radio group: one name, one tab stop, the keyboard included', async () => {
    mount(settings({ printStationId: 'st-1', printerDeviceId: 'dev-1' }), [
      station(),
      station({
        id: 'st-2', name: 'Postazione giardino',
        devices: [device({ id: 'dev-9', displayName: 'CP1500' })],
      }),
    ]);
    view();

    const chosen = await screen.findByTestId('party-print-option-dev-1');
    // NATIVE radios, not clickable divs: the browser gives the group its
    // arrow keys, its single tab stop and its announced state for free, and a
    // hand-rolled `role="radio"` would have to reimplement all three.
    expect(chosen).toHaveAttribute('type', 'radio');
    expect(chosen).toBeChecked();
    const other = screen.getByTestId('party-print-option-dev-9');
    expect(other).not.toBeChecked();
    // One group, so exactly one of them can be chosen.
    expect(chosen.getAttribute('name')).toBe(other.getAttribute('name'));
  });

  it('chooses a printer with the keyboard alone', async () => {
    const mock = mount(
      settings({ enabled: true, printStationId: 'st-1', printerDeviceId: 'dev-1' }),
      [
        station(),
        station({
          id: 'st-2', name: 'Postazione giardino',
          devices: [device({ id: 'dev-9', displayName: 'CP1500' })],
        }),
      ],
      () => jsonResponse(settings({ printStationId: 'st-2', printerDeviceId: 'dev-9' })),
    );
    const user = userEvent.setup();
    view();

    const chosen = await screen.findByTestId('party-print-option-dev-1');
    chosen.focus();
    await user.keyboard('{ArrowDown}');
    expect(screen.getByTestId('party-print-option-dev-9')).toBeChecked();

    // And the choice is the one that gets saved: a printer belongs to a
    // station, so BOTH ids travel together and never half of a stale pair.
    await user.click(screen.getByTestId('party-print-save'));
    await screen.findByTestId('party-print-saved');
    const patch = mock.calls.find((c) => c.method === 'PATCH');
    expect(JSON.parse(patch!.body!)).toMatchObject({
      printStationId: 'st-2', printerDeviceId: 'dev-9',
    });
  });

  it('keeps an offline printer in the list, and says it is offline', async () => {
    mount(
      // Printing switched ON, so the state line is about the PRINTER rather
      // than about a party that is not printing at all.
      settings({ enabled: true, printStationId: 'st-1', printerDeviceId: 'dev-1' }),
      [station({ status: 'offline' })],
    );
    view();

    // "The printer in the hall is offline" is information a host needs; a list
    // that silently shrank would leave them wondering where it went.
    const card = await screen.findByTestId('party-print-option-dev-1-card');
    expect(within(card).getByText('Offline')).toBeInTheDocument();
    expect(within(card).getByText(/non risponde/)).toBeInTheDocument();
    // And the party's own state says it would not print right now.
    expect(screen.getByTestId('party-print-readiness'))
      .toHaveTextContent('Stampante offline');
  });

  it('names where each printer is, so a host recognises the one by the door', async () => {
    mount(settings(), [station({ name: 'Sala' })]);
    view();
    const card = await screen.findByTestId('party-print-option-dev-1-card');
    expect(within(card).getByText('DS620')).toBeInTheDocument();
    expect(within(card).getByText('Postazione: Sala')).toBeInTheDocument();
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

    await user.click(await screen.findByTestId('party-print-enabled'));
    await user.click(screen.getByTestId('party-print-option-dev-1'));
    await user.click(screen.getByTestId('party-print-photo-enabled'));
    await user.type(screen.getByLabelText(/Foto 10×15 — Stampe massime/), '25');
    await user.type(screen.getByLabelText('Cosa scrivere sul foglio'), 'Auguri Anna');
    await user.click(screen.getByTestId('party-print-save'));

    await screen.findByTestId('party-print-saved');
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
    await user.click(await screen.findByTestId('party-print-save'));
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
    await user.click(await screen.findByTestId('party-print-save'));
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
    await user.click(await screen.findByTestId('party-print-save'));
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
    await user.click(await screen.findByTestId('party-print-save'));
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Le impostazioni non sono state salvate.');
    expect(alert).not.toHaveTextContent(/Npgsql|boom/);
  });

  it('says a failed read failed, and offers the way back', async () => {
    // It used to render NOTHING here, which is indistinguishable from a slow
    // read for as long as the host is willing to wait — and, once the panel
    // grew a loading skeleton, indistinguishable from one that never ends.
    let fail = true;
    const mock = installFetchMock({
      [`GET /api/albums/${ALBUM}/party-print-settings`]: () =>
        (fail ? errorResponse(500) : jsonResponse(settings())),
      'GET /api/print/stations': () => jsonResponse([]),
    });
    view();

    expect(await screen.findByTestId('party-print-failed')).toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent(/impostazioni di stampa/i);

    fail = false;
    await userEvent.click(screen.getByRole('button', { name: 'Riprova' }));

    expect(await screen.findByTestId('party-print-settings')).toBeInTheDocument();
    expect(mock.calls.filter((c) => c.url.includes('party-print-settings'))).toHaveLength(2);
  });

  // --- The hardware boundary ----------------------------------------------

  it('never shows a collaborator the venue’s equipment', async () => {
    const fetchMock = installFetchMock({
      [`GET /api/party-crew/parties/p1/print-settings`]: () => jsonResponse(settings()),
      // A station route the crew surface must never call. Registering it means
      // a request would SUCCEED — so if one appears, the assertion below is
      // about a real call and not about a missing mock.
      'GET /api/print/stations': () => jsonResponse([station()]),
    });
    render(
      <I18nProvider>
        <PartyApiProvider api={crewPartyApi('p1', [CREW_CAPABILITIES.printManage])}>
          <PartyPrintSettings albumId={ALBUM} />
        </PartyApiProvider>
      </I18nProvider>,
    );

    // The printer is the host administering their own equipment, not this
    // evening's configuration: said in one line, with no picker, no switch and
    // no station or device name anywhere on the surface.
    expect(await screen.findByTestId('party-print-crew-station')).toBeInTheDocument();
    expect(screen.queryByTestId('party-print-choices')).not.toBeInTheDocument();
    expect(screen.queryByTestId('party-print-enabled')).not.toBeInTheDocument();
    const text = document.body.textContent ?? '';
    expect(text).not.toMatch(/Postazione sala|DS620|DNP|st-1|dev-1/);

    // And the listing was never even asked for: the crew client answers it
    // with an empty list instead of reaching for an owner route.
    expect(fetchMock.calls.map((c) => c.url)).not.toContain('/api/print/stations');
  });
});
