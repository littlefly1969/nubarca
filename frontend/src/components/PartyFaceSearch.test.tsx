import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  act, cleanup, render, screen, waitFor, within,
} from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import {
  PartyFaceSearch, downscaleSelfie, FACE_CONFIRM_MS, type PartyFaceFilter,
} from './PartyFaceSearch';
import { installFetchMock, jsonResponse } from '../test-utils';
import { SCAN_MIN_PASSES, SCAN_PASS_MS } from './useFaceScan';
import { I18nProvider } from '../i18n';

/**
 * THE FLOW UNDER TEST is a sequence, so these tests walk it:
 *
 *   camera → shutter → detecting_face → face_confirmed → scanning → results
 *
 * Every step is mocked at its own seam — the CAMERA (getUserMedia + the canvas
 * the capture draws into), the DETECTOR (`/face-search/detect`), the SEARCH
 * (`/face-search`) and the CLOCK — so a test can hold the detector open, or
 * answer the search in eight milliseconds, and assert what the guest sees in
 * between. None of them is a seam the product itself has: the component knows
 * nothing about any of this.
 */

/**
 * Ask for no motion.
 *
 * This stills the sweep; it does NOT skip the wait, which applies to everyone.
 * These tests still sit through it, because that is what a guest does.
 */
function stillnessPlease() {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query.includes('prefers-reduced-motion'),
    media: query,
    addEventListener: () => {},
    removeEventListener: () => {},
    addListener: () => {},
    removeListener: () => {},
    onchange: null,
    dispatchEvent: () => false,
  }));
}

/** Tracks what the camera was asked for, and whether it was handed back. */
interface CameraSpy {
  requests: MediaStreamConstraints[];
  stopped: number;
}

/**
 * A working front camera.
 *
 * jsdom has no `getUserMedia`, no `videoWidth` and no `canvas.toBlob`, so all
 * three are stubbed: the stream's tracks count their own `stop()` (that is how
 * "the camera light goes out" is asserted), the video element reports a real
 * size, and the canvas hands back bytes.
 */
function installCamera({ deny = false, absent = false } = {}): CameraSpy {
  const spy: CameraSpy = { requests: [], stopped: 0 };

  // `mediaDevices` is defined ON the real navigator rather than replacing it:
  // a substituted navigator object is not a Navigator instance, and every other
  // getter on it (`languages`, which the i18n provider reads) then throws.
  if (absent) {
    Object.defineProperty(navigator, 'mediaDevices', {
      value: undefined, configurable: true,
    });
    return spy;
  }

  const track = { stop: () => { spy.stopped += 1; }, kind: 'video' };
  const stream = { getTracks: () => [track] } as unknown as MediaStream;
  const getUserMedia = vi.fn(async (constraints: MediaStreamConstraints) => {
    spy.requests.push(constraints);
    if (deny) {
      const err = new Error('denied');
      err.name = 'NotAllowedError';
      throw err;
    }
    return stream;
  });
  Object.defineProperty(navigator, 'mediaDevices', {
    value: { getUserMedia }, configurable: true,
  });

  // A <video> that claims to be showing something, and a canvas that can
  // encode it.
  Object.defineProperty(HTMLVideoElement.prototype, 'videoWidth', {
    configurable: true, get: () => 720,
  });
  Object.defineProperty(HTMLVideoElement.prototype, 'videoHeight', {
    configurable: true, get: () => 960,
  });
  HTMLVideoElement.prototype.play = vi.fn(async () => {});
  HTMLCanvasElement.prototype.getContext = vi.fn(() => ({
    drawImage: () => {},
  })) as unknown as HTMLCanvasElement['getContext'];
  HTMLCanvasElement.prototype.toBlob = function toBlob(callback: BlobCallback) {
    callback(new Blob([new Uint8Array([1, 2, 3])], { type: 'image/jpeg' }));
  };

  return spy;
}

