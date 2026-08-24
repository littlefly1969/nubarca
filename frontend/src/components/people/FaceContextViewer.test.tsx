import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { FaceContextViewer, type FaceReviewControls } from './FaceContextViewer';
import { AuthedWrapper, installFetchMock, jsonResponse, type MockHandler } from '../../test-utils';

// The stage is a measured box and the canvas is the bitmap's contain-fit inside
// it; jsdom reports 0 for every rect and 0 for naturalWidth, so both are stubbed
// here exactly as the media-wall suites stub their container width. Without
// them the viewer is correct to draw no boxes at all — see faceViewerGeometry.
const STAGE = { width: 1000, height: 800 };
const NATURAL = { width: 4000, height: 3000 };

beforeEach(() => {
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(
    () => ({
      width: STAGE.width, height: STAGE.height, top: 0, left: 0,
      right: STAGE.width, bottom: STAGE.height, x: 0, y: 0, toJSON: () => ({}),
    }) as DOMRect,
  );
  globalThis.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
  Object.defineProperty(HTMLImageElement.prototype, 'naturalWidth', {
    configurable: true, get: () => NATURAL.width,
  });
  Object.defineProperty(HTMLImageElement.prototype, 'naturalHeight', {
    configurable: true, get: () => NATURAL.height,
  });
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const b = (x: number) => ({ x, y: 0.2, width: 0.15, height: 0.15 });

function context(
  faceId: string,
  personName: string | null = null,
  over: Record<string, unknown> = {},
): MockHandler {
  return () =>
    jsonResponse({
      fileItemId: 'file-1',
      fileName: 'crowd.jpg',
      selectedFaceId: faceId,
      selectedBox: faceId === 'f-2' ? b(0.6) : b(0.2),
      faces: [
        { faceId: 'f-1', box: b(0.2) },
        { faceId: 'f-2', box: b(0.6) },
      ],
      personId: personName ? 'p-1' : null,
      personName,
      isIgnored: false,
      effectiveDateTaken: '2019-07-14T10:30:00Z',
      effectiveDateTakenSource: 'embedded',
      ...over,
    });
}

function renderViewer(
  faceIds: string[],
  index: number,
  onIndexChange = vi.fn(),
  onClose = vi.fn(),
  reviewControls?: FaceReviewControls,
) {
  return {
    onIndexChange,
    onClose,
    ...render(
      <AuthedWrapper>
        <MemoryRouter>
          <FaceContextViewer
            faceIds={faceIds}
            index={index}
            onIndexChange={onIndexChange}
            onClose={onClose}
            reviewControls={reviewControls}
          />
        </MemoryRouter>
      </AuthedWrapper>,
    ),
  };
}

/** The image has to report it loaded before the canvas has a real size. */
async function settled(): Promise<void> {
  const img = await screen.findByRole('img');
  fireEvent.load(img);
  await waitFor(() => expect(screen.getAllByTestId('face-viewer-box').length).toBeGreaterThan(0));
}

function reviewControls(over: Partial<FaceReviewControls> = {}): FaceReviewControls {
  return {
    progressLabel: 'Volto 1 di 3',
    canSkipFace: true,
    onSkipFace: vi.fn(),
    canNextPhoto: true,
    onNextPhoto: vi.fn(),
    onIgnoreRemaining: vi.fn(),
    ignoreRemainingBusy: false,
    ...over,
  };
}

// ---------------------------------------------------------------- the header

describe('the header says which photo this is', () => {
  it('shows the file name and the capture date', async () => {
    installFetchMock({ 'GET /api/people/faces/f-1/context': context('f-1', 'Alice') });
    renderViewer(['f-1', 'f-2'], 0);

    expect(await screen.findByTestId('face-viewer-file-name')).toHaveTextContent('crowd.jpg');
    expect(screen.getByTestId('face-viewer-date')).toHaveTextContent(/^Scattata il /);
  });

  it('says "Caricata il" when the date is only when the file arrived', async () => {
    // An uploaded-source date is an arrival time. Captioning it "Scattata il"
    // would state something false about the photograph.
    installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1', null, {
        effectiveDateTakenSource: 'uploaded',
      }),
    });
    renderViewer(['f-1'], 0);

    expect(await screen.findByTestId('face-viewer-date')).toHaveTextContent(/^Caricata il /);
  });

  it('says "Scattata il" for an owner-typed date as well as an embedded one', async () => {
    installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1', null, {
        effectiveDateTakenSource: 'user',
      }),
    });
    renderViewer(['f-1'], 0);

    expect(await screen.findByTestId('face-viewer-date')).toHaveTextContent(/^Scattata il /);
  });

  it('carries no face action at all', async () => {
    installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1'),
      'GET /api/people': () => jsonResponse([]),
    });
    renderViewer(['f-1'], 0, vi.fn(), vi.fn(), reviewControls());
    await screen.findByTestId('face-viewer-file-name');

    // Identity only: everything operable lives in the bottom bar.
    const header = document.querySelector('.face-viewer-top')!;
    expect(within(header as HTMLElement).queryByTestId('face-viewer-next-photo')).toBeNull();
    expect(within(header as HTMLElement).queryByTestId('face-viewer-ignore')).toBeNull();
    expect(within(header as HTMLElement).queryByTestId('face-viewer-ignore-all')).toBeNull();
    expect(within(header as HTMLElement).queryByTestId('face-viewer-skip')).toBeNull();
  });
});

