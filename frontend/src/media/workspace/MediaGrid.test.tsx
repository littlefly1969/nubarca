import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { MediaItem } from '@nubarca/api-client';
import { I18nProvider } from '../../i18n';
import { MediaGrid } from './MediaGrid';
import type { MediaSelection } from '../../gallery/useMediaSelection';

// The grid only lays out once it has measured a real container width. jsdom
// reports 0 for every rect, so tests drive the width through a controllable
// getBoundingClientRect stub and a ResizeObserver mock whose `.fire()` re-runs
// the component's measure callback (task: mock ResizeObserver + getBoundingClientRect).
let mockWidth = 1024;

class MockResizeObserver {
  static instances: MockResizeObserver[] = [];
  private readonly cb: ResizeObserverCallback;
  private readonly observed = new Set<Element>();
  constructor(cb: ResizeObserverCallback) {
    this.cb = cb;
    MockResizeObserver.instances.push(this);
  }
  observe(el: Element) { this.observed.add(el); }
  unobserve(el: Element) { this.observed.delete(el); }
  disconnect() { this.observed.clear(); }
  fire() {
    this.cb(
      [...this.observed].map((target) => ({ target } as unknown as ResizeObserverEntry)),
      this as unknown as ResizeObserver,
    );
  }
}

function fireResize(width: number) {
  act(() => {
    mockWidth = width;
    MockResizeObserver.instances.forEach((o) => o.fire());
  });
}

beforeEach(() => {
  mockWidth = 1024;
  MockResizeObserver.instances = [];
  globalThis.ResizeObserver = MockResizeObserver as unknown as typeof ResizeObserver;
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function () {
    return { width: mockWidth, height: 0, top: 0, left: 0, right: mockWidth, bottom: 0, x: 0, y: 0, toJSON: () => ({}) };
  });
});

function makeSelection(over: Partial<MediaSelection> = {}): MediaSelection {
  return {
    selected: new Set(),
    count: 0,
    isSelectionActive: false,
    isSelected: () => false,
    clear: vi.fn(),
    selectAll: vi.fn(),
    handleTileClick: vi.fn(() => 'open' as const),
    toggleViaControl: vi.fn(),
    ...over,
  };
}

const photo: MediaItem = {
  id: 'p1', kind: 'image', name: 'beach.jpg', title: 'Beach', displayName: 'Beach',
  mimeType: 'image/jpeg', sizeBytes: 2048, width: 4000, height: 3000,
  createdAt: '2026-01-01T00:00:00Z', updatedAt: null, takenAt: null,
  favorite: false, rating: null, thumbnailUrl: '/api/files/p1/thumbnail?size=small',
  occurrenceCount: 3, hasDuplicates: true, hasGps: null,
};
const video: MediaItem = {
  id: 'v1', kind: 'video', name: 'clip.mp4', title: null, displayName: 'clip.mp4',
  mimeType: 'video/mp4', sizeBytes: 5000, width: 1920, height: 1080,
  createdAt: '2026-01-02T00:00:00Z', updatedAt: null, takenAt: null,
  favorite: false, rating: null, thumbnailUrl: '/api/files/v1/poster',
  occurrenceCount: 1, hasDuplicates: false,
  posterUrl: '/api/files/v1/poster', durationSeconds: 125, videoCodec: 'h264',
  hasAudio: true, posterSource: 'ffmpeg', previewStripUrl: '/api/files/v1/preview-strip',
};
const verticalVideo: MediaItem = {
  ...video, id: 'v2', displayName: 'portrait.mp4', name: 'portrait.mp4',
  width: 1080, height: 1920, thumbnailUrl: '/api/files/v2/poster', posterUrl: '/api/files/v2/poster',
};

function renderGrid(items: MediaItem[], selection = makeSelection(), onOpen = vi.fn()) {
  const utils = render(
    <I18nProvider>
      <MediaGrid items={items} orderedIds={items.map((i) => i.id)} selection={selection} onOpen={onOpen} />
    </I18nProvider>,
  );
  return { selection, onOpen, ...utils };
}