beforeEach(() => {
  stillnessPlease();
  vi.stubGlobal('URL', Object.assign(Object.create(URL), {
    createObjectURL: () => 'blob:selfie',
    revokeObjectURL: vi.fn(),
  }));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  window.localStorage.clear();
  window.history.replaceState({}, '', '/');
});

interface Handlers {
  onFilterChange?: (f: PartyFaceFilter | null) => void;
  onCancelSearch?: (searchId: string | null) => void;
  onOpenChange?: (open: boolean) => void;
  onShowResults?: () => void;
  open?: boolean;
}

// The sheet is CONTROLLED by the page: there is no launcher inside it. This
// harness plays the page, so every test exercises the same contract the real
// page uses.
function renderSheet(handlers: Handlers = {}) {
  return render(
    <I18nProvider>
      <PartyFaceSearch
        token="tok-1"
        open={handlers.open ?? true}
        onOpenChange={handlers.onOpenChange ?? (() => {})}
        onFilterChange={handlers.onFilterChange ?? (() => {})}
        onCancelSearch={handlers.onCancelSearch ?? (() => {})}
        onShowResults={handlers.onShowResults ?? (() => {})}
      />
    </I18nProvider>,
  );
}

const FACE = { x: 0.3, y: 0.25, width: 0.25, height: 0.3 };

const readyBody = {
  status: 'ready',
  searchId: 's1',
  resultCount: 1,
  face: FACE,
  items: [
    {
      id: 'f1', mediaType: 'image',
      thumbnailUrl: '/api/party/tok-1/media/f1/thumbnail',
      previewUrl: '/api/party/tok-1/media/f1/preview',
      downloadUrl: '/api/party/tok-1/media/f1/download',
    },
  ],
};

const DETECT = 'POST /api/party/tok-1/face-search/detect';
const SEARCH = 'POST /api/party/tok-1/face-search';

/** Wait until the camera is live and press the shutter. */
async function shoot() {
  const shutter = await screen.findByTestId('party-face-shutter');
  await waitFor(() => expect(shutter).not.toBeDisabled());
  await userEvent.setup().click(shutter);
}

/**
 * Walk the whole flow and sit through the scan, exactly as a guest does.
 *
 * Waiting for the SWEEP to appear first matters: the face lands half a second
 * before the line starts, so a wait that only looked for the line's absence
 * would pass instantly, before anything had happened.
 */
async function completeSearch() {
  await shoot();
  await waitFor(
    () => expect(screen.getByTestId('party-face-scanline')).toBeInTheDocument(),
    { timeout: FACE_CONFIRM_MS * 4 },
  );
  await waitFor(
    () => expect(screen.queryByTestId('party-face-scanline')).toBeNull(),
    { timeout: SCAN_PASS_MS * (SCAN_MIN_PASSES + 3) },
  );
}