// ------------------------------------------------------------ action grouping

describe('the bottom bar groups by consequence', () => {
  it('puts photo navigation and view tools in the viewport group', async () => {
    installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1'),
      'GET /api/people': () => jsonResponse([]),
    });
    renderViewer(['f-1'], 0, vi.fn(), vi.fn(), reviewControls());
    await screen.findByTestId('face-viewer-file-name');

    const tools = document.querySelector('.face-viewer-tools') as HTMLElement;
    // "Foto successiva" changes WHAT is on screen and decides no face, so it
    // belongs here rather than among the decisions.
    expect(within(tools).getByTestId('face-viewer-next-photo')).toBeTruthy();
    expect(within(tools).getByTestId('face-viewer-fit')).toBeTruthy();
    expect(within(tools).getByTestId('face-viewer-focus')).toBeTruthy();
    expect(within(tools).getByLabelText('Zoom')).toBeTruthy();
  });

  it('puts every face decision in the decisions group', async () => {
    installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1'),
      'GET /api/people': () => jsonResponse([]),
    });
    renderViewer(['f-1'], 0, vi.fn(), vi.fn(), reviewControls());
    await screen.findByTestId('face-viewer-file-name');

    const decisions = document.querySelector('.face-viewer-decisions') as HTMLElement;
    expect(within(decisions).getByTestId('face-viewer-skip')).toBeTruthy();
    expect(within(decisions).getByTestId('face-viewer-ignore')).toBeTruthy();
    expect(within(decisions).getByTestId('face-viewer-ignore-all')).toBeTruthy();
    expect(within(decisions).getByRole('button', { name: 'Assegna persona' })).toBeTruthy();
  });

  it('keeps no overflow menu behind', async () => {
    // Nothing is left to put in one, and an empty "…" advertises a surface that
    // does not exist.
    installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1'),
      'GET /api/people': () => jsonResponse([]),
    });
    renderViewer(['f-1'], 0, vi.fn(), vi.fn(), reviewControls());
    await screen.findByTestId('face-viewer-file-name');

    expect(screen.queryByTestId('face-viewer-more')).toBeNull();
    expect(screen.queryByTestId('face-viewer-more-list')).toBeNull();
    expect(screen.queryByRole('button', { name: /altre azioni/i })).toBeNull();
  });

  it('names the assign control for what it will do', async () => {
    installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1', 'Alice'),
      'GET /api/people': () => jsonResponse([]),
    });
    renderViewer(['f-1'], 0);
    await screen.findByTestId('face-viewer-file-name');

    // Already somebody's face: the control edits, it does not assign.
    expect(screen.getByRole('button', { name: 'Modifica persona' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Assegna persona' })).toBeNull();
  });

  it('shows no review chrome when no queue opened it', async () => {
    // People and Person Detail open the viewer to LOOK at a face. Skip, Next
    // photo and the bulk ignore belong to a review queue, and there is none here.
    installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1'),
      'GET /api/people': () => jsonResponse([]),
    });
    renderViewer(['f-1', 'f-2'], 0);
    await screen.findByTestId('face-viewer-file-name');

    expect(screen.queryByTestId('face-viewer-skip')).toBeNull();
    expect(screen.queryByTestId('face-viewer-next-photo')).toBeNull();
    expect(screen.queryByTestId('face-viewer-ignore-all')).toBeNull();
    expect(screen.queryByTestId('face-viewer-progress')).toBeNull();
    // Looking at a face and deciding it are still both possible.
    expect(screen.getByTestId('face-viewer-ignore')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Assegna persona' })).toBeTruthy();
  });
});

