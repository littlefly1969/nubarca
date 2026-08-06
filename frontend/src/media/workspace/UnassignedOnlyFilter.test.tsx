import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { MediaWorkspace } from './MediaWorkspace';
import {
  emptyIdentity, filtersToUrlParams, identityFromUrlParams,
  type MediaWorkspaceIdentity, type MediaWorkspaceSource,
} from './mediaWorkspaceQuery';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../../test-utils';

// "Solo da organizzare": hide media already filed into an album so the library
// shows only what still needs sorting.
//
// The rule these defend: the toggle is a VIEW of the library, so it must live
// in the URL (reload, Back/Forward and deep links all reproduce it), it must
// reach the server as a filter rather than being applied client-side, and it
// must exist only where it means something — never on an album page.

const LIBRARY: MediaWorkspaceSource = { kind: 'library' };
const ALBUM: MediaWorkspaceSource = { kind: 'album', albumId: 'alb-1' };

beforeEach(() => {
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(
    () => ({
      width: 1024, height: 768, top: 0, left: 0, right: 1024, bottom: 768,
      x: 0, y: 0, toJSON: () => ({}),
    }) as DOMRect,
  );
  globalThis.ResizeObserver = class {
    observe() {} unobserve() {} disconnect() {}
  } as unknown as typeof ResizeObserver;
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

function emptyPage() {
  return { items: [], nextCursor: null, hasMore: false, total: 0, photoCount: 0, videoCount: 0 };
}

function renderWorkspace(
  identity: MediaWorkspaceIdentity, source: MediaWorkspaceSource = LIBRARY,
) {
  const onIdentityChange = vi.fn();
  render(
    <MemoryRouter>
      <AuthedWrapper>
        <MediaWorkspace
          source={source}
          identity={identity}
          onIdentityChange={onIdentityChange}
          searchPlaceholder="Cerca"
        />
      </AuthedWrapper>
    </MemoryRouter>,
  );
  return onIdentityChange;
}

function unassigned(source: MediaWorkspaceSource = LIBRARY): MediaWorkspaceIdentity {
  const id = emptyIdentity(source);
  return {
    ...id,
    filters: { ...id.filters, common: { ...id.filters.common, albumMembership: 'unassigned' } },
  };
}

// ── placement ──────────────────────────────────────────────────────────────

describe('toggle placement', () => {
  it('renders in the standard media command bar, off by default', async () => {
    installFetchMock({ 'GET /api/media': () => jsonResponse(emptyPage()) });
    renderWorkspace(emptyIdentity(LIBRARY));

    const toggle = await screen.findByTestId('ws-unassigned-only');
    expect(toggle).toBeInTheDocument();
    // Off by default: the library keeps showing everything.
    expect(toggle).toHaveAttribute('aria-pressed', 'false');
  });

  it('does not render on an album detail workspace', async () => {
    installFetchMock({ 'GET /api/albums/alb-1/media': () => jsonResponse(emptyPage()) });
    renderWorkspace(emptyIdentity(ALBUM), ALBUM);

    await screen.findByTestId('ws-command-bar');
    // Filtering "media not in an album" inside an album is meaningless, so the
    // control does not exist there — not merely disabled.
    expect(screen.queryByTestId('ws-unassigned-only')).not.toBeInTheDocument();
  });

  it('exposes its state and help text accessibly', async () => {
    installFetchMock({ 'GET /api/media': () => jsonResponse(emptyPage()) });
    renderWorkspace(unassigned());

    const toggle = await screen.findByTestId('ws-unassigned-only');
    // Pressed state, not colour, carries "on" to assistive tech.
    expect(toggle).toHaveAttribute('aria-pressed', 'true');
    expect(toggle).toHaveAccessibleName(/organizzare/i);
    expect(toggle).toHaveAttribute('title', expect.stringMatching(/già presenti in un album/i));
  });
});

// ── interaction ────────────────────────────────────────────────────────────

describe('toggle interaction', () => {
  it('turns the filter on through the identity model', async () => {
    installFetchMock({ 'GET /api/media': () => jsonResponse(emptyPage()) });
    const onIdentityChange = renderWorkspace(emptyIdentity(LIBRARY));

    await userEvent.click(await screen.findByTestId('ws-unassigned-only'));

    expect(onIdentityChange).toHaveBeenCalledTimes(1);
    expect(onIdentityChange.mock.calls[0][0].filters.common.albumMembership)
      .toBe('unassigned');
  });

  it('turns the filter back off', async () => {
    installFetchMock({ 'GET /api/media': () => jsonResponse(emptyPage()) });
    const onIdentityChange = renderWorkspace(unassigned());

    await userEvent.click(await screen.findByTestId('ws-unassigned-only'));

    expect(onIdentityChange.mock.calls[0][0].filters.common.albumMembership).toBe('any');
  });

  it('is keyboard operable', async () => {
    installFetchMock({ 'GET /api/media': () => jsonResponse(emptyPage()) });
    const onIdentityChange = renderWorkspace(emptyIdentity(LIBRARY));

    const toggle = await screen.findByTestId('ws-unassigned-only');
    toggle.focus();
    expect(toggle).toHaveFocus();
    await userEvent.keyboard('{Enter}');
    expect(onIdentityChange).toHaveBeenCalled();
  });
});

// ── the request actually carries the filter ────────────────────────────────

describe('server-side filtering', () => {
  it('sends the filter on the ordinary media query', async () => {
    const mock = installFetchMock({ 'GET /api/media': () => jsonResponse(emptyPage()) });
    renderWorkspace(unassigned());

    await waitFor(() => expect(mock.calls.length).toBeGreaterThan(0));
    const url = new URL(mock.calls.at(-1)!.url, 'http://x');
    // Filtering happens server-side, before pagination — never in the client.
    expect(url.searchParams.get('albumMembership')).toBe('unassigned');
  });

  it('sends the filter on a semantic search too, so ranking sees it first', async () => {
    const mock = installFetchMock({
      'GET /api/media/semantic': () => jsonResponse({
        items: [], nextCursor: null, hasMore: false, semanticStatus: 'ok', total: 0,
      }),
    });
    const id = unassigned();
    renderWorkspace({
      ...id,
      filters: { ...id.filters, photo: { ...id.filters.photo, visualQuery: 'mare' } },
    });

    await waitFor(() => expect(mock.calls.length).toBeGreaterThan(0));
    const url = new URL(mock.calls.at(-1)!.url, 'http://x');
    expect(url.pathname).toBe('/api/media/semantic');
    // A physical filter must reach the candidate scope, not be applied to an
    // already-ranked page.
    expect(url.searchParams.get('albumMembership')).toBe('unassigned');
  });

  it('omits the parameter entirely when the filter is off', async () => {
    const mock = installFetchMock({ 'GET /api/media': () => jsonResponse(emptyPage()) });
    renderWorkspace(emptyIdentity(LIBRARY));

    await waitFor(() => expect(mock.calls.length).toBeGreaterThan(0));
    const url = new URL(mock.calls.at(-1)!.url, 'http://x');
    expect(url.searchParams.has('albumMembership')).toBe(false);
  });
});

// ── URL persistence: reload, Back/Forward and deep links ───────────────────

describe('url persistence', () => {
  it('writes the filter to the URL', () => {
    const sp = filtersToUrlParams(unassigned());
    expect(sp.get('albumMembership')).toBe('unassigned');
  });

  it('omits it when off, so ordinary links stay clean', () => {
    expect(filtersToUrlParams(emptyIdentity(LIBRARY)).has('albumMembership')).toBe(false);
  });

  it('round-trips, which is what makes reload and Back/Forward work', () => {
    const restored = identityFromUrlParams(LIBRARY, filtersToUrlParams(unassigned()));
    expect(restored.filters.common.albumMembership).toBe('unassigned');
  });

  it('ignores the parameter outside the library, where it has no meaning', () => {
    const sp = new URLSearchParams({ albumMembership: 'unassigned' });
    expect(identityFromUrlParams(ALBUM, sp).filters.common.albumMembership).toBe('any');
  });

  it('falls back to "any" for a malformed value rather than failing', () => {
    const sp = new URLSearchParams({ albumMembership: 'not-a-mode' });
    expect(identityFromUrlParams(LIBRARY, sp).filters.common.albumMembership).toBe('any');
  });
});
