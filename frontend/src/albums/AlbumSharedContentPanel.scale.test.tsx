import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, configure, fireEvent, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AlbumSharedContentPanel } from './AlbumSharedContentPanel';
import {
  AuthedWrapper,
  errorResponse,
  installFetchMock,
  jsonResponse,
  stubContentListGeometry,
  type MockHandler,
  type MockRequest,
} from '../test-utils';

// The album content manager at SCALE.
//
// A 520-item album is the unit of every test here, because the defect these
// tests guard against only shows at size: the whole album fetched on open, one
// row — and several buttons, and a full-size thumbnail — per item. Each test
// below would fail against that implementation.

const TOTAL = 520;
const PAGE = 40;
const ROW_PX = 72;
const VIEWPORT_PX = 600;
// The viewport's rows plus the overscan, with room to spare — an order of
// magnitude below the album, whatever the album's size.
const DOM_ROW_BOUND = 30;

// Vitest's per-test budget is five seconds, which suits a unit test. One test
// here mounts a 520-item list and walks thirteen pages of it through a mock
// server, and on a cold run — which is what CI always does — that is honestly
// longer. The waits stay well inside the budget, so a genuine hang still fails
// rather than hanging the suite.
vi.setConfig({ testTimeout: 30_000, hookTimeout: 30_000 });

const PATIENCE_MS = 10_000;

function settle<T>(assertion: () => T) {
  return vi.waitFor(assertion, { timeout: PATIENCE_MS, interval: 25 });
}

let scrollToBefore: Element['scrollTo'] | undefined;

beforeEach(() => {
  configure({ asyncUtilTimeout: PATIENCE_MS });
  stubContentListGeometry({ rowPx: ROW_PX, viewportPx: VIEWPORT_PX });
  globalThis.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
  // A programmatic scroll that actually moves, as a browser's does.
  scrollToBefore = Element.prototype.scrollTo;
  Element.prototype.scrollTo = function scrollTo(this: Element, options?: ScrollToOptions | number) {
    this.scrollTop = typeof options === 'object' ? options.top ?? 0 : 0;
    this.dispatchEvent(new Event('scroll'));
  } as Element['scrollTo'];
});

afterEach(() => {
  configure({ asyncUtilTimeout: 1000 });
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  if (scrollToBefore) {
    Element.prototype.scrollTo = scrollToBefore;
  } else {
    delete (Element.prototype as { scrollTo?: unknown }).scrollTo;
  }
});

function row(i: number) {
  const n = String(i).padStart(3, '0');
  return {
    albumItemId: `ai-${n}`,
    fileItemId: `f-${n}`,
    kind: 'image' as const,
    thumbnailUrl: `/api/files/f-${n}/thumbnail?size=micro`,
    origin: 'owner' as const,
    contributorDisplayName: null,
    contributorMaskedEmail: null,
    sourceState: 'available' as const,
    addedAt: '2026-07-01T00:00:00Z',
    isCover: false,
  };
}
type Row = ReturnType<typeof row>;

// A faithful stand-in for the paged read and the cover command: a page
// continues after the row it names, AT the version it names, and an edit is
// refused at any other version.
function albumServer() {
  let version = 7;
  let order: Row[] = Array.from({ length: TOTAL }, (_, i) => row(i));
  const pageRequests: URLSearchParams[] = [];
  const editBodies: unknown[] = [];
  // Continuations to leave unanswered (until the client cancels them).
  const held = new Set<string>();

  const content: MockHandler = (req: MockRequest) => {
    const q = new URL(req.url, 'http://x').searchParams;
    pageRequests.push(q);
    const cursor = q.get('cursor');
    if (cursor !== null && held.has(cursor)) {
      return new Promise<Response>((_, reject) => {
        req.init?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
      });
    }
    const expected = q.get('expectedVersion');
    if (expected !== null && Number(expected) !== version) {
      return errorResponse(409, { error: 'changed', albumId: 'alb-1', version });
    }
    const start = cursor === null ? 0 : order.findIndex((r) => r.albumItemId === cursor) + 1;
    const limit = Number(q.get('limit'));
    const items = order.slice(start, start + limit);
    return jsonResponse({
      version,
      canEdit: true,
      coverFileItemId: null,
      totalCount: order.length,
      items,
      nextCursor: start + limit < order.length ? items[items.length - 1].albumItemId : null,
    });
  };

  const cover: MockHandler = (req) => {
    const body = JSON.parse(req.body!) as { expectedVersion: number; fileItemId: string | null };
    editBodies.push(body);
    if (body.expectedVersion !== version) {
      return errorResponse(409, { error: 'changed', albumId: 'alb-1', version });
    }
    version += 1;
    return jsonResponse({
      albumId: 'alb-1', version, name: 'Big', description: null, coverFileItemId: body.fileItemId,
    });
  };

  const handlers: Record<string, MockHandler> = {
    'GET /api/albums/alb-1/content': content,
    'PUT /api/shared-albums/alb-1/cover': cover,
  };
  const spy = installFetchMock(handlers);

  return {
    spy,
    pageRequests,
    editBodies,
    hold(cursor: string) { held.add(cursor); },
    release(cursor: string) { held.delete(cursor); },
    // Another curator reverses the whole album.
    changeElsewhere() {
      order = [...order].reverse();
      version += 1;
    },
  };
}

function renderPanel() {
  return render(
    <AuthedWrapper>
      <AlbumSharedContentPanel albumId="alb-1" onClose={vi.fn()} />
    </AuthedWrapper>,
  );
}

