import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ShareLinkPanel } from './ShareLinkPanel';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const FILE_ID = 'file-1';

function summary(overrides: Partial<{
  id: string;
  createdAt: string;
  expiresAt: string | null;
  revokedAt: string | null;
  maxDownloads: number | null;
  downloadCount: number;
  lastAccessedAt: string | null;
  isRevoked: boolean;
  isExpired: boolean;
  isExhausted: boolean;
}>) {
  return {
    id: 'link-x',
    createdAt: '2026-05-20T10:00:00Z',
    expiresAt: null,
    revokedAt: null,
    maxDownloads: null,
    downloadCount: 0,
    lastAccessedAt: null,
    isRevoked: false,
    isExpired: false,
    isExhausted: false,
    ...overrides,
  };
}

describe('ShareLinkPanel', () => {
  it('renders existing links with status badges and no raw token', async () => {
    const active = summary({ id: 'link-active' });
    const revoked = summary({
      id: 'link-revoked',
      revokedAt: '2026-05-21T10:00:00Z',
      isRevoked: true,
    });
    installFetchMock({
      [`GET /api/files/${FILE_ID}/share-links`]: () =>
        jsonResponse([active, revoked]),
    });

    render(
      <AuthedWrapper>
        <ShareLinkPanel
          fileId={FILE_ID}
          fileName="doc.txt"
          onClose={() => {}}
          onFileMissing={() => {}}
        />
      </AuthedWrapper>,
    );

    const list = await screen.findByRole('list', { name: 'Link di condivisione esistenti' });
    expect(within(list).getByText('Attivo')).toBeInTheDocument();
    expect(within(list).getByText('Revocato')).toBeInTheDocument();

    // Raw tokens / public URLs are never rendered for existing links.
    expect(list.textContent).not.toMatch(/\/s\//);
    expect(list.textContent).not.toMatch(/token/i);
  });

  it('shows a Revoke button only for active links', async () => {
    installFetchMock({
      [`GET /api/files/${FILE_ID}/share-links`]: () =>
        jsonResponse([
          summary({ id: 'link-active' }),
          summary({
            id: 'link-revoked',
            revokedAt: '2026-05-21T10:00:00Z',
            isRevoked: true,
          }),
          summary({
            id: 'link-expired',
            expiresAt: '2026-05-20T11:00:00Z',
            isExpired: true,
          }),
        ]),
    });

    render(
      <AuthedWrapper>
        <ShareLinkPanel
          fileId={FILE_ID}
          fileName="doc.txt"
          onClose={() => {}}
          onFileMissing={() => {}}
        />
      </AuthedWrapper>,
    );

    const list = await screen.findByRole('list', { name: 'Link di condivisione esistenti' });
    // Exactly one Revoke button — for the single active row.
    expect(within(list).getAllByRole('button', { name: 'Revoca' })).toHaveLength(1);
  });

  it('shows a metadata warning on the share-link creation form', async () => {
    installFetchMock({
      [`GET /api/files/${FILE_ID}/share-links`]: () => jsonResponse([]),
    });

    render(
      <AuthedWrapper>
        <ShareLinkPanel
          fileId={FILE_ID}
          fileName="photo.jpg"
          onClose={() => {}}
          onFileMissing={() => {}}
        />
      </AuthedWrapper>,
    );

    // The warning lives on the create form so the owner is reminded before
    // they commit to publishing the original bytes.
    const warning = await screen.findByTestId('share-metadata-warning');
    expect(warning.textContent).toMatch(/metadati incorporati/i);
    expect(warning.textContent).toMatch(/fotocamera|posizione/i);
    // Slice 66: the warning now points users at the removal options.
    expect(warning.textContent).toMatch(/Rimuovi metadati|senza metadati/i);
  });

  it('renders the new URL once after a successful create', async () => {
    installFetchMock({
      [`GET /api/files/${FILE_ID}/share-links`]: () => jsonResponse([]),
      [`POST /api/files/${FILE_ID}/share-links`]: () =>
        jsonResponse(
          {
            id: 'link-new',
            token: 'fakeToken123',
            url: '/s/fakeToken123',
            expiresAt: null,
            maxDownloads: null,
          },
          201,
        ),
    });

    render(
      <AuthedWrapper>
        <ShareLinkPanel
          fileId={FILE_ID}
          fileName="doc.txt"
          onClose={() => {}}
          onFileMissing={() => {}}
        />
      </AuthedWrapper>,
    );

    // Wait for the (empty) existing-links section to finish loading.
    await screen.findByText('Nessun link di condivisione per questo file.');

    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Crea link' }));

    const urlInput = (await screen.findByLabelText('URL di condivisione')) as HTMLInputElement;
    expect(urlInput.value).toMatch(/\/s\/fakeToken123$/);
  });
});
