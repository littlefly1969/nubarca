import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { TvPage } from './TvPage';
import { TvPairApprovalPage } from './TvPairApprovalPage';
import { TvDevicesPanel } from '../cloud/TvDevicesPanel';
import {
  AuthedWrapper,
  emptyResponse,
  errorResponse,
  installFetchMock,
  jsonResponse,
  type InstalledFetchMock,
} from '../test-utils';
import { I18nProvider } from '../i18n';

vi.mock('qrcode', () => ({
  default: { toString: vi.fn(async () => '<svg data-testid="generated-qr"></svg>') },
}));

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const activeSession = () => jsonResponse({
  status: 'active',
  expiresAt: '2026-08-05T12:00:00Z',
  lastSeenAt: '2026-07-05T12:00:00Z',
  language: 'it',
});

function renderTv() {
  render(<I18nProvider><MemoryRouter><TvPage /></MemoryRouter></I18nProvider>);
}

// Enter a directional code the way a remote does: arrow keys and Enter on the
// blind entry surface. There is deliberately NO keypad to click — the visible
// numeric keypad was the security defect, because the focus ring walking from
// key to key told anyone watching the television what the secret was.
const CODE_KEY: Record<string, string> = {
  U: '{ArrowUp}', D: '{ArrowDown}', L: '{ArrowLeft}', R: '{ArrowRight}', S: '{Enter}',
};

async function enterCode(code: string) {
  const user = userEvent.setup();
  const surface = screen.getByTestId('tv-code-entry');
  surface.focus();
  for (const symbol of code) {
    await user.keyboard(CODE_KEY[symbol]);
  }
}

const VALID_CODE = 'URDLSUDLR';
const WRONG_CODE = 'LLLLLLLLL';

