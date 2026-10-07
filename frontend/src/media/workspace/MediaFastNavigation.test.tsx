import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { I18nProvider } from '../../i18n';
import { installFetchMock, jsonResponse } from '../../test-utils';
import { MediaFastNavigation } from './MediaFastNavigation';
import { emptyIdentity } from './mediaWorkspaceQuery';
import { mediaNavigationKey, navigationIndexAt, navigationLabel } from './mediaNavigation';

const identity = { ...emptyIdentity({ kind: 'library' }), sort: 'datetaken' as const };
const buckets = [{ key: '2025-02', count: 10 }, { key: '2023-06', count: 7 }, { key: '2022-01', count: 4 }];
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); vi.useRealTimers(); });

function setup(over: Partial<React.ComponentProps<typeof MediaFastNavigation>> = {}) {
  const { calls } = installFetchMock({ 'GET /api/media/navigation': () => jsonResponse({ buckets }) });
  const onJump = vi.fn(async () => true);
  const props = { identity, revision: 0, currentKey: '2025-02', busy: false, onJump, onAuthError: vi.fn(), ...over };
  const rendered = render(<I18nProvider><MediaFastNavigation {...props} /></I18nProvider>);
  return { ...rendered, calls, onJump, props };
}

it('shows an accessible timeline from the whole server index and tracks the visible month', async () => {
  const { rerender, props } = setup();
  const slider = await screen.findByRole('slider');
  expect(slider).toHaveAttribute('aria-orientation', 'vertical');
  expect(slider).toHaveAttribute('aria-valuemax', '2');
  rerender(<I18nProvider><MediaFastNavigation {...props} currentKey="2023-06" /></I18nProvider>);
  expect(slider).toHaveAttribute('aria-valuenow', '1');
  expect(slider).toHaveAttribute('aria-valuetext', 'giugno 2023');
});

it('supports keyboard jumps and a precise year/month chooser', async () => {
  const { onJump } = setup();
  fireEvent.keyDown(await screen.findByRole('slider'), { key: 'End' });
  await waitFor(() => expect(onJump).toHaveBeenCalledWith('2022-01'));
  await userEvent.click(screen.getByRole('button', { name: 'Vai a…' }));
  const dialog = await screen.findByRole('dialog');
  expect(dialog).toHaveAccessibleName('Vai a…');
  await userEvent.selectOptions(screen.getByLabelText('Anno'), '2023');
  await userEvent.click(screen.getByRole('button', { name: /^Vai$/ }));
  await waitFor(() => expect(onJump).toHaveBeenLastCalledWith('2023-06'));
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
});

it('keeps loading feedback and offers manual retry after a failed destination', async () => {
  let resolve: (ok: boolean) => void = () => {};
  const onJump = vi.fn(() => new Promise<boolean>((done) => { resolve = done; }));
  setup({ onJump });
  fireEvent.keyDown(await screen.findByRole('slider'), { key: 'ArrowDown' });
  expect(screen.getByRole('status')).toHaveTextContent('Caricamento…');
  await act(async () => resolve(false));
  expect(screen.getByRole('status')).toHaveTextContent('Impossibile raggiungere');
  await userEvent.click(screen.getByRole('button', { name: 'Riprova' }));
  expect(onJump).toHaveBeenCalledTimes(2);
});

it('only lets the newest destination settle feedback', async () => {
  const resolvers: Array<(ok: boolean) => void> = [];
  setup({ onJump: () => new Promise<boolean>((resolve) => resolvers.push(resolve)) });
  const slider = await screen.findByRole('slider');
  fireEvent.keyDown(slider, { key: 'ArrowDown' });
  fireEvent.keyDown(slider, { key: 'End' });
  await act(async () => resolvers[1](true));
  await act(async () => resolvers[0](false));
  expect(screen.queryByText('Impossibile raggiungere questo punto.')).not.toBeInTheDocument();
});

it('uses the alphabet and only the letters returned by the server', async () => {
  installFetchMock({ 'GET /api/media/navigation': () => jsonResponse({ buckets: [{ key: 'n:a', count: 9 }, { key: 'n:m', count: 12 }] }) });
  render(<I18nProvider><MediaFastNavigation identity={{ ...identity, sort: 'name' }} revision={0}
    currentKey="n:a" busy={false} onJump={vi.fn()} onAuthError={vi.fn()} /></I18nProvider>);
  expect(await screen.findByRole('slider')).toHaveAccessibleName('Scorrimento rapido per nome');
  await userEvent.click(screen.getByRole('button', { name: 'Vai a…' }));
  expect(screen.queryByLabelText('Anno')).not.toBeInTheDocument();
  expect(screen.getByLabelText('Lettera').querySelectorAll('option')).toHaveLength(2);
});

