import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import type { MediaItem } from '@nubarca/api-client';
import { I18nProvider } from '../../i18n';
import { useMediaSelection } from '../../gallery/useMediaSelection';
import { MediaTile } from './MediaGrid';

class TouchPointerEvent extends MouseEvent {
  pointerType: string; pointerId: number; isPrimary: boolean;
  constructor(type: string, options: PointerEventInit) {
    super(type, options);
    this.pointerType = options.pointerType ?? 'touch';
    this.pointerId = options.pointerId ?? 1;
    this.isPrimary = options.isPrimary ?? true;
  }
}
beforeEach(() => { vi.stubGlobal('PointerEvent', TouchPointerEvent); vi.useFakeTimers(); });
afterEach(() => { cleanup(); vi.useRealTimers(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

function setup(explicitMode = true) {
  const onOpen = vi.fn();
  function Tiles() {
    const selection = useMediaSelection({ explicitMode });
    return <I18nProvider>
      <output data-testid="mode">{selection.mode}</output>
      <output data-testid="count">{selection.count}</output>
      <button onClick={selection.clear}>Clear</button>
      <button onClick={() => selection.enterSelection()}>Select</button>
      {['A', 'B', 'C'].map((id, index) => <MediaTile key={id}
        item={{ id, kind: 'image', displayName: id, sizeBytes: 1, thumbnailUrl: '/thumb' } as MediaItem}
        index={index} width={100} height={100} orderedIds={['A', 'B', 'C']}
        selection={selection} interactionMode={explicitMode ? selection.mode : undefined} onOpen={() => onOpen(id)} />)}
    </I18nProvider>;
  }
  const rendered = render(<Tiles />);
  return { ...rendered, onOpen, tile: () => screen.getAllByTestId('media-open')[0] };
}
const hold = (node: HTMLElement) => fireEvent.pointerDown(node, { clientX: 20, clientY: 20, button: 0 });
const finishHold = () => act(() => vi.advanceTimersByTime(480));

it('long-press selects the first media and consumes its generated click', () => {
  const { tile, onOpen } = setup();
  expect(screen.queryByTestId('media-select-control')).not.toBeInTheDocument();
  hold(tile()); finishHold();
  expect(screen.getByTestId('mode')).toHaveTextContent('select');
  expect(screen.getByTestId('count')).toHaveTextContent('1');
  expect(tile()).toHaveAttribute('aria-pressed', 'true');
  fireEvent.pointerUp(tile()); fireEvent.click(tile());
  expect(tile()).toHaveAttribute('aria-pressed', 'true');
  expect(onOpen).not.toHaveBeenCalled();
  fireEvent.click(screen.getAllByTestId('media-open')[1]);
  expect(screen.getByTestId('count')).toHaveTextContent('2');
  expect(onOpen).not.toHaveBeenCalled();
});

it.each(['movement', 'pointercancel', 'scroll', 'blur', 'lostcapture'])(
  'cancels long-press on %s before it can select', (reason) => {
    const { tile, onOpen } = setup();
    hold(tile());
    if (reason === 'movement') fireEvent.pointerMove(tile(), { clientX: 31, clientY: 20 });
    if (reason === 'pointercancel') fireEvent.pointerCancel(tile());
    if (reason === 'scroll') fireEvent.scroll(window);
    if (reason === 'blur') fireEvent.blur(window);
    if (reason === 'lostcapture') fireEvent.lostPointerCapture(tile());
    finishHold();
    expect(screen.getByTestId('mode')).toHaveTextContent('browse');
    expect(screen.getByTestId('count')).toHaveTextContent('0');
    expect(onOpen).not.toHaveBeenCalled();
  },
);

it('a short tap opens the viewer and mouse holds never select', () => {
  const { tile, onOpen } = setup();
  hold(tile()); act(() => vi.advanceTimersByTime(100));
  fireEvent.pointerUp(tile()); fireEvent.click(tile());
  expect(onOpen).toHaveBeenCalledWith('A');
  fireEvent.pointerDown(tile(), { pointerType: 'mouse', button: 0 }); finishHold();
  expect(screen.getByTestId('mode')).toHaveTextContent('browse');
});

it('select mode remains active at zero selected items; Clear returns to browse', () => {
  const { tile, onOpen } = setup();
  fireEvent.click(screen.getByText('Select'));
  expect(screen.getByTestId('mode')).toHaveTextContent('select');
  fireEvent.click(tile()); fireEvent.click(tile());
  expect(screen.getByTestId('count')).toHaveTextContent('0');
  expect(screen.getByTestId('mode')).toHaveTextContent('select');
  expect(onOpen).not.toHaveBeenCalled();
  fireEvent.click(screen.getByText('Clear'));
  expect(screen.getByTestId('mode')).toHaveTextContent('browse');
  fireEvent.click(tile()); expect(onOpen).toHaveBeenCalledWith('A');
});

it('preserves Ctrl/Cmd toggling and Shift range selection', () => {
  const { onOpen } = setup();
  const tiles = screen.getAllByTestId('media-open');
  fireEvent.click(tiles[0], { ctrlKey: true });
  fireEvent.click(tiles[1], { metaKey: true });
  fireEvent.click(tiles[2], { shiftKey: true });
  expect(screen.getByTestId('count')).toHaveTextContent('3');
  expect(onOpen).not.toHaveBeenCalled();
});

it('keeps tile keyboard activation meaningful in both modes', () => {
  const { tile, onOpen } = setup();
  act(() => tile().focus()); fireEvent.click(tile(), { detail: 0 });
  expect(onOpen).toHaveBeenCalledWith('A');
  fireEvent.click(screen.getByText('Select'));
  act(() => tile().focus()); fireEvent.click(tile(), { detail: 0 });
  expect(tile()).toHaveAttribute('aria-pressed', 'true');
  expect(onOpen).toHaveBeenCalledTimes(1);
});

it('cancels pending holds when tiles unmount', () => {
  const { tile, unmount, onOpen } = setup();
  hold(tile()); unmount(); finishHold();
  expect(onOpen).not.toHaveBeenCalled();
  expect(vi.getTimerCount()).toBe(0);
});

it('preserves the plain-click viewer contract for other media surfaces', () => {
  const { onOpen } = setup(false);
  const tiles = screen.getAllByTestId('media-open');
  fireEvent.click(tiles[0], { ctrlKey: true });
  fireEvent.click(tiles[1]);
  expect(onOpen).toHaveBeenCalledWith('B');
  expect(screen.getByTestId('count')).toHaveTextContent('1');
});
