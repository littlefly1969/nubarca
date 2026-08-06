import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../test-utils';
import { MediaViewer, type MediaViewerItem } from './MediaViewer';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function metadataDoc(overrides: Record<string, unknown> = {}) {
  return {
    id: 'f1',
    name: 'IMG_1248.JPG',
    mimeType: 'image/jpeg',
    sizeBytes: 5_033_164,
    createdAt: '2026-02-02T09:00:00Z',
    updatedAt: null,
    blob: {
      width: 4000, height: 3000, detectedContentType: 'image/jpeg',
      embedded: null, video: null,
    },
    user: {
      title: null, description: null, tags: [], rating: null, favorite: false,
      dateTakenOverride: null, locationOverride: null,
    },
    effective: {
      displayName: 'IMG_1248.JPG',
      dateTaken: '2025-07-14T18:42:00Z',
      dateTakenSource: 'embedded',
      location: null,
    },
    ...overrides,
  };
}

const photo: MediaViewerItem = {
  id: 'f1',
  name: 'IMG_1248.JPG',
  displayName: 'IMG_1248.JPG',
  kind: 'image',
  sizeBytes: 5_033_164,
};

function renderViewer(item: MediaViewerItem = photo) {
  render(
    <AuthedWrapper>
      <MediaViewer
        items={[item]}
        index={0}
        onClose={vi.fn()}
        onIndexChange={vi.fn()}
      />
    </AuthedWrapper>,
  );
}

describe('MediaViewer summary', () => {
  it('keeps the display name and shows size · Date Taken under it', async () => {
    installFetchMock({ 'GET /api/files/f1/metadata': () => jsonResponse(metadataDoc()) });
    renderViewer();

    expect(screen.getByTestId('media-viewer-title')).toHaveTextContent('IMG_1248.JPG');
    const summary = await screen.findByTestId('media-viewer-summary');
    // 5_033_164 bytes = 4.8 MiB, and the effective capture date.
    expect(summary).toHaveTextContent('4.8 MiB');
    expect(summary.textContent).toContain('·');
    expect(summary.textContent).toMatch(/2025/);
  });

  it('shows the size immediately, before the metadata request resolves', () => {
    let resolveMeta: ((v: Response) => void) | null = null;
    installFetchMock({
      'GET /api/files/f1/metadata': () => new Promise<Response>((res) => { resolveMeta = res; }),
    });
    renderViewer();

    // Size is on the loaded item, so it needs no request at all.
    expect(screen.getByTestId('media-viewer-summary')).toHaveTextContent('4.8 MiB');
    expect(resolveMeta).not.toBeNull();
  });

  it('never presents the upload-time fallback as Date Taken', async () => {
    installFetchMock({
      'GET /api/files/f1/metadata': () => jsonResponse(metadataDoc({
        effective: {
          displayName: 'IMG_1248.JPG',
          dateTaken: '2026-02-02T09:00:00Z',
          dateTakenSource: 'uploaded',
          location: null,
        },
      })),
    });
    renderViewer();
    await waitFor(() => expect(screen.getByTestId('media-viewer-summary')).toBeInTheDocument());

    const summary = screen.getByTestId('media-viewer-summary');
    // Only the available field is rendered — no separator, no upload date.
    expect(summary).toHaveTextContent('4.8 MiB');
    expect(summary.textContent).not.toContain('·');
    expect(summary.textContent).not.toMatch(/2026/);
  });

  it('renders no summary at all when neither field is available', async () => {
    installFetchMock({
      'GET /api/files/f1/metadata': () => jsonResponse(metadataDoc({
        sizeBytes: undefined,
        effective: {
          displayName: 'x', dateTaken: '2026-02-02T09:00:00Z',
          dateTakenSource: 'uploaded', location: null,
        },
      })),
    });
    renderViewer({ ...photo, sizeBytes: null });
    await waitFor(() => expect(screen.getByTestId('media-viewer-title')).toBeInTheDocument());
    expect(screen.queryByTestId('media-viewer-summary')).not.toBeInTheDocument();
  });

  it('fetches metadata for the open item only — one request', async () => {
    const mock = installFetchMock({ 'GET /api/files/f1/metadata': () => jsonResponse(metadataDoc()) });
    renderViewer();
    await screen.findByTestId('media-viewer-summary');
    const metaCalls = mock.calls.filter((c) => c.url.includes('/metadata'));
    expect(metaCalls).toHaveLength(1);
    expect(metaCalls[0].url).toContain('/api/files/f1/metadata');
  });

  it('keeps the info and close controls on the right', () => {
    installFetchMock({ 'GET /api/files/f1/metadata': () => jsonResponse(metadataDoc()) });
    renderViewer();
    expect(screen.getByRole('button', { name: 'Dettagli' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Chiudi' })).toBeInTheDocument();
  });

  it('still closes on Escape and navigates with the arrow keys', async () => {
    const onClose = vi.fn();
    const onIndexChange = vi.fn();
    installFetchMock({
      'GET /api/files/f1/metadata': () => jsonResponse(metadataDoc()),
      'GET /api/files/f2/metadata': () => jsonResponse(metadataDoc({ id: 'f2' })),
    });
    render(
      <AuthedWrapper>
        <MediaViewer
          items={[photo, { ...photo, id: 'f2', name: 'B.JPG', displayName: 'B.JPG' }]}
          index={0}
          onClose={onClose}
          onIndexChange={onIndexChange}
        />
      </AuthedWrapper>,
    );
    const user = userEvent.setup();

    await user.keyboard('{ArrowRight}');
    expect(onIndexChange).toHaveBeenCalledWith(1);

    await user.keyboard('{Escape}');
    expect(onClose).toHaveBeenCalled();
  });

  it('reuses the loaded document in the default drawer without a second request', async () => {
    const mock = installFetchMock({ 'GET /api/files/f1/metadata': () => jsonResponse(metadataDoc()) });
    renderViewer();
    await screen.findByTestId('media-viewer-summary');

    await userEvent.setup().click(screen.getByRole('button', { name: 'Dettagli' }));

    // The drawer renders straight away from the already-loaded document.
    expect(await screen.findByRole('link', { name: 'Scarica' })).toBeInTheDocument();
    expect(mock.calls.filter((c) => c.url.includes('/metadata'))).toHaveLength(1);
  });

  it('links the default drawer download to the immutable original', async () => {
    installFetchMock({ 'GET /api/files/f1/metadata': () => jsonResponse(metadataDoc()) });
    renderViewer();
    await screen.findByTestId('media-viewer-summary');
    await userEvent.setup().click(screen.getByRole('button', { name: 'Dettagli' }));

    const link = await screen.findByRole('link', { name: 'Scarica' });
    expect(link).toHaveAttribute('href', '/api/files/f1/content');
  });

  it('shows the medium preview, never the original, as the viewed image', () => {
    installFetchMock({ 'GET /api/files/f1/metadata': () => jsonResponse(metadataDoc()) });
    renderViewer();
    const img = screen.getByAltText('IMG_1248.JPG') as HTMLImageElement;
    expect(img.getAttribute('src')).toBe('/api/files/f1/preview');
  });
});