describe('/tv Personal Area', () => {
  it('paired startup always opens mode selection with Party focused', async () => {
    installFetchMock({ 'GET /api/tv/session': activeSession });
    renderTv();

    expect(await screen.findByText('Come vuoi usare NubArca?')).toBeInTheDocument();
    const party = screen.getByTestId('tv-mode-party');
    expect(party).toHaveFocus();
    expect(screen.getByTestId('tv-mode-personal')).toHaveTextContent('Area personale');
    // No albums were fetched yet — neither mode is auto-entered.
    expect(screen.queryByRole('heading', { name: 'I tuoi album TV' })).not.toBeInTheDocument();
  });

  it('renders a focusable third "Laboratorio bellezza" mode card', async () => {
    installFetchMock({ 'GET /api/tv/session': activeSession });
    renderTv();

    const card = await screen.findByTestId('tv-mode-beauty-lab');
    expect(card).toHaveTextContent('Laboratorio bellezza');
    expect(card.tagName).toBe('BUTTON'); // focusable via D-pad like the others
  });

  it('Beauty Lab uses the existing PIN flow and a successful unlock opens the lab grid', async () => {
    installFetchMock({
      'GET /api/tv/session': activeSession,
      'GET /api/tv/personal/status': () => jsonResponse({ pinConfigured: true, unlocked: false, scheme: 'dpad-v1' }),
      'POST /api/tv/personal/unlock': () => jsonResponse({ unlockToken: 'grant-1', expiresAt: '2026-08-05T12:00:00Z' }),
      'GET /api/tv/personal/home': () => jsonResponse({ displayName: 'Owner', galleryAvailable: true }),
      'GET /api/tv/personal/aesthetics/items': () => jsonResponse({ items: [], nextCursor: null }),
    });
    renderTv();

    await userEvent.setup().click(await screen.findByTestId('tv-mode-beauty-lab'));
    // Same unlock screen as the Personal Area — no second credential system.
    expect(await screen.findByTestId('tv-code-entry')).toBeInTheDocument();
    await enterCode(VALID_CODE);

    // Unlock lands on the Beauty Lab, not the Personal Area home.
    expect(await screen.findByTestId('tv-beauty-lab')).toBeInTheDocument();
    expect(screen.queryByTestId('tv-personal-home')).not.toBeInTheDocument();
  });

  it('BACK from the Beauty Lab root locks and returns to mode selection', async () => {
    installFetchMock({
      'GET /api/tv/session': activeSession,
      'GET /api/tv/personal/status': () => jsonResponse({ pinConfigured: true, unlocked: false, scheme: 'dpad-v1' }),
      'POST /api/tv/personal/unlock': () => jsonResponse({ unlockToken: 'grant-1', expiresAt: '2026-08-05T12:00:00Z' }),
      'GET /api/tv/personal/home': () => jsonResponse({ displayName: 'Owner', galleryAvailable: true }),
      'GET /api/tv/personal/aesthetics/items': () => jsonResponse({ items: [], nextCursor: null }),
      'POST /api/tv/personal/lock': () => emptyResponse(204),
    });
    renderTv();
    await userEvent.setup().click(await screen.findByTestId('tv-mode-beauty-lab'));
    await screen.findByTestId('tv-code-entry');
    await enterCode(VALID_CODE);
    const lab = await screen.findByTestId('tv-beauty-lab');

    fireEvent.keyDown(lab, { key: 'Escape' });
    // Locked → back on the mode selector.
    expect(await screen.findByText('Come vuoi usare NubArca?')).toBeInTheDocument();
  });

  it('Personal area opens PIN entry; a wrong PIN shows a generic error, clears input, and stays locked', async () => {
    installFetchMock({
      'GET /api/tv/session': activeSession,
      'GET /api/tv/personal/status': () => jsonResponse({ pinConfigured: true, unlocked: false, scheme: 'dpad-v1' }),
      'POST /api/tv/personal/unlock': () => errorResponse(403),
    });
    renderTv();
    await userEvent.setup().click(await screen.findByTestId('tv-mode-personal'));

    expect(await screen.findByTestId('tv-code-entry')).toBeInTheDocument();
    await enterCode(WRONG_CODE);

    expect(await screen.findByTestId('tv-pin-error')).toHaveTextContent('Codice non valido.');
    // The code is cleared after the failure and we are still on the unlock screen.
    expect(screen.getByTestId('tv-code-entry')).toBeInTheDocument();
    // No progress dot is filled any more. Asserted on the DOTS specifically —
    // the instructional remote ring also draws a centre glyph, and it is STATIC
    // by design, so it must survive a failure untouched.
    expect(document.querySelectorAll('.tv-pin-dot-filled')).toHaveLength(0);
    expect(screen.queryByTestId('tv-personal-home')).not.toBeInTheDocument();
  });

  it('a throttled unlock (429) shows the cooldown message', async () => {
    installFetchMock({
      'GET /api/tv/session': activeSession,
      'GET /api/tv/personal/status': () => jsonResponse({ pinConfigured: true, unlocked: false, scheme: 'dpad-v1' }),
      'POST /api/tv/personal/unlock': () => errorResponse(429),
    });
    renderTv();
    await userEvent.setup().click(await screen.findByTestId('tv-mode-personal'));
    await screen.findByTestId('tv-code-entry');
    await enterCode(VALID_CODE);

    expect(await screen.findByTestId('tv-pin-error')).toHaveTextContent('Troppi tentativi');
  });

  it('a paired session whose owner has no PIN is treated as an incomplete association', async () => {
    // Legacy/corrupted state — unreachable through the atomic pairing flow.
    // Instead of a mode selector that silently allows Party or a PIN pad that
    // can never succeed, /tv shows the recovery message and offers re-pairing.
    installFetchMock({
      'GET /api/tv/session': activeSession,
      'GET /api/tv/personal/status': () => jsonResponse({ pinConfigured: false, unlocked: false }),
    });
    renderTv();

    expect(await screen.findByTestId('tv-incomplete')).toHaveTextContent(
      'Associazione incompleta. Collega di nuovo questa TV.',
    );
    expect(screen.getByRole('button', { name: 'Abbina di nuovo questa TV' })).toBeInTheDocument();
    expect(screen.queryByTestId('tv-mode-party')).not.toBeInTheDocument();
    expect(screen.queryByTestId('tv-code-entry')).not.toBeInTheDocument();
  });

  function unlockableHandlers(mock: { locks: number }): Parameters<typeof installFetchMock>[0] {
    return {
      'GET /api/tv/session': activeSession,
      'GET /api/tv/personal/status': () => jsonResponse({ pinConfigured: true, unlocked: false, scheme: 'dpad-v1' }),
      'POST /api/tv/personal/unlock': () => jsonResponse({
        unlockToken: 'grant-token-1', expiresAt: '2026-07-11T23:59:00Z',
      }),
      'GET /api/tv/personal/home': () => jsonResponse({
        displayName: 'Stefano', galleryAvailable: true,
      }),
      'POST /api/tv/personal/lock': () => {
        mock.locks += 1;
        return emptyResponse(204);
      },
      // The real Personal Gallery loads its first page on entry.
      'GET /api/tv/personal/gallery': () => jsonResponse({
        items: [], nextCursor: null, hasMore: false,
      }),
    };
  }

  async function unlockToHome(fetchMock: InstalledFetchMock) {
    renderTv();
    await userEvent.setup().click(await screen.findByTestId('tv-mode-personal'));
    await screen.findByTestId('tv-code-entry');
    await enterCode(VALID_CODE);
    await screen.findByTestId('tv-personal-home');
    return fetchMock;
  }

  it('a valid PIN opens the Personal Area home; the gallery shell proves the grant is enforced', async () => {
    const state = { locks: 0 };
    const fetchMock = installFetchMock(unlockableHandlers(state));
    await unlockToHome(fetchMock);

    expect(screen.getByText('Stefano')).toBeInTheDocument();

    // The unlock grant travels ONLY in the dedicated header — never in a URL —
    // and is never persisted to browser storage.
    const homeCalls = fetchMock.calls.filter((c) => c.url.includes('/api/tv/personal/home'));
    expect(homeCalls.length).toBeGreaterThan(0);
    for (const call of homeCalls) {
      const headers = call.init?.headers as Record<string, string>;
      expect(headers['X-Tv-Personal-Unlock']).toBe('grant-token-1');
      expect(call.url).not.toContain('grant-token-1');
    }
    expect(window.localStorage.length).toBe(0);
    expect(window.sessionStorage.length).toBe(0);

    // Open the gallery (the list request re-validates the grant server-side
    // and carries it in the SAME dedicated header).
    await userEvent.setup().click(screen.getByRole('button', { name: 'Galleria' }));
    expect(await screen.findByTestId('tv-personal-gallery')).toBeInTheDocument();
    expect(await screen.findByTestId('tv-personal-empty')).toHaveTextContent('La galleria è vuota.');
    const galleryCalls = fetchMock.calls.filter((c) => c.url.includes('/api/tv/personal/gallery'));
    expect(galleryCalls.length).toBeGreaterThan(0);
    for (const call of galleryCalls) {
      const headers = call.init?.headers as Record<string, string>;
      expect(headers['X-Tv-Personal-Unlock']).toBe('grant-token-1');
      expect(call.url).not.toContain('grant-token-1');
    }

    // BACK from the gallery root returns to the Personal Area home (no lock).
    fireEvent.keyDown(screen.getByTestId('tv-personal-gallery'), { key: 'Backspace' });
    expect(await screen.findByTestId('tv-personal-home')).toBeInTheDocument();
    expect(state.locks).toBe(0);
  });

  it('BACK from the Personal Area home locks immediately and returning requires the PIN again', async () => {
    const state = { locks: 0 };
    const fetchMock = installFetchMock(unlockableHandlers(state));
    await unlockToHome(fetchMock);

    fireEvent.keyDown(screen.getByTestId('tv-personal-home'), { key: 'Backspace' });

    // Locked: back on mode selection, lock endpoint called.
    expect(await screen.findByText('Come vuoi usare NubArca?')).toBeInTheDocument();
    await waitFor(() => expect(state.locks).toBe(1));

    // Entering the Personal Area again always re-asks the PIN.
    await userEvent.setup().click(screen.getByTestId('tv-mode-personal'));
    expect(await screen.findByTestId('tv-code-entry')).toBeInTheDocument();
    expect(screen.queryByTestId('tv-personal-home')).not.toBeInTheDocument();
  });

  it('locks locally even when the lock API call fails', async () => {
    const handlers = unlockableHandlers({ locks: 0 });
    handlers['POST /api/tv/personal/lock'] = () => errorResponse(500);
    const fetchMock = installFetchMock(handlers);
    await unlockToHome(fetchMock);

    fireEvent.keyDown(screen.getByTestId('tv-personal-home'), { key: 'Backspace' });
    // The user is never trapped: mode selection is shown regardless.
    expect(await screen.findByText('Come vuoi usare NubArca?')).toBeInTheDocument();
  });

  it('pairing revocation during PIN entry returns to the revoked screen', async () => {
    installFetchMock({
      'GET /api/tv/session': activeSession,
      'GET /api/tv/personal/status': () => jsonResponse({ pinConfigured: true, unlocked: false, scheme: 'dpad-v1' }),
      'POST /api/tv/personal/unlock': () => errorResponse(401),
      'POST /api/tv/pairing/start': () => jsonResponse({
        publicCode: 'NEWCODE1',
        pairingSecret: 'a'.repeat(43),
        approvalUrl: `https://nubarca.test/tv/pair?code=NEWCODE1#secret=${'a'.repeat(43)}`,
        expiresAt: '2026-07-05T12:10:00Z',
      }),
    });
    renderTv();
    await userEvent.setup().click(await screen.findByTestId('tv-mode-personal'));
    await screen.findByTestId('tv-code-entry');
    await enterCode(VALID_CODE);

    expect(await screen.findByText('Questa sessione TV è stata revocata.')).toBeInTheDocument();
    expect(screen.queryByTestId('tv-personal-home')).not.toBeInTheDocument();
  });

  it('pairing revocation inside the gallery clears personal state and returns to the revoked screen', async () => {
    const state = { locks: 0 };
    const handlers = unlockableHandlers(state);
    // Entering the gallery hits the grant-gated list endpoint of a revoked
    // session.
    handlers['GET /api/tv/personal/gallery'] = () => errorResponse(401);
    handlers['POST /api/tv/pairing/start'] = () => jsonResponse({
      publicCode: 'NEWCODE1',
      pairingSecret: 'a'.repeat(43),
      approvalUrl: `https://nubarca.test/tv/pair?code=NEWCODE1#secret=${'a'.repeat(43)}`,
      expiresAt: '2026-07-05T12:10:00Z',
    });
    const fetchMock = installFetchMock(handlers);
    await unlockToHome(fetchMock);

    await userEvent.setup().click(screen.getByRole('button', { name: 'Galleria' }));
    expect(await screen.findByText('Questa sessione TV è stata revocata.')).toBeInTheDocument();
    expect(screen.queryByTestId('tv-personal-gallery')).not.toBeInTheDocument();
  });

  it('a PIN change evicts the Personal Area to mode selection with the notice, keeping Party usable', async () => {
    const state = { locks: 0 };
    const handlers = unlockableHandlers(state);
    let pinChanged = false;
    handlers['GET /api/tv/personal/home'] = () => (pinChanged
      ? jsonResponse({ error: 'pin_changed' }, 403)
      : jsonResponse({ displayName: 'Stefano', galleryAvailable: true }));
    handlers['GET /api/tv/personal/gallery'] = () => (pinChanged
      ? jsonResponse({ error: 'pin_changed' }, 403)
      : jsonResponse({ items: [], nextCursor: null, hasMore: false }));
    handlers['GET /api/tv/albums'] = () => jsonResponse([]);
    const fetchMock = installFetchMock(handlers);
    await unlockToHome(fetchMock);

    // The owner changes the PIN; the next personal request (entering the
    // gallery) finds the stale grant.
    pinChanged = true;
    await userEvent.setup().click(screen.getByRole('button', { name: 'Galleria' }));

    // Evicted to MODE SELECTION (not pairing — the TV association is valid),
    // with the "PIN was changed" notice; the local grant was dropped and the
    // idempotent lock endpoint called.
    expect(await screen.findByText('Come vuoi usare NubArca?')).toBeInTheDocument();
    expect(screen.getByTestId('tv-pin-changed-notice')).toHaveTextContent(
      'Il PIN è stato modificato. Inserisci il nuovo PIN.',
    );
    expect(screen.queryByTestId('tv-personal-gallery')).not.toBeInTheDocument();
    expect(screen.queryByTestId('tv-personal-home')).not.toBeInTheDocument();
    await waitFor(() => expect(state.locks).toBe(1));

    // Party still opens without any PIN; re-entering Personal re-asks the PIN.
    await userEvent.setup().click(screen.getByTestId('tv-mode-party'));
    expect(await screen.findByTestId('tv-albums-empty')).toBeInTheDocument();
    fireEvent.keyDown(screen.getByTestId('tv-albums-empty').parentElement!, { key: 'Backspace' });
    await userEvent.setup().click(await screen.findByTestId('tv-mode-personal'));
    expect(await screen.findByTestId('tv-code-entry')).toBeInTheDocument();
  });

  it('BACK from the Party root returns to mode selection without a PIN', async () => {
    installFetchMock({
      'GET /api/tv/session': activeSession,
      'GET /api/tv/albums': () => jsonResponse([]),
    });
    renderTv();
    await userEvent.setup().click(await screen.findByTestId('tv-mode-party'));
    const empty = await screen.findByTestId('tv-albums-empty');

    fireEvent.keyDown(empty.parentElement!, { key: 'Backspace' });
    expect(await screen.findByText('Come vuoi usare NubArca?')).toBeInTheDocument();

    // Party re-opens directly — no PIN gate on the Party path.
    await userEvent.setup().click(screen.getByTestId('tv-mode-party'));
    expect(await screen.findByTestId('tv-albums-empty')).toBeInTheDocument();
  });
});

