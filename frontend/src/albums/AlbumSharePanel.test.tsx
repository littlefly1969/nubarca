import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AlbumSharePanel } from './AlbumSharePanel';
import {
  AuthedWrapper,
  emptyResponse,
  errorResponse,
  installFetchMock,
  jsonResponse,
} from '../test-utils';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

function member(over: Partial<Record<string, unknown>> = {}) {
  return {
    membershipId: 'mem-1',
    displayName: 'Bruno',
    maskedEmail: 'b•••o@example.com',
    role: 'viewer',
    state: 'accepted',
    allowOriginalDownload: false,
    invitedAt: '2026-07-01T00:00:00Z',
    acceptedAt: '2026-07-02T00:00:00Z',
    declinedAt: null,
    revokedAt: null,
    ...over,
  };
}

function renderPanel(onClose = vi.fn()) {
  return render(
    <AuthedWrapper>
      <AlbumSharePanel albumId="alb-1" albumName="Vacanze" onClose={onClose} />
    </AuthedWrapper>,
  );
}

describe('AlbumSharePanel', () => {
  it('lists members by display name and a MASKED hint — never a full address', async () => {
    installFetchMock({
      'GET /api/albums/alb-1/members': () => jsonResponse([member()]),
    });
    renderPanel();

    const row = await screen.findByTestId('album-share-member');
    expect(row).toHaveTextContent('Bruno');
    expect(screen.getByTestId('album-share-hint')).toHaveTextContent('b•••o@example.com');
    // The hint is masked, and no user id is rendered.
    expect(row.textContent).not.toContain('bruno@example.com');
    expect(row.textContent).not.toMatch(/[0-9a-f]{8}-[0-9a-f]{4}/i);
  });

  it('tells two identically-named members apart', async () => {
    // The case the hint exists for: same display name, different accounts.
    installFetchMock({
      'GET /api/albums/alb-1/members': () => jsonResponse([
        member(),
        member({ membershipId: 'mem-2', maskedEmail: 'c•••l@example.com' }),
      ]),
    });
    renderPanel();

    const rows = await screen.findAllByTestId('album-share-member');
    expect(rows).toHaveLength(2);
    const hints = screen.getAllByTestId('album-share-hint').map((h) => h.textContent);
    expect(new Set(hints).size).toBe(2);
  });

  it('names the member unambiguously in the revoke confirmation', async () => {
    const confirmSpy = vi.fn(() => false);
    vi.stubGlobal('confirm', confirmSpy);
    installFetchMock({
      'GET /api/albums/alb-1/members': () => jsonResponse([member()]),
    });
    renderPanel();

    await userEvent.click(await screen.findByTestId('album-share-revoke'));
    // Display name alone would be ambiguous with two "Bruno"s.
    expect(confirmSpy).toHaveBeenCalledWith(
      expect.stringContaining('Bruno (b•••o@example.com)'),
    );
  });

  it('renders no hint when the server could not produce one', async () => {
    installFetchMock({
      'GET /api/albums/alb-1/members': () => jsonResponse([member({ maskedEmail: '' })]),
    });
    renderPanel();

    await screen.findByTestId('album-share-member');
    expect(screen.queryByTestId('album-share-hint')).not.toBeInTheDocument();
  });

  it('invites in two steps: confirm the person, then send', async () => {
    let invited = false;
    installFetchMock({
      'GET /api/albums/alb-1/members': () => jsonResponse(
        invited ? [member({ state: 'pending', acceptedAt: null })] : [],
      ),
      'POST /api/albums/alb-1/members/resolve': () => jsonResponse({ displayName: 'Bruno' }),
      'POST /api/albums/alb-1/members': () => {
        invited = true;
        return jsonResponse(member({ state: 'pending', acceptedAt: null }));
      },
    });
    renderPanel();

    await screen.findByTestId('album-share-empty');
    await userEvent.type(screen.getByTestId('album-share-email'), 'bruno@example.com');
    await userEvent.click(screen.getByTestId('album-share-resolve'));

    // Nothing is sent yet — the owner sees who they are about to share with.
    const confirm = await screen.findByTestId('album-share-confirm');
    expect(confirm).toHaveTextContent('Bruno');

    await userEvent.click(screen.getByTestId('album-share-send'));
    expect(await screen.findByTestId('album-share-member')).toHaveTextContent('Bruno');
    expect(screen.getByTestId('album-share-state')).toHaveTextContent(/in attesa/i);
  });

  it('never sends when the recipient cannot be resolved', async () => {
    const spy = installFetchMock({
      'GET /api/albums/alb-1/members': () => jsonResponse([]),
      // Unknown, disabled and self all arrive as the same 404.
      'POST /api/albums/alb-1/members/resolve': () => errorResponse(404),
    });
    renderPanel();

    await screen.findByTestId('album-share-empty');
    await userEvent.type(screen.getByTestId('album-share-email'), 'nobody@example.com');
    await userEvent.click(screen.getByTestId('album-share-resolve'));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      /nessun account nubarca può essere invitato/i,
    );
    expect(screen.queryByTestId('album-share-confirm')).not.toBeInTheDocument();
    expect(spy.calls.some((c) => c.method === 'POST' && c.url === '/api/albums/alb-1/members'))
      .toBe(false);
  });

  it('reports a duplicate invitation as a conflict, keeping the confirm step', async () => {
    installFetchMock({
      'GET /api/albums/alb-1/members': () => jsonResponse([]),
      'POST /api/albums/alb-1/members/resolve': () => jsonResponse({ displayName: 'Bruno' }),
      'POST /api/albums/alb-1/members': () => errorResponse(409),
    });
    renderPanel();

    await screen.findByTestId('album-share-empty');
    await userEvent.type(screen.getByTestId('album-share-email'), 'bruno@example.com');
    await userEvent.click(screen.getByTestId('album-share-resolve'));
    await userEvent.click(await screen.findByTestId('album-share-send'));

    expect(await screen.findByRole('alert')).toHaveTextContent(/già accesso o un invito/i);
    expect(screen.getByTestId('album-share-confirm')).toBeInTheDocument();
  });

  it('sends the address only in a POST body, never in a URL', async () => {
    const spy = installFetchMock({
      'GET /api/albums/alb-1/members': () => jsonResponse([]),
      'POST /api/albums/alb-1/members/resolve': () => jsonResponse({ displayName: 'Bruno' }),
    });
    renderPanel();

    await screen.findByTestId('album-share-empty');
    await userEvent.type(screen.getByTestId('album-share-email'), 'bruno@example.com');
    await userEvent.click(screen.getByTestId('album-share-resolve'));
    await screen.findByTestId('album-share-confirm');

    // An address in a URL lands in access logs, browser history and Referer.
    for (const call of spy.calls) {
      expect(call.url).not.toContain('bruno@example.com');
      expect(call.url).not.toContain('bruno%40example.com');
    }
    const resolve = spy.calls.find((c) => c.url === '/api/albums/alb-1/members/resolve');
    expect(resolve?.body).toContain('bruno@example.com');
  });

  it('toggles a member’s original-download permission', async () => {
    let allowed = false;
    const spy = installFetchMock({
      'GET /api/albums/alb-1/members': () => jsonResponse(
        [member({ allowOriginalDownload: allowed })],
      ),
      'PATCH /api/albums/alb-1/members/mem-1': () => {
        allowed = true;
        return jsonResponse(member({ allowOriginalDownload: true }));
      },
    });
    renderPanel();

    const toggle = await screen.findByLabelText(/consenti a bruno .*di scaricare/i);
    expect(toggle).not.toBeChecked();
    await userEvent.click(toggle);

    expect(await screen.findByLabelText(/consenti a bruno .*di scaricare/i)).toBeChecked();
    expect(spy.calls.some((c) => c.method === 'PATCH')).toBe(true);
  });

  it('revokes after confirming, warning that downloaded files cannot be recalled', async () => {
    let revoked = false;
    const confirmSpy = vi.fn(() => true);
    vi.stubGlobal('confirm', confirmSpy);
    installFetchMock({
      'GET /api/albums/alb-1/members': () => jsonResponse(
        revoked ? [member({ state: 'revoked', revokedAt: '2026-07-03T00:00:00Z' })] : [member()],
      ),
      'DELETE /api/albums/alb-1/members/mem-1': () => { revoked = true; return emptyResponse(); },
    });
    renderPanel();

    await userEvent.click(await screen.findByTestId('album-share-revoke'));

    expect(confirmSpy).toHaveBeenCalledWith(
      expect.stringContaining('già scaricato'),
    );
    // Revoked rows move to history and out of "people with access".
    expect(await screen.findByTestId('album-share-empty')).toBeInTheDocument();
    expect(screen.getByTestId('album-share-past')).toHaveTextContent('Bruno');
  });

  it('does not revoke when the confirmation is dismissed', async () => {
    vi.stubGlobal('confirm', vi.fn(() => false));
    const spy = installFetchMock({
      'GET /api/albums/alb-1/members': () => jsonResponse([member()]),
    });
    renderPanel();

    await userEvent.click(await screen.findByTestId('album-share-revoke'));
    expect(spy.calls.some((c) => c.method === 'DELETE')).toBe(false);
  });

  it('labels a pending invitation’s action as cancel, not revoke', async () => {
    installFetchMock({
      'GET /api/albums/alb-1/members': () => jsonResponse(
        [member({ state: 'pending', acceptedAt: null })],
      ),
    });
    renderPanel();

    expect(await screen.findByTestId('album-share-revoke')).toHaveTextContent(/annulla invito/i);
  });

  it('closes on Escape', async () => {
    const onClose = vi.fn();
    installFetchMock({ 'GET /api/albums/alb-1/members': () => jsonResponse([]) });
    renderPanel(onClose);

    await screen.findByTestId('album-share-empty');
    await userEvent.keyboard('{Escape}');
    expect(onClose).toHaveBeenCalled();
  });

  it('surfaces a load failure without losing the invite form', async () => {
    installFetchMock({ 'GET /api/albums/alb-1/members': () => errorResponse(500) });
    renderPanel();

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(screen.getByTestId('album-share-email')).toBeInTheDocument();
  });
});
