import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { PartyImageViewer } from './PartyImageViewer';
import { AnonWrapper } from '../test-utils';

// The END of a pinch, which is where the viewer used to fall over.
//
// The gesture arithmetic is tested as pure functions in imageTransform.test.ts.
// What those cannot see is the plumbing between pointer events and React state.
// A pinch sends a stream of pointermoves and then lifts its fingers, and React
// applies queued state updaters at its next render — AFTER the lift. An updater
// that read the pinch's ref at that point found it already cleared and threw
// during render, and with no error boundary the whole party page went blank the
// moment a guest let go of a photograph they had just enlarged.
//
// One act() holds React's render until the whole sequence has been dispatched,
// which is exactly the ordering a real phone produces.

afterEach(cleanup);

function renderViewer() {
  render(
    <AnonWrapper>
      <PartyImageViewer src="/api/party/t/media/f1/preview" label="Menu" onClose={vi.fn()} />
    </AnonWrapper>,
  );
  return screen.getByTestId('party-viewer-stage');
}

const touch = (pointerId: number, clientX: number, clientY: number) => ({
  pointerId, clientX, clientY, pointerType: 'touch', isPrimary: pointerId === 1,
});

// Two fingers 100 px apart spreading to 210 px: a pinch to ~2x.
function spreadTwoFingers(stage: HTMLElement) {
  fireEvent.pointerDown(stage, touch(1, 150, 300));
  fireEvent.pointerDown(stage, touch(2, 250, 300));
  fireEvent.pointerMove(stage, touch(2, 280, 300));
  fireEvent.pointerMove(stage, touch(2, 320, 300));
  fireEvent.pointerMove(stage, touch(1, 110, 300));
}

describe('PartyImageViewer — the end of a pinch', () => {
  it('keeps the photograph when the first finger lifts before React renders the pinch', () => {
    const stage = renderViewer();

    act(() => {
      spreadTwoFingers(stage);
      fireEvent.pointerUp(stage, touch(1, 110, 300));
    });

    // Still on screen — and still enlarged, which is what the guest asked for.
    expect(screen.getByTestId('party-image-viewer')).toBeInTheDocument();
    expect(screen.getByTestId('party-viewer-stage')).toHaveAttribute('data-zoomed', 'true');
  });

  it('keeps the photograph, enlarged, once both fingers have lifted', () => {
    const stage = renderViewer();

    act(() => {
      spreadTwoFingers(stage);
      fireEvent.pointerUp(stage, touch(1, 110, 300));
      fireEvent.pointerUp(stage, touch(2, 320, 300));
    });

    expect(screen.getByTestId('party-image-viewer')).toBeInTheDocument();
    expect(screen.getByTestId('party-viewer-stage')).toHaveAttribute('data-zoomed', 'true');
  });
});