describe('atomic pairing approval', () => {
  function renderApproval() {
    render(
      <MemoryRouter initialEntries={[`/tv/pair?code=ABCD2345#secret=${'s'.repeat(43)}`]}>
        <AuthedWrapper><TvPairApprovalPage /></AuthedWrapper>
      </MemoryRouter>,
    );
  }

  // The approval page is an owner-private page on the owner's OWN device, so
  // it deliberately SHOWS the code while it is being chosen — the opposite of
  // the television, which never renders a symbol. Entry is by the same
  // directions the remote uses.
  async function typeCode(field: HTMLElement, code: string) {
    const user = userEvent.setup();
    field.focus();
    for (const symbol of code) {
      await user.keyboard(CODE_KEY[symbol]);
    }
  }

  // Backspace removes one move, exactly as it does on the remote. A rejected
  // submit deliberately does NOT clear the fields — client-side validation
  // returns before the request, so the user can correct what they entered
  // rather than start over.
  async function clearCode(field: HTMLElement) {
    const user = userEvent.setup();
    field.focus();
    for (let i = 0; i < 9; i++) await user.keyboard('{Backspace}');
  }

  it('owner without a code sees mandatory create+confirm fields in ONE atomic approval', async () => {
    let approveBody: string | null = null;
    installFetchMock({
      'GET /api/tv-personal/pin': () => jsonResponse({ configured: false, updatedAt: null, scheme: null }),
      'POST /api/tv/pairing/ABCD2345/approve': ({ body }) => {
        approveBody = body;
        return jsonResponse({ status: 'approved', expiresAt: '2026-07-05T12:10:00Z' });
      },
    });
    renderApproval();
    const user = userEvent.setup();

    // One coherent form: the code fields are part of the approval itself.
    const submit = await screen.findByRole('button', { name: 'Crea il codice e approva la TV' });
    const codeField = document.getElementById('tv-pair-code')!;
    const confirmField = document.getElementById('tv-pair-code-confirm')!;

    // Incomplete → rejected client-side; the approve endpoint is NEVER called.
    await typeCode(codeField, 'URD');
    await typeCode(confirmField, 'URD');
    await user.click(submit);
    expect(await screen.findByRole('alert'))
      .toHaveTextContent('Il codice deve essere di 9 movimenti.');
    expect(approveBody).toBeNull();

    // Mismatch → rejected client-side. Both fields reach nine symbols; only the
    // last one differs.
    await typeCode(codeField, 'LSUDLR');            // completes the first field
    await typeCode(confirmField, 'LSUDLL');         // nine symbols, different
    await user.click(submit);
    expect(codeField.querySelectorAll('.is-filled')).toHaveLength(9);
    expect(confirmField.querySelectorAll('.is-filled')).toHaveLength(9);
    expect(await screen.findByRole('alert')).toHaveTextContent('I codici non coincidono.');
    expect(approveBody).toBeNull();
    expect(screen.queryByRole('heading', { name: 'TV approvata' })).not.toBeInTheDocument();

    // Valid + confirmed → ONE server call carrying secret + code; success only
    // after the atomic server completion.
    await clearCode(codeField);
    await clearCode(confirmField);
    await typeCode(codeField, VALID_CODE);
    await typeCode(confirmField, VALID_CODE);
    await user.click(submit);
    expect(await screen.findByRole('heading', { name: 'TV approvata' })).toBeInTheDocument();
    expect(approveBody!).toContain('pairingSecret');
    expect(approveBody!).toContain(`"personalCode":"${VALID_CODE}"`);
    expect(approveBody!).toContain(`"personalCodeConfirmation":"${VALID_CODE}"`);
  });

  it('a server-side rejection keeps the form (no false success) and clears the code fields', async () => {
    installFetchMock({
      'GET /api/tv-personal/pin': () => jsonResponse({ configured: false, updatedAt: null, scheme: null }),
      'POST /api/tv/pairing/ABCD2345/approve': () =>
        errorResponse(400, { error: 'invalid_code' }),
    });
    renderApproval();
    const user = userEvent.setup();

    const submit = await screen.findByRole('button', { name: 'Crea il codice e approva la TV' });
    const codeField = document.getElementById('tv-pair-code')!;
    const confirmField = document.getElementById('tv-pair-code-confirm')!;
    await typeCode(codeField, VALID_CODE);
    await typeCode(confirmField, VALID_CODE);
    await user.click(submit);

    expect(await screen.findByRole('alert'))
      .toHaveTextContent('Il codice deve essere di 9 movimenti.');
    expect(screen.queryByRole('heading', { name: 'TV approvata' })).not.toBeInTheDocument();
    // The entered code never lingers after a submit.
    expect(codeField.textContent).not.toContain('↑');
  });

  it('owner with an existing PIN approves with one tap and sees no PIN fields', async () => {
    let approveBody: string | null = null;
    installFetchMock({
      'GET /api/tv-personal/pin': () => jsonResponse({
        configured: true, updatedAt: '2026-07-01T10:00:00Z',
      }),
      'POST /api/tv/pairing/ABCD2345/approve': ({ body }) => {
        approveBody = body;
        return jsonResponse({ status: 'approved', expiresAt: '2026-07-05T12:10:00Z' });
      },
    });
    renderApproval();

    const submit = await screen.findByRole('button', { name: 'Approva la TV' });
    expect(screen.queryByLabelText(/PIN/)).not.toBeInTheDocument();
    await userEvent.setup().click(submit);

    expect(await screen.findByRole('heading', { name: 'TV approvata' })).toBeInTheDocument();
    // The existing PIN is never replaced from the pairing flow.
    expect(approveBody!).not.toContain('"personalPin":"');
  });
});

