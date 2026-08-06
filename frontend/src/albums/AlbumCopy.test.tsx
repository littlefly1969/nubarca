import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { AlbumCopyPanel } from './AlbumCopyPanel';
import { ReceivedCopiesPanel } from './ReceivedCopiesPanel';
import {
  AuthedWrapper,
  emptyResponse,
  errorResponse,
  installFetchMock,
  jsonResponse,
} from '../test-utils';

// SHARE-COPY-01 frontend.
//
// The rule every test here defends: a copy is irreversible, so the UI must
// never understate that, never offer an action the server would refuse, and
// never name a blocking file the API deliberately kept anonymous.

const navigate = vi.fn();
vi.mock('react-router', async () => {
  const actual = await vi.importActual<typeof import('react-router')>('react-router');
  return { ...actual, useNavigate: () => navigate };
});

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });
beforeEach(() => { navigate.mockReset(); });

function preview(over: Record<string, unknown> = {}) {
  return {
    albumTitle: 'Iceland',
    eligibleItemCount: 12,
    eligibleSizeBytes: 4_194_304,
    blockers: [],
    canSend: true,
    ...over,
  };
}

function sent(over: Record<string, unknown> = {}) {
  return {
    id: 't-1',
    sourceAlbumId: 'alb-1',
    title: 'Iceland',
    recipientDisplayName: 'Bob',
    recipientEmailMask: 'b•••b@nubarca.local',
    itemCount: 12,
    totalSizeBytes: 4_194_304,
    state: 'pending',
    createdAt: '2026-08-01T10:00:00Z',
    expiresAt: '2026-08-31T10:00:00Z',
    respondedAt: null,
    cancelledAt: null,
    ...over,
  };
}

// A promise WE control, rather than a setTimeout. Ambient fake timers leaking
// from another test file would fast-forward a timeout, letting the request
// settle between clicks and making a double-submit test silently meaningless.
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => { resolve = r; });
  return { promise, resolve };
}

function received(over: Record<string, unknown> = {}) {
  return {
    id: 't-1',
    title: 'Iceland',
    description: null,
    senderDisplayName: 'Alice',
    senderEmailMask: 'a•••e@nubarca.local',
    itemCount: 12,
    totalSizeBytes: 4_194_304,
    state: 'pending',
    createdAt: '2026-08-01T10:00:00Z',
    expiresAt: '2026-08-31T10:00:00Z',
    createdAlbumId: null,
    ...over,
  };
}

function renderSender(handlers: Record<string, ReturnType<typeof jsonResponse> | unknown> = {}) {
  const mock = installFetchMock({
    'GET /api/albums/alb-1/transfer-preview': () => jsonResponse(preview()),
    'GET /api/album-transfers/sent': () => jsonResponse([]),
    ...(handlers as Record<string, never>),
  });
  render(
    <AuthedWrapper>
      <MemoryRouter>
        <AlbumCopyPanel albumId="alb-1" albumName="Iceland" onClose={() => {}} />
      </MemoryRouter>
    </AuthedWrapper>,
  );
  return mock;
}

function renderRecipient(handlers: Record<string, unknown> = {}) {
  const mock = installFetchMock({
    'GET /api/album-transfers/received': () => jsonResponse([received()]),
    ...(handlers as Record<string, never>),
  });
  render(
    <AuthedWrapper>
      <MemoryRouter>
        <ReceivedCopiesPanel />
      </MemoryRouter>
    </AuthedWrapper>,
  );
  return mock;
}

