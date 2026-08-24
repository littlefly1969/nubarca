import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { PhotoFaceReviewTab } from './PhotoFaceReviewTab';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../../test-utils';
import { I18nProvider } from '../../i18n';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const box = { x: 0.1, y: 0.1, width: 0.2, height: 0.2 };

// Two photos: one with three undecided faces, one with a single face. The queue
// must finish the first photo before it touches the second.
const photos = {
  items: [
    { fileItemId: 'file-a', name: 'IMG_A.jpg', unassignedCount: 3, faceIds: ['f1', 'f2', 'f3'] },
    { fileItemId: 'file-b', name: 'IMG_B.jpg', unassignedCount: 1, faceIds: ['f9'] },
  ],
  nextCursor: null,
  profileAvailable: true,
};

const context = (faceId: string, fileItemId = 'file-a') => () => jsonResponse({
  selectedFaceId: faceId,
  fileItemId,
  fileName: fileItemId === 'file-a' ? 'IMG_A.jpg' : 'IMG_B.jpg',
  personId: null,
  personName: null,
  isIgnored: false,
  selectedBox: box,
  faces: [{ faceId, box, personId: null, personName: null }],
});

function renderTab(handlers: Record<string, unknown> = {}) {
  const mock = installFetchMock({
    'GET /api/people/photos-with-unassigned-faces': () => jsonResponse(photos),
    'GET /api/people': () => jsonResponse([{ personId: 'p-1', name: 'Alice', faceCount: 2, representative: null }]),
    'GET /api/people/faces/f1/context': context('f1'),
    'GET /api/people/faces/f2/context': context('f2'),
    'GET /api/people/faces/f3/context': context('f3'),
    'GET /api/people/faces/f9/context': context('f9', 'file-b'),
    ...handlers,
  });
  render(
    <I18nProvider>
      <AuthedWrapper>
        <MemoryRouter>
          <PhotoFaceReviewTab />
        </MemoryRouter>
      </AuthedWrapper>
    </I18nProvider>,
  );
  return mock;
}

const openFirstPhoto = async () => {
  await waitFor(() => expect(screen.getByText('IMG_A.jpg')).toBeTruthy());
  await userEvent.click(screen.getByText('IMG_A.jpg'));
  await waitFor(() => expect(screen.getByText('Volto 1 di 3')).toBeTruthy());
};

it('lists photos with the number of faces still to decide', async () => {
  renderTab();
  await waitFor(() => expect(screen.getByText('IMG_A.jpg')).toBeTruthy());
  expect(screen.getByText('3 da decidere')).toBeTruthy();
  expect(screen.getByText('1 da decidere')).toBeTruthy();
});

it('ignoring a face advances within the SAME photo', async () => {
  renderTab({ 'POST /api/people/faces/f1/ignore': () => new Response(null, { status: 204 }) });
  await openFirstPhoto();

  await userEvent.click(screen.getByRole('button', { name: 'Ignora volto' }));

  // Still inside IMG_A, now on its second undecided face — not jumped to IMG_B.
  // The name appears twice (queue row + viewer), which is itself the proof that
  // the open photo is still this one.
  await waitFor(() => expect(screen.getByText('Volto 1 di 2')).toBeTruthy());
  expect(screen.getAllByText('IMG_A.jpg').length).toBeGreaterThan(0);
  expect(screen.queryByText('Volto 1 di 1')).toBeNull();
});

it('assigning a face also advances within the same photo', async () => {
  // The regression this guards: assignment reports through onChanged, which the
  // viewer used only to refresh itself, so the queue would sit on a face it had
  // already given away.
  renderTab({
    'POST /api/people/faces/f1/assign': () =>
      jsonResponse({ personId: 'p-1', name: 'Alice', faceCount: 3, representative: null }),
  });
  await openFirstPhoto();

  await userEvent.click(screen.getByRole('button', { name: 'Assegna persona' }));
  await userEvent.click(screen.getByRole('button', { name: 'Alice' }));

  await waitFor(() => expect(screen.getByText('Volto 1 di 2')).toBeTruthy());
});

it('skipping leaves the face undecided and moves on', async () => {
  const mock = renderTab();
  await openFirstPhoto();

  await userEvent.click(screen.getByRole('button', { name: 'Salta volto' }));

  // The count is unchanged — nothing was decided — but the position moved.
  await waitFor(() => expect(screen.getByText('Volto 2 di 3')).toBeTruthy());
  expect(mock.calls.some((c) => c.method === 'POST')).toBe(false);
});

