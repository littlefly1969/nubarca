// Semantic retrieval, end to end through the REAL application path.
//
// The previous version of this feature was modelled, tested and gated off, and
// the tests passed the whole time — they proved the pure functions agreed with
// each other and never that anything called them. These assertions follow the
// wiring instead: the filter is offered, Apply produces a canonical semantic
// REQUEST, the request goes to the canonical service, and nothing anywhere
// degrades into substring search.

import assert from 'node:assert/strict';
import test from 'node:test';
import {
  emptyIdentity, isRelevanceOrdered, isSemanticActive, queryFingerprint, semanticToWire,
  SEMANTIC_RETRIEVAL_AVAILABLE, activeFilterCount, clearActiveFilters, cloneMediaFilters,
  type MediaKindScope, type MediaWorkspaceIdentity,
} from './mediaWorkspaceQuery.ts';
import { resolveTvFilterFocus, tvFilterRows } from './tvFilterCatalog.ts';
import { semanticRoute } from './mediaWorkspaceQuery.ts';
import { read } from '../testing/sourceText.ts';

// C# strips through the same helper: `//` and `/* */` mean the same there.
const src = (path: string) => read(import.meta.url, path);

const screen = src('../screens/PersonalLibraryScreen.tsx');
const api = src('../api/personalMedia.ts');
const panel = src('../screens/library/LibraryFilterPanel.tsx');
const catalog = src('./tvFilterCatalog.ts');
const endpoints = src('../../../src/NubArca.Api/Endpoints/TvEndpoints.cs');
const service = src('../../../src/NubArca.Api/Tv/TvPersonalMediaService.cs');

const LIBRARY = { kind: 'library' } as const;
function withQuery(kind: MediaKindScope, visualQuery: string): MediaWorkspaceIdentity {
  const identity = { ...emptyIdentity(LIBRARY), mediaKind: kind };
  identity.filters.common.visualQuery = visualQuery;
  identity.filters.common.semanticTopK = 200;
  return identity;
}

// --------------------------------------------------- A. capability + execution

test('the capability is really enabled, not merely modelled', () => {
  assert.equal(SEMANTIC_RETRIEVAL_AVAILABLE, true);
});

test('Apply causes a canonical SEMANTIC request, not a structural one', () => {
  // The bar the gated version failed: showing the row in the catalog is not
  // execution. The screen must ROUTE on the same predicate.
  assert.match(screen, /isSemanticActive\(target\)\s*\?\s*searchPersonalMediaSemantic\(target, cursor, PAGE_SIZE\)\s*:\s*listPersonalMedia\(target, cursor, PAGE_SIZE\)/);
  assert.match(api, /\/api\/tv\/personal\/media\/semantic\$\{qs\}/);
});

