import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { MediaItem, SemanticBestMatch } from '@nubarca/api-client';
import { MediaTile, type SemanticTileMatches } from './MediaGrid';
import { markerLeftPercent, toMarkers } from './SemanticMarkerStrip';
import { AuthedWrapper } from '../../test-utils';

// SEARCH-SEM-01: semantic match markers on video tiles.
//
// The contract these defend: a video is ONE result with several reachable
// moments. Activating a moment must open that moment and nothing else — not the
// tile's default action, not selection — and every moment the backend returned
// must be reachable, including by keyboard.

afterEach(() => { cleanup(); vi.restoreAllMocks(); });

function match(ms: number): SemanticBestMatch {
  return {
    evidenceType: 'visual',
    startMilliseconds: Math.max(0, ms - 1_000),
    endMilliseconds: ms + 1_000,
    representativeMilliseconds: ms,
  };
}

function video(over: Partial<MediaItem> = {}): MediaItem {
  return {
    id: 'v1',
    name: 'clip.mp4',
    displayName: 'clip.mp4',
    kind: 'video',
    sizeBytes: 1024,
    width: 1920,
    height: 1080,
    durationSeconds: 600,
    thumbnailUrl: '/api/files/v1/thumbnail?size=small',
    ...(over as object),
  } as MediaItem;
}

// MediaItem is a discriminated union: an image has no durationSeconds at all,
// so this builds one rather than reshaping a video.
function photo(over: Partial<MediaItem> = {}): MediaItem {
  return {
    id: 'p1',
    name: 'shot.jpg',
    displayName: 'shot.jpg',
    kind: 'image',
    sizeBytes: 1024,
    width: 1920,
    height: 1080,
    thumbnailUrl: '/api/files/p1/thumbnail?size=small',
    ...(over as object),
  } as MediaItem;
}

const noSelection = {
  isSelected: () => false,
  handleTileClick: () => 'open' as const,
  toggleViaControl: () => {},
  count: 0,
};

function renderTile(opts: {
  item?: MediaItem;
  matches?: SemanticTileMatches | null;
  onOpen?: (atMs?: number) => void;
  selection?: Partial<typeof noSelection>;
} = {}) {
  const onOpen = opts.onOpen ?? vi.fn();
  render(
    <AuthedWrapper>
      <MediaTile
        item={opts.item ?? video()}
        index={0}
        width={320}
        height={180}
        orderedIds={['v1']}
        selection={{ ...noSelection, ...opts.selection } as never}
        onOpen={onOpen}
        semanticMatches={opts.matches ?? null}
      />
    </AuthedWrapper>,
  );
  return onOpen;
}

const threeMatches: SemanticTileMatches = {
  bestMatch: match(240_000),
  additionalMatches: [match(60_000), match(420_000)],
};

// ── pure helpers ───────────────────────────────────────────────────────────

describe('toMarkers', () => {
  it('orders chronologically even though the backend sends best first', () => {
    const markers = toMarkers(threeMatches.bestMatch, threeMatches.additionalMatches);
    expect(markers.map((m) => m.representativeMilliseconds)).toEqual([60_000, 240_000, 420_000]);
    // …while still remembering which one was best.
    expect(markers.find((m) => m.isBest)?.representativeMilliseconds).toBe(240_000);
  });

  it('de-duplicates identical representative timestamps, keeping the best', () => {
    const markers = toMarkers(match(1_000), [match(1_000), match(2_000)]);
    expect(markers).toHaveLength(2);
    expect(markers[0]).toMatchObject({ representativeMilliseconds: 1_000, isBest: true });
  });

  it('drops matches with no representative timestamp instead of rendering NaN', () => {
    const photoLike = { ...match(0), representativeMilliseconds: null } as SemanticBestMatch;
    expect(toMarkers(photoLike, [])).toHaveLength(0);
  });
});

describe('markerLeftPercent', () => {
  it('is proportional to duration', () => {
    expect(markerLeftPercent(300_000, 600)).toBe(50);
    expect(markerLeftPercent(150_000, 600)).toBe(25);
  });

  it.each([
    ['null duration', null],
    ['zero duration', 0],
    ['negative duration', -10],
    ['non-finite duration', Number.NaN],
  ])('produces a valid percentage for %s', (_label, duration) => {
    const value = markerLeftPercent(5_000, duration as number | null);
    expect(Number.isFinite(value)).toBe(true);
    expect(value).toBeGreaterThanOrEqual(0);
    expect(value).toBeLessThanOrEqual(100);
  });

  it('clamps a negative timestamp to the start and an overrun to the end', () => {
    expect(markerLeftPercent(-5_000, 600)).toBe(0);
    expect(markerLeftPercent(9_999_000, 600)).toBe(100);
  });
});

// ── rendering ──────────────────────────────────────────────────────────────

