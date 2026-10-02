import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import type { PartyGuestbookEntry } from '@nubarca/api-client';
import { I18nProvider } from '../i18n';
import { PartyGuestbookTvStage } from './PartyGuestbookTvStage';
import {
  GUESTBOOK_DWELL_MS,
  GUESTBOOK_LONG_DWELL_MS,
  guestbookCrop,
  guestbookFrameAspect,
  guestbookPhotoPlacement,
  guestbookTvPhotoBox,
} from '../tv/semantics/partyGuestbook';

/**
 * THE GUEST BOOK ON THE TELEVISION, drawn.
 *
 * The rules (dwell, density, frame, crop, deck) are tested in their pure
 * module and held to the app's by the parity test. These test that the stage
 * DRAWS them: the template the memory names, its crop, its words as written,
 * its signature; one memory at a time, following the book; the words shrunk
 * until they fit and never cut. What jsdom cannot measure — the real 720p and
 * 1080p layout — is checked in Chromium by scripts/check-guestbook-tv-layout.mjs.
 */

beforeEach(() => vi.useFakeTimers());
afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

function memory(id: string, over: Partial<PartyGuestbookEntry> = {}): PartyGuestbookEntry {
  return {
    id,
    authorDisplayName: `Autore ${id}`,
    body: `Dedica ${id}`,
    createdAt: '2027-06-12T20:00:00Z',
    template: { key: 'nubarca', version: 1 },
    media: {
      url: `/api/tv/party/guestbook/${id}/photo`,
      width: 1600,
      height: 1200,
      orientation: 'landscape',
      crop: { centerX: 0.5, centerY: 0.5, zoom: 1 },
    },
    ...over,
  } as PartyGuestbookEntry;
}

function setScreen(width: number, height: number) {
  Object.defineProperty(window, 'innerWidth', { configurable: true, value: width });
  Object.defineProperty(window, 'innerHeight', { configurable: true, value: height });
}

function mount(entries: PartyGuestbookEntry[]) {
  const view = render(
    <I18nProvider>
      <PartyGuestbookTvStage entries={entries} />
    </I18nProvider>,
  );
  return {
    ...view,
    update: (next: PartyGuestbookEntry[]) => view.rerender(
      <I18nProvider>
        <PartyGuestbookTvStage entries={next} />
      </I18nProvider>,
    ),
  };
}

async function advance(ms: number) {
  await act(async () => { await vi.advanceTimersByTimeAsync(ms); });
}

const shown = () => document.querySelector('[data-memory-id]')?.getAttribute('data-memory-id') ?? null;
const card = () => screen.getByTestId('guestbook-tv-memory');
const photoBox = () => card().querySelector('.guestbook-memory-photo') as HTMLElement;
const words = () => card().querySelector('.guestbook-memory-words') as HTMLElement;

