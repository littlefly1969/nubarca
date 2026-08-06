import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { useMediaWorkspace, type MediaViewerController } from './useMediaWorkspace';
import { emptyIdentity } from './mediaWorkspaceQuery';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../../test-utils';

// SEARCH-SEM-01: the viewer's explicit seek contract.
//
// The rule that matters is NEGATIVE: a timestamp belongs to the ONE item it was
// requested for. If it survived a close, or followed the user to the next item
// in the viewer, an unrelated video would silently start partway through — a
// bug that is invisible in a screenshot and irritating in use.

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

let controller: MediaViewerController | null = null;

function Probe() {
  const ws = useMediaWorkspace({
    source: { kind: 'library' },
    identity: emptyIdentity({ kind: 'library' }),
    translate: (k: string) => k,
  } as never);
  controller = ws.viewer;
  return <span data-testid="probe">{String(ws.viewer.seekMs)}</span>;
}

function renderProbe() {
  installFetchMock({
    'GET /api/media': () => jsonResponse({
      items: [], nextCursor: null, hasMore: false, total: 0,
    }),
  });
  render(
    <MemoryRouter>
      <AuthedWrapper><Probe /></AuthedWrapper>
    </MemoryRouter>,
  );
}

describe('viewer seek handoff', () => {
  it('carries an explicit timestamp for the item it was requested for', () => {
    renderProbe();
    act(() => controller!.open(2, 420_000));
    expect(controller!.index).toBe(2);
    expect(controller!.seekMs).toBe(420_000);
    expect(screen.getByTestId('probe')).toHaveTextContent('420000');
  });

  it('opens without a timestamp when none is supplied', () => {
    renderProbe();
    act(() => controller!.open(1));
    expect(controller!.seekMs).toBeNull();
  });

  it('does not let a previous timestamp leak onto the next item opened normally', () => {
    renderProbe();
    act(() => controller!.open(0, 90_000));
    expect(controller!.seekMs).toBe(90_000);

    // A plain open of a different item must start from its own default.
    act(() => controller!.open(3));
    expect(controller!.index).toBe(3);
    expect(controller!.seekMs).toBeNull();
  });

  it('clears the timestamp when navigating inside the viewer', () => {
    renderProbe();
    act(() => controller!.open(0, 90_000));
    act(() => controller!.setIndex(1));
    expect(controller!.index).toBe(1);
    expect(controller!.seekMs).toBeNull();
  });

  it('clears the timestamp on close, so reopening starts clean', () => {
    renderProbe();
    act(() => controller!.open(0, 90_000));
    act(() => controller!.close());
    expect(controller!.isOpen).toBe(false);
    expect(controller!.seekMs).toBeNull();

    act(() => controller!.open(0));
    expect(controller!.seekMs).toBeNull();
  });

  it('replaces the timestamp when the same item is reopened at another marker', () => {
    renderProbe();
    act(() => controller!.open(0, 60_000));
    act(() => controller!.close());
    act(() => controller!.open(0, 240_000));
    expect(controller!.seekMs).toBe(240_000);
  });
});
