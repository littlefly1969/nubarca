import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { SharesPage } from './SharesPage';
import { AuthedWrapper, installFetchMock, jsonResponse, emptyResponse } from '../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function makeLink(overrides: Record<string, unknown> = {}) {
  return {
    id: 'link-1',
    fileName: 'report.pdf',
    folderPath: '/Docs',
    createdAt: '2026-05-20T10:00:00Z',
    expiresAt: null,
    revokedAt: null,
    maxDownloads: null,
    downloadCount: 3,
    lastAccessedAt: null,
    isRevoked: false,
    isExpired: false,
    isExhausted: false,
    ...overrides,
  };
}

describe('SharesPage', () => {
  it('renders share links from /api/share-links with file name, path and status', async () => {
    installFetchMock({
      'GET /api/share-links': () =>
        jsonResponse({
          items: [
            makeLink(),
            makeLink({ id: 'link-2', fileName: 'old.zip', folderPath: '/', isRevoked: true, revokedAt: '2026-05-21T10:00:00Z' }),
          ],
          limit: 50,
          offset: 0,
          total: 2,
        }),
    });

    render(
      <AuthedWrapper>
        <SharesPage />
      </AuthedWrapper>,
    );

    expect(await screen.findByText('report.pdf')).toBeInTheDocument();
    expect(screen.getByText('old.zip')).toBeInTheDocument();
    // Status badges from the precomputed booleans. Scope to the badge span so
    // we don't match the same-named <option>s in the status filter select.
    expect(screen.getByText('Attivo', { selector: 'span.share-status-badge' })).toBeInTheDocument();
    expect(screen.getByText('Revocato', { selector: 'span.share-status-badge' })).toBeInTheDocument();
    // The folder path is surfaced so the user can locate the file.
    expect(screen.getByText('/Docs')).toBeInTheDocument();
  });

  it('never renders a raw token or a /s/ URL or a copy affordance', async () => {
    installFetchMock({
      'GET /api/share-links': () =>
        jsonResponse({ items: [makeLink()], limit: 50, offset: 0, total: 1 }),
    });

    const { container } = render(
      <AuthedWrapper>
        <SharesPage />
      </AuthedWrapper>,
    );

    await screen.findByText('report.pdf');
    // The management page can never reveal a token (recoverable only at
    // creation). There is no Copy button and no /s/ link anywhere.
    expect(screen.queryByRole('button', { name: /copy/i })).not.toBeInTheDocument();
    expect(container.textContent ?? '').not.toContain('/s/');
  });

  it('shows the empty state when the owner has no links', async () => {
    installFetchMock({
      'GET /api/share-links': () =>
        jsonResponse({ items: [], limit: 50, offset: 0, total: 0 }),
    });

    render(
      <AuthedWrapper>
        <SharesPage />
      </AuthedWrapper>,
    );

    expect(
      await screen.findByText('Non hai ancora creato link di condivisione.'),
    ).toBeInTheDocument();
  });

  it('revokes a link after confirmation and reloads', async () => {
    const mock = installFetchMock({
      'GET /api/share-links': () =>
        jsonResponse({ items: [makeLink()], limit: 50, offset: 0, total: 1 }),
      'POST /api/share-links/link-1/revoke': () => emptyResponse(204),
    });

    render(
      <AuthedWrapper>
        <SharesPage />
      </AuthedWrapper>,
    );

    await screen.findByText('report.pdf');

    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Revoca' }));

    expect(confirmSpy).toHaveBeenCalled();
    await waitFor(() => {
      expect(
        mock.calls.some(
          (c) => c.method === 'POST' && c.url === '/api/share-links/link-1/revoke',
        ),
      ).toBe(true);
    });
  });

  it('does not revoke when the confirmation is declined', async () => {
    const mock = installFetchMock({
      'GET /api/share-links': () =>
        jsonResponse({ items: [makeLink()], limit: 50, offset: 0, total: 1 }),
      'POST /api/share-links/link-1/revoke': () => emptyResponse(204),
    });

    render(
      <AuthedWrapper>
        <SharesPage />
      </AuthedWrapper>,
    );

    await screen.findByText('report.pdf');

    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false);
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Revoca' }));

    expect(confirmSpy).toHaveBeenCalled();
    expect(
      mock.calls.some((c) => c.url === '/api/share-links/link-1/revoke'),
    ).toBe(false);
  });
});
