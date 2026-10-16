import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../test-utils';
import { MediaViewer, OWNER_FILE_SOURCES, type MediaViewerItem } from '../components/MediaViewer';
import { SubjectCutoutDialog, cutoutDownloadName } from './SubjectCutoutDialog';
import { resetSubjectCutoutAvailabilityForTests } from './useSubjectCutoutAvailable';

function pngResponse(): Response {
  return new Response(new Uint8Array([137, 80, 78, 71]), {
    status: 200,
    headers: { 'content-type': 'image/png' },
  });
}

beforeEach(() => {
  resetSubjectCutoutAvailabilityForTests();
  vi.stubGlobal('URL', Object.assign(URL, {
    createObjectURL: vi.fn(() => 'blob:cutout'),
    revokeObjectURL: vi.fn(),
  }));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function renderDialog(onClose = vi.fn()) {
  render(
    <AuthedWrapper>
      <SubjectCutoutDialog fileId="f1" fileName="IMG_0001.HEIC" onClose={onClose} />
    </AuthedWrapper>,
  );
  return onClose;
}

describe('SubjectCutoutDialog', () => {
  it('shows the cutout and offers download and save', async () => {
    installFetchMock({ 'POST /api/files/f1/subject-cutout': () => pngResponse() });
    renderDialog();

    expect(screen.getByRole('status')).toHaveTextContent(/sfondo/);
    const image = await screen.findByTestId('subject-cutout-image');
    expect(image).toHaveAttribute('src', 'blob:cutout');
    const download = screen.getByRole('link', { name: 'Scarica' });
    expect(download).toHaveAttribute('download', 'IMG_0001-cutout.png');
    expect(screen.getByRole('button', { name: 'Salva in NubArca' })).toBeEnabled();
  });

  it('saves a copy next to the photo and says where', async () => {
    const mock = installFetchMock({
      'POST /api/files/f1/subject-cutout': () => pngResponse(),
      'POST /api/files/f1/subject-cutout/save': () => jsonResponse({
        id: 'f2', name: 'IMG_0001-cutout.png', mimeType: 'image/png', sizeBytes: 10, createdAt: '2026-10-16T00:00:00Z',
      }, 201),
    });
    renderDialog();

    await userEvent.click(await screen.findByRole('button', { name: 'Salva in NubArca' }));

    expect(await screen.findByTestId('subject-cutout-notice')).toHaveTextContent('IMG_0001-cutout.png');
    expect(screen.getByRole('button', { name: 'Salva in NubArca' })).toBeDisabled();
    expect(mock.calls.filter((c) => c.url.endsWith('/save'))).toHaveLength(1);
  });

  it('copies the PNG to the clipboard when the browser can', async () => {
    const write = vi.fn(async () => {});
    vi.stubGlobal('ClipboardItem', class { constructor(public items: Record<string, Blob>) {} });
    Object.defineProperty(navigator, 'clipboard', { value: { write }, configurable: true });
    installFetchMock({ 'POST /api/files/f1/subject-cutout': () => pngResponse() });
    renderDialog();

    await userEvent.click(await screen.findByRole('button', { name: 'Copia' }));

    expect(write).toHaveBeenCalledTimes(1);
    expect(await screen.findByTestId('subject-cutout-notice')).toHaveTextContent('Copiato');
  });

  it('explains a photo with no subject and lets the owner retry', async () => {
    let calls = 0;
    installFetchMock({
      'POST /api/files/f1/subject-cutout': () => {
        calls += 1;
        return calls === 1
          ? jsonResponse({ code: 'no_subject' }, 422)
          : pngResponse();
      },
    });
    renderDialog();

    expect(await screen.findByRole('alert')).toHaveTextContent('non ho trovato un soggetto');
    await userEvent.click(screen.getByRole('button', { name: 'Riprova' }));
    expect(await screen.findByTestId('subject-cutout-image')).toBeInTheDocument();
  });

  it('does not offer a retry when the server cannot do cutouts', async () => {
    installFetchMock({
      'POST /api/files/f1/subject-cutout': () => jsonResponse({ code: 'unavailable' }, 503),
    });
    renderDialog();

    expect(await screen.findByRole('alert')).toHaveTextContent('non è disponibile');
    expect(screen.queryByRole('button', { name: 'Riprova' })).toBeNull();
  });

  it('closes on Escape', async () => {
    installFetchMock({ 'POST /api/files/f1/subject-cutout': () => pngResponse() });
    const onClose = renderDialog();
    await screen.findByTestId('subject-cutout-image');

    await userEvent.keyboard('{Escape}');

    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('names downloads like the server names saved copies', () => {
    expect(cutoutDownloadName('IMG_0001.HEIC')).toBe('IMG_0001-cutout.png');
    expect(cutoutDownloadName('.jpg')).toBe('photo-cutout.png');
    expect(cutoutDownloadName('scan')).toBe('scan-cutout.png');
  });
});

const photo: MediaViewerItem = {
  id: 'f1', sources: OWNER_FILE_SOURCES, name: 'IMG_0001.HEIC', displayName: 'IMG_0001.HEIC', kind: 'image',
};

function renderViewer(item: MediaViewerItem, onClose = vi.fn()) {
  render(
    <AuthedWrapper>
      <MediaViewer items={[item]} index={0} onClose={onClose} onIndexChange={vi.fn()} />
    </AuthedWrapper>,
  );
  return onClose;
}

describe('MediaViewer subject cutout action', () => {
  it('is offered on a photo when the server offers it, and opens the dialog', async () => {
    installFetchMock({
      'GET /api/subject-cutout': () => jsonResponse({ available: true }),
      'GET /api/files/f1/metadata': () => jsonResponse({}, 404),
      'POST /api/files/f1/subject-cutout': () => pngResponse(),
    });
    const onClose = renderViewer(photo);

    await userEvent.click(await screen.findByTestId('viewer-subject-cutout'));
    expect(await screen.findByTestId('subject-cutout-image')).toBeInTheDocument();

    // The dialog owns Escape: it closes, the viewer stays.
    await userEvent.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByTestId('subject-cutout-dialog')).toBeNull());
    expect(onClose).not.toHaveBeenCalled();
  });

  it('is absent when the server does not offer it', async () => {
    const mock = installFetchMock({
      'GET /api/subject-cutout': () => jsonResponse({ available: false }),
      'GET /api/files/f1/metadata': () => jsonResponse({}, 404),
    });
    renderViewer(photo);

    await waitFor(() => expect(mock.calls.some((c) => c.url === '/api/subject-cutout')).toBe(true));
    expect(screen.queryByTestId('viewer-subject-cutout')).toBeNull();
  });

  it('is never offered on a video', async () => {
    installFetchMock({
      'GET /api/subject-cutout': () => jsonResponse({ available: true }),
      '* /api/files/f1/metadata': () => jsonResponse({}, 404),
      '* /api/files/f1/video': () => new Response(null, { status: 404 }),
    });
    renderViewer({ ...photo, kind: 'video' });

    await new Promise((r) => setTimeout(r, 20));
    expect(screen.queryByTestId('viewer-subject-cutout')).toBeNull();
  });
});