// ------------------------------------------------------------------ decisions

describe('ignoring', () => {
  it('ignores ONE face with no confirmation at all', async () => {
    // A single reversible decision must not ask. The reviewer makes this one
    // hundreds of times.
    const mock = installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1'),
      'GET /api/people': () => jsonResponse([]),
      'POST /api/people/faces/f-1/ignore': () => jsonResponse({}),
    });
    renderViewer(['f-1'], 0, vi.fn(), vi.fn(), reviewControls());
    await screen.findByTestId('face-viewer-file-name');

    await userEvent.click(screen.getByTestId('face-viewer-ignore'));

    expect(screen.queryByTestId('face-viewer-ignore-all-confirm')).toBeNull();
    await waitFor(() => expect(
      mock.calls.some((c) => c.method === 'POST' && c.url.includes('/faces/f-1/ignore')),
    ).toBe(true));
  });

  it('asks before ignoring every remaining face, and cancelling costs nothing', async () => {
    const controls = reviewControls();
    const mock = installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1'),
      'GET /api/people': () => jsonResponse([]),
    });
    renderViewer(['f-1'], 0, vi.fn(), vi.fn(), controls);
    await screen.findByTestId('face-viewer-file-name');

    await userEvent.click(screen.getByTestId('face-viewer-ignore-all'));
    expect(await screen.findByTestId('face-viewer-ignore-all-confirm')).toBeTruthy();

    await userEvent.click(screen.getByTestId('face-viewer-ignore-all-cancel'));
    expect(screen.queryByTestId('face-viewer-ignore-all-confirm')).toBeNull();
    // Not one request, and the queue was never told to do anything.
    expect(controls.onIgnoreRemaining).not.toHaveBeenCalled();
    expect(mock.calls.some((c) => c.method === 'POST')).toBe(false);
  });

  it('confirming runs the bulk operation exactly once', async () => {
    const controls = reviewControls();
    installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1'),
      'GET /api/people': () => jsonResponse([]),
    });
    renderViewer(['f-1'], 0, vi.fn(), vi.fn(), controls);
    await screen.findByTestId('face-viewer-file-name');

    await userEvent.click(screen.getByTestId('face-viewer-ignore-all'));
    await userEvent.click(screen.getByTestId('face-viewer-ignore-all-accept'));

    // ONE bulk call, never one request per face.
    expect(controls.onIgnoreRemaining).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId('face-viewer-ignore-all-confirm')).toBeNull();
  });

  it('Escape dismisses the confirmation without closing the viewer', async () => {
    const onClose = vi.fn();
    installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1'),
      'GET /api/people': () => jsonResponse([]),
    });
    renderViewer(['f-1'], 0, vi.fn(), onClose, reviewControls());
    await screen.findByTestId('face-viewer-file-name');

    await userEvent.click(screen.getByTestId('face-viewer-ignore-all'));
    await screen.findByTestId('face-viewer-ignore-all-confirm');
    fireEvent.keyDown(window, { key: 'Escape' });

    await waitFor(() => expect(screen.queryByTestId('face-viewer-ignore-all-confirm')).toBeNull());
    expect(onClose).not.toHaveBeenCalled();
  });
});

// --------------------------------------------------------------- double click