test('the request reaches the canonical service, with no TV ranking', () => {
  assert.match(service, /_semantic\.SearchAsync\(\s*ownerUserId, query, kind, limit, cursor, filters/);
  // Comment-stripped: the service's own prose says it adds "no embedding, no
  // vector store", and prose saying so must not be read as code doing so.
  const tvCode = [service, screen, api].join('\n');
  for (const forbidden of [/cosine/i, /embedding/i, /new Vector/i, /rank\(/i, /score\s*\*/]) {
    assert.doesNotMatch(tvCode, forbidden, `the TV must not implement retrieval: ${forbidden}`);
  }
});

// --------------------------------------------------------------------- B. kind

test('All, Photos and Videos each produce a semantic query carrying their kind', () => {
  for (const kind of ['all', 'image', 'video'] as const) {
    const identity = withQuery(kind, 'un cane sulla spiaggia');
    assert.equal(isSemanticActive(identity), true, kind);
    const wire = semanticToWire(identity, null);
    assert.equal(wire.q, 'un cane sulla spiaggia', kind);
    assert.equal(wire.kind, kind, 'kind is carried in the canonical query, not post-filtered');
  }
});

test('kind discrimination happens in the query, never as a UI post-filter', () => {
  assert.doesNotMatch(screen, /\.filter\([^)]*kind\s*===/,
    'results must not be narrowed by kind after they arrive');
});

test('the filter is offered on every kind', () => {
  for (const kind of ['all', 'image', 'video'] as const) {
    const identity = { ...emptyIdentity(LIBRARY), mediaKind: kind };
    const rows = tvFilterRows(identity, identity.filters).map((r) => r.id);
    assert.ok(rows.includes('semanticQuery'), kind);
  }
});

// -------------------------------------------------------------------- C. state

test('an empty query does not apply the filter', () => {
  for (const value of ['', '   ']) {
    const identity = withQuery('all', value);
    assert.equal(isSemanticActive(identity), false, JSON.stringify(value));
    assert.deepEqual(semanticToWire(identity, null), {}, 'and it reaches no wire');
  }
});

test('Clear removes only the semantic query', () => {
  const identity = withQuery('all', 'tramonto');
  identity.filters.common.favorite = true;
  identity.filters.common.minRating = 4;
  const cleared = clearActiveFilters(identity);
  // clearActiveFilters clears the whole COMMON group by design; what matters
  // here is the dedicated path the screen uses for the unavailable state,
  // which touches only the two semantic fields.
  assert.equal(cleared.common.visualQuery, '');
  assert.equal(cleared.common.semanticTopK, 0);
  assert.match(screen, /common: \{ \.\.\.current\.filters\.common, visualQuery: '', semanticTopK: 0 \}/);
});

test('the applied query is reconstructible from canonical state alone', () => {
  // No shadow copy: the panel reads and writes filters.common.visualQuery.
  assert.match(panel, /value: filters\.common\.visualQuery\.length > 0 \? filters\.common\.visualQuery : anyLabel/);
  assert.match(panel, /patchCommon\(\{\s*visualQuery,/);
  for (const shadow of [/selectedSemantic/, /semanticDraft/, /appliedSemantic/, /useState.*[Ss]emantic/]) {
    assert.doesNotMatch(screen + panel, shadow, `parallel semantic state: ${shadow}`);
  }
});

test('an active semantic query counts as an applied filter', () => {
  assert.equal(activeFilterCount(withQuery('all', 'neve')), 1);
  assert.equal(activeFilterCount(withQuery('all', '')), 0);
});

// -------------------------------------------------------------- D. composition

test('semantic composes with the filters the canonical route supports', () => {
  const identity = withQuery('all', 'montagna');
  identity.filters.common.favorite = true;
  identity.filters.common.minRating = 3;
  identity.filters.common.dateTakenFrom = '2026-01-01T00:00:00.000Z';
  identity.filters.common.dateTakenTo = '2026-06-01T00:00:00.000Z';
  const wire = semanticToWire(identity, null);
  assert.equal(wire.favorite, 'true');
  assert.equal(wire.minRating, '3');
  assert.equal(wire.dateTakenFrom, '2026-01-01T00:00:00.000Z');
  assert.equal(wire.dateTakenTo, '2026-06-01T00:00:00.000Z');
  assert.equal(wire.q, 'montagna', 'and the semantic query survives composition');
});

test('semantic never cancels the other filters — on the SEMANTIC wire', () => {
  // This test used to assert against queryToWire, which is the STRUCTURAL
  // builder and is not called at all while semantic is active. It therefore
  // passed while People, GPS and duplicate-collapse were being dropped from
  // every semantic request. The wire under test must be the one actually sent.
  const identity = withQuery('image', 'cane');
  identity.filters.photo.includePeople = ['p-1'];
  identity.filters.photo.hasGps = true;
  identity.filters.photo.collapseDuplicates = true;
  identity.filters.common.favorite = true;
  identity.filters.common.metadataQuery = 'estate';

  const wire = semanticToWire(identity, null);
  assert.equal(wire.q, 'cane', 'the visual query still ranks');
  assert.equal(wire.favorite, 'true');
  assert.equal(wire.includePeople, 'p-1');
  assert.equal(wire.hasGps, 'true');
  assert.equal(wire.collapseDuplicates, 'true');
  assert.equal(wire.metadataQuery, 'estate', 'metadata text is its own parameter, not q');
});

test('every row shown as APPLIED actually reaches the semantic wire', () => {
  // The general form of the defect: a filter the user can see marked active
  // that the request never carries. Checked for all three kinds and both
  // sources, so it cannot come back on a combination nobody thought about.
  const WIRED: Record<string, string[]> = {
    metadataQuery: ['metadataQuery'], semanticQuery: ['q'],
    favorite: ['favorite'], minRating: ['minRating'],
    period: ['dateTakenFrom', 'dateTakenTo'], albumMembership: ['albumMembership'],
    people: ['includePeople', 'excludePeople'], hasGps: ['hasGps'],
    collapseDuplicates: ['collapseDuplicates'],
    durationMin: ['durationMin'], durationMax: ['durationMax'],
    minHeight: ['minHeight'], codec: ['codec'], hasAudio: ['hasAudio'],
  };
  for (const source of [{ kind: 'library' } as const, { kind: 'album', albumId: 'a-1' } as const]) {
    for (const kind of ['all', 'image', 'video'] as const) {
      const identity = { ...emptyIdentity(source), mediaKind: kind };
      const f = identity.filters;
      f.common.visualQuery = 'cane';
      f.common.semanticTopK = 200;
      f.common.metadataQuery = 'estate';
      f.common.favorite = true;
      f.common.minRating = 3;
      f.common.albumMembership = 'unassigned';
      f.common.dateTakenFrom = '2024-01-01';
      f.common.dateTakenTo = '2024-12-31';
      f.photo.includePeople = ['p-1'];
      f.photo.hasGps = true;
      f.photo.collapseDuplicates = true;
      f.video.codec = 'h264';
      f.video.hasAudio = true;
      f.video.minHeight = 1080;
      if (!isSemanticActive(identity)) continue;

      const wire = semanticToWire(identity, null);
      for (const row of tvFilterRows(identity, f).filter((r) => r.active)) {
        const keys = WIRED[row.id] ?? [];
        assert.ok(keys.some((key) => key in wire),
          `${source.kind}/${kind}: row "${row.id}" is shown APPLIED but reaches no wire key`);
      }
    }
  }
});

test('a semantic query changes the result identity', () => {
  const plain = { ...emptyIdentity(LIBRARY), mediaKind: 'all' as const };
  assert.notEqual(queryFingerprint(withQuery('all', 'cane')), queryFingerprint(plain));
  assert.notEqual(queryFingerprint(withQuery('all', 'cane')), queryFingerprint(withQuery('all', 'gatto')));
});

// ------------------------------------------------------------------ E. safety

test('the owner is derived server-side and never accepted from the television', () => {
  const handler = endpoints.slice(endpoints.indexOf('"/api/tv/personal/media/semantic"'));
  const body = handler.slice(0, handler.indexOf('.WithName('));
  assert.match(body, /ResolveTvPersonalAccessAsync\(httpContext, personal, cancellationToken\)/);
  assert.match(body, /if \(failure is not null\) return failure;/);
  // No ownerUserId parameter can be supplied by the client.
  assert.doesNotMatch(body, /\[FromQuery\][^\n]*owner/i);
  assert.doesNotMatch(body, /\[FromQuery\][^\n]*userId/i);
});

test('the resolved owner must hold the semantic permission', () => {
  const handler = endpoints.slice(endpoints.indexOf('"/api/tv/personal/media/semantic"'));
  const body = handler.slice(0, handler.indexOf('.WithName('));
  assert.match(body, /GetEffectiveAsync\(ownerUserId, cancellationToken\)/);
  assert.match(body, /Permissions\.SemanticSearchAccess/);
  assert.match(body, /return Results\.Forbid\(\);/);
});

test('unavailable retrieval fails closed — never a substring fallback', () => {
  // Backend: 503 with a sanitized token, never a 200 empty page.
  assert.match(service, /if \(!page\.Available\)/);
  assert.match(service, /new TvPersonalMediaSemanticResult\(null, false, page\.UnavailableReason, false\)/);
  const handler = endpoints.slice(endpoints.indexOf('"/api/tv/personal/media/semantic"'));
  assert.match(handler, /Status503ServiceUnavailable/);
  // Client: a distinct error type, and NO call to the structural list on that
  // path. A fallback would present metadata matches as semantic results.
  assert.match(api, /class SemanticUnavailableError/);
  const catchBlock = api.slice(api.indexOf('} catch (error) {'), api.indexOf('throw error;'));
  assert.doesNotMatch(catchBlock, /listPersonalMedia/);
  assert.match(screen, /phase: err instanceof SemanticUnavailableError \? 'semanticUnavailable'/);
});

test('scores and vectors never cross into a TV DTO', () => {
  assert.match(service, /page\.Items\.Select\(result => Project\(result\.Media\)\)/,
    'only the media survives — BestMatch carries raw similarity scores');
  assert.doesNotMatch(service, /BestMatch\s*[,)]/);
  assert.doesNotMatch(api, /score|vector|embedding/i);
});

// -------------------------------------------------------------------- F. race

test('a late response from a previous query cannot replace a newer one', () => {
  // The screen guards every continuation with the generation counter, which is
  // bumped whenever the fingerprint changes — and a semantic query changes it.
  assert.match(screen, /if \(cancelled \|\| generationRef\.current !== gen\) return;/);
  assert.match(screen, /if \(s\.generation !== gen\) return s;/);
});

// ------------------------------------------------------------ G. existing flow

test('semantic results use the same grid, viewer and paging', () => {
  // One page DTO, so nothing downstream can tell the two retrievals apart.
  assert.match(api, /Promise<TvPersonalMediaPage>/);
  assert.match(service, /new TvPersonalMediaPageDto\(/);
  // And paging folds through the same total policy.
  assert.match(screen, /mergePagedTotal\(s\.totalCount, page\.totalCount\)/);
});

// ------------------------------- the corrective slice's three defects

test('PHOTOS go to the canonical PHOTO pipeline, not the unified one', () => {
  // The requirement, and the thing an earlier version got wrong while its own
  // comments claimed otherwise: semanticRoute() said 'photo' for image and was
  // never called, so every kind reached MediaSemanticSearchService.
  assert.equal(semanticRoute(withQuery('image', 'cane')), 'photo');
  assert.equal(semanticRoute(withQuery('all', 'cane')), 'media');
  assert.equal(semanticRoute(withQuery('video', 'cane')), 'media');
  // And it now GOVERNS: the wire differs by route, and the backend branches.
  assert.match(src('./mediaWorkspaceQuery.ts'), /semanticRoute\(identity\) === 'photo'/);
  assert.match(service, /kind == MediaKindScope\.Image\s*\?\s*await SearchPhotosAsync/);
  assert.match(service, /_photoSemantic\.SearchAsync\(/);
  assert.match(service, /_semantic\.SearchAsync\(/);
});

test('photo ranking is hydrated into MediaItem, preserving relevance order', () => {
  // The reason the photo route was avoided — ImageItem carries no favorite,
  // rating or takenAt — is solved rather than accepted: the RANKED IDS are
  // re-hydrated through the unified projection.
  assert.match(service, /page\.Items\.Select\(item => item\.Id\)/);
  assert.match(service, /ListGalleryMediaByRankAsync\(\s*ownerUserId, rankedIds/);
  assert.match(service, /page\.NextCursor, page\.HasMore, page\.TotalCount/,
    'the canonical cursor and total are preserved');
});

test('a photo search inside an album is album-SCOPED', () => {
  // Without albumId the search answers an in-album question with the owner's
  // whole library — silently, because the unified route takes no album.
  const inAlbum = { ...emptyIdentity({ kind: 'album', albumId: 'alb-7' }), mediaKind: 'image' as const };
  inAlbum.filters.common.visualQuery = 'cane';
  inAlbum.filters.common.semanticTopK = 200;
  assert.equal(semanticToWire(inAlbum, null).albumId, 'alb-7');
  // In the library there is no album to scope to.
  assert.equal('albumId' in semanticToWire(withQuery('image', 'cane'), null), false);
  // Backend: AlbumId enters the canonical ImageFilters, and the album is
  // owner-validated BEFORE the search runs.
  assert.match(service, /AlbumId = albumId,/);
  assert.match(endpoints, /GetAlbumAsync\(ownerUserId, requestedAlbum, cancellationToken\) is null/);
  assert.match(endpoints, /'albumId' is only supported with kind=image/);
});

test('filters no semantic route honours are not offered at all', () => {
  // Video metadata filters have no semantic-aware path, so with a visual query
  // running they must not be settable — not merely ignored.
  const video = withQuery('video', 'cane');
  video.filters.video.codec = 'h264';
  const rows = tvFilterRows(video, video.filters).map((r) => r.id);
  for (const unsupported of ['codec', 'hasAudio', 'minHeight', 'durationMin', 'durationMax'] as const) {
    assert.ok(!rows.includes(unsupported), `${unsupported} must not be offered with semantic active`);
  }
  // Without semantic they are back.
  const plain = { ...emptyIdentity(LIBRARY), mediaKind: 'video' as const };
  assert.ok(tvFilterRows(plain, plain.filters).map((r) => r.id).includes('codec'));
});

test('the backend refuses photo-only parameters on the other kinds', () => {
  // Accepting and ignoring them is what "applied but inert" looks like from
  // the server side.
  assert.match(endpoints, /semantic-aware with kind=image/);
  assert.match(endpoints, /'hasGps', 'collapseDuplicates' and People filters are only/);
});

test('relevance order is not a choice the user can edit', () => {
  // The mirror of the filter defect, on the ORDER controls: semanticToWire
  // correctly sends neither sort nor direction, so leaving them editable
  // offers a setting the request cannot carry.
  for (const kind of ['all', 'image', 'video'] as const) {
    const searching = { ...withQuery(kind, 'cane'), sort: 'created' as const };
    assert.ok(isRelevanceOrdered(searching), `${kind}: semantic must be relevance-ordered`);

    const wire = semanticToWire(searching, null);
    assert.ok(!('sort' in wire), `${kind}: sort must not reach the semantic wire`);
    assert.ok(!('direction' in wire), `${kind}: direction must not reach the semantic wire`);

    // …and browsing again restores the user's own order.
    assert.equal(isRelevanceOrdered(withQuery(kind, '')), false);
  }
});

test('the ORDER section offers relevance instead of editable rows', () => {
  // A row the panel renders as editable while the request ignores it is the
  // same class of defect as an inert filter, so this reads the PANEL.
  const order = panel.slice(panel.indexOf('isRelevanceOrdered(draftIdentity)'));
  const branch = order.slice(0, order.indexOf('</>'));
  assert.match(branch, /filters\.sort\.relevance/);
  assert.match(branch, /opensEditor=\{false\}/);
  // The editable Sort and Direction rows live in the OTHER branch.
  assert.doesNotMatch(branch, /onSelect=\{\(\) => open/);

  // 'sort' and 'direction' stay STATIC focus keys, so resolveTvFilterFocus can
  // still answer either one while neither editable row exists. The relevance
  // row must claim both, or restored focus lands on nothing.
  assert.match(branch, /focusKey === 'sort' \|\| focusKey === 'direction'/);
  assert.ok(resolveTvFilterFocus('direction', []) === 'direction',
    'direction is still honoured as a static focus key');
});

test('an identical semantic request keeps one identity, whatever the sort was', () => {
  // queryFingerprint drives request de-duplication. While relevance is in
  // charge, a sort the wire never carries must not split one request in two —
  // or the same search runs twice and the later answer wins by luck.
  const byDate = { ...withQuery('all', 'cane'), sort: 'created' as const };
  const byName = { ...withQuery('all', 'cane'), sort: 'name' as const };
  assert.equal(queryFingerprint(byDate), queryFingerprint(byName));

  const asc = { ...withQuery('all', 'cane'), direction: 'asc' as const };
  const desc = { ...withQuery('all', 'cane'), direction: 'desc' as const };
  assert.equal(queryFingerprint(asc), queryFingerprint(desc));

  // Browsing without a query still distinguishes them — this narrows the
  // fingerprint only while relevance is in charge.
  const plainAsc = { ...withQuery('all', ''), direction: 'asc' as const };
  const plainDesc = { ...withQuery('all', ''), direction: 'desc' as const };
  assert.notEqual(queryFingerprint(plainAsc), queryFingerprint(plainDesc));
});

test('albumMembership reaches the wire on every kind, and the endpoint declares it', () => {
  // The row was catalogued as always-supported and shown as a chip, but the
  // photo branch did not emit it and the endpoint had no such parameter. Both
  // halves are asserted, because either one alone leaves it inert.
  for (const kind of ['all', 'image', 'video'] as const) {
    const identity = withQuery(kind, 'cane');
    identity.filters.common.albumMembership = 'unassigned';
    assert.equal(semanticToWire(identity, null).albumMembership, 'unassigned',
      `${kind}: albumMembership must reach the semantic wire`);
  }
  // An album source scopes by albumId instead, so membership is not sent.
  const inAlbum = { ...emptyIdentity({ kind: 'album', albumId: 'a-1' }), mediaKind: 'image' as const };
  inAlbum.filters.common.visualQuery = 'cane';
  inAlbum.filters.common.albumMembership = 'unassigned';
  assert.ok(!('albumMembership' in semanticToWire(inAlbum, null)));

  assert.match(endpoints, /string\?\s+albumMembership/);
  assert.match(endpoints, /TryParseAlbumMembership\(/);
  assert.match(endpoints, /AlbumMembership = membership/);
});

test('typing a visual query hides the incompatible rows BEFORE Apply', () => {
  // The panel's real shape: what is APPLIED is still a plain structural query,
  // and the semantic one exists only in the draft the user is editing. If
  // applicability is read from the applied identity, codec/resolution/audio and
  // the duration rows stay on screen and editable for the whole time between
  // typing the query and pressing Apply — and semanticToWire then drops them.
  const applied = { ...emptyIdentity(LIBRARY), mediaKind: 'video' as const };
  assert.equal(isSemanticActive(applied), false);

  const draft = cloneMediaFilters(applied.filters);
  draft.common.visualQuery = 'cane';
  draft.video.codec = 'h264';
  draft.video.hasAudio = true;
  draft.video.minHeight = 1080;
  draft.video.durationMinSeconds = 60;

  // Passing the COMMITTED identity on purpose: that is what the panel did, and
  // the catalog must answer from the draft anyway. Handing it a pre-drafted
  // identity would test the test, not the guarantee.
  const shown: string[] = tvFilterRows(applied, draft).map((row) => row.id);
  for (const gone of ['codec', 'hasAudio', 'minHeight', 'durationMin', 'durationMax']) {
    assert.ok(!shown.includes(gone),
      `"${gone}" is still offered while the draft carries a visual query`);
  }

  // The panel's own two readings must agree: a row shown as APPLIED and a chip
  // the counter counts are the same set. They diverged because the count read
  // the draft while the rows read the committed identity.
  const active: string[] = tvFilterRows(applied, draft)
    .filter((row) => row.active).map((row) => row.id);
  assert.equal(active.length, activeFilterCount({ ...applied, filters: draft }),
    'a row is marked active that the filter counter does not count');
});

test('clearing the visual query brings the video rows back, also BEFORE Apply', () => {
  // The symmetric direction, which a one-way check would miss: semantic is what
  // is APPLIED, and the user empties the query in the draft. The rows the
  // structural request can carry must return without waiting for Apply.
  const applied = { ...emptyIdentity(LIBRARY), mediaKind: 'video' as const };
  applied.filters.common.visualQuery = 'cane';
  assert.ok(isSemanticActive(applied));
  assert.ok(!tvFilterRows(applied, applied.filters).some((row) => row.id === 'codec'));

  const draft = cloneMediaFilters(applied.filters);
  draft.common.visualQuery = '';

  const shown: string[] = tvFilterRows(applied, draft).map((row) => row.id);
  for (const back of ['codec', 'hasAudio', 'minHeight', 'durationMin', 'durationMax']) {
    assert.ok(shown.includes(back), `"${back}" did not come back when the query was cleared`);
  }
});

test('the panel decides applicability from the draft, not from what is applied', () => {
  // Read the PANEL, because the defect was a single call site passing the
  // committed identity while every other line around it used the draft.
  assert.match(panel, /const rows = tvFilterRows\(draftIdentity, filters\)/);
  // …and the catalog makes that call site impossible to get wrong anyway.
  const start = catalog.indexOf('export function tvFilterRows');
  const body = catalog.slice(start, catalog.indexOf('export ', start + 1));
  assert.match(body, /\{ \.\.\.identity, filters: draft \}/);
  assert.match(body, /tvFilterApplies\(descriptor, drafted\)/);
});
