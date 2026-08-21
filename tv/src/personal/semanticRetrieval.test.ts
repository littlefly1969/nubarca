// Semantic retrieval, end to end through the REAL application path.
//
// The previous version of this feature was modelled, tested and gated off, and
// the tests passed the whole time — they proved the pure functions agreed with
// each other and never that anything called them. These assertions follow the
// wiring instead: the filter is offered, Apply produces a canonical semantic
// REQUEST, the request goes to the canonical service, and nothing anywhere
// degrades into substring search.

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import {
  emptyIdentity, isSemanticActive, queryFingerprint, queryToWire, semanticToWire,
  SEMANTIC_RETRIEVAL_AVAILABLE, activeFilterCount, clearActiveFilters,
  type MediaKindScope, type MediaWorkspaceIdentity,
} from './mediaWorkspaceQuery.ts';
import { tvFilterRows } from './tvFilterCatalog.ts';

const read = (p: string) => readFileSync(new URL(p, import.meta.url), 'utf8');
const code = (s: string) => s.replace(/\/\*[\s\S]*?\*\//g, '')
  .split('\n').filter((l) => !l.trimStart().startsWith('//')).join('\n');

const screen = code(read('../screens/PersonalLibraryScreen.tsx'));
const api = code(read('../api/personalMedia.ts'));
const panel = code(read('../screens/library/LibraryFilterPanel.tsx'));
const endpoints = read('../../../src/NubArca.Api/Endpoints/TvEndpoints.cs');
const service = read('../../../src/NubArca.Api/Tv/TvPersonalMediaService.cs');

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
  const tvCode = [code(service), screen, api].join('\n');
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

test('semantic never cancels the other filters', () => {
  const identity = withQuery('image', 'cane');
  identity.filters.photo.includePeople = ['p-1'];
  identity.filters.common.favorite = true;
  // The structural query still carries everything it did before.
  const structural = queryToWire({ ...identity, filters: identity.filters }, null);
  assert.equal(structural.favorite, 'true');
  assert.equal(structural.includePeople, 'p-1');
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
