import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import type { FileMetadata } from '@nubarca/api-client';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../../test-utils';
import { MediaMetadataPanel } from './MediaMetadataPanel';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function doc(overrides: Record<string, unknown> = {}): FileMetadata {
  return {
    id: 'f1',
    name: 'IMG_1248.JPG',
    mimeType: 'image/jpeg',
    sizeBytes: 5_033_164,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: null,
    blob: {
      mediaCategory: 'image',
      detectedContentType: 'image/jpeg',
      detectedFormat: 'JPEG',
      width: 4000,
      height: 3000,
      pixelCount: 12_000_000,
      thumbnailStatus: 'ready',
      extractionStatus: 'ready',
      embedded: {
        cameraMake: null, cameraModel: null, lensModel: null, iso: null,
        aperture: null, exposureTime: null, focalLength: null, colorSpace: null,
        orientation: null, hasGps: false, dateTaken: null, dateTakenSource: null,
      },
      video: null,
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
  } as FileMetadata;
}

function renderPanel(props: Partial<Parameters<typeof MediaMetadataPanel>[0]> = {}) {
  render(
    <AuthedWrapper>
      <MemoryRouter>
        <MediaMetadataPanel fileId="f1" kind="image" initialData={doc()} {...props} />
      </MemoryRouter>
    </AuthedWrapper>,
  );
}

describe('photo information drawer — actions', () => {
  it('renders the actions in ordered, labelled groups', () => {
    installFetchMock({});
    renderPanel({ onFindSimilarInLibrary: vi.fn(), onExploreSimilar: vi.fn() });
    const headings = screen.getAllByRole('heading', { level: 4 }).map((h) => h.textContent);
    expect(headings).toEqual(['Metadati', 'Organizza', 'Scopri', 'File']);
  });

  it('keeps Edit metadata', async () => {
    installFetchMock({});
    renderPanel();
    expect(screen.getByTestId('media-edit-metadata')).toBeInTheDocument();
    await userEvent.setup().click(screen.getByTestId('media-edit-metadata'));
    // The editor replaces the read-only view.
    expect(await screen.findByRole('button', { name: 'Salva' })).toBeInTheDocument();
  });

  it('exposes no strip / remove-metadata action', () => {
    installFetchMock({});
    renderPanel();
    expect(screen.queryByTestId('media-strip-metadata')).not.toBeInTheDocument();
    expect(screen.queryByText(/rimuovi metadati/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/strip/i)).not.toBeInTheDocument();
  });

  it('keeps Write Date Taken only for a JPEG that actually has an override', () => {
    installFetchMock({});
    renderPanel({
      initialData: doc({
        user: {
          title: null, description: null, tags: [], rating: null, favorite: false,
          dateTakenOverride: '2020-01-01T00:00:00Z', locationOverride: null,
        },
      }),
    });
    expect(screen.getByTestId('media-write-datetaken')).toBeInTheDocument();
    cleanup();

    installFetchMock({});
    renderPanel(); // no override
    expect(screen.queryByTestId('media-write-datetaken')).not.toBeInTheDocument();
  });

  it('opens the shared album picker instead of an inline native select', async () => {
    installFetchMock({
      'GET /api/albums': () => jsonResponse([
        { id: 'a1', name: 'Holidays', description: null, itemCount: 3, showOnTv: false,
          createdAt: 'x', updatedAt: 'x', photoCount: 3, videoCount: 0, excludedCount: 0, coverItems: [] },
      ]),
    });
    renderPanel();

    // No inline album <select> in the drawer body any more.
    expect(screen.queryByTestId('add-to-album-section')).not.toBeInTheDocument();

    await userEvent.setup().click(screen.getByTestId('add-to-album-btn'));

    const dialog = await screen.findByRole('dialog', { name: 'Aggiungi ad album' });
    // The same picker the bulk selection bar uses: pick existing or create new.
    expect(await within(dialog).findByTestId('album-picker-select')).toBeInTheDocument();
    expect(within(dialog).getByTestId('album-picker-create')).toBeInTheDocument();
  });

  it('consumes Escape so the surface behind it is not dismissed too', async () => {
    // The picker is opened from the media viewer's details drawer, and both
    // listen for Escape on `window`. One Escape used to close the picker AND
    // the viewer behind it, dropping the user out of the photo entirely.
    const behind = vi.fn();
    window.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') behind();
    });

    installFetchMock({
      'GET /api/albums': () => jsonResponse([
        { id: 'a1', name: 'Holidays', description: null, itemCount: 3, showOnTv: false,
          createdAt: 'x', updatedAt: 'x', photoCount: 3, videoCount: 0, excludedCount: 0, coverItems: [] },
      ]),
    });
    renderPanel();
    const user = userEvent.setup();

    await user.click(screen.getByTestId('add-to-album-btn'));
    await screen.findByTestId('album-picker-select');

    await user.keyboard('{Escape}');

    // The picker closes…
    await waitFor(() =>
      expect(screen.queryByTestId('album-picker-select')).not.toBeInTheDocument());
    // …and the listener standing in for the viewer never saw the event.
    expect(behind).not.toHaveBeenCalled();
  });

  it('adds the single open photo to the chosen album', async () => {
    let addedTo: string | null = null;
    let addedIds: unknown = null;
    installFetchMock({
      'GET /api/albums': () => jsonResponse([
        { id: 'a1', name: 'Holidays', description: null, itemCount: 3, showOnTv: false,
          createdAt: 'x', updatedAt: 'x', photoCount: 3, videoCount: 0, excludedCount: 0, coverItems: [] },
      ]),
      'POST /api/albums/a1/items/bulk': (req) => {
        addedTo = 'a1';
        addedIds = JSON.parse(req.body ?? '{}');
        return jsonResponse({ succeeded: 1, skipped: 0, failed: 0 });
      },
    });
    renderPanel();
    const user = userEvent.setup();

    await user.click(screen.getByTestId('add-to-album-btn'));
    await screen.findByTestId('album-picker-select');
    await user.click(screen.getByTestId('album-picker-add'));

    await waitFor(() => expect(addedTo).toBe('a1'));
    expect(addedIds).toEqual({ fileItemIds: ['f1'] });
    expect(await screen.findByTestId('album-picker-message')).toBeInTheDocument();
  });
});