describe('PartyFaceSearch (public "find your photos")', () => {
  // Every search sits through three sweeps by design, so the default timeout is
  // not enough room for a test that then goes on to assert something.
  vi.setConfig({ testTimeout: 30_000 });

  it('is an accessible dialog named by its title, with no launcher of its own', async () => {
    installCamera();
    installFetchMock({});
    renderSheet();
    const dialog = screen.getByRole('dialog');
    expect(dialog).toHaveAttribute('aria-modal', 'true');
    expect(within(dialog).getByRole('heading', { name: 'Trova le tue foto' })).toBeInTheDocument();
    expect(screen.queryByTestId('party-face-open')).not.toBeInTheDocument();
    expect(dialog.contains(document.activeElement)).toBe(true);
  });

  it('opens the FRONT camera, and asks for no microphone', async () => {
    const camera = installCamera();
    installFetchMock({});
    renderSheet();

    await waitFor(() => expect(camera.requests).toHaveLength(1));
    const video = camera.requests[0].video as MediaTrackConstraints;
    expect(video.facingMode).toBe('user');
    // A search for your own face has no business listening to the room.
    expect(camera.requests[0].audio).toBe(false);
  });

  it('renders the face-search UI localized in Italian by default', async () => {
    installCamera();
    installFetchMock({});
    renderSheet();
    expect(await screen.findByText('Inquadra il tuo viso e scatta.')).toBeInTheDocument();
    // Privacy is stated up front, in the words the backend actually guarantees.
    expect(screen.getByText(
      'Il selfie non viene salvato. Finché la ricerca resta attiva viene conservato'
      + ' temporaneamente solo un piccolo ritaglio del volto.',
    )).toBeInTheDocument();
  });

  it('shows English copy when the language is English', async () => {
    window.localStorage.setItem('nubarca.lang', 'en');
    installCamera();
    installFetchMock({});
    renderSheet();
    expect(await screen.findByText('Frame your face and take the shot.')).toBeInTheDocument();
  });

  it('says so when the camera is refused, and offers the phone’s own camera', async () => {
    installCamera({ deny: true });
    installFetchMock({});
    renderSheet();

    expect(await screen.findByTestId('party-face-camera-error')).toHaveTextContent(
      /Non abbiamo il permesso di usare la fotocamera/);
    // A refusal is not a dead end: the operating system's camera still works,
    // through a real, focusable input behind the label.
    const input = screen.getByTestId('party-face-input');
    expect(input).toHaveAttribute('accept', 'image/*');
    expect(input).toHaveAttribute('capture', 'user');
    expect(screen.getByTestId('party-face-pick')).toHaveAttribute('for', 'party-face-file');
  });

  it('tells a browser with no camera apart from a person who said no', async () => {
    installCamera({ absent: true });
    installFetchMock({});
    renderSheet();

    expect(await screen.findByTestId('party-face-camera-error')).toHaveTextContent(
      /Questo browser non ci lascia aprire la fotocamera/);
  });

  it('DETECTS before it searches, and releases the camera at the shutter', async () => {
    const camera = installCamera();
    const order: string[] = [];
    installFetchMock({
      [DETECT]: () => { order.push('detect'); return jsonResponse({ status: 'found', face: FACE }); },
      [SEARCH]: () => { order.push('search'); return jsonResponse(readyBody); },
    });
    renderSheet();
    await completeSearch();

    // Detection first, and only then a search. That ordering is the whole
    // reason a face can be shown before the results are.
    expect(order).toEqual(['detect', 'search']);
    // The light goes out when the frame freezes, not when the sheet closes.
    expect(camera.stopped).toBeGreaterThan(0);
  });

  it('frames the DETECTED face, using the box the search will use', async () => {
    installCamera();
    installFetchMock({
      [DETECT]: () => jsonResponse({ status: 'found', face: FACE }),
      [SEARCH]: () => jsonResponse(readyBody),
    });
    renderSheet();
    await shoot();

    // The tile holds the guest's own frozen frame and crops to the face the
    // detector returned — a crop of the bytes the search is about to receive,
    // not a box drawn over a camera that is still moving.
    await waitFor(() =>
      expect(screen.getByTestId('party-face-scanner')).toHaveAttribute('data-framed', 'face'));
    const img = screen.getByTestId('party-face-scanner-img');
    expect(img).toHaveAttribute('src', 'blob:selfie');
    expect(img.getAttribute('style')).toMatch(/width:/);
  });

  it('refuses a selfie with no face WITHOUT ever calling the search', async () => {
    let searched = 0;
    installCamera();
    installFetchMock({
      [DETECT]: () => jsonResponse({ status: 'no_face' }),
      [SEARCH]: () => { searched += 1; return jsonResponse(readyBody); },
    });
    renderSheet();
    await shoot();

    expect(await screen.findByTestId('party-face-noface')).toHaveTextContent(
      /Non riusciamo a vedere bene il tuo volto/);
    // THE POINT OF THE DETECT STEP: nothing was embedded, matched or recorded.
    expect(searched).toBe(0);
    expect(screen.getByTestId('party-face-retry')).toBeInTheDocument();
  });

  it('refuses a crowded selfie WITHOUT ever calling the search', async () => {
    let searched = 0;
    installCamera();
    installFetchMock({
      [DETECT]: () => jsonResponse({ status: 'multiple_faces' }),
      [SEARCH]: () => { searched += 1; return jsonResponse(readyBody); },
    });
    renderSheet();
    await shoot();

    expect(await screen.findByTestId('party-face-multiple')).toHaveTextContent(
      /Nel selfie ci sono più persone/);
    // Guessing which of several faces belongs to the guest is the one failure
    // this feature cannot accept, so it does not guess and does not search.
    expect(searched).toBe(0);
  });

  it('retry goes back to the camera and asks for it again', async () => {
    const camera = installCamera();
    installFetchMock({ [DETECT]: () => jsonResponse({ status: 'no_face' }) });
    renderSheet();
    await shoot();

    await screen.findByTestId('party-face-retry');
    await userEvent.setup().click(screen.getByTestId('party-face-retry'));

    expect(await screen.findByTestId('party-face-viewfinder')).toBeInTheDocument();
    // A second stream, because the first was released at the first shutter.
    await waitFor(() => expect(camera.requests.length).toBeGreaterThanOrEqual(2));
  });

  it('a completed search filters only the phone and offers no TV activation', async () => {
    const filters: (PartyFaceFilter | null)[] = [];
    installCamera();
    const mock = installFetchMock({
      [DETECT]: () => jsonResponse({ status: 'found', face: FACE }),
      [SEARCH]: () => jsonResponse(readyBody),
    });
    renderSheet({ onFilterChange: (f) => filters.push(f) });
    await completeSearch();

    expect(await screen.findByTestId('party-face-count')).toHaveTextContent('1 foto trovata');
    expect(filters.at(-1)).toEqual({ searchId: 's1', itemIds: ['f1'] });
    expect(screen.queryByTestId('party-face-show-tv')).not.toBeInTheDocument();
    expect(mock.calls.some((c) => c.url.includes('/tv'))).toBe(false);
  });

  it('holds an instant answer until the face has been swept three times', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      installCamera();
      installFetchMock({
        [DETECT]: () => jsonResponse({ status: 'found', face: FACE }),
        [SEARCH]: () => jsonResponse(readyBody),
      });
      renderSheet();
      await shoot();

      // The face lands, then the sweep starts.
      await waitFor(() => expect(screen.getByTestId('party-face-step'))
        .toHaveTextContent('Ecco il tuo volto.'));
      await act(async () => { vi.advanceTimersByTime(FACE_CONFIRM_MS); });
      await waitFor(() => expect(screen.getByTestId('party-face-scanline')).toBeInTheDocument());

      // Two sweeps in, the answer is already here and is NOT being shown.
      await act(async () => { vi.advanceTimersByTime(SCAN_PASS_MS * 2); });
      expect(screen.queryByTestId('party-face-count')).toBeNull();
      expect(screen.getByTestId('party-face-scanline')).toBeInTheDocument();

      await act(async () => { vi.advanceTimersByTime(SCAN_PASS_MS); });
      await waitFor(() => expect(screen.getByTestId('party-face-count')).toBeInTheDocument());
    } finally {
      vi.useRealTimers();
    }
  });

  it('keeps sweeping past three when the answer is slow', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let release: ((value: Response) => void) | null = null;
    try {
      installCamera();
      installFetchMock({
        [DETECT]: () => jsonResponse({ status: 'found', face: FACE }),
        [SEARCH]: () => new Promise<Response>((resolve) => { release = resolve; }),
      });
      renderSheet();
      await shoot();
      await act(async () => { vi.advanceTimersByTime(FACE_CONFIRM_MS); });
      await waitFor(() => expect(screen.getByTestId('party-face-scanline')).toBeInTheDocument());

      // Six sweeps, and the line is still going: the minimum is a floor, not a
      // schedule, and nothing here pretends to know how far along the search is.
      await act(async () => { vi.advanceTimersByTime(SCAN_PASS_MS * 6); });
      expect(screen.getByTestId('party-face-scanline')).toBeInTheDocument();
      expect(screen.getByTestId('party-face-passes'))
        .toHaveAttribute('data-passes', expect.stringMatching(/^[6-9]$/));
      expect(screen.queryByTestId('party-face-count')).toBeNull();

      await act(async () => { release!(jsonResponse(readyBody)); });
      await waitFor(() => expect(screen.getByTestId('party-face-count')).toBeInTheDocument());
    } finally {
      vi.useRealTimers();
    }
  });

  it('shows a short generic error, never a status code or API detail', async () => {
    installCamera();
    installFetchMock({
      [DETECT]: () => jsonResponse({ status: 'found', face: FACE }),
      [SEARCH]: () => new Response('boom', { status: 500 }),
    });
    renderSheet();
    await shoot();

    const error = await screen.findByTestId('party-face-error', {}, { timeout: 5_000 });
    expect(error).toHaveTextContent('Impossibile completare la ricerca. Riprova.');
    expect(document.body.textContent).not.toMatch(/500|boom/);
    // The sweep stops: a failure is not a wait to sit through.
    expect(screen.queryByTestId('party-face-scanline')).toBeNull();
    expect(screen.getByTestId('party-face-retry')).toBeInTheDocument();
  });

  it('localizes the unavailable state (capability off / 503)', async () => {
    installCamera();
    installFetchMock({
      [DETECT]: () => jsonResponse({ status: 'found', face: FACE }),
      // 503 with the safe DTO in the body: the client normalises it back into
      // a response the sheet renders as a state, rather than throwing.
      [SEARCH]: () => jsonResponse(
        { status: 'unavailable', searchId: null, resultCount: 0, items: [] }, 503,
      ),
    });
    renderSheet();
    await completeSearch();

    expect(await screen.findByTestId('party-face-unavailable'))
      .toHaveTextContent('La ricerca per volto non è ancora disponibile.');
  });

  it('an empty result stays local and applies no filter', async () => {
    const filters: (PartyFaceFilter | null)[] = [];
    installCamera();
    installFetchMock({
      [DETECT]: () => jsonResponse({ status: 'found', face: FACE }),
      [SEARCH]: () => jsonResponse({ ...readyBody, resultCount: 0, items: [] }),
    });
    renderSheet({ onFilterChange: (f) => filters.push(f) });
    await completeSearch();

    expect(await screen.findByTestId('party-face-empty')).toBeInTheDocument();
    // Nothing matched, so nothing is filtered — never an empty album.
    expect(filters.filter(Boolean)).toHaveLength(0);
  });

  it('"Cancel search" clears the local filter, drops the search and reopens the camera', async () => {
    const cancelled: (string | null)[] = [];
    const filters: (PartyFaceFilter | null)[] = [];
    installCamera();
    installFetchMock({
      [DETECT]: () => jsonResponse({ status: 'found', face: FACE }),
      [SEARCH]: () => jsonResponse(readyBody),
    });
    renderSheet({
      onCancelSearch: (id) => cancelled.push(id),
      onFilterChange: (f) => filters.push(f),
    });
    await completeSearch();
    await screen.findByTestId('party-face-count');

    await userEvent.setup().click(screen.getByTestId('party-face-cancel'));
    expect(cancelled).toEqual(['s1']);
    expect(filters.at(-1)).toBeNull();
    expect(await screen.findByTestId('party-face-viewfinder')).toBeInTheDocument();
  });

  it('cancelling MID-SWEEP forgets the scan and releases everything', async () => {
    const cancelled: (string | null)[] = [];
    installCamera();
    installFetchMock({
      [DETECT]: () => jsonResponse({ status: 'found', face: FACE }),
      [SEARCH]: () => new Promise<Response>(() => { /* never answers */ }),
    });
    renderSheet({ onCancelSearch: (id) => cancelled.push(id) });
    await shoot();
    await waitFor(() => expect(screen.getByTestId('party-face-cancel')).toBeInTheDocument());

    await userEvent.setup().click(screen.getByTestId('party-face-cancel'));

    // Back to the camera, with no sweep left running over a request nobody is
    // waiting for any more.
    expect(await screen.findByTestId('party-face-viewfinder')).toBeInTheDocument();
    expect(screen.queryByTestId('party-face-scanline')).toBeNull();
    // No search id to drop: the search never completed.
    expect(cancelled).toEqual([null]);
  });

  it('"See my photos" closes the sheet and KEEPS the filter applied', async () => {
    const opens: boolean[] = [];
    let shown = 0;
    installCamera();
    installFetchMock({
      [DETECT]: () => jsonResponse({ status: 'found', face: FACE }),
      [SEARCH]: () => jsonResponse(readyBody),
    });
    renderSheet({ onOpenChange: (o) => opens.push(o), onShowResults: () => { shown += 1; } });
    await completeSearch();
    await screen.findByTestId('party-face-count');

    await userEvent.setup().click(screen.getByTestId('party-face-show-results'));
    expect(opens.at(-1)).toBe(false);
    expect(shown).toBe(1);
  });

  it('a response arriving after the sheet is closed can never apply a stale filter', async () => {
    const filters: (PartyFaceFilter | null)[] = [];
    let release: ((value: Response) => void) | null = null;
    installCamera();
    installFetchMock({
      [DETECT]: () => jsonResponse({ status: 'found', face: FACE }),
      [SEARCH]: () => new Promise<Response>((resolve) => { release = resolve; }),
    });
    const view = renderSheet({ onFilterChange: (f) => filters.push(f) });
    await shoot();
    await waitFor(() => expect(screen.getByTestId('party-face-cancel')).toBeInTheDocument());

    view.unmount();
    await act(async () => { release?.(jsonResponse(readyBody)); });

    // The guest walked away; the answer to a question they withdrew changes
    // nothing.
    expect(filters.filter(Boolean)).toHaveLength(0);
  });

  it('releases the camera and the selfie when the sheet closes', async () => {
    const camera = installCamera();
    installFetchMock({ [DETECT]: () => jsonResponse({ status: 'found', face: FACE }) });
    const view = renderSheet();
    await waitFor(() => expect(camera.requests).toHaveLength(1));

    view.unmount();
    // The camera light goes out with the overlay: the visible half of "the
    // selfie is a query and nothing else".
    expect(camera.stopped).toBeGreaterThan(0);
  });

  it('never renders face/person/score internals', async () => {
    installCamera();
    installFetchMock({
      [DETECT]: () => jsonResponse({ status: 'found', face: FACE }),
      [SEARCH]: () => jsonResponse(readyBody),
    });
    renderSheet();
    await completeSearch();
    await screen.findByTestId('party-face-count');

    const text = document.body.textContent ?? '';
    for (const leak of ['similarity', 'score', 'personId', 'faceId', 'embedding', 'vector']) {
      expect(text).not.toContain(leak);
    }
  });
});

describe('downscaleSelfie', () => {
  it('hands the original back when the browser cannot decode it', async () => {
    // No createImageBitmap in this environment: the fallback path must return
    // the file unchanged rather than throwing at the guest.
    const file = new File([new Uint8Array([1, 2, 3])], 'selfie.png', { type: 'image/png' });
    vi.stubGlobal('createImageBitmap', undefined);
    await expect(downscaleSelfie(file)).resolves.toBe(file);
  });
});