describe('owner Personal Area TV code panel', () => {
  const activeDevice = {
    id: 's1', deviceLabel: null, userAgent: null, status: 'active',
    createdAt: '2026-07-01T10:00:00Z', lastSeenAt: '2026-07-10T10:00:00Z',
    expiresAt: '2026-08-01T10:00:00Z', revokedAt: null,
  };

  function renderDevices() {
    render(
      <MemoryRouter>
        <AuthedWrapper><TvDevicesPanel /></AuthedWrapper>
      </MemoryRouter>,
    );
  }

  // The account page is the one place the code is deliberately VISIBLE: it is
  // the owner's own authenticated device and they are choosing a secret they
  // have to remember. The television never renders a symbol.
  async function typeCode(id: string, code: string) {
    const user = userEvent.setup();
    document.getElementById(id)!.focus();
    for (const symbol of code) {
      await user.keyboard(CODE_KEY[symbol]);
    }
  }

  it('shows the unconfigured status and configures a missing code', async () => {
    let setBody: string | null = null;
    installFetchMock({
      'GET /api/tv-devices': () => jsonResponse([activeDevice]),
      'GET /api/tv-personal/pin': () =>
        jsonResponse({ configured: false, updatedAt: null, scheme: null }),
      'POST /api/tv-personal/tv-code': ({ body }) => {
        setBody = body;
        return jsonResponse({
          configured: true, updatedAt: '2026-07-11T10:00:00Z', scheme: 'dpad-v1',
        });
      },
    });
    renderDevices();

    expect(await screen.findByTestId('tv-pin-status'))
      .toHaveTextContent('Nessun codice impostato.');
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Configura il codice' }));

    await typeCode('tv-personal-code', VALID_CODE);
    await typeCode('tv-personal-code-confirm', VALID_CODE);
    await user.click(screen.getByRole('button', { name: 'Configura il codice' }));

    expect(await screen.findByText('Codice aggiornato. Ogni TV chiederà il nuovo codice.'))
      .toBeInTheDocument();
    expect(setBody!).toContain(`"code":"${VALID_CODE}"`);
    // Status flips to configured, and the editor closes with nothing retained.
    expect(screen.getByRole('button', { name: 'Cambia codice' })).toBeInTheDocument();
    // The secret is never written to browser storage.
    expect(window.localStorage.length).toBe(0);
    expect(window.sessionStorage.length).toBe(0);
  });

  it('validates length and confirmation before calling the API', async () => {
    let called = false;
    installFetchMock({
      'GET /api/tv-devices': () => jsonResponse([]),
      'GET /api/tv-personal/pin': () => jsonResponse({
        configured: true, updatedAt: '2026-07-01T10:00:00Z', scheme: 'dpad-v1',
      }),
      'POST /api/tv-personal/tv-code': () => {
        called = true;
        return jsonResponse({
          configured: true, updatedAt: '2026-07-11T10:00:00Z', scheme: 'dpad-v1',
        });
      },
    });
    renderDevices();
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Cambia codice' }));

    // Incomplete → refused client-side; the API is never called.
    await typeCode('tv-personal-code', 'URD');
    await typeCode('tv-personal-code-confirm', 'URD');
    await user.click(screen.getByRole('button', { name: 'Cambia codice' }));
    expect(await screen.findByRole('alert'))
      .toHaveTextContent('Il codice deve essere di 9 movimenti.');
    expect(called).toBe(false);

    // Complete but mismatched → also refused client-side.
    await typeCode('tv-personal-code', 'LSUDLR');
    await typeCode('tv-personal-code-confirm', 'LSUDLL');
    await user.click(screen.getByRole('button', { name: 'Cambia codice' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('I codici non coincidono.');
    expect(called).toBe(false);
  });

  it('an account still on the legacy PIN is told to configure the new code', async () => {
    // The pairing is fine and its televisions still unlock — only the
    // credential needs upgrading, and the current TV app has no numeric entry
    // surface to offer. That is a call to action, not a broken pairing.
    installFetchMock({
      'GET /api/tv-devices': () => jsonResponse([activeDevice]),
      'GET /api/tv-personal/pin': () => jsonResponse({
        configured: true, updatedAt: '2026-07-01T10:00:00Z', scheme: 'pin-v1',
      }),
    });
    renderDevices();

    expect(await screen.findByTestId('tv-pin-legacy'))
      .toHaveTextContent('vecchio PIN numerico');
    expect(screen.getByRole('button', { name: 'Configura il codice' })).toBeInTheDocument();
  });

  it('an API failure keeps no code in the fields', async () => {
    installFetchMock({
      'GET /api/tv-devices': () => jsonResponse([]),
      'GET /api/tv-personal/pin': () => jsonResponse({
        configured: true, updatedAt: '2026-07-01T10:00:00Z', scheme: 'dpad-v1',
      }),
      'POST /api/tv-personal/tv-code': () => errorResponse(500),
    });
    renderDevices();
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Cambia codice' }));
    await typeCode('tv-personal-code', VALID_CODE);
    await typeCode('tv-personal-code-confirm', VALID_CODE);
    await user.click(screen.getByRole('button', { name: 'Cambia codice' }));

    expect(await screen.findByRole('alert'))
      .toHaveTextContent('Impossibile salvare il codice. Riprova.');
    // The secret never lingers in the control after a submit.
    expect(document.getElementById('tv-personal-code')!.textContent).not.toContain('↑');
  });
});