describe('photo information drawer — discover', () => {
  it('offers two distinctly named similarity destinations', () => {
    installFetchMock({});
    renderPanel({ onFindSimilarInLibrary: vi.fn(), onExploreSimilar: vi.fn() });

    const inLibrary = screen.getByTestId('viewer-find-similar');
    const explore = screen.getByTestId('viewer-explore-similar');
    expect(inLibrary).toHaveTextContent('Trova simili nella Libreria');
    expect(explore).toHaveTextContent('Esplora foto simili');
    expect(inLibrary.textContent).not.toBe(explore.textContent);
  });

  it('invokes the library filter and the explorer separately', async () => {
    const onFindSimilarInLibrary = vi.fn();
    const onExploreSimilar = vi.fn();
    installFetchMock({});
    renderPanel({ onFindSimilarInLibrary, onExploreSimilar });
    const user = userEvent.setup();

    await user.click(screen.getByTestId('viewer-find-similar'));
    expect(onFindSimilarInLibrary).toHaveBeenCalledTimes(1);
    expect(onExploreSimilar).not.toHaveBeenCalled();

    await user.click(screen.getByTestId('viewer-explore-similar'));
    expect(onExploreSimilar).toHaveBeenCalledTimes(1);
    expect(onFindSimilarInLibrary).toHaveBeenCalledTimes(1);
  });

  it('renders no inline Similar Photos panel below the actions', () => {
    installFetchMock({});
    renderPanel({ onFindSimilarInLibrary: vi.fn(), onExploreSimilar: vi.fn() });
    expect(screen.queryByTestId('similar-photos-panel')).not.toBeInTheDocument();
    expect(document.querySelector('.similar-photos-panel')).toBeNull();
  });

  it('omits the discover group entirely for a video', () => {
    installFetchMock({});
    renderPanel({
      kind: 'video',
      initialData: doc({
        mimeType: 'video/mp4',
        blob: {
          mediaCategory: 'video', detectedContentType: 'video/mp4', detectedFormat: 'MP4',
          width: 1920, height: 1080, pixelCount: 2_073_600,
          thumbnailStatus: 'ready', extractionStatus: 'ready', embedded: null,
          video: {
            durationSeconds: 12, videoCodec: 'h264', audioCodec: 'aac', frameRate: 25,
            videoBitrate: null, hasAudio: true, audioChannels: 2, audioSampleRate: 48000,
            rotation: 0,
          },
        },
      }),
    });
    expect(screen.queryByTestId('viewer-find-similar')).not.toBeInTheDocument();
    expect(screen.queryByTestId('viewer-explore-similar')).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Scopri' })).not.toBeInTheDocument();
  });
});

describe('photo information drawer — download', () => {
  it('downloads the immutable original, not a derivative', () => {
    installFetchMock({});
    renderPanel();
    const link = screen.getByTestId('download-original');

    expect(link).toHaveAttribute('href', '/api/files/f1/content');
    // Explicitly none of the derived artifacts.
    const href = link.getAttribute('href')!;
    expect(href).not.toContain('/preview');
    expect(href).not.toContain('/thumbnail');
    expect(href).not.toContain('/poster');
    expect(href).not.toContain('privacy-safe');
  });

  it('keeps the stripped-copy download clearly separate from the original', () => {
    installFetchMock({});
    renderPanel();
    expect(screen.getByTestId('privacy-safe-download'))
      .toHaveAttribute('href', '/api/files/f1/content/privacy-safe');
    expect(screen.getByTestId('download-original'))
      .toHaveAttribute('href', '/api/files/f1/content');
  });

  it('offers only the original for a format with no stripped variant', () => {
    installFetchMock({});
    renderPanel({
      initialData: doc({
        blob: {
          mediaCategory: 'image', detectedContentType: 'image/heic', detectedFormat: 'HEIC',
          width: 1, height: 1, pixelCount: 1, thumbnailStatus: 'ready', extractionStatus: 'ready',
          embedded: null, video: null,
        },
      }),
    });
    expect(screen.getByTestId('download-original')).toBeInTheDocument();
    expect(screen.queryByTestId('privacy-safe-download')).not.toBeInTheDocument();
  });
});

describe('photo information drawer — loading', () => {
  it('renders immediately from a host-supplied document without refetching', () => {
    const mock = installFetchMock({});
    renderPanel();
    expect(screen.getByTestId('media-edit-metadata')).toBeInTheDocument();
    expect(mock.calls.some((c) => c.url.includes('/metadata'))).toBe(false);
  });

  it('fetches its own document when the host supplies none', async () => {
    const mock = installFetchMock({
      'GET /api/files/f1/metadata': () => jsonResponse(doc()),
    });
    renderPanel({ initialData: null });
    expect(await screen.findByTestId('media-edit-metadata')).toBeInTheDocument();
    expect(mock.calls.some((c) => c.url.includes('/api/files/f1/metadata'))).toBe(true);
  });

  it('reports the host load failure instead of retrying it', () => {
    const mock = installFetchMock({});
    renderPanel({ initialData: null, loadError: true });
    expect(screen.getByRole('alert')).toBeInTheDocument();
    expect(mock.calls.some((c) => c.url.includes('/metadata'))).toBe(false);
  });
});