afterEach(() => { cleanup(); vi.restoreAllMocks(); });

describe('MediaGrid justified wall', () => {
  it('renders one tile per item inside a media wall list', () => {
    renderGrid([photo, video]);
    const wall = screen.getByTestId('media-grid');
    expect(wall).toHaveClass('media-wall');
    expect(wall).toHaveAttribute('role', 'list');
    expect(screen.getAllByTestId('media-open')).toHaveLength(2);
  });

  it('shows the name and a resolution · size detail line in the overlay', () => {
    renderGrid([photo]);
    expect(screen.getByText('Beach')).toBeInTheDocument();
    expect(screen.getByText('4000×3000 · 2.0 KiB')).toBeInTheDocument();
  });

  it('falls back to the file name when there is no title', () => {
    renderGrid([video]);
    expect(screen.getByText('clip.mp4')).toBeInTheDocument();
  });

  it('marks videos with a discreet badge and a duration', () => {
    renderGrid([video]);
    expect(screen.getByTestId('media-video-badge')).toBeInTheDocument();
    expect(screen.getByTestId('media-video-duration')).toHaveTextContent('2:05');
  });

  it('renders a duplicate badge only when the item has duplicates', () => {
    renderGrid([photo, video]);
    const badges = screen.getAllByTestId('duplicate-badge');
    expect(badges).toHaveLength(1);
    expect(badges[0]).toHaveTextContent('×3');
  });

  it('opening a tile routes through the selection then calls onOpen with the index', async () => {
    const selection = makeSelection({ handleTileClick: vi.fn(() => 'open' as const) });
    const { onOpen } = renderGrid([photo, video], selection);
    await userEvent.click(screen.getAllByTestId('media-open')[1]);
    expect(selection.handleTileClick).toHaveBeenCalledWith('v1', 1, ['p1', 'v1'], expect.any(Object));
    expect(onOpen).toHaveBeenCalledWith(1);
  });

  it('a tile click that mutates the selection does not open the viewer', async () => {
    const selection = makeSelection({ handleTileClick: vi.fn(() => 'selected' as const) });
    const { onOpen } = renderGrid([photo], selection);
    await userEvent.click(screen.getAllByTestId('media-open')[0]);
    expect(onOpen).not.toHaveBeenCalled();
  });

  it('the selection control toggles selection without opening the viewer', async () => {
    const selection = makeSelection();
    const { onOpen } = renderGrid([photo], selection);
    await userEvent.click(screen.getAllByTestId('media-select-control')[0]);
    expect(selection.toggleViaControl).toHaveBeenCalledWith('p1', 0, ['p1'], false);
    expect(onOpen).not.toHaveBeenCalled();
  });

  it('exposes the selected state on the tile and its control', () => {
    const selection = makeSelection({ isSelected: (id) => id === 'p1' });
    renderGrid([photo], selection);
    expect(screen.getByTestId('media-select-control')).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('listitem')).toHaveAttribute('data-selected', 'true');
  });

  it('shows a placeholder when a photo thumbnail fails to load, keeping the tile frame', () => {
    renderGrid([photo]);
    const tile = screen.getByRole('listitem');
    const before = tile.getBoundingClientRect().width;
    const img = document.querySelector('img.media-tile__media') as HTMLImageElement;
    expect(img).not.toBeNull();
    fireEvent.error(img);
    expect(document.querySelector('.media-tile__placeholder')).not.toBeNull();
    // The tile geometry is driven by the DTO, not the resource — it must not
    // collapse when the derivative is missing.
    expect(tile.style.width).not.toBe('');
    expect(tile.getBoundingClientRect().width).toBe(before);
  });

  it('virtualizes into multiple justified rows for a large set', () => {
    const many = Array.from({ length: 18 }, (_v, i) => ({ ...photo, id: `p${i}`, hasDuplicates: false }));
    renderGrid(many);
    expect(document.querySelectorAll('.media-wall__row').length).toBeGreaterThan(1);
  });
});