it('ignoring every undecided face finishes the photo and opens the next one', async () => {
  const mock = renderTab({
    'POST /api/people/photos/file-a/ignore-unassigned-faces': () => jsonResponse({ ignored: 3 }),
  });
  await openFirstPhoto();

  // It sits beside the single ignore — same kind of answer, larger scale — and
  // it is the one action here that asks first, because it decides several faces
  // at once.
  await userEvent.click(screen.getByTestId('face-viewer-ignore-all'));
  await userEvent.click(await screen.findByTestId('face-viewer-ignore-all-accept'));

  // One request, not one per face — and the queue moved to the next photo.
  // IMG_B's name appears twice once it is open (queue row + viewer), which is
  // itself the evidence, so this counts rather than expecting exactly one.
  await waitFor(() => expect(screen.getByText('Volto 1 di 1')).toBeTruthy());
  expect(screen.getAllByText('IMG_B.jpg').length).toBeGreaterThan(0);
  // IMG_A leaving is an EVENTUAL state, not an immediate one: the progress label
  // updates synchronously from the queue, while the viewer's file name arrives
  // from a fetch. Asserting it without waiting was the flake.
  await waitFor(() => expect(screen.queryByText('IMG_A.jpg')).toBeNull());
  expect(mock.calls.filter((c) => c.method === 'POST').length).toBe(1);
});

it('deciding the last face of the last photo ends the review', async () => {
  renderTab({ 'POST /api/people/faces/f9/ignore': () => new Response(null, { status: 204 }) });
  await waitFor(() => expect(screen.getByText('IMG_B.jpg')).toBeTruthy());
  await userEvent.click(screen.getByText('IMG_B.jpg'));
  await waitFor(() => expect(screen.getByText('Volto 1 di 1')).toBeTruthy());

  await userEvent.click(screen.getByRole('button', { name: 'Ignora volto' }));

  await waitFor(() => expect(screen.queryByText('Volto 1 di 1')).toBeNull());
});

// ---------------------------------------------------------------------------
// Next photo: MANUAL navigation, and nothing else.
//
// The distinction these tests exist to hold: the queue advances by itself when
// a photo is FINISHED, and that advance removes the photo. Next photo is the
// other thing — parking an unresolved photo and coming back to it — so it must
// leave the queue, the counts and the server exactly as they were.
// ---------------------------------------------------------------------------

it('Next photo opens the next loaded photo without finishing this one', async () => {
  const mock = renderTab();
  await openFirstPhoto();

  await userEvent.click(screen.getByTestId('face-viewer-next-photo'));

  // IMG_B is open...
  await waitFor(() => expect(screen.getByText('Volto 1 di 1')).toBeTruthy());
  await waitFor(() => expect(screen.getAllByText('IMG_B.jpg').length).toBeGreaterThan(0));
  // ...and IMG_A is STILL in the queue, with all three faces still to decide.
  expect(screen.getByText('IMG_A.jpg')).toBeTruthy();
  expect(screen.getByText('3 da decidere')).toBeTruthy();
  // Nothing was assigned, ignored, or otherwise written.
  expect(mock.calls.some((c) => c.method !== 'GET')).toBe(false);
});

it('Next photo is disabled on the last loaded photo and does not wrap', async () => {
  renderTab();
  await openFirstPhoto();

  await userEvent.click(screen.getByTestId('face-viewer-next-photo'));
  await waitFor(() => expect(screen.getByText('Volto 1 di 1')).toBeTruthy());

  // IMG_B is the last loaded photo: there is nowhere further to go, and the
  // control says so rather than quietly returning to the top of the queue.
  const next = screen.getByTestId('face-viewer-next-photo');
  expect(next).toBeDisabled();
  await userEvent.click(next);
  expect(screen.getByText('Volto 1 di 1')).toBeTruthy();
});

it('skipping stays on the same photo, and is offered only when there is another face', async () => {
  const mock = renderTab();
  await openFirstPhoto();

  await userEvent.click(screen.getByTestId('face-viewer-skip'));

  // Same photo, next undecided face, nothing decided and nothing sent.
  await waitFor(() => expect(screen.getByText('Volto 2 di 3')).toBeTruthy());
  expect(screen.getAllByText('IMG_A.jpg').length).toBeGreaterThan(0);
  expect(mock.calls.some((c) => c.method === 'POST')).toBe(false);
});

it('offers no Skip on a photo with a single undecided face', async () => {
  renderTab();
  await waitFor(() => expect(screen.getByText('IMG_B.jpg')).toBeTruthy());
  await userEvent.click(screen.getByText('IMG_B.jpg'));
  await waitFor(() => expect(screen.getByText('Volto 1 di 1')).toBeTruthy());
  expect(screen.getByTestId('face-viewer-skip')).toBeDisabled();
});