describe('sender panel', () => {
  it('summarises the snapshot and states that the copy is independent', async () => {
    renderSender();

    expect(await screen.findByTestId('album-copy-summary')).toBeInTheDocument();
    expect(screen.getByTestId('album-copy-count')).toHaveTextContent('12');
    // Logical size, formatted — the contract requires it be shown before send.
    expect(screen.getByTestId('album-copy-size')).toHaveTextContent('4.0 MiB');
    // The irreversibility must be stated, not implied.
    expect(screen.getByTestId('album-copy-intro')).toHaveTextContent(/copia indipendente/i);
    expect(screen.getByTestId('album-copy-panel')).toHaveTextContent(/non potrai più revocare/i);
  });

  it('refuses the whole album when it holds another user’s contribution', async () => {
    renderSender({
      'GET /api/albums/alb-1/transfer-preview': () => jsonResponse(preview({
        canSend: false,
        eligibleItemCount: 9,
        blockers: [{ reason: 'ContributedByAnotherUser', itemCount: 3 }],
      })),
    });

    const blocked = await screen.findByTestId('album-copy-blocked');
    expect(blocked).toHaveTextContent('3');
    expect(blocked).toHaveTextContent(/contributi collegati di altri utenti/i);
    // The corrective action is offered…
    expect(blocked).toHaveTextContent(/Rimuovili dall’album/i);
    // …and the send control is ABSENT, not merely disabled: there is nothing
    // the user could do here that would succeed.
    expect(screen.queryByTestId('album-copy-send')).not.toBeInTheDocument();
    expect(screen.queryByTestId('album-copy-email')).not.toBeInTheDocument();
  });

  it.each([
    ['InPrivateVault', /Vault privato/i],
    ['Trashed', /cestino/i],
    ['Unavailable', /non più disponibili/i],
  ])('reports the %s blocker with its own wording', async (reason, pattern) => {
    renderSender({
      'GET /api/albums/alb-1/transfer-preview': () => jsonResponse(preview({
        canSend: false,
        blockers: [{ reason, itemCount: 2 }],
      })),
    });

    const blocker = await screen.findByTestId(`album-copy-blocker-${reason}`);
    expect(blocker).toHaveTextContent(pattern);
    expect(blocker).toHaveTextContent('2');
  });

  it('never names a blocking file, only counts and a category', async () => {
    renderSender({
      'GET /api/albums/alb-1/transfer-preview': () => jsonResponse(preview({
        canSend: false,
        blockers: [{ reason: 'ContributedByAnotherUser', itemCount: 1 }],
      })),
    });

    const panel = await screen.findByTestId('album-copy-panel');
    const text = panel.textContent ?? '';
    // The API returns no identifiers; the UI must not imply any either.
    expect(text).not.toMatch(/\.jpg|\.png|\.mp4/i);
    expect(text).not.toMatch(/[0-9a-f]{8}-[0-9a-f]{4}/i);
  });

  it('sends the copy and reports who received it', async () => {
    let sentList: unknown[] = [];
    const mock = renderSender({
      'GET /api/album-transfers/sent': () => jsonResponse(sentList),
      'POST /api/albums/alb-1/transfers': () => {
        sentList = [sent()];
        return jsonResponse(sent());
      },
    });

    await screen.findByTestId('album-copy-send');
    await userEvent.type(screen.getByTestId('album-copy-email'), 'bob@example.com');
    await userEvent.click(screen.getByTestId('album-copy-send'));

    expect(await screen.findByTestId('album-copy-notice')).toHaveTextContent('Bob');
    // The address travels in the BODY, never in the URL.
    const post = mock.calls.find((c) => c.method === 'POST');
    expect(post?.url).not.toContain('bob@example.com');
    expect(post?.body).toContain('bob@example.com');
  });

  it.each([
    ['recipient_not_found', /Nessun account attivo/i],
    ['recipient_is_sender', /inviare una copia a te stesso/i],
    ['already_pending', /copia in sospeso/i],
    ['empty_album', /non contiene elementi/i],
  ])('explains the %s refusal', async (code, pattern) => {
    renderSender({
      'POST /api/albums/alb-1/transfers': () => errorResponse(400, { error: code }),
    });

    await screen.findByTestId('album-copy-send');
    await userEvent.type(screen.getByTestId('album-copy-email'), 'x@example.com');
    await userEvent.click(screen.getByTestId('album-copy-send'));

    expect(await screen.findByTestId('album-copy-error')).toHaveTextContent(pattern);
  });

  it('lets the sender cancel a pending copy', async () => {
    let list = [sent()];
    const mock = renderSender({
      'GET /api/album-transfers/sent': () => jsonResponse(list),
      'POST /api/album-transfers/t-1/cancel': () => {
        list = [sent({ state: 'cancelled', cancelledAt: '2026-08-02T10:00:00Z' })];
        return emptyResponse();
      },
    });

    await userEvent.click(await screen.findByTestId('album-copy-cancel-t-1'));

    await waitFor(() =>
      expect(screen.getByTestId('album-copy-state-t-1')).toHaveTextContent(/Annullata/i));
    expect(mock.calls.some((c) => c.url.endsWith('/cancel'))).toBe(true);
    // A cancelled row offers no further action.
    expect(screen.queryByTestId('album-copy-cancel-t-1')).not.toBeInTheDocument();
  });

  it('tells the sender an accepted copy cannot be recalled', async () => {
    renderSender({
      'GET /api/album-transfers/sent': () => jsonResponse([sent()]),
      'POST /api/album-transfers/t-1/cancel': () =>
        errorResponse(409, { error: 'already_resolved' }),
    });

    await userEvent.click(await screen.findByTestId('album-copy-cancel-t-1'));

    expect(await screen.findByTestId('album-copy-error'))
      .toHaveTextContent(/non può essere richiamata/i);
  });

  it('does not fire a second send on a double click', async () => {
    // Unlike accept, a duplicate SEND is not idempotent — it would be a second
    // offer. The guard must hold before any re-render.
    const gate = deferred<Response>();
    const mock = renderSender({
      'POST /api/albums/alb-1/transfers': () => gate.promise,
    });

    await screen.findByTestId('album-copy-send');
    await userEvent.type(screen.getByTestId('album-copy-email'), 'bob@example.com');
    const button = screen.getByTestId('album-copy-send');
    await userEvent.click(button);
    await userEvent.click(button);
    await userEvent.click(button);
    expect(mock.calls.filter((c) => c.method === 'POST')).toHaveLength(1);

    gate.resolve(jsonResponse(sent()));
    await waitFor(() => expect(screen.getByTestId('album-copy-notice')).toBeInTheDocument());
  });

  it('shows only this album’s copies, not every copy the user ever sent', async () => {
    renderSender({
      'GET /api/album-transfers/sent': () => jsonResponse([
        sent(),
        sent({ id: 't-2', sourceAlbumId: 'other-album', title: 'Somewhere else' }),
      ]),
    });

    const list = await screen.findByTestId('album-copy-sent');
    expect(within(list).getByTestId('album-copy-sent-t-1')).toBeInTheDocument();
    expect(within(list).queryByTestId('album-copy-sent-t-2')).not.toBeInTheDocument();
  });

  it('disambiguates recipients by masked address, never the full one', async () => {
    renderSender({ 'GET /api/album-transfers/sent': () => jsonResponse([sent()]) });

    const row = await screen.findByTestId('album-copy-sent-t-1');
    expect(row).toHaveTextContent('b•••b@nubarca.local');
    expect(row.textContent).not.toContain('bob@example.com');
  });
});