describe('double-clicking a face box', () => {
  it('single click only selects — it opens no dialog', async () => {
    installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1'),
      'GET /api/people/faces/f-2/context': context('f-2'),
      'GET /api/people': () => jsonResponse([]),
    });
    renderViewer(['f-1', 'f-2'], 0);
    await settled();

    const other = screen.getByRole('button', { name: 'Altri volti nella foto' });
    await userEvent.click(other);

    await waitFor(() => expect(
      screen.getByRole('button', { name: 'Volto selezionato' }).dataset.faceId,
    ).toBe('f-2'));
    expect(screen.queryByRole('dialog', { name: 'Assegna a persona' })).toBeNull();
  });

  it('double-clicking the SELECTED face opens its assign dialog', async () => {
    installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1'),
      'GET /api/people': () => jsonResponse([]),
    });
    renderViewer(['f-1'], 0);
    await settled();

    fireEvent.doubleClick(screen.getByRole('button', { name: 'Volto selezionato' }));

    expect(await screen.findByRole('dialog', { name: 'Assegna a persona' })).toBeTruthy();
  });

  it('double-clicking ANOTHER face waits for that face before opening', async () => {
    // The dialog must describe the face that was clicked. Opening it against
    // the previous context would offer "Già assegnato a Alice" for a stranger.
    installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1', 'Alice'),
      'GET /api/people/faces/f-2/context': context('f-2'), // unassigned
      'GET /api/people': () => jsonResponse([]),
    });
    renderViewer(['f-1', 'f-2'], 0);
    await settled();

    fireEvent.doubleClick(screen.getByRole('button', { name: 'Altri volti nella foto' }));

    // f-2 is unassigned, so the dialog that opens is the ASSIGN one — and it
    // carries no trace of the person f-1 belonged to.
    const dialog = await screen.findByRole('dialog', { name: 'Assegna a persona' });
    expect(within(dialog).queryByText(/Alice/)).toBeNull();
    expect(screen.getByRole('button', { name: 'Volto selezionato' }).dataset.faceId).toBe('f-2');
  });

  it('offers move and remove when the double-clicked face already has a person', async () => {
    installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1', 'Alice'),
      'GET /api/people': () => jsonResponse([]),
    });
    renderViewer(['f-1'], 0);
    await settled();

    fireEvent.doubleClick(screen.getByRole('button', { name: 'Volto selezionato' }));

    const dialog = await screen.findByRole('dialog', { name: 'Assegna a persona' });
    expect(within(dialog).getByText('Alice')).toBeTruthy();
    expect(within(dialog).getByRole('button', { name: /rimuovi dalla persona/i })).toBeTruthy();
  });

  it('Escape closes the dialog and leaves the viewer open', async () => {
    const onClose = vi.fn();
    installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1'),
      'GET /api/people': () => jsonResponse([]),
    });
    renderViewer(['f-1'], 0, vi.fn(), onClose);
    await settled();

    fireEvent.doubleClick(screen.getByRole('button', { name: 'Volto selezionato' }));
    await screen.findByRole('dialog', { name: 'Assegna a persona' });
    fireEvent.keyDown(window, { key: 'Escape' });

    await waitFor(() => expect(
      screen.queryByRole('dialog', { name: 'Assegna a persona' }),
    ).toBeNull());
    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByTestId('face-viewer-file-name')).toBeTruthy();
  });
});

// ------------------------------------------------------------------- geometry

describe('the canvas is the picture', () => {
  it('sizes the canvas to the contain-fit of the bitmap in the stage', async () => {
    installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1'),
      'GET /api/people': () => jsonResponse([]),
    });
    renderViewer(['f-1'], 0);
    await settled();

    // 4000×3000 into 1000×800 → limited by width: 1000×750.
    const canvas = screen.getByTestId('face-viewer-canvas');
    expect(canvas.style.width).toBe('1000px');
    expect(canvas.style.height).toBe('750px');
    // And the image fills it exactly, so a box at 20% is at 20% of the picture.
    expect(screen.getByRole('img').className).toContain('face-viewer-image');
  });

  it('draws no box until the canvas is the picture', async () => {
    installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1'),
      'GET /api/people': () => jsonResponse([]),
    });
    renderViewer(['f-1'], 0);
    await screen.findByTestId('face-viewer-file-name');

    // The image has not reported its natural size yet: a box drawn now would
    // sit beside its face for a frame and then jump.
    expect(screen.queryAllByTestId('face-viewer-box')).toHaveLength(0);
    expect(screen.getByTestId('face-viewer-canvas').className).toContain('is-measuring');
  });
});

