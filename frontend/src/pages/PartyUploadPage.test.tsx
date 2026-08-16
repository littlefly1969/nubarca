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
  static body = '{"accepted":2,"rejected":1}';
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
  send() { MockXHR.last = this; }
  abort() { this.onabort?.(); }
  progress(loaded: number, total: number) { this.upload.onprogress?.({ lengthComputable: true, loaded, total }); }
  finish() { this.status = MockXHR.status; this.responseText = MockXHR.body; this.onload?.(); }
}

beforeEach(() => {
  MockXHR.last = null;
  MockXHR.status = 200;
  MockXHR.body = '{"accepted":2,"rejected":1}';
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

describe('PartyUploadPage (public anonymous upload)', () => {
  it('shows progress + a do-not-close warning while uploading, then the result', async () => {
    installFetchMock({
      // Upload token cannot read the album header → 404 → generic page.
      'GET /api/party/uptok-1': () => errorResponse(404),
    });

    render(wrapper());
    const input = await screen.findByLabelText(/Scegli le foto da caricare/i);
    await userEvent.setup().upload(input as HTMLInputElement, [jpeg('a.jpg'), jpeg('b.jpg')]);
    await userEvent.setup().click(screen.getByRole('button', { name: /Carica/i }));

    // In-flight: the progress bar and the "do not close" warning are shown.
    const progress = await screen.findByTestId('upload-progress');
    expect(progress).toBeInTheDocument();
    expect(screen.getByText(/Non chiudere questa schermata/i)).toBeInTheDocument();

    // A progress event moves the bar / label.
    act(() => { MockXHR.last!.progress(50, 100); });
    expect(screen.getByText(/Caricamento 50%/i)).toBeInTheDocument();
    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '50');

    // Completing the request clears the progress and shows the counts.
    act(() => { MockXHR.last!.finish(); });
    const result = await screen.findByTestId('upload-result');
    expect(result).toHaveTextContent(/Caricate 2 foto/i);
    expect(result).toHaveTextContent(/1 è stata rifiutata/i);
    expect(screen.queryByTestId('upload-progress')).not.toBeInTheDocument();

    // No login or album-browsing surface on the upload page.
    expect(screen.queryByText(/sign in|log in|password/i)).not.toBeInTheDocument();
  });

  it('keeps the screen awake only while the Party upload is active', async () => {
    installFetchMock({ 'GET /api/party/uptok-1': () => errorResponse(404) });
    const wake = installWakeLock();

    render(wrapper());
    const input = await screen.findByLabelText(/Scegli le foto da caricare/i);
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
    const input = await screen.findByLabelText(/Scegli le foto da caricare/i);
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
    const input = await screen.findByLabelText(/Scegli le foto da caricare/i);
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
    const input = await screen.findByLabelText(/Scegli le foto da caricare/i);
    await userEvent.setup().upload(input as HTMLInputElement, [jpeg()]);
    await userEvent.setup().click(screen.getByRole('button', { name: /Carica/i }));
    await waitFor(() => expect(request).toHaveBeenCalledOnce());
    act(() => { MockXHR.last!.finish(); });
    expect(await screen.findByTestId('upload-result')).toHaveTextContent(/Caricate 2 foto/i);
  });

  it('shows an unavailable message when the upload link is revoked (404 on POST)', async () => {
    installFetchMock({ 'GET /api/party/uptok-1': () => errorResponse(404) });
    MockXHR.status = 404;
    MockXHR.body = '';

    render(wrapper());
    const input = await screen.findByLabelText(/Scegli le foto da caricare/i);
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
    await screen.findByLabelText(/Scegli le foto da caricare/i);
    const button = screen.getByRole('button', { name: /Carica/i });
    expect(button).toBeDisabled();
  });
});