const rendered = () => screen.queryAllByTestId('album-content-row');
const renderedIds = () => rendered().map((r) => r.getAttribute('data-item-id'));
const rowById = (id: string) => document.querySelector<HTMLElement>(`[data-item-id="${id}"]`);
const scroller = () => screen.getByTestId('album-content-scroller');

async function opened() {
  await settle(() => expect(rendered().length).toBeGreaterThan(5));
}

// Sets the list's scroll offset and lets whatever it asks for arrive.
async function scrollListTo(top: number) {
  await act(async () => {
    scroller().scrollTop = top;
    fireEvent.scroll(scroller());
    await new Promise((resolve) => setTimeout(resolve, 0));
  });
}

// Scrolls a viewport at a time to the very end, as a user dragging down would —
// never past the bottom of what the list currently holds — letting every page
// it asks for arrive on the way. Returns the most rows the DOM ever held.
async function scrollToEnd(): Promise<number> {
  const list = screen.getByTestId('album-content-list');
  let most = rendered().length;
  for (let step = 0; step < 400; step++) {
    const bottom = Math.max(0, parseFloat(list.style.height) - VIEWPORT_PX);
    await scrollListTo(Math.min(scroller().scrollTop + VIEWPORT_PX, bottom));
    most = Math.max(most, rendered().length);
    const last = rendered().at(-1);
    if (last && within(last).queryByText(new RegExp(`^${TOTAL} di ${TOTAL}`))) return most;
  }
  throw new Error('the list never reached the end of the album');
}

describe('AlbumSharedContentPanel at 520 items', () => {
  it('opens on one page, a bounded window of rows and one control per row', async () => {
    const server = albumServer();
    renderPanel();
    await opened();

    // One request, for the first page only.
    expect(server.pageRequests).toHaveLength(1);
    expect(server.pageRequests[0].get('limit')).toBe(String(PAGE));
    expect(server.pageRequests[0].get('cursor')).toBeNull();

    // A window onto the album, not the album.
    expect(rendered().length).toBeLessThanOrEqual(DOM_ROW_BOUND);
    // ONE control per row (its Actions), plus the dialog's own close — not
    // four moves, a cover and a removal for every item.
    const panel = screen.getByTestId('album-content-panel');
    expect(within(panel).getAllByRole('button')).toHaveLength(rendered().length + 1);
    // Thumbnails only for the rows that exist, and each the curation icon.
    const images = Array.from(panel.querySelectorAll('img'));
    expect(images).toHaveLength(rendered().length);
    for (const img of images) expect(img.getAttribute('src')).toMatch(/\?size=micro$/);
    // Positions are the album's, not the page's.
    expect(within(rendered()[0]).getByText(/^1 di 520/)).toBeInTheDocument();
  });

  it('stays bounded while scrolling, and reads a page only as the rows held run out', async () => {
    const server = albumServer();
    renderPanel();
    await opened();

    // Well inside the first page: nothing more is asked for.
    await scrollListTo(8 * ROW_PX);
    expect(server.pageRequests).toHaveLength(1);

    // Nearing its end: the next page, after the last row held, at the version
    // the first page was read at.
    await scrollListTo(26 * ROW_PX);
    await settle(() => expect(server.pageRequests).toHaveLength(2));
    expect(server.pageRequests[1].get('cursor')).toBe('ai-039');
    expect(server.pageRequests[1].get('expectedVersion')).toBe('7');

    // All the way down: every page read once, and the DOM never holds more than
    // a window — the rows already visited are not kept.
    const most = await scrollToEnd();
    expect(server.pageRequests).toHaveLength(TOTAL / PAGE);
    expect(most).toBeLessThanOrEqual(DOM_ROW_BOUND);
    expect(rendered().at(-1)).toHaveAttribute('data-item-id', 'ai-519');
    expect(screen.getByTestId('album-content-panel').querySelectorAll('img').length)
      .toBeLessThanOrEqual(DOM_ROW_BOUND);
  });

  it('reloads the current album and says so when an edit meets a newer version', async () => {
    const server = albumServer();
    renderPanel();
    await opened();
    server.changeElsewhere();

    const second = rowById('ai-001')!;
    await userEvent.click(within(second).getByTestId('album-content-actions'));
    await userEvent.click(within(second).getByTestId('album-content-set-cover'));

    expect(await screen.findByTestId('album-content-notice'))
      .toHaveTextContent(/modificato da un altro utente/i);
    // Never retried: re-read from the top, at whatever version is current.
    expect(server.editBodies).toHaveLength(1);
    await settle(() => expect(server.pageRequests).toHaveLength(2));
    expect(server.pageRequests[1].get('cursor')).toBeNull();
    await settle(() => expect(rendered()[0]).toHaveAttribute('data-item-id', 'ai-519'));
  });

  it('starts again at the current version when the album changes under a scroll', async () => {
    const server = albumServer();
    renderPanel();
    await opened();
    server.changeElsewhere();
    await scrollListTo(30 * ROW_PX);

    // The continuation named the version it was read at, and was refused…
    expect(await screen.findByTestId('album-content-notice')).toHaveTextContent(/mentre lo scorrevi/i);
    expect(server.pageRequests[1].get('expectedVersion')).toBe('7');
    // …so the list was read again, from the top, instead of mixing two albums.
    await settle(() => expect(server.pageRequests[2]?.get('cursor')).toBeNull());
    await settle(() => expect(rendered()[0]).toHaveAttribute('data-item-id', 'ai-519'));
    expect(renderedIds()).not.toContain('ai-000');
  });
});
