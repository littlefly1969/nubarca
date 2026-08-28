import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { PartyUploadPage } from './PartyUploadPage';
import { errorResponse, installFetchMock, jsonResponse } from '../test-utils';
import { I18nProvider } from '../i18n';

// The upload itself goes via XMLHttpRequest (for byte progress), so it is not
// intercepted by the fetch mock — drive it through this controllable mock.
type ProgressCb = (e: { lengthComputable: boolean; loaded: number; total: number }) => void;
class MockXHR {
  static last: MockXHR | null = null;
  static status = 200;
  static body = '{"accepted":1,"rejected":0,"acceptedPhotos":1,"acceptedVideos":0,"quotaRejectedPhotos":0,"quotaRejectedVideos":0,"remainingPhotos":null,"remainingVideos":null}';
  method = '';
  url = '';
  withCredentials = false;
  status = 0;
  responseText = '';
  upload: { onprogress?: ProgressCb } = {};
  onload: (() => void) | null = null;
  onerror: (() => void) | null = null;
  onabort: (() => void) | null = null;
  open(method: string, url: string) { this.method = method; this.url = url; }
  addEventListener() {}
  static autoFinish = false;
  send() {
    MockXHR.last = this;
    MockXHR.sent.push(this);
    // The upload queue awaits each request before starting the next, so a mock
    // that never completes would stall the run. Tests that want to observe
    // progress mid-flight keep autoFinish off and drive finish() themselves.
    if (MockXHR.autoFinish) queueMicrotask(() => this.finish());
  }
  static sent: MockXHR[] = [];
  abort() { this.onabort?.(); }
  progress(loaded: number, total: number) { this.upload.onprogress?.({ lengthComputable: true, loaded, total }); }
  static bodies: string[] = [];
  finish() {
    this.status = MockXHR.status;
    this.responseText = MockXHR.bodies.length > 0 ? MockXHR.bodies.shift()! : MockXHR.body;
    this.onload?.();
  }
}

beforeEach(() => {
  MockXHR.last = null;
  MockXHR.sent = [];
  MockXHR.bodies = [];
  MockXHR.autoFinish = false;
  MockXHR.status = 200;
  MockXHR.body = '{"accepted":1,"rejected":0,"acceptedPhotos":1,"acceptedVideos":0,"quotaRejectedPhotos":0,"quotaRejectedVideos":0,"remainingPhotos":null,"remainingVideos":null}';
  vi.stubGlobal('XMLHttpRequest', MockXHR as unknown as typeof XMLHttpRequest);
});

afterEach(() => {
  cleanup();
  Reflect.deleteProperty(navigator, 'wakeLock');
  Reflect.deleteProperty(document, 'visibilityState');
  vi.unstubAllGlobals();
});

function installWakeLock() {
  const releases: Array<ReturnType<typeof vi.fn>> = [];
  const request = vi.fn(async () => {
    const release = vi.fn(async () => {});
    releases.push(release);
    return {
      released: false,
      release,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      dispatchEvent: vi.fn(),
      onrelease: null,
    } as unknown as WakeLockSentinel;
  });
  Object.defineProperty(navigator, 'wakeLock', {
    configurable: true,
    value: { request },
  });
  return { request, releases };
}

function wrapper(token = 'uptok-1') {
  return (
    <I18nProvider>
      <MemoryRouter initialEntries={[`/party/${token}/upload`]}>
        <Routes>
          <Route path="/party/:token/upload" element={<PartyUploadPage />} />
        </Routes>
      </MemoryRouter>
    </I18nProvider>
  );
}

function jpeg(name = 'photo.jpg') {
  return new File([new Uint8Array([0xff, 0xd8, 0xff])], name, { type: 'image/jpeg' });
}

function mp4(name = 'clip.mp4') {
  return new File([new Uint8Array([0, 0, 0, 24])], name, { type: 'video/mp4' });
}

function session(over: Record<string, unknown> = {}) {
  return {
    maxPhotos: null, maxVideos: null, usedPhotos: 0, usedVideos: 0,
    remainingPhotos: null, remainingVideos: null, ...over,
  };
}