// ------------------------------------------------------- viewport behaviour

describe('the viewport', () => {
  it('opens at 100% and does not auto-zoom into the face', async () => {
    installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1', 'Alice'),
      'GET /api/people': () => jsonResponse([]),
    });
    renderViewer(['f-1', 'f-2'], 0);
    await settled();

    expect(screen.getByLabelText('Zoom').textContent).toBe('100%');
    expect(screen.getByRole('button', { name: 'Volto selezionato' })).toBeTruthy();
  });

  it('zooms in and out and returns to the whole photo', async () => {
    installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1'),
      'GET /api/people': () => jsonResponse([]),
    });
    renderViewer(['f-1'], 0);
    await settled();

    await userEvent.click(screen.getByRole('button', { name: 'Zoom avanti' }));
    expect(screen.getByLabelText('Zoom').textContent).toBe('125%');

    await userEvent.click(screen.getByRole('button', { name: 'Zoom indietro' }));
    expect(screen.getByLabelText('Zoom').textContent).toBe('100%');

    await userEvent.click(screen.getByTestId('face-viewer-focus'));
    expect(screen.getByLabelText('Zoom').textContent).not.toBe('100%');

    // "Foto intera" is the way back to the uncropped picture, unpanned.
    await userEvent.click(screen.getByTestId('face-viewer-fit'));
    expect(screen.getByLabelText('Zoom').textContent).toBe('100%');
    expect(screen.getByTestId('face-viewer-canvas').style.transform)
      .toContain('translate(0px, 0px)');
  });

  it('centres the selected face over the face itself', async () => {
    installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1'),
      'GET /api/people': () => jsonResponse([]),
    });
    renderViewer(['f-1'], 0);
    await settled();

    await userEvent.click(screen.getByTestId('face-viewer-focus'));

    // The box is at x 0.2..0.35, y 0.2..0.35 → centre (0.275, 0.275), which is
    // up and left of the middle, so the picture is pushed right and down.
    const transform = screen.getByTestId('face-viewer-canvas').style.transform;
    const [, panX, panY] = /translate\((-?[\d.]+)px, (-?[\d.]+)px\)/.exec(transform)!;
    expect(Number(panX)).toBeGreaterThan(0);
    expect(Number(panY)).toBeGreaterThan(0);

    // The image and the boxes live in ONE transformed element, which is what
    // keeps the square over the face at any zoom.
    const canvas = screen.getByTestId('face-viewer-canvas');
    expect(within(canvas).getByRole('img')).toBeTruthy();
    expect(within(canvas).getAllByTestId('face-viewer-box').length).toBe(2);
  });

  it('keeps the viewport when moving between faces of the SAME photo', async () => {
    // The reviewer zoomed in for a reason; resetting on every face would undo
    // that work several times per picture.
    installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1'),
      'GET /api/people/faces/f-2/context': context('f-2'),
      'GET /api/people': () => jsonResponse([]),
    });
    const { rerender } = renderViewer(['f-1', 'f-2'], 0);
    await settled();
    await userEvent.click(screen.getByRole('button', { name: 'Zoom avanti' }));
    expect(screen.getByLabelText('Zoom').textContent).toBe('125%');

    rerender(
      <AuthedWrapper>
        <MemoryRouter>
          <FaceContextViewer faceIds={['f-1', 'f-2']} index={1} onIndexChange={vi.fn()} onClose={vi.fn()} />
        </MemoryRouter>
      </AuthedWrapper>,
    );

    await waitFor(() => expect(
      screen.getByRole('button', { name: 'Volto selezionato' }).dataset.faceId,
    ).toBe('f-2'));
    expect(screen.getByLabelText('Zoom').textContent).toBe('125%');
  });

  it('opens a DIFFERENT photo at fit', async () => {
    let file = 'file-1';
    installFetchMock({
      'GET /api/people/faces/f-1/context': () => jsonResponse({
        fileItemId: file, fileName: 'crowd.jpg', selectedFaceId: 'f-1', selectedBox: b(0.2),
        faces: [{ faceId: 'f-1', box: b(0.2) }], personId: null, personName: null,
        isIgnored: false,
        effectiveDateTaken: '2019-07-14T10:30:00Z', effectiveDateTakenSource: 'embedded',
      }),
      'GET /api/people': () => jsonResponse([]),
    });
    renderViewer(['f-1'], 0);
    await settled();
    await userEvent.click(screen.getByRole('button', { name: 'Zoom avanti' }));
    expect(screen.getByLabelText('Zoom').textContent).toBe('125%');

    // The same face id resolving to another photo is exactly what "next photo"
    // does to this component: a new picture must arrive whole.
    file = 'file-2';
    fireEvent.keyDown(window, { key: 'ArrowRight' });
    await userEvent.click(screen.getByTestId('face-viewer-fit'));
    expect(screen.getByLabelText('Zoom').textContent).toBe('100%');
  });
});