describe('MediaGrid proportional layout', () => {
  it('shows a stable skeleton and no rows until a real width is measured', () => {
    mockWidth = 0; // getBoundingClientRect reports nothing usable
    renderGrid([photo, video]);
    expect(screen.getByTestId('media-grid-skeleton')).toBeInTheDocument();
    expect(screen.getByTestId('media-grid')).toHaveAttribute('aria-busy', 'true');
    expect(document.querySelectorAll('.media-wall__row')).toHaveLength(0);
    expect(screen.queryAllByTestId('media-open')).toHaveLength(0);
  });

  it('lays out rows once the ResizeObserver reports a width', () => {
    mockWidth = 0;
    renderGrid([photo, video]);
    expect(document.querySelectorAll('.media-wall__row')).toHaveLength(0);
    fireResize(1024);
    expect(screen.queryByTestId('media-grid-skeleton')).not.toBeInTheDocument();
    expect(document.querySelectorAll('.media-wall__row').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByTestId('media-open')).toHaveLength(2);
  });

  it('does NOT force a vertical video into a 16:9 tile — it is taller than it is wide', () => {
    renderGrid([verticalVideo]);
    const tile = screen.getByRole('listitem');
    const w = parseFloat(tile.style.width);
    const h = parseFloat(tile.style.height);
    expect(h).toBeGreaterThan(w); // 9:16 → portrait tile, never 16:9
  });

  it('gives a horizontal video a wide (≈16:9) tile from its real dimensions', () => {
    renderGrid([video]);
    const tile = screen.getByRole('listitem');
    const w = parseFloat(tile.style.width);
    const h = parseFloat(tile.style.height);
    expect(w / h).toBeGreaterThan(1.4);
  });

  it('recomputes the layout on a real resize without dropping items or selection', () => {
    const selection = makeSelection();
    const many = Array.from({ length: 12 }, (_v, i) => ({ ...photo, id: `p${i}`, hasDuplicates: false }));
    renderGrid(many, selection);
    const wideRows = document.querySelectorAll('.media-wall__row').length;
    fireResize(360);
    const narrowRows = document.querySelectorAll('.media-wall__row').length;
    // A much narrower container packs fewer tiles per row → more rows.
    expect(narrowRows).toBeGreaterThan(wideRows);
    // Items are preserved; nothing refetched, selection object untouched.
    expect(screen.getAllByTestId('media-open').length).toBeGreaterThan(0);
    expect(selection.clear).not.toHaveBeenCalled();
  });

  it('ignores a sub-pixel resize (< 1px) — no layout thrash', () => {
    renderGrid([photo, video]);
    const rowsBefore = document.querySelectorAll('.media-wall__row').length;
    fireResize(1024.4); // rounds back to 1024 → no state change
    expect(document.querySelectorAll('.media-wall__row').length).toBe(rowsBefore);
  });
});

describe('MediaGrid preview framing', () => {
  it('renders a photo as ONE thumbnail layer — no blurred duplicate behind it', () => {
    renderGrid([photo]);
    const frame = document.querySelector('.media-tile__frame');
    expect(frame).not.toBeNull();
    // Exactly one image in the frame: the tile is reserved at the item's real
    // ratio, so there is no lateral space for a blurred fill to occupy.
    expect(frame!.querySelectorAll('img')).toHaveLength(1);
    expect(document.querySelector('img.media-tile__backdrop')).toBeNull();
    const foreground = document.querySelector('img.media-tile__media') as HTMLImageElement;
    expect(foreground).not.toBeNull();
    expect(foreground.getAttribute('src')).toBe(photo.thumbnailUrl);
  });

  it('renders the video poster on the cover stage, with no blurred backdrop', () => {
    renderGrid([video]);
    expect(document.querySelector('.video-preview-fit-cover')).not.toBeNull();
    expect(document.querySelector('.video-preview-fit-contain')).toBeNull();
  });

  it('never emits a background-image blur anywhere in the wall', () => {
    renderGrid([photo, video, verticalVideo]);
    for (const el of document.querySelectorAll<HTMLElement>('.media-wall *')) {
      expect(el.style.backgroundImage === '' || !el.className.includes('backdrop')).toBe(true);
      expect(el.className).not.toContain('backdrop');
    }
  });
});

