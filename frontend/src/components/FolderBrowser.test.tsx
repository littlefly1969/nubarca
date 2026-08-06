import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FolderBrowser } from './FolderBrowser';
import {
  AuthedWrapper,
  installFetchMock,
  jsonResponse,
  emptyResponse,
  errorResponse,
  makeAuthValue,
  triggerIntersection,
  type MockHandler,
} from '../test-utils';
import { AuthContext } from '../auth/AuthContext';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  try { localStorage.clear(); } catch { /* ignore */ }
});

interface FileLike {
  id: string;
  name: string;
  mimeType: string;
  sizeBytes: number;
  createdAt?: string;
  width?: number;
  height?: number;
}

function childrenPage(opts: {
  folderId?: string | null;
  folders?: { id: string; name: string; createdAt?: string }[];
  files?: FileLike[];
  nextCursor?: string | null;
  hasMore?: boolean;
}): MockHandler {
  return () =>
    jsonResponse({
      folderId: opts.folderId ?? null,
      folders: (opts.folders ?? []).map((f) => ({
        id: f.id,
        name: f.name,
        createdAt: f.createdAt ?? '2026-05-24T10:00:00Z',
      })),
      files: (opts.files ?? []).map((f) => ({
        id: f.id,
        name: f.name,
        mimeType: f.mimeType,
        sizeBytes: f.sizeBytes,
        createdAt: f.createdAt ?? '2026-05-24T10:00:00Z',
        width: f.width ?? null,
        height: f.height ?? null,
      })),
      nextCursor: opts.nextCursor ?? null,
      hasMore: opts.hasMore ?? false,
    });
}

function renderBrowser(value?: Parameters<typeof makeAuthValue>[1]) {
  if (value) {
    const auth = makeAuthValue(
      { status: 'authed', user: { id: 'user-1', email: 'dev@nubarca.local', displayName: 'Dev', isAdmin: false, language: 'it' } },
      value,
    );
    return render(
      <AuthContext.Provider value={auth}>
        <FolderBrowser />
      </AuthContext.Provider>,
    );
  }
  return render(
    <AuthedWrapper>
      <FolderBrowser />
    </AuthedWrapper>,
  );
}