describe('PartyUploadPage (public anonymous upload)', () => {
  it('offers photo-or-video and message as two explicit contributions', async () => {
    installFetchMock({
      'GET /api/party/uptok-1': () => errorResponse(404),
      'POST /api/party/uptok-1/messages': () =>
        jsonResponse({ id: 'm1', status: 'visible', createdAt: '2026-01-01T20:00:00Z' }),
    });
    render(wrapper());
    const user = userEvent.setup();

    // Media is the default: a party is still mostly photographs.
    await screen.findByLabelText(/Scegli foto e video da caricare/i);
    expect(screen.getByRole('tab', { name: /foto o video/i })).toHaveAttribute('aria-selected', 'true');

    await user.click(screen.getByRole('tab', { name: /^messaggio$/i }));

    // The message form replaces the picker rather than sitting beside it, so
    // the guest is never looking at two "send" buttons.
    expect(screen.getByLabelText(/il tuo messaggio/i)).toBeInTheDocument();
    expect(screen.queryByLabelText(/Scegli foto e video da caricare/i)).toBeNull();

    await user.type(screen.getByLabelText(/il tuo messaggio/i), 'Auguri!');
    await user.click(screen.getByRole('button', { name: /invia messaggio/i }));
    await screen.findByTestId('party-message-sent');

    // And going back to media leaves the upload path exactly as it was.
    await user.click(screen.getByRole('tab', { name: /foto o video/i }));
    expect(await screen.findByLabelText(/Scegli foto e video da caricare/i)).toBeInTheDocument();
  });

  it('locks the contribution choice while media is in flight', async () => {
    installFetchMock({ 'GET /api/party/uptok-1': () => errorResponse(404) });
    render(wrapper());
    const user = userEvent.setup();

    const input = await screen.findByLabelText(/Scegli foto e video da caricare/i);
    await user.upload(input as HTMLInputElement, [jpeg('a.jpg')]);
    await user.click(screen.getByRole('button', { name: /Carica/i }));
    await screen.findByTestId('upload-progress');

    // Switching away mid-upload would leave the queue running behind a hidden
    // progress bar, with nothing on screen saying not to close the tab.
    expect(screen.getByRole('tab', { name: /^messaggio$/i })).toBeDisabled();

    await act(async () => { MockXHR.last!.finish(); });
    await screen.findByTestId('upload-result');
    expect(screen.getByRole('tab', { name: /^messaggio$/i })).toBeEnabled();
  });

  it('shows progress + a do-not-close warning while uploading, then the result', async () => {
    installFetchMock({
      // Upload token cannot read the album header → 404 → generic page.
      'GET /api/party/uptok-1': () => errorResponse(404),
    });

    render(wrapper());
    const input = await screen.findByLabelText(/Scegli foto e video da caricare/i);
    await userEvent.setup().upload(input as HTMLInputElement, [jpeg('a.jpg'), jpeg('b.jpg')]);
    await userEvent.setup().click(screen.getByRole('button', { name: /Carica/i }));

    // In-flight: the progress bar and the "do not close" warning are shown.
    const progress = await screen.findByTestId('upload-progress');
    expect(progress).toBeInTheDocument();
    expect(screen.getByText(/Non chiudere questa schermata/i)).toBeInTheDocument();

    // Two files are two REQUESTS to the same endpoint, sent one at a time, so
    // progress is aggregated across the whole run: half of the first file is a
    // quarter of the batch.
    act(() => { MockXHR.last!.progress(50, 100); });
    expect(screen.getByText(/Caricamento 25%/i)).toBeInTheDocument();
    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '25');

    // Finishing the first request starts the second.
    await act(async () => { MockXHR.last!.finish(); });
    await waitFor(() => expect(MockXHR.sent).toHaveLength(2));
    await act(async () => { MockXHR.last!.finish(); });

    const result = await screen.findByTestId('upload-result');
    expect(result).toHaveTextContent(/Caricati 2 foto e 0 video/i);
    expect(screen.queryByTestId('upload-progress')).not.toBeInTheDocument();

    // No login or album-browsing surface on the upload page.
    expect(screen.queryByText(/sign in|log in|password/i)).not.toBeInTheDocument();
  });

  it('keeps the screen awake only while the Party upload is active', async () => {
    installFetchMock({ 'GET /api/party/uptok-1': () => errorResponse(404) });
    const wake = installWakeLock();

    render(wrapper());
    const input = await screen.findByLabelText(/Scegli foto e video da caricare/i);
    await userEvent.setup().upload(input as HTMLInputElement, [jpeg()]);
    await userEvent.setup().click(screen.getByRole('button', { name: /Carica/i }));

    await waitFor(() => expect(wake.request).toHaveBeenCalledWith('screen'));
    expect(wake.releases).toHaveLength(1);
    expect(wake.releases[0]).not.toHaveBeenCalled();

    act(() => { MockXHR.last!.finish(); });
    await screen.findByTestId('upload-result');
    await waitFor(() => expect(wake.releases[0]).toHaveBeenCalledOnce());
  });

  it('reacquires the wake lock after visibility returns during an upload', async () => {
    installFetchMock({ 'GET /api/party/uptok-1': () => errorResponse(404) });
    let visibility: DocumentVisibilityState = 'visible';
    Object.defineProperty(document, 'visibilityState', {
      configurable: true,
      get: () => visibility,
    });
    const wake = installWakeLock();

    render(wrapper());
    const input = await screen.findByLabelText(/Scegli foto e video da caricare/i);
    await userEvent.setup().upload(input as HTMLInputElement, [jpeg()]);
    await userEvent.setup().click(screen.getByRole('button', { name: /Carica/i }));
    await waitFor(() => expect(wake.request).toHaveBeenCalledTimes(1));

    visibility = 'hidden';
    document.dispatchEvent(new Event('visibilitychange'));
    await waitFor(() => expect(wake.releases[0]).toHaveBeenCalledOnce());
    visibility = 'visible';
    document.dispatchEvent(new Event('visibilitychange'));
    await waitFor(() => expect(wake.request).toHaveBeenCalledTimes(2));

    act(() => { MockXHR.last!.finish(); });
    await waitFor(() => expect(wake.releases[1]).toHaveBeenCalledOnce());
  });

  it('retries when visibility returns before the hidden request has settled', async () => {
    installFetchMock({ 'GET /api/party/uptok-1': () => errorResponse(404) });
    let visibility: DocumentVisibilityState = 'visible';
    Object.defineProperty(document, 'visibilityState', {
      configurable: true,
      get: () => visibility,
    });
    let rejectFirst!: (reason: unknown) => void;
    const first = new Promise<WakeLockSentinel>((_resolve, reject) => { rejectFirst = reject; });
    const release = vi.fn(async () => {});
    const second = {
      released: false,
      release,
      addEventListener: vi.fn(),
    } as unknown as WakeLockSentinel;
    const request = vi.fn()
      .mockReturnValueOnce(first)
      .mockResolvedValueOnce(second);
    Object.defineProperty(navigator, 'wakeLock', {
      configurable: true,
      value: { request },
    });

    render(wrapper());
    const input = await screen.findByLabelText(/Scegli foto e video da caricare/i);
    await userEvent.setup().upload(input as HTMLInputElement, [jpeg()]);
    await userEvent.setup().click(screen.getByRole('button', { name: /Carica/i }));
    expect(request).toHaveBeenCalledTimes(1);

    visibility = 'hidden';
    document.dispatchEvent(new Event('visibilitychange'));
    visibility = 'visible';
    document.dispatchEvent(new Event('visibilitychange'));
    await act(async () => { rejectFirst(new DOMException('Hidden', 'NotAllowedError')); });
    await waitFor(() => expect(request).toHaveBeenCalledTimes(2));

    act(() => { MockXHR.last!.finish(); });
    await waitFor(() => expect(release).toHaveBeenCalledOnce());
  });

  it('continues uploading when the browser denies the wake lock', async () => {
    installFetchMock({ 'GET /api/party/uptok-1': () => errorResponse(404) });
    const request = vi.fn().mockRejectedValue(new DOMException('Denied', 'NotAllowedError'));
    Object.defineProperty(navigator, 'wakeLock', {
      configurable: true,
      value: { request },
    });

    render(wrapper());
    const input = await screen.findByLabelText(/Scegli foto e video da caricare/i);
    await userEvent.setup().upload(input as HTMLInputElement, [jpeg()]);
    await userEvent.setup().click(screen.getByRole('button', { name: /Carica/i }));
    await waitFor(() => expect(request).toHaveBeenCalledOnce());
    act(() => { MockXHR.last!.finish(); });
    expect(await screen.findByTestId('upload-result')).toHaveTextContent(/Caricati 1 foto e 0 video/i);
  });

  it('shows an unavailable message when the upload link is revoked (404 on POST)', async () => {
    installFetchMock({ 'GET /api/party/uptok-1': () => errorResponse(404) });
    MockXHR.status = 404;
    MockXHR.body = '';

    render(wrapper());
    const input = await screen.findByLabelText(/Scegli foto e video da caricare/i);
    await userEvent.setup().upload(input as HTMLInputElement, [jpeg()]);
    await userEvent.setup().click(screen.getByRole('button', { name: /Carica/i }));
    act(() => { MockXHR.last!.finish(); });

    expect(await screen.findByText(/non è più disponibile/i)).toBeInTheDocument();
  });

  it('disables the upload button until a file is chosen', async () => {
    installFetchMock({
      'GET /api/party/uptok-1': () => jsonResponse({ albumName: 'Beach Party', itemCount: 0 }),
    });

    render(wrapper());
    await screen.findByLabelText(/Scegli foto e video da caricare/i);
    const button = screen.getByRole('button', { name: /Carica/i });
    expect(button).toBeDisabled();
  });

  it('accepts images AND the supported video types', async () => {
    installFetchMock({
      'GET /api/party/uptok-1': () => errorResponse(404),
      'POST /api/party/uptok-1/upload-session': () => jsonResponse(session()),
    });
    render(wrapper());
    const input = await screen.findByLabelText(/Scegli foto e video da caricare/i);
    const accept = (input as HTMLInputElement).accept;
    expect(accept).toContain('image/*');
    expect(accept).toContain('video/mp4');
    expect(accept).toContain('video/webm');
    expect(accept).toContain('video/quicktime');
  });

  it('shows the remaining photo and video quota, and unlimited when there is none', async () => {
    installFetchMock({
      'GET /api/party/uptok-1': () => errorResponse(404),
      'POST /api/party/uptok-1/upload-session': () => jsonResponse(
        session({ maxPhotos: 20, remainingPhotos: 7, usedPhotos: 13 })),
    });
    render(wrapper());
    const quota = await screen.findByTestId('upload-quota');
    expect(quota).toHaveTextContent(/Foto: 7 di 20 disponibili/i);
    // Videos are unconstrained here and must not read as "0 left".
    expect(quota).toHaveTextContent(/Video: illimitati/i);
  });

  it('warns when the selection exceeds the remaining quota for a kind', async () => {
    installFetchMock({
      'GET /api/party/uptok-1': () => errorResponse(404),
      'POST /api/party/uptok-1/upload-session': () => jsonResponse(
        session({ maxPhotos: 5, remainingPhotos: 1, maxVideos: 5, remainingVideos: 2 })),
    });
    render(wrapper());
    const input = await screen.findByLabelText(/Scegli foto e video da caricare/i);
    await userEvent.setup().upload(input as HTMLInputElement, [jpeg('a.jpg'), jpeg('b.jpg')]);

    expect(await screen.findByText(/più foto di quante puoi ancora caricare/i)).toBeInTheDocument();
    // The video selection is within quota, so no video warning appears.
    expect(screen.queryByText(/più video di quanti puoi ancora caricare/i)).not.toBeInTheDocument();
  });

  it('stops sending a kind the server reports as full, and keeps sending the other', async () => {
    installFetchMock({
      'GET /api/party/uptok-1': () => errorResponse(404),
      'POST /api/party/uptok-1/upload-session': () => jsonResponse(session()),
    });
    // The FIRST response says photos are now full. The queue must not send the
    // second photo, but must still send the video — the quotas are independent.
    MockXHR.body = JSON.stringify({
      accepted: 1, rejected: 0, acceptedPhotos: 1, acceptedVideos: 0,
      quotaRejectedPhotos: 0, quotaRejectedVideos: 0,
      remainingPhotos: 0, remainingVideos: null,
    });
    MockXHR.autoFinish = true;

    render(wrapper());
    const input = await screen.findByLabelText(/Scegli foto e video da caricare/i);
    await userEvent.setup().upload(
      input as HTMLInputElement, [jpeg('a.jpg'), jpeg('b.jpg'), mp4('c.mp4')]);
    await userEvent.setup().click(screen.getByRole('button', { name: /Carica/i }));

    await screen.findByTestId('upload-result');
    // Three files selected, but only two requests: the second photo was never
    // sent because the server had already said there was no room for it.
    expect(MockXHR.sent).toHaveLength(2);
    expect(screen.getByTestId('upload-result')).toHaveTextContent(/limite di foto/i);
  });

  it('reports a server-side quota rejection that the client could not predict', async () => {
    installFetchMock({
      'GET /api/party/uptok-1': () => errorResponse(404),
      // The page believes there is room…
      'POST /api/party/uptok-1/upload-session': () => jsonResponse(
        session({ maxPhotos: 5, remainingPhotos: 5 })),
    });
    // …but another tab (or a lowered quota) got there first.
    MockXHR.body = JSON.stringify({
      accepted: 0, rejected: 1, acceptedPhotos: 0, acceptedVideos: 0,
      quotaRejectedPhotos: 1, quotaRejectedVideos: 0,
      remainingPhotos: 0, remainingVideos: null,
    });
    MockXHR.autoFinish = true;

    render(wrapper());
    const input = await screen.findByLabelText(/Scegli foto e video da caricare/i);
    await userEvent.setup().upload(input as HTMLInputElement, [jpeg('a.jpg')]);
    await userEvent.setup().click(screen.getByRole('button', { name: /Carica/i }));

    const result = await screen.findByTestId('upload-result');
    expect(result).toHaveTextContent(/limite di foto/i);
  });

  it('reports a mixed batch as photos AND videos', async () => {
    installFetchMock({
      'GET /api/party/uptok-1': () => errorResponse(404),
      'POST /api/party/uptok-1/upload-session': () => jsonResponse(session()),
    });
    MockXHR.autoFinish = true;
    MockXHR.bodies = [
      JSON.stringify({ accepted: 1, rejected: 0, acceptedPhotos: 1, acceptedVideos: 0, remainingPhotos: null, remainingVideos: null }),
      JSON.stringify({ accepted: 1, rejected: 0, acceptedPhotos: 0, acceptedVideos: 1, remainingPhotos: null, remainingVideos: null }),
    ];

    render(wrapper());
    const input = await screen.findByLabelText(/Scegli foto e video da caricare/i);
    await userEvent.setup().upload(input as HTMLInputElement, [jpeg('a.jpg'), mp4('c.mp4')]);
    await userEvent.setup().click(screen.getByRole('button', { name: /Carica/i }));

    const result = await screen.findByTestId('upload-result');
    expect(result).toHaveTextContent(/Caricati 1 foto e 1 video/i);
  });

  it('still lets the guest upload when the session probe fails', async () => {
    installFetchMock({
      'GET /api/party/uptok-1': () => errorResponse(404),
      // The upload endpoint resolves the session itself, so a failed probe must
      // not block uploading — it only removes the quota header.
      'POST /api/party/uptok-1/upload-session': () => errorResponse(500),
    });
    render(wrapper());
    const input = await screen.findByLabelText(/Scegli foto e video da caricare/i);
    expect(screen.queryByTestId('upload-quota')).not.toBeInTheDocument();
    await userEvent.setup().upload(input as HTMLInputElement, [jpeg('a.jpg')]);
    expect(screen.getByRole('button', { name: /Carica/i })).toBeEnabled();
  });

});