it('does not request navigation for relevance, similarity or size ordering', () => {
  for (const override of [{ sort: 'size' as const }, { filters: { ...identity.filters,
    photo: { ...identity.filters.photo, visualQuery: 'sea' } } }, { filters: { ...identity.filters,
    photo: { ...identity.filters.photo, similarTo: 'photo-id' } } }]) {
    const { calls, unmount } = setup({ identity: { ...identity, ...override } });
    expect(calls).toHaveLength(0);
    expect(screen.queryByRole('slider')).not.toBeInTheDocument();
    unmount();
  }
});

it('aborts an outdated index and forwards the exact active filters for its replacement', async () => {
  const { calls, props, rerender } = setup();
  await screen.findByRole('slider');
  rerender(<I18nProvider><MediaFastNavigation {...props} identity={{ ...identity, mediaKind: 'video',
    filters: { ...identity.filters, common: { ...identity.filters.common, favorite: true } } }} /></I18nProvider>);
  await waitFor(() => expect(calls).toHaveLength(2));
  expect((calls[0].init?.signal as AbortSignal).aborted).toBe(true);
  expect(calls[1].url).toContain('kind=video');
  expect(calls[1].url).toContain('favorite=true');
});

it('bounds drag positions and keeps calendar components independent of the device zone', () => {
  expect(navigationIndexAt(-10, 0, 100, 3)).toBe(0);
  expect(navigationIndexAt(1000, 0, 100, 3)).toBe(2);
  expect(navigationIndexAt(50, 0, 100, 3)).toBe(1);
  expect(mediaNavigationKey({ createdAt: '2023-06-01T00:00:00Z', takenAt: '2022-01-01T00:00:00Z' } as never, 'datetaken')).toBe('2022-01');
  expect(navigationLabel('2023-06', 'it')).toBe('giugno 2023');
  expect(navigationLabel('n:é', 'it')).toBe('É');
  expect(mediaNavigationKey({ displayName: '🌄.jpg' } as never, 'name')).toBe('n:🌄');
});

it('previews a captured drag immediately, coalesces movement and commits once on release', async () => {
  class TestPointerEvent extends MouseEvent {
    pointerId: number; isPrimary: boolean;
    constructor(type: string, options: PointerEventInit) {
      super(type, options); this.pointerId = options.pointerId ?? 1; this.isPrimary = options.isPrimary ?? true;
    }
  }
  vi.stubGlobal('PointerEvent', TestPointerEvent);
  const { onJump } = setup();
  const slider = await screen.findByRole('slider');
  const capture = vi.fn(), release = vi.fn();
  Object.assign(slider, { setPointerCapture: capture, releasePointerCapture: release });
  vi.spyOn(slider, 'getBoundingClientRect').mockReturnValue({ top: 100, height: 332 } as DOMRect);
  vi.useFakeTimers();
  fireEvent.pointerDown(slider, { clientY: 120, pointerId: 3, button: 0 });
  fireEvent.pointerMove(slider, { clientY: 270, pointerId: 3 });
  expect(slider).toHaveAttribute('aria-valuetext', 'giugno 2023');
  expect(onJump).not.toHaveBeenCalled();
  fireEvent.pointerMove(slider, { clientY: 420, pointerId: 3 });
  await act(async () => { vi.advanceTimersByTime(180); });
  expect(onJump).toHaveBeenCalledTimes(1);
  expect(onJump).toHaveBeenCalledWith('2022-01');
  fireEvent.pointerUp(slider, { clientY: 420, pointerId: 3 });
  expect(onJump).toHaveBeenCalledTimes(1);
  expect(capture).toHaveBeenCalledWith(3);
  expect(release).toHaveBeenCalledWith(3);
});

it('cancels a pending drag without initiating a jump', async () => {
  class TestPointerEvent extends MouseEvent {
    pointerId = 1; isPrimary = true;
  }
  vi.stubGlobal('PointerEvent', TestPointerEvent);
  const { onJump } = setup();
  const slider = await screen.findByRole('slider');
  Object.assign(slider, { setPointerCapture: vi.fn() });
  vi.spyOn(slider, 'getBoundingClientRect').mockReturnValue({ top: 0, height: 332 } as DOMRect);
  vi.useFakeTimers();
  fireEvent.pointerDown(slider, { clientY: 280, button: 0 });
  fireEvent.pointerCancel(slider);
  await act(async () => { vi.advanceTimersByTime(1000); });
  expect(onJump).not.toHaveBeenCalled();
});