describe('marker rendering', () => {
  it('renders the best marker and every additional marker', () => {
    renderTile({ matches: threeMatches });
    const strip = screen.getByTestId('semantic-marker-strip');
    expect(within(strip).getAllByRole('button')).toHaveLength(3);
    expect(within(strip).getAllByTestId('semantic-marker-best')).toHaveLength(1);
  });

  it('places markers chronologically at duration-proportional positions', () => {
    renderTile({ matches: threeMatches });
    const buttons = screen.getAllByRole('button', { name: /corrispondenza/i });
    // 60s, 240s and 420s of a 600s video.
    expect(buttons.map((b) => (b as HTMLElement).style.left)).toEqual(['10%', '40%', '70%']);
  });

  it('never emits an invalid CSS percentage when duration is missing', () => {
    renderTile({ item: video({ durationSeconds: null }), matches: threeMatches });
    const strip = screen.getByTestId('semantic-marker-strip');
    expect(strip.getAttribute('data-positioned')).toBe('even');
    for (const b of within(strip).getAllByRole('button')) {
      const left = (b as HTMLElement).style.left;
      expect(left).not.toMatch(/NaN|Infinity|-/);
      expect(left).toMatch(/^\d+(\.\d+)?%$/);
    }
  });

  it('renders no strip for a photo', () => {
    renderTile({ item: photo(), matches: threeMatches });
    expect(screen.queryByTestId('semantic-marker-strip')).not.toBeInTheDocument();
  });

  it('renders no strip for an ordinary video with no semantic evidence', () => {
    renderTile({ matches: null });
    expect(screen.queryByTestId('semantic-marker-strip')).not.toBeInTheDocument();
  });
});

// ── interaction ────────────────────────────────────────────────────────────

describe('marker interaction', () => {
  it('opens the video at the marker timestamp, not the best one', async () => {
    const onOpen = renderTile({ matches: threeMatches });
    const last = screen.getAllByRole('button', { name: /corrispondenza a /i }).at(-1)!;
    await userEvent.click(last);
    expect(onOpen).toHaveBeenCalledTimes(1);
    expect(onOpen).toHaveBeenCalledWith(420_000);
  });

  it('opens at the BEST timestamp when the best marker is activated', async () => {
    const onOpen = renderTile({ matches: threeMatches });
    await userEvent.click(screen.getByTestId('semantic-marker-best'));
    expect(onOpen).toHaveBeenCalledWith(240_000);
  });

  it('does not also run the tile default action', async () => {
    const handleTileClick = vi.fn(() => 'open' as const);
    const onOpen = renderTile({ matches: threeMatches, selection: { handleTileClick } });
    await userEvent.click(screen.getByTestId('semantic-marker-best'));
    // The tile's own click path never ran, so it neither opened again nor
    // consulted selection.
    expect(handleTileClick).not.toHaveBeenCalled();
    expect(onOpen).toHaveBeenCalledTimes(1);
  });

  it('does not toggle selection', async () => {
    const toggleViaControl = vi.fn();
    renderTile({ matches: threeMatches, selection: { toggleViaControl } });
    await userEvent.click(screen.getByTestId('semantic-marker-best'));
    expect(toggleViaControl).not.toHaveBeenCalled();
  });

  it.each([
    ['Enter', '{Enter}'],
    ['Space', ' '],
  ])('activates with %s from the keyboard', async (_label, key) => {
    const onOpen = renderTile({ matches: threeMatches });
    const best = screen.getByTestId('semantic-marker-best');
    best.focus();
    expect(best).toHaveFocus();
    await userEvent.keyboard(key);
    expect(onOpen).toHaveBeenCalledWith(240_000);
  });

  it('reaches every marker through the tab order', async () => {
    renderTile({ matches: threeMatches });
    const strip = screen.getByTestId('semantic-marker-strip');
    const buttons = within(strip).getAllByRole('button');
    for (const b of buttons) {
      b.focus();
      expect(b).toHaveFocus();
      expect(b.tagName).toBe('BUTTON');
    }
  });
});

// ── accessibility ──────────────────────────────────────────────────────────

describe('marker accessibility', () => {
  it('names every marker with its formatted timestamp', () => {
    renderTile({ matches: threeMatches });
    expect(screen.getByRole('button', { name: /1:00/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /4:00/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /7:00/ })).toBeInTheDocument();
  });

  it('distinguishes the best match by name and state, not colour alone', () => {
    renderTile({ matches: threeMatches });
    const best = screen.getByTestId('semantic-marker-best');
    // Two independent non-colour cues: the accessible name and aria-pressed.
    expect(best).toHaveAccessibleName(/migliore/i);
    expect(best).toHaveAttribute('aria-pressed', 'true');
    for (const other of screen.getAllByTestId('semantic-marker')) {
      expect(other).toHaveAttribute('aria-pressed', 'false');
    }
  });

  it('groups the markers under a labelled region', () => {
    renderTile({ matches: threeMatches });
    expect(screen.getByRole('group', { name: /momenti/i })).toBeInTheDocument();
  });

  it('describes a timestamp as a duration offset, never a date', () => {
    renderTile({ matches: threeMatches });
    const name = screen.getByTestId('semantic-marker-best').getAttribute('aria-label') ?? '';
    expect(name).toMatch(/\d+:\d{2}/);
    expect(name).not.toMatch(/\d{4}|\/|AM|PM/);
  });
});