// ------------------------------------------------------------------ unchanged

describe('what this slice must not have broken', () => {
  it('shows the medium preview and never the original', async () => {
    installFetchMock({ 'GET /api/people/faces/f-1/context': context('f-1', 'Alice') });
    renderViewer(['f-1', 'f-2'], 0);
    await screen.findByTestId('face-viewer-file-name');

    expect(screen.getByRole('img').getAttribute('src')).toBe('/api/files/file-1/preview');
  });

  it('navigates faces with the edge controls and the arrow keys', async () => {
    installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1'),
      'GET /api/people/faces/f-2/context': context('f-2'),
      'GET /api/people': () => jsonResponse([]),
    });
    const onIndexChange = vi.fn();
    renderViewer(['f-1', 'f-2'], 0, onIndexChange);
    await screen.findByTestId('face-viewer-file-name');

    await userEvent.click(screen.getByRole('button', { name: 'Volto successivo' }));
    expect(onIndexChange).toHaveBeenCalledWith(1);

    onIndexChange.mockClear();
    fireEvent.keyDown(window, { key: 'ArrowRight' });
    expect(onIndexChange).toHaveBeenCalledWith(1);

    onIndexChange.mockClear();
    // At the first face there is no previous one, so ArrowLeft is a no-op.
    fireEvent.keyDown(window, { key: 'ArrowLeft' });
    expect(onIndexChange).not.toHaveBeenCalled();
  });

  it('closes on Escape', async () => {
    installFetchMock({ 'GET /api/people/faces/f-1/context': context('f-1') });
    const onClose = vi.fn();
    renderViewer(['f-1'], 0, vi.fn(), onClose);
    await screen.findByTestId('face-viewer-file-name');

    fireEvent.keyDown(window, { key: 'Escape' });
    expect(onClose).toHaveBeenCalled();
  });

  it('assigns the selected face to a person from the viewer', async () => {
    const mock = installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1'), // unassigned
      'GET /api/people': () =>
        jsonResponse([{ personId: 'p-1', name: 'Alice', faceCount: 1, representative: null }]),
      'POST /api/people/faces/f-1/assign': () =>
        jsonResponse({ personId: 'p-1', name: 'Alice', faceCount: 2, representative: null }),
    });
    renderViewer(['f-1'], 0);
    await screen.findByTestId('face-viewer-file-name');

    await userEvent.click(screen.getByRole('button', { name: 'Assegna persona' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Alice' }));

    await waitFor(() =>
      expect(mock.calls.some((c) => c.method === 'POST' && c.url.includes('/faces/f-1/assign'))).toBe(true),
    );
  });

  it('renders no storage internals', async () => {
    installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1', 'Alice'),
      'GET /api/people': () => jsonResponse([]),
    });
    const { container } = renderViewer(['f-1', 'f-2'], 0);
    await screen.findByTestId('face-viewer-file-name');

    const html = container.innerHTML;
    for (const needle of ['blobObjectId', 'storageKey', 'sha256', '/storage/objects/', 'profileId']) {
      expect(html).not.toContain(needle);
    }
  });

  it('does not show a redundant "Apri nella foto" action inside the viewer', async () => {
    installFetchMock({
      'GET /api/people/faces/f-1/context': context('f-1'),
      'GET /api/people': () => jsonResponse([]),
    });
    renderViewer(['f-1'], 0);
    await screen.findByTestId('face-viewer-file-name');

    expect(screen.queryByRole('button', { name: 'Apri nella foto' })).toBeNull();
  });
});