describe('recipient inbox', () => {
  it('shows title, count, size and sender before any decision', async () => {
    renderRecipient();

    expect(await screen.findByTestId('received-copy-t-1')).toHaveTextContent('Iceland');
    expect(screen.getByTestId('received-copy-details-t-1')).toHaveTextContent('12');
    expect(screen.getByTestId('received-copy-details-t-1')).toHaveTextContent('4.0 MiB');
    expect(screen.getByTestId('received-copy-t-1')).toHaveTextContent('Alice');
    expect(screen.getByTestId('received-copy-t-1')).toHaveTextContent('a•••e@nubarca.local');
  });

  it('states the four consequences before the accept button', async () => {
    renderRecipient();

    const explain = await screen.findByTestId('received-copy-explain-t-1');
    expect(explain).toHaveTextContent(/album indipendente/i);
    expect(explain).toHaveTextContent(/quota logica/i);
    expect(explain).toHaveTextContent(/non potrà più revocare/i);
    expect(explain).toHaveTextContent(/nomi delle Persone.*non verranno copiati/i);
  });

  it('exposes no media whatsoever for a pending offer', async () => {
    renderRecipient();

    await screen.findByTestId('received-copy-t-1');
    expect(document.querySelectorAll('img')).toHaveLength(0);
    const text = screen.getByTestId('received-copies-section').textContent ?? '';
    for (const forbidden of ['blobObjectId', 'storageKey', 'sha256', 'fileItemId']) {
      expect(text).not.toContain(forbidden);
    }
  });

  it('accepts and navigates to the new album', async () => {
    // Pending on first read, accepted after — otherwise the accept button the
    // test needs to click would never have rendered.
    let list = [received()];
    renderRecipient({
      'GET /api/album-transfers/received': () => jsonResponse(list),
      'POST /api/album-transfers/t-1/accept': () => {
        list = [received({ state: 'accepted', createdAlbumId: 'new-alb' })];
        return jsonResponse({ albumId: 'new-alb' });
      },
    });

    await userEvent.click(await screen.findByTestId('received-copy-accept-t-1'));

    await waitFor(() => expect(navigate).toHaveBeenCalledWith('/albums/new-alb'));
  });

  it('leaves no accept or decline control once accepted', async () => {
    renderRecipient({
      'GET /api/album-transfers/received': () => jsonResponse([
        received({ state: 'accepted', createdAlbumId: 'new-alb' }),
      ]),
    });

    await screen.findByTestId('received-copy-t-1');
    expect(screen.queryByTestId('received-copy-accept-t-1')).not.toBeInTheDocument();
    expect(screen.queryByTestId('received-copy-decline-t-1')).not.toBeInTheDocument();
    expect(screen.getByTestId('received-copy-open-t-1')).toBeInTheDocument();
  });

  it('declines without creating anything', async () => {
    let list = [received()];
    const mock = renderRecipient({
      'GET /api/album-transfers/received': () => jsonResponse(list),
      'POST /api/album-transfers/t-1/decline': () => {
        list = [received({ state: 'declined' })];
        return emptyResponse();
      },
    });

    await userEvent.click(await screen.findByTestId('received-copy-decline-t-1'));

    await waitFor(() =>
      expect(screen.getByTestId('received-copy-state-t-1')).toHaveTextContent(/Rifiutata/i));
    expect(navigate).not.toHaveBeenCalled();
    expect(mock.calls.some((c) => c.url.endsWith('/accept'))).toBe(false);
  });

  it('does not fire a second accept on a double click', async () => {
    const gate = deferred<Response>();
    const mock = renderRecipient({
      'POST /api/album-transfers/t-1/accept': () => gate.promise,
    });

    const button = await screen.findByTestId('received-copy-accept-t-1');
    // All three clicks land while the first request is still in flight.
    await userEvent.click(button);
    await userEvent.click(button);
    await userEvent.click(button);
    expect(mock.calls.filter((c) => c.url.endsWith('/accept'))).toHaveLength(1);

    gate.resolve(jsonResponse({ albumId: 'new-alb' }));
    await waitFor(() => expect(navigate).toHaveBeenCalled());
    expect(mock.calls.filter((c) => c.url.endsWith('/accept'))).toHaveLength(1);
  });

  it.each([
    [409, 'cancelled', /mittente ha annullato/i],
    [409, 'expired', /è scaduta/i],
    [409, 'sender_unavailable', /non è più disponibile/i],
    [409, 'already_resolved', /già stata gestita/i],
    [404, undefined, /non è più disponibile/i],
  ])('explains a %s/%s failure without retrying', async (statusCode, code, pattern) => {
    const mock = renderRecipient({
      'POST /api/album-transfers/t-1/accept': () =>
        errorResponse(statusCode as number, code ? { error: code } : null),
    });

    await userEvent.click(await screen.findByTestId('received-copy-accept-t-1'));

    expect(await screen.findByTestId('received-copies-error')).toHaveTextContent(pattern);
    expect(navigate).not.toHaveBeenCalled();
    // NO automatic retry: exactly one attempt was made.
    expect(mock.calls.filter((c) => c.url.endsWith('/accept'))).toHaveLength(1);
  });

  it('reports a quota refusal with both figures and creates nothing', async () => {
    renderRecipient({
      'POST /api/album-transfers/t-1/accept': () => errorResponse(409, {
        error: 'quota_exceeded',
        requiredBytes: 4_194_304,
        remainingBytes: 1_048_576,
      }),
    });

    await userEvent.click(await screen.findByTestId('received-copy-accept-t-1'));

    const error = await screen.findByTestId('received-copies-error');
    expect(error).toHaveTextContent('4.0 MiB');
    expect(error).toHaveTextContent('1.0 MiB');
    expect(navigate).not.toHaveBeenCalled();
  });

  it('shows nothing when the inbox is empty', async () => {
    renderRecipient({ 'GET /api/album-transfers/received': () => jsonResponse([]) });

    expect(await screen.findByTestId('received-copies-empty')).toBeInTheDocument();
  });
});
