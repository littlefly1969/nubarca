import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { TvPersonalGallery } from './TvPersonalGallery';
import { draftSummaryLines } from './TvPersonalGallery';
import { installFetchMock, jsonResponse, type MockHandler } from '../test-utils';
import { I18nProvider } from '../i18n';

// /tv natural-language command entry: local interpretation → draft confirmation
// → explicit Apply. Covers the grant-in-header rule, POST-body-only command,
// draft summary, Apply issuing a semantic query, Cancel/failure preserving the
// prior filters, ambiguity resolution, and the no-persistence rule.

const GRANT = 'grant-token-nl';

function item(id: string) {
  return {
    id, name: `${id}.jpg`, mediaType: 'image', width: 100, height: 80,
    createdAt: '2026-07-01T10:00:00Z',
    thumbnailUrl: `/api/tv/personal/media/${id}/thumbnail`,
    previewUrl: `/api/tv/personal/media/${id}/preview`,
    favorite: false, occurrenceCount: 1,
  };
}
function page(items: unknown[], extra: Record<string, unknown> = {}) {
  return jsonResponse({ items, nextCursor: null, hasMore: false, totalCount: items.length, ...extra });
}
const media: MockHandler = () => new Response(new Uint8Array([1]), {
  status: 200, headers: { 'content-type': 'image/jpeg' },
});
function withMedia(handlers: Record<string, MockHandler>, ids: string[]) {
  for (const id of ids) {
    handlers[`GET /api/tv/personal/media/${id}/thumbnail`] = media;
    handlers[`GET /api/tv/personal/media/${id}/preview`] = media;
  }
  return handlers;
}

function draft(overrides: Record<string, unknown> = {}) {
  return {
    draft: {
      version: 1, operation: 'replace', peopleInclude: [], peopleExclude: [], peopleMatch: 'all',
      favorite: true, minRating: null, hasGps: null, dateTakenFrom: null, dateTakenTo: null,
      collapseDuplicates: null, sort: null, sortDirection: null, metadataSearch: null,
      semanticQuery: 'mare al tramonto', semanticQueryEnglish: null, semanticTopK: 300,
      ...overrides,
    },
    resolvedPeople: [], ambiguities: [], warnings: [], requiresClarification: false,
  };
}

function renderGallery() {
  return render(
    <I18nProvider>
      <TvPersonalGallery grant={GRANT} onBack={() => {}} onPersonalError={() => false} />
    </I18nProvider>,
  );
}

