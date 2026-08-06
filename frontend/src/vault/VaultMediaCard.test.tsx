import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import type { VaultFile } from '@nubarca/api-client';
import { I18nProvider } from '../i18n';
import { triggerIntersection } from '../test-utils';
import { VaultMediaCard } from './VaultMediaCard';

// Vault media card: lazy fetch (only after it enters the viewport), token in the
// header only, object URL revoked on unmount, neutral placeholder for other
// kinds / missing derivatives.

let created: string[];
let revoked: string[];
let fetchMock: ReturnType<typeof vi.fn>;

function imageResponse(): Response {
  return new Response(new Uint8Array([1, 2, 3]), {
    status: 200,
    headers: { 'content-type': 'image/jpeg' },
  });
}

function photo(overrides: Partial<VaultFile> = {}): VaultFile {
  return {
    id: 'p1',
    name: 'holiday.png',
    title: null,
    displayName: 'holiday.png',
    mediaKind: 'image',
    mimeType: 'image/png',
    sizeBytes: 2048,
    createdAt: '2026-01-01T00:00:00Z',
    width: 800,
    height: 600,
    thumbnailAvailable: true,
    posterAvailable: false,
    ...overrides,
  };
}

function renderCard(file: VaultFile) {
  return render(
    <I18nProvider>
      <VaultMediaCard
        token="tok"
        file={file}
        selectable={false}
        selected={false}
        onToggleSelect={() => {}}
        onOpen={() => {}}
        onExpired={() => {}}
      />
    </I18nProvider>,
  );
}

beforeEach(() => {
  created = [];
  revoked = [];
  URL.createObjectURL = vi.fn(() => {
    const u = `blob:mock/${created.length}`;
    created.push(u);
    return u;
  }) as unknown as typeof URL.createObjectURL;
  URL.revokeObjectURL = vi.fn((u: string) => revoked.push(u));
  fetchMock = vi.fn(async () => imageResponse());
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

it('does not fetch until the card enters the viewport, then loads the thumbnail', async () => {
  renderCard(photo());
  // Before intersection: neutral placeholder, no fetch.
  expect(fetchMock).not.toHaveBeenCalled();

  triggerIntersection();
  await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));

  const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
  expect(url).toContain('/api/private-vault/media/p1/thumbnail?size=small');
  expect(url).not.toContain('tok');
  expect((init.headers as Record<string, string>)['X-Vault-Token']).toBe('tok');

  const img = await screen.findByRole('img');
  expect(img.getAttribute('src')).toBe('blob:mock/0');
  // The token never lands in the DOM.
  expect(document.body.innerHTML).not.toContain('tok');
});

it('revokes the object URL on unmount', async () => {
  const { unmount } = renderCard(photo());
  triggerIntersection();
  await screen.findByRole('img');
  unmount();
  expect(revoked).toContain('blob:mock/0');
});

it('renders a neutral placeholder and fetches nothing for other files', async () => {
  renderCard(photo({ mediaKind: 'other', mimeType: 'application/pdf', thumbnailAvailable: false }));
  triggerIntersection();
  // No media request for a non-image/video file.
  await waitFor(() => expect(fetchMock).not.toHaveBeenCalled());
  expect(screen.queryByRole('img')).toBeNull();
});

it('does not fetch when the derivative is unavailable', async () => {
  renderCard(photo({ thumbnailAvailable: false }));
  triggerIntersection();
  await waitFor(() => expect(fetchMock).not.toHaveBeenCalled());
});
