import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { parseSubjectCutoutRegion, subjectPointOnPhoto } from '@nubarca/api-client';
import userEvent from '@testing-library/user-event';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../test-utils';
import { MediaViewer, OWNER_FILE_SOURCES, type MediaViewerItem } from '../components/MediaViewer';
import { SubjectCutoutDialog, cutoutDownloadName } from './SubjectCutoutDialog';
import { resetSubjectCutoutAvailabilityForTests } from './useSubjectCutoutAvailable';

function pngResponse(region?: string): Response {
  return new Response(new Uint8Array([137, 80, 78, 71]), {
    status: 200,
    headers: {
      'content-type': 'image/png',
      ...(region ? { 'X-Subject-Cutout-Region': region } : {}),
    },
  });
}

function cutoutPosts<T extends { url: string; method: string }>(calls: T[]): T[] {
  return calls.filter((c) => c.method === 'POST' && c.url.endsWith('/subject-cutout'));
}

// jsdom lays nothing out: give the shown cutout a 200x100 box at (10, 20).
function tapCutout(image: HTMLElement, x: number, y: number) {
  image.getBoundingClientRect = () => ({
    left: 10, top: 20, width: 200, height: 100, right: 210, bottom: 120, x: 10, y: 20, toJSON: () => ({}),
  });
  fireEvent.click(image, { clientX: 10 + x, clientY: 20 + y });
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

  it('keeps only the tapped subject, saves that, and goes back to all subjects', async () => {
    const mock = installFetchMock({
      'GET /api/subject-cutout': () => jsonResponse({ available: true, pointSelection: true }),
      // The whole cutout covers the right half of the photo.
      'POST /api/files/f1/subject-cutout': () => pngResponse('0.5,0,0.5,1'),
      'POST /api/files/f1/subject-cutout/save': () => jsonResponse({
        id: 'f2', name: 'IMG_0001-cutout.png', mimeType: 'image/png', sizeBytes: 10, createdAt: '2026-10-16T00:00:00Z',
      }, 201),
    });
    renderDialog();

    expect(await screen.findByTestId('subject-cutout-hint')).toHaveTextContent('Tocca un soggetto');
    // A tap at the middle of the shown cutout is (0.75, 0.5) on the photo.
    tapCutout(screen.getByTestId('subject-cutout-image'), 100, 50);

    await waitFor(() => expect(cutoutPosts(mock.calls)).toHaveLength(2));
    const tapped = cutoutPosts(mock.calls)[1];
    expect(JSON.parse(tapped.body!)).toEqual({ point: { x: 0.75, y: 0.5 } });
    expect(await screen.findByTestId('subject-cutout-hint')).toHaveTextContent('altro punto');

    await userEvent.click(await screen.findByRole('button', { name: 'Salva in NubArca' }));
    await screen.findByTestId('subject-cutout-notice');
    const save = mock.calls.find((c) => c.url.endsWith('/save'))!;
    expect(JSON.parse(save.body!)).toEqual({ point: { x: 0.75, y: 0.5 } });

    await userEvent.click(screen.getByRole('button', { name: 'Tutti i soggetti' }));
    await waitFor(() => expect(cutoutPosts(mock.calls)).toHaveLength(3));
    expect(cutoutPosts(mock.calls)[2].body).toBeNull();
  });

  it('says so when the tap hit no subject, and offers all subjects instead of a retry', async () => {
    installFetchMock({
      'GET /api/subject-cutout': () => jsonResponse({ available: true, pointSelection: true }),
      'POST /api/files/f1/subject-cutout': ({ body }) =>
        body === null ? pngResponse('0,0,1,1') : jsonResponse({ code: 'no_subject' }, 422),
    });
    renderDialog();

    await screen.findByTestId('subject-cutout-hint');
    tapCutout(screen.getByTestId('subject-cutout-image'), 5, 5);

    expect(await screen.findByRole('alert')).toHaveTextContent('In quel punto non');
    expect(screen.queryByRole('button', { name: 'Riprova' })).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Tutti i soggetti' }));
    expect(await screen.findByTestId('subject-cutout-image')).toBeInTheDocument();
  });

  it('ignores taps when the server cannot select a subject', async () => {
    const mock = installFetchMock({
      'GET /api/subject-cutout': () => jsonResponse({ available: true, pointSelection: false }),
      'POST /api/files/f1/subject-cutout': () => pngResponse('0,0,1,1'),
    });
    renderDialog();

    const image = await screen.findByTestId('subject-cutout-image');
    await waitFor(() => expect(mock.calls.some((c) => c.url === '/api/subject-cutout')).toBe(true));
    tapCutout(image, 50, 50);

    expect(screen.queryByTestId('subject-cutout-hint')).toBeNull();
    expect(mock.calls.filter((c) => c.url.endsWith('/subject-cutout') && c.method === 'POST')).toHaveLength(1);
  });

  it('maps taps on the cutout onto the photo and reads only well-formed regions', () => {
    expect(parseSubjectCutoutRegion('0.25,0,0.5,1')).toEqual({ x: 0.25, y: 0, width: 0.5, height: 1 });
    expect(parseSubjectCutoutRegion(null)).toBeNull();
    expect(parseSubjectCutoutRegion('0,0,1')).toBeNull();
    expect(parseSubjectCutoutRegion('0,0,2,1')).toBeNull();
    expect(parseSubjectCutoutRegion('0,0,0,1')).toBeNull();
    expect(subjectPointOnPhoto({ x: 0.25, y: 0, width: 0.5, height: 1 }, 0.5, 0.5)).toEqual({ x: 0.5, y: 0.5 });
    expect(subjectPointOnPhoto({ x: 0.5, y: 0.5, width: 0.5, height: 0.5 }, 1.5, -1)).toEqual({ x: 1, y: 0.5 });
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