beforeEach(() => {
  (URL as unknown as { createObjectURL: unknown }).createObjectURL = vi.fn(() => 'blob:mock');
  (URL as unknown as { revokeObjectURL: unknown }).revokeObjectURL = vi.fn();
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

async function openNl() {
  renderGallery();
  await screen.findByTestId('tv-personal-count');
  fireEvent.click(screen.getByTestId('tv-personal-toggle-filters'));
  return screen.getByTestId('tv-personal-nl-input');
}

describe('/tv natural-language command', () => {
  it('sends the command in the POST body with the grant header, then shows the draft', async () => {
    const fetchMock = installFetchMock(withMedia({
      'GET /api/tv/personal/gallery': () => page([item('a')]),
      'POST /api/tv/personal/gallery/interpret-command': () => jsonResponse(draft()),
    }, ['a']));

    const input = await openNl();
    fireEvent.change(input, { target: { value: 'foto al mare al tramonto preferite' } });
    fireEvent.click(screen.getByTestId('tv-personal-nl-submit'));

    await screen.findByTestId('tv-personal-draft');
    const call = fetchMock.calls.find((c) => c.url.includes('interpret-command'));
    expect(call).toBeDefined();
    expect(call!.init?.method).toBe('POST');
    // Command is in the body, NEVER in the URL.
    expect(call!.url).not.toContain('mare');
    const body = JSON.parse(call!.init?.body as string);
    expect(body.command).toContain('mare');
    const headers = call!.init?.headers as Record<string, string>;
    expect(headers['X-Tv-Personal-Unlock']).toBe(GRANT);
    expect(window.localStorage.length).toBe(0);
    expect(window.sessionStorage.length).toBe(0);
  });

  it('Apply issues a new gallery query carrying the semantic query', async () => {
    const fetchMock = installFetchMock(withMedia({
      'GET /api/tv/personal/gallery': () => page([item('a')]),
      'POST /api/tv/personal/gallery/interpret-command': () => jsonResponse(draft()),
    }, ['a']));

    const input = await openNl();
    fireEvent.change(input, { target: { value: 'mare al tramonto preferite' } });
    fireEvent.click(screen.getByTestId('tv-personal-nl-submit'));
    await screen.findByTestId('tv-personal-draft');
    fireEvent.click(screen.getByTestId('tv-personal-draft-apply'));

    await waitFor(() => {
      const semanticCall = fetchMock.calls.find(
        (c) => c.url.startsWith('/api/tv/personal/gallery?') && c.url.includes('semanticQuery'),
      );
      expect(semanticCall).toBeDefined();
      const params = new URLSearchParams(semanticCall!.url.split('?')[1]);
      expect(params.get('semanticQuery')).toBe('mare al tramonto');
      expect(params.get('semanticTopK')).toBe('300');
      expect(params.get('favorite')).toBe('true');
    });
  });

  it('Cancel discards the draft and keeps the prior filters', async () => {
    installFetchMock(withMedia({
      'GET /api/tv/personal/gallery': () => page([item('a')]),
      'POST /api/tv/personal/gallery/interpret-command': () => jsonResponse(draft()),
    }, ['a']));

    const input = await openNl();
    fireEvent.change(input, { target: { value: 'mare al tramonto' } });
    fireEvent.click(screen.getByTestId('tv-personal-nl-submit'));
    await screen.findByTestId('tv-personal-draft');
    fireEvent.click(screen.getByRole('button', { name: 'Annulla' }));
    expect(screen.queryByTestId('tv-personal-filters')).toBeNull();
    // Filter count unchanged (still zero).
    expect(screen.getByTestId('tv-personal-toggle-filters').textContent).not.toContain('(');
  });

  it('interpretation failure preserves the prior gallery and shows a notice', async () => {
    installFetchMock(withMedia({
      'GET /api/tv/personal/gallery': () => page([item('a')]),
      'POST /api/tv/personal/gallery/interpret-command': () => jsonResponse({ error: 'model_busy' }, 429),
    }, ['a']));

    const input = await openNl();
    fireEvent.change(input, { target: { value: 'qualcosa' } });
    fireEvent.click(screen.getByTestId('tv-personal-nl-submit'));

    expect(await screen.findByTestId('tv-personal-nl-error')).toBeInTheDocument();
    expect(screen.queryByTestId('tv-personal-draft')).toBeNull();
    // The photo from the prior query is still shown (filters unchanged).
    expect(screen.getByText('a.jpg')).toBeInTheDocument();
  });

  it('resolves an ambiguous person before Apply is enabled', async () => {
    installFetchMock(withMedia({
      'GET /api/tv/personal/gallery': () => page([item('a')]),
      'POST /api/tv/personal/gallery/interpret-command': () => jsonResponse(draft({
        __ambiguities: undefined,
      })),
    }, ['a']));
    // Re-mock interpret with an ambiguity payload.
    installFetchMock(withMedia({
      'GET /api/tv/personal/gallery': () => page([item('a')]),
      'POST /api/tv/personal/gallery/interpret-command': () => jsonResponse({
        draft: draft().draft,
        resolvedPeople: [],
        ambiguities: [{ text: 'Marco', mode: 'include', candidates: [
          { personId: 'p1', name: 'Marco Rossi', faceCount: 30 },
          { personId: 'p2', name: 'Marco Bianchi', faceCount: 8 },
        ] }],
        warnings: [],
        requiresClarification: true,
      }),
    }, ['a']));

    const input = await openNl();
    fireEvent.change(input, { target: { value: 'foto di Marco' } });
    fireEvent.click(screen.getByTestId('tv-personal-nl-submit'));
    await screen.findByTestId('tv-personal-draft');

    const apply = screen.getByTestId('tv-personal-draft-apply') as HTMLButtonElement;
    expect(apply.disabled).toBe(true);
    fireEvent.click(screen.getByTestId('tv-personal-ambiguity-p1'));
    expect(apply.disabled).toBe(false);
  });
});

describe('draftSummaryLines', () => {
  const L = (it: string, _en: string) => it;

  it('clear returns a single line', () => {
    expect(draftSummaryLines({ ...draft().draft, operation: 'clear' } as never, [], L))
      .toEqual(['Azzera tutti i filtri']);
  });

  it('summarizes people + favorites + semantic + best-K', () => {
    const lines = draftSummaryLines(
      { ...draft().draft, peopleInclude: ['x'], peopleMatch: 'all', favorite: true } as never,
      ['Anna'], L,
    );
    expect(lines.some((l) => l.includes('Anna'))).toBe(true);
    expect(lines).toContain('Solo preferite');
    expect(lines.some((l) => l.includes('mare al tramonto'))).toBe(true);
    expect(lines.some((l) => l.includes('Migliori 300'))).toBe(true);
  });
});