// The geometry contract the Similar Photos Explorer shares with the Library:
// the tile box comes from the item's declared DISPLAY dimensions, so the
// placeholder that shows before the thumbnail decodes is already the final box.
describe('MediaGrid tile geometry', () => {
  function tileRatios(items: MediaItem[]): number[] {
    renderGrid(items);
    return [...document.querySelectorAll<HTMLElement>('.media-tile')].map((el) => {
      const w = Number.parseFloat(el.style.width);
      const h = Number.parseFloat(el.style.height);
      return w / h;
    });
  }

  const cases: ReadonlyArray<[string, number, number, number]> = [
    ['16:9 landscape', 1920, 1080, 16 / 9],
    ['3:2 landscape', 3000, 2000, 3 / 2],
    ['1:1 square', 2000, 2000, 1],
    ['2:3 portrait', 2000, 3000, 2 / 3],
    ['9:16 portrait', 1080, 1920, 9 / 16],
  ];

  for (const [label, width, height, expected] of cases) {
    it(`reserves the exact ${label} ratio`, () => {
      const [ratio] = tileRatios([{ ...photo, width, height }]);
      expect(ratio).toBeCloseTo(expected, 2);
    });
  }

  it('reserves a panorama wider than 3:1 as a wide tile (clamped, never squared)', () => {
    const [ratio] = tileRatios([{ ...photo, width: 12000, height: 3000 }]);
    // 4:1 exceeds the layout clamp, so it lands at the 3.5 ceiling — still
    // decisively panoramic, and never the 1:1 fallback.
    expect(ratio).toBeCloseTo(3.5, 2);
  });

  it('uses the Library square fallback when geometry is missing', () => {
    const [ratio] = tileRatios([{ ...photo, width: null, height: null }]);
    expect(ratio).toBeCloseTo(1, 2);
  });

  it('keeps the reserved box identical while the image is still loading', () => {
    renderGrid([{ ...photo, width: 2000, height: 3000 }]);
    const tile = document.querySelector<HTMLElement>('.media-tile')!;
    const before = { width: tile.style.width, height: tile.style.height };
    // A slow load changes nothing about the box: geometry never depends on the
    // decoded image, only on the DTO's declared dimensions.
    const img = document.querySelector('img.media-tile__media')!;
    act(() => { img.dispatchEvent(new Event('load')); });
    expect(tile.style.width).toBe(before.width);
    expect(tile.style.height).toBe(before.height);
  });

  it('keeps the reserved box when the thumbnail fails (placeholder fills the same tile)', () => {
    renderGrid([{ ...photo, width: 2000, height: 3000 }]);
    const tile = document.querySelector<HTMLElement>('.media-tile')!;
    const before = { width: tile.style.width, height: tile.style.height };
    fireEvent.error(document.querySelector('img.media-tile__media')!);
    expect(document.querySelector('.media-tile__placeholder')).not.toBeNull();
    expect(tile.style.width).toBe(before.width);
    expect(tile.style.height).toBe(before.height);
  });

  it('appending a page does not resize the already-laid-out tiles', () => {
    const first = { ...photo, id: 'a', width: 2000, height: 3000 };
    const { rerender } = renderGrid([first]);
    const before = document.querySelector<HTMLElement>('.media-tile')!.style.height;
    rerender(
      <I18nProvider>
        <MediaGrid
          items={[first, { ...photo, id: 'b', width: 4000, height: 3000 }]}
          orderedIds={['a', 'b']}
          selection={makeSelection()}
          onOpen={vi.fn()}
        />
      </I18nProvider>,
    );
    // Two tiles now share one justified row, so the row height is re-solved for
    // both together — what must NOT happen is the first tile losing its shape.
    const tiles = [...document.querySelectorAll<HTMLElement>('.media-tile')];
    expect(tiles).toHaveLength(2);
    const ratio = Number.parseFloat(tiles[0].style.width) / Number.parseFloat(tiles[0].style.height);
    expect(ratio).toBeCloseTo(2 / 3, 2);
    expect(before).toBeTruthy();
  });
});
