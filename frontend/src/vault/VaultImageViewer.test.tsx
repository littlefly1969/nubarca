import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { useState } from 'react';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { VaultFile } from '@nubarca/api-client';
import { I18nProvider } from '../i18n';
import { VaultImageViewer } from './VaultImageViewer';

// Vault viewer: MEDIUM preview for photos (never the original), poster + a
// "playback not available yet" message for videos, keyboard/arrow navigation
// among photos, Escape closes, and focus returns to the opening element.

let fetchMock: ReturnType<typeof vi.fn>;

function imageResponse(): Response {
  return new Response(new Uint8Array([1, 2, 3]), {
    status: 200,
    headers: { 'content-type': 'image/jpeg' },
  });
}

function file(id: string, kind: VaultFile['mediaKind'], name: string): VaultFile {
  return {
    id,
    name,
    title: null,
    displayName: name,
    mediaKind: kind,
    mimeType: kind === 'video' ? 'video/mp4' : 'image/png',
    sizeBytes: 1024,
    createdAt: '2026-01-01T00:00:00Z',
    width: 800,
    height: 600,
    thumbnailAvailable: kind === 'image',
    posterAvailable: kind === 'video',
  };
}

const FILES = [file('p1', 'image', 'photo-1.png'), file('p2', 'image', 'photo-2.png'), file('v1', 'video', 'clip.mp4')];

function renderViewer(startId: string, onClose = vi.fn()) {
  render(
    <I18nProvider>
      <VaultImageViewer token="tok" files={FILES} startId={startId} onClose={onClose} onExpired={() => {}} />
    </I18nProvider>,
  );
  return onClose;
}

beforeEach(() => {
  URL.createObjectURL = vi.fn(() => 'blob:mock/x') as unknown as typeof URL.createObjectURL;
  URL.revokeObjectURL = vi.fn();
  fetchMock = vi.fn(async (url: RequestInfo | URL) => {
    if (String(url).includes('/info')) {
      return new Response(
        JSON.stringify({ id: 'p1', name: 'photo-1.png', displayName: 'photo-1.png', tags: [], mediaKind: 'image' }),
        { status: 200, headers: { 'content-type': 'application/json' } },
      );
    }
    return imageResponse();
  });
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

it('loads the medium preview and navigates between photos with arrow keys', async () => {
  renderViewer('p1');
  expect(await screen.findByText('photo-1.png')).toBeTruthy();
  await waitFor(() =>
    expect(fetchMock.mock.calls.some((c) => String(c[0]).includes('/media/p1/preview'))).toBe(true),
  );

  await userEvent.keyboard('{ArrowRight}');
  expect(await screen.findByText('photo-2.png')).toBeTruthy();
  await waitFor(() =>
    expect(fetchMock.mock.calls.some((c) => String(c[0]).includes('/media/p2/preview'))).toBe(true),
  );

  await userEvent.keyboard('{ArrowLeft}');
  expect(await screen.findByText('photo-1.png')).toBeTruthy();

  // Never requests an original or a video endpoint for a photo.
  expect(fetchMock.mock.calls.every((c) => !String(c[0]).includes('/video'))).toBe(true);
});

it('Escape closes the viewer', async () => {
  const onClose = renderViewer('p1');
  await screen.findByText('photo-1.png');
  await userEvent.keyboard('{Escape}');
  expect(onClose).toHaveBeenCalledTimes(1);
});

it('shows the poster and a not-available message for a video, no player', async () => {
  renderViewer('v1');
  expect(await screen.findByText('clip.mp4')).toBeTruthy();
  expect(await screen.findByTestId('vault-viewer-video-message')).toBeTruthy();
  // Poster (not preview) is fetched; no <video> element is rendered.
  await waitFor(() =>
    expect(fetchMock.mock.calls.some((c) => String(c[0]).includes('/media/v1/poster'))).toBe(true),
  );
  expect(document.querySelector('video')).toBeNull();
});

it('toggles the read-only details panel', async () => {
  renderViewer('p1');
  await screen.findByText('photo-1.png');
  await userEvent.click(screen.getByTestId('vault-viewer-details'));
  await waitFor(() =>
    expect(fetchMock.mock.calls.some((c) => String(c[0]).includes('/media/p1/info'))).toBe(true),
  );
});

it('restores focus to the opening element on close', async () => {
  function Harness() {
    const [open, setOpen] = useState(false);
    return (
      <I18nProvider>
        <button type="button" data-testid="trigger" onClick={() => setOpen(true)}>
          open
        </button>
        {open && (
          <VaultImageViewer
            token="tok"
            files={FILES}
            startId="p1"
            onClose={() => setOpen(false)}
            onExpired={() => {}}
          />
        )}
      </I18nProvider>
    );
  }
  render(<Harness />);
  const trigger = screen.getByTestId('trigger');
  await userEvent.click(trigger);
  await screen.findByTestId('vault-viewer');
  await userEvent.keyboard('{Escape}');
  await waitFor(() => expect(document.activeElement).toBe(trigger));
});