describe('FolderBrowser (Files UI v2)', () => {
  it('renders folders and files from the directory listing', async () => {
    installFetchMock({
      'GET /api/folders/children': childrenPage({
        folders: [{ id: 'folder-1', name: 'Photos' }],
        files: [{ id: 'file-1', name: 'note.txt', mimeType: 'text/plain', sizeBytes: 12 }],
      }),
    });

    renderBrowser();

    expect(await screen.findByRole('button', { name: 'Open folder Photos' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Open note.txt' })).toBeInTheDocument();
  });

  it('shows a skeleton while the first page is loading', () => {
    // A never-resolving fetch keeps the listing in its loading state.
    installFetchMock({
      'GET /api/folders/children': () => new Promise<Response>(() => {}),
    });
    const { container } = renderBrowser();
    expect(container.querySelector('.files-view')?.getAttribute('aria-busy')).toBe('true');
    expect(container.querySelector('.skeleton')).not.toBeNull();
  });

  it('shows the empty state when the listing has no folders or files', async () => {
    installFetchMock({ 'GET /api/folders/children': childrenPage({}) });
    renderBrowser();
    expect(await screen.findByText('This folder is empty.')).toBeInTheDocument();
  });

  it('shows an error state with retry on a non-401 failure', async () => {
    installFetchMock({ 'GET /api/folders/children': () => errorResponse(500) });
    renderBrowser();
    expect(await screen.findByText('Could not load this folder. Please try again.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument();
  });

  it('invokes invalidateAuth() on a 401 from the listing', async () => {
    installFetchMock({ 'GET /api/folders/children': () => errorResponse(401) });
    const invalidateAuth = vi.fn();
    renderBrowser({ invalidateAuth });
    await waitFor(() => expect(invalidateAuth).toHaveBeenCalled());
  });

  it('requests the small thumbnail (never the original/preview) for image files in the grid', async () => {
    installFetchMock({
      'GET /api/folders/children': childrenPage({
        files: [{ id: 'img-1', name: 'photo.jpg', mimeType: 'image/jpeg', sizeBytes: 4096, width: 800, height: 600 }],
      }),
    });
    const { container } = renderBrowser();
    await screen.findByRole('button', { name: 'Open photo.jpg' });

    const thumb = container.querySelector('img.file-thumb-img') as HTMLImageElement | null;
    expect(thumb).not.toBeNull();
    expect(thumb!.getAttribute('src')).toBe('/api/files/img-1/thumbnail?size=small');
    expect(thumb!.getAttribute('src')).not.toMatch(/\/content|\/preview/);
  });

  it('switches between grid and list views', async () => {
    installFetchMock({
      'GET /api/folders/children': childrenPage({
        files: [{ id: 'file-1', name: 'note.txt', mimeType: 'text/plain', sizeBytes: 12 }],
      }),
    });
    const { container } = renderBrowser();
    await screen.findByRole('button', { name: 'Open note.txt' });

    // Default is grid.
    expect(container.querySelector('.files-grid')).not.toBeNull();

    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'List view' }));
    expect(container.querySelector('.files-list')).not.toBeNull();
    expect(container.querySelector('.files-grid')).toBeNull();
  });

  it('navigates into a folder and lists its children', async () => {
    installFetchMock({
      'GET /api/folders/children': childrenPage({ folders: [{ id: 'folder-1', name: 'Photos' }] }),
      'GET /api/folders/folder-1/children': childrenPage({
        folderId: 'folder-1',
        files: [{ id: 'file-2', name: 'inside.txt', mimeType: 'text/plain', sizeBytes: 9 }],
      }),
    });
    const user = userEvent.setup();
    renderBrowser();

    await user.click(await screen.findByRole('button', { name: 'Open folder Photos' }));
    expect(await screen.findByRole('button', { name: 'Open inside.txt' })).toBeInTheDocument();
    // Breadcrumb gained the folder name (Home is now a link).
    expect(screen.getByRole('button', { name: 'Home' })).toBeInTheDocument();
  });

  it('re-requests the listing with the chosen sort field', async () => {
    const mock = installFetchMock({
      'GET /api/folders/children': childrenPage({
        files: [{ id: 'file-1', name: 'a.txt', mimeType: 'text/plain', sizeBytes: 1 }],
      }),
    });
    const user = userEvent.setup();
    renderBrowser();
    await screen.findByRole('button', { name: 'Open a.txt' });

    await user.selectOptions(screen.getByLabelText('Sort by'), 'size');

    await waitFor(() =>
      expect(mock.calls.some((c) => c.url.includes('sort=size'))).toBe(true),
    );
  });

  it('selects items via the checkbox and surfaces the bulk action bar', async () => {
    installFetchMock({
      'GET /api/folders/children': childrenPage({
        files: [
          { id: 'file-1', name: 'a.txt', mimeType: 'text/plain', sizeBytes: 1 },
          { id: 'file-2', name: 'b.txt', mimeType: 'text/plain', sizeBytes: 2 },
        ],
      }),
    });
    const user = userEvent.setup();
    renderBrowser();
    await screen.findByRole('button', { name: 'Open a.txt' });

    expect(screen.queryByRole('region', { name: 'Selection actions' })).toBeNull();

    await user.click(screen.getByRole('checkbox', { name: 'Select a.txt' }));
    const bar = await screen.findByRole('region', { name: 'Selection actions' });
    expect(within(bar).getByText('1 selected')).toBeInTheDocument();

    await user.click(screen.getByRole('checkbox', { name: 'Select b.txt' }));
    expect(within(bar).getByText('2 selected')).toBeInTheDocument();
  });

  it('bulk-deletes the selection (with confirm) and reports the outcome', async () => {
    const deleted: string[] = [];
    installFetchMock({
      'GET /api/folders/children': childrenPage({
        files: [{ id: 'file-1', name: 'a.txt', mimeType: 'text/plain', sizeBytes: 1 }],
      }),
      'DELETE /api/files/file-1': () => { deleted.push('file-1'); return emptyResponse(204); },
    });
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const user = userEvent.setup();
    renderBrowser();
    await screen.findByRole('button', { name: 'Open a.txt' });

    await user.click(screen.getByRole('checkbox', { name: 'Select a.txt' }));
    await user.click(screen.getByRole('button', { name: 'Delete' }));

    await waitFor(() => expect(deleted).toEqual(['file-1']));
    expect(await screen.findByText(/Moved 1 item to Trash\./)).toBeInTheDocument();
  });

  it('opens the media viewer for an image, using the medium preview', async () => {
    installFetchMock({
      'GET /api/folders/children': childrenPage({
        files: [{ id: 'img-1', name: 'photo.jpg', mimeType: 'image/jpeg', sizeBytes: 4096 }],
      }),
    });
    const user = userEvent.setup();
    renderBrowser();

    await user.click(await screen.findByRole('button', { name: 'Open photo.jpg' }));
    const dialog = await screen.findByRole('dialog', { name: /Visualizzatore multimediale: photo\.jpg/ });
    const img = dialog.querySelector('img.media-viewer-media') as HTMLImageElement | null;
    expect(img).not.toBeNull();
    expect(img!.getAttribute('src')).toBe('/api/files/img-1/preview');
  });

  it('opens the details panel for a non-media file', async () => {
    installFetchMock({
      'GET /api/folders/children': childrenPage({
        files: [{ id: 'file-1', name: 'note.txt', mimeType: 'text/plain', sizeBytes: 12 }],
      }),
    });
    const user = userEvent.setup();
    renderBrowser();
    await user.click(await screen.findByRole('button', { name: 'Open note.txt' }));
    expect(await screen.findByRole('complementary', { name: 'Details: note.txt' })).toBeInTheDocument();
  });

  it('loads the next page when the scroll sentinel intersects', async () => {
    installFetchMock({
      'GET /api/folders/children': (req) => {
        if (req.url.includes('cursor=')) {
          return childrenPage({
            files: [{ id: 'file-2', name: 'b.txt', mimeType: 'text/plain', sizeBytes: 2 }],
            hasMore: false,
          })(req);
        }
        return childrenPage({
          files: [{ id: 'file-1', name: 'a.txt', mimeType: 'text/plain', sizeBytes: 1 }],
          nextCursor: 'CURSOR2',
          hasMore: true,
        })(req);
      },
    });
    renderBrowser();
    await screen.findByRole('button', { name: 'Open a.txt' });

    triggerIntersection();

    expect(await screen.findByRole('button', { name: 'Open b.txt' })).toBeInTheDocument();
  });

  it('never renders storage internals', async () => {
    installFetchMock({
      'GET /api/folders/children': childrenPage({
        folders: [{ id: 'folder-1', name: 'Photos' }],
        files: [{ id: 'img-1', name: 'photo.jpg', mimeType: 'image/jpeg', sizeBytes: 4096 }],
      }),
    });
    renderBrowser();
    await screen.findByRole('button', { name: 'Open photo.jpg' });

    const html = document.body.innerHTML;
    for (const needle of ['storageKey', 'blobObjectId', 'objects/', 'sha256', 'ownerUserId']) {
      expect(html).not.toContain(needle);
    }
  });
});