describe('one memory, drawn as its author made it', () => {
  beforeEach(() => setScreen(1920, 1080));

  it.each(['nubarca', 'polaroid', 'editorial', 'celebration'])('draws the %s design it names', (key) => {
    mount([memory('g1', { template: { key, version: 1 } })]);
    expect(card()).toHaveAttribute('data-template', key);
    expect(card()).toHaveAttribute('data-template-version', '1');
    expect(card()).toHaveClass(`guestbook-memory--${key}-1`, 'guestbook-memory--tv');
  });

  it('gives a landscape and a portrait photograph the frame their template decides', () => {
    for (const [width, height] of [[1600, 1200], [1200, 1600]]) {
      cleanup();
      mount([memory('g1', { template: { key: 'editorial', version: 1 }, media: {
        url: '/x', width, height, orientation: width > height ? 'landscape' : 'portrait',
        crop: { centerX: 0.5, centerY: 0.5, zoom: 1 },
      } })]);
      const frame = guestbookFrameAspect('editorial', 1, width / height);
      const box = guestbookTvPhotoBox(Math.round(1920 * 0.9), Math.round(1080 * 0.88), frame, 'airy');
      expect(photoBox().style.width).toBe(`${box.width}px`);
      expect(photoBox().style.height).toBe(`${box.height}px`);
      expect(width > height ? frame : 1 / frame).toBeCloseTo(width > height ? 3 / 2 : 4 / 3);
    }
  });

  it('places the photograph by the guest\'s crop and zoom', () => {
    const crop = { centerX: 0.3, centerY: 0.7, zoom: 2 };
    mount([memory('g1', { media: { url: '/p', width: 1600, height: 1200, orientation: 'landscape', crop } })]);
    const img = photoBox().querySelector('img') as HTMLImageElement;
    const placement = guestbookPhotoPlacement(guestbookCrop(1600 / 1200, 4 / 3, crop));
    expect(parseFloat(img.style.width)).toBeCloseTo(placement.width * 100, 6);
    expect(parseFloat(img.style.left)).toBeCloseTo(placement.left * 100, 6);
    expect(parseFloat(img.style.top)).toBeCloseTo(placement.top * 100, 6);
    expect(img).toHaveAttribute('src', '/p');
  });

  it('keeps every line break, blank lines included, and signs the memory', () => {
    const body = 'Cara Anna,\n\n\nche festa!\nUn abbraccio';
    mount([memory('g1', { body, authorDisplayName: 'Marco' })]);
    const text = screen.getByTestId('guestbook-tv-memory-body');
    // As text, exactly — rendered with pre-wrap, never interpreted.
    expect(text.textContent).toBe(body);
    expect(text.innerHTML).not.toMatch(/<br|<p|<a/);
    expect(screen.getByTestId('guestbook-tv-memory-author')).toHaveTextContent('Marco');
  });

  it('gives a long dedication more room and more time', async () => {
    const long = 'Una dedica lunga. '.repeat(25).trim();
    mount([memory('g1', { body: long }), memory('g2')]);
    expect(screen.getByTestId('guestbook-tv-stage')).toHaveAttribute('data-density', 'compact');
    // Not after the normal dwell…
    await advance(GUESTBOOK_DWELL_MS + 100);
    expect(shown()).toBe('g1');
    // …after the long one.
    await advance(GUESTBOOK_LONG_DWELL_MS - GUESTBOOK_DWELL_MS);
    expect(shown()).toBe('g2');
  });

  it('shows the whole dedication, shrinking the words until they fit — never cutting them', () => {
    // jsdom has no layout: model a column 400px tall whose content is twenty
    // lines of the current type size.
    const heightOf = (el: HTMLElement) => {
      const px = parseFloat(el.style.getPropertyValue('--guestbook-tv-font')) || 0;
      return px * 20;
    };
    const scroll = vi.spyOn(HTMLElement.prototype, 'scrollHeight', 'get')
      .mockImplementation(function (this: HTMLElement) {
        return this.classList.contains('guestbook-memory-words') ? heightOf(this) : 0;
      });
    const client = vi.spyOn(HTMLElement.prototype, 'clientHeight', 'get')
      .mockImplementation(function (this: HTMLElement) {
        return this.classList.contains('guestbook-memory-words') ? 400 : 0;
      });
    try {
      const body = 'x'.repeat(1000);
      mount([memory('g1', { body })]);
      const px = parseFloat(words().style.getPropertyValue('--guestbook-tv-font'));
      expect(px * 20).toBeLessThanOrEqual(400);
      expect(px).toBeGreaterThan(0);
      // Every character is still there.
      expect(screen.getByTestId('guestbook-tv-memory-body').textContent).toBe(body);
    } finally {
      scroll.mockRestore();
      client.mockRestore();
    }
  });

  it('never clips or clamps the words in its stylesheets', () => {
    const css = ['../pages/PartyGuestbook.css', './PartyGuestbookTvStage.css']
      .map((f) => readFileSync(resolve(__dirname, f), 'utf8'))
      .join('\n');
    const tvRules = css.slice(css.indexOf('The television'));
    expect(tvRules).not.toMatch(/text-overflow|line-clamp|-webkit-line-clamp/);
    expect(tvRules).not.toMatch(/guestbook-memory-(words|body|author)[^{]*\{[^}]*overflow:\s*hidden/);
    expect(readFileSync(resolve(__dirname, './PartyGuestbookTvStage.css'), 'utf8')).not.toMatch(/overflow/);
  });

  it('honours a viewer who asked for less motion', () => {
    const css = readFileSync(resolve(__dirname, './PartyGuestbookTvStage.css'), 'utf8');
    expect(css).toMatch(/@media \(prefers-reduced-motion: reduce\)\s*\{\s*\.guestbook-tv-memory\s*\{\s*animation:\s*none/);
  });
});

describe('the deck, following the book', () => {
  beforeEach(() => setScreen(1920, 1080));

  it('leaves a single memory still: no timer, no fade, nothing re-drawn', async () => {
    mount([memory('g1')]);
    const before = document.querySelector('[data-memory-id]');
    await advance(5 * 60_000);
    expect(shown()).toBe('g1');
    // The very same element: the memory was never re-mounted.
    expect(document.querySelector('[data-memory-id]')).toBe(before);
  });

  it('moves through several memories in the book\'s order, and round again', async () => {
    mount([memory('g1'), memory('g2'), memory('g3')]);
    expect(shown()).toBe('g1');
    await advance(GUESTBOOK_DWELL_MS);
    expect(shown()).toBe('g2');
    await advance(GUESTBOOK_DWELL_MS);
    expect(shown()).toBe('g3');
    await advance(GUESTBOOK_DWELL_MS);
    expect(shown()).toBe('g1');
  });

  it('lets a new memory join without moving the one being read', async () => {
    const { update } = mount([memory('g1'), memory('g2')]);
    await advance(GUESTBOOK_DWELL_MS / 2);
    update([memory('g1'), memory('g2'), memory('g3')]);
    await advance(1);
    expect(shown()).toBe('g1');
    await advance(GUESTBOOK_DWELL_MS);
    expect(shown()).toBe('g2');
  });

  it('replaces a hidden memory with the next one, not the first', async () => {
    const { update } = mount([memory('g1'), memory('g2'), memory('g3')]);
    await advance(GUESTBOOK_DWELL_MS);
    expect(shown()).toBe('g2');
    update([memory('g1'), memory('g3')]);
    await advance(1);
    expect(shown()).toBe('g3');
  });

  it('shows nothing once the last memory has left — the server returns the slideshow', async () => {
    const { update } = mount([memory('g1')]);
    update([]);
    await advance(1);
    expect(shown()).toBeNull();
    expect(screen.getByTestId('guestbook-tv-stage')).toHaveAttribute('aria-busy', 'true');
  });
});

describe('a television of any resolution', () => {
  it.each([[1280, 720], [1920, 1080]])('composes the memory for %ix%i inside the safe area', (w, h) => {
    setScreen(w, h);
    mount([memory('g1', { body: 'Auguri!' })]);
    const box = { width: parseFloat(photoBox().style.width), height: parseFloat(photoBox().style.height) };
    expect(box.width).toBeLessThanOrEqual(w * 0.9 * 0.6 + 1);
    expect(box.height).toBeLessThanOrEqual(h * 0.88 * 0.78 + 1);
    // The words column is exactly as tall as the photograph, and sized for this screen.
    expect(words().style.height).toBe(`${box.height}px`);
    const px = parseFloat(words().style.getPropertyValue('--guestbook-tv-font'));
    expect(px).toBe(Math.round(52 * (Math.round(h * 0.88) / 1080)));
  });
});
