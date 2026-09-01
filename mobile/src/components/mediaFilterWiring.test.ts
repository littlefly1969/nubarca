// Filter UI WIRING contract (§7, §9, §11, §17, §19).
//
// The behaviour lives in tested pure modules; what this pins is that the
// screens and sheets are actually plugged into them, and — the part a type
// checker cannot see — that a control the backend would reject is never
// OFFERED for the wrong media kind.

import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';

const here = dirname(fileURLToPath(import.meta.url));
const cache = new Map<string, string>();

async function sourceOf(relativePath: string): Promise<string> {
  const cached = cache.get(relativePath);
  if (cached !== undefined) return cached;
  const text = code(await readFile(join(here, relativePath), 'utf8'));
  cache.set(relativePath, text);
  return text;
}

const PHOTOS = '../../app/(tabs)/photos.tsx';
const VIDEOS = '../../app/(tabs)/videos.tsx';

test('the filter model is the SHARED one; mobile defines no filter vocabulary', async () => {
  const state = await sourceOf('../media/mediaFilterState.ts');
  assert.match(state, /from '@nubarca\/contracts'/);
  for (const name of ['CommonMediaFilters', 'PhotoMediaFilters', 'VideoMediaFilters',
    'MediaWorkspaceFilters', 'MediaWorkspaceIdentity', 'FilterChipDescriptor']) {
    assert.doesNotMatch(state, new RegExp(`(interface|type) ${name} =?\\s*\\{`),
      `mediaFilterState redefines ${name}`);
  }
  // And it builds no query of its own.
  assert.doesNotMatch(state, /new URLSearchParams|\.set\('kind'/);
});

test('both tabs drive the list from the filter binding, not a fixed query', async () => {
  for (const screen of [PHOTOS, VIDEOS]) {
    const source = await sourceOf(screen);
    assert.match(source, /useMediaFilters\(/, screen);
    assert.match(source, /usePagedList<MediaItem>\(\(i\) => i\.id, filters\.fetchPage\)/, screen);
    // The hardcoded query the tabs used to carry is gone.
    assert.doesNotMatch(source, /sort: 'datetaken',\s*\n\s*direction: 'desc'/, screen);
  }
});

test('a new query generation refreshes the list, and paging does not', async () => {
  // §19: the effect keys on the GENERATION, and skips the first one so opening
  // a tab does not immediately double-fetch.
  for (const screen of [PHOTOS, VIDEOS]) {
    const source = await sourceOf(screen);
    assert.match(source, /const generation = filters\.generation;/, screen);
    assert.match(source, /if \(generation === firstGeneration\.current\) return;/, screen);
    assert.match(source, /void refresh\(\);\s*\n\s*\}, \[generation, refresh/, screen);
  }
});

test('a filter change releases the selection, which may no longer be in results', async () => {
  const source = await sourceOf(PHOTOS);
  const effect = source.slice(source.indexOf('firstGeneration.current = generation;'));
  assert.match(effect.slice(0, 200), /selectionState\.cancel\(\)/);
});

test('chips describe the applied query and remove one filter at a time', async () => {
  for (const screen of [PHOTOS, VIDEOS]) {
    const source = await sourceOf(screen);
    assert.match(source, /<MediaFilterChips[\s\S]*?chips=\{filters\.chips\}/, screen);
    assert.match(source, /onRemove=\{filters\.removeChip\}/, screen);
    assert.match(source, /onClearAll=\{filters\.clearAll\}/, screen);
  }
});

test('the sheet offers photo controls ONLY on the photo tab, video ONLY on video', async () => {
  // The rule the wire already enforces, enforced again at the point of offer:
  // a control the backend would 400 is never even shown.
  const sheet = await sourceOf('MediaFilterSheet.tsx');
  const photoBlock = sheet.slice(sheet.indexOf("kind === 'image' && ("), sheet.indexOf("kind === 'video' && ("));
  const videoBlock = sheet.slice(sheet.indexOf("kind === 'video' && ("));
  for (const photoOnly of ['hasGps', 'collapseDuplicates', 'filters.people']) {
    assert.ok(photoBlock.includes(photoOnly), `${photoOnly} must be inside the photo branch`);
    assert.ok(!videoBlock.includes(photoOnly), `${photoOnly} leaked into the video branch`);
  }
  for (const videoOnly of ['durationMinSeconds', 'minHeight', 'hasAudio']) {
    assert.ok(videoBlock.includes(videoOnly), `${videoOnly} must be inside the video branch`);
    assert.ok(!photoBlock.includes(videoOnly), `${videoOnly} leaked into the photo branch`);
  }
});

test('album membership is offered only where it means something', async () => {
  const sheet = await sourceOf('MediaFilterSheet.tsx');
  assert.match(sheet, /draft\.source\.kind === 'library' && \([\s\S]{0,400}albumMembership/);
});

test('the sheet edits a DRAFT and commits on apply', async () => {
  const sheet = await sourceOf('MediaFilterSheet.tsx');
  assert.match(sheet, /draftFrom\(identity\)/);
  assert.match(sheet, /if \(visible\) setDraft\(draftFrom\(identity\)\);/);
  assert.match(sheet, /onApply\(draft\.filters, draft\.sort, draft\.direction\)/);
});

test('the People picker can choose people and nothing else', async () => {
  // §15/§16: management is a separate future screen, never inside a filter.
  const picker = await sourceOf('PeopleFilterSheet.tsx');
  for (const verb of ['createPerson', 'renamePerson', 'deletePerson', 'mergePeople',
    'splitPerson', 'assignFace', 'removeFace', 'acceptSuggestion']) {
    assert.doesNotMatch(picker, new RegExp(`\\b${verb}\\b`), `picker exposes ${verb}`);
  }
  assert.match(picker, /listPeopleForFilter\(/);
  assert.match(picker, /togglePerson\(filters, item\.personId, target\)/);
});

test('the picker keys on personId, and treats the name as a label only', async () => {
  const picker = await sourceOf('PeopleFilterSheet.tsx');
  assert.match(picker, /keyExtractor=\{\(p\) => p\.personId\}/);
  assert.match(picker, /matchesPersonQuery\(p, query\)/);
  // Nothing writes a name into the filter.
  assert.doesNotMatch(picker, /includePeople.*name|name.*includePeople/);
});

test('the picker reloads its catalogue and cancels on close', async () => {
  // A stale catalogue would offer people who are gone and miss newly
  // recognised ones.
  const picker = await sourceOf('PeopleFilterSheet.tsx');
  assert.match(picker, /if \(!visible\) return undefined;/);
  assert.match(picker, /return \(\) => controller\.abort\(\);/);
});

test('the People catalogue is not fetched for an unfiltered library', async () => {
  const hook = await sourceOf('../media/useMediaFilters.ts');
  assert.match(hook, /if \(referenced\.length === 0\) return undefined;/);
});

test('visual search routes to the SEMANTIC endpoint, not the listing (§10)', async () => {
  // Two different backend operations. Flattening them would mean either losing
  // relevance ranking or sending a term the listing ignores.
  const hook = await sourceOf('../media/useMediaFilters.ts');
  assert.match(hook, /if \(isSemanticActive\(current\)\) \{/);
  assert.match(hook, /searchSemanticMedia\(\{/);
  assert.match(hook, /const query = pageQuery\(current, cursor, pageSize\);/);
});

test('visual search is offered for VIDEOS too, not just photos', async () => {
  // The field sits outside the kind === 'image' branch: the semantic route
  // ranks videos as well, and hiding it there would drop a real capability.
  const sheet = await sourceOf('MediaFilterSheet.tsx');
  const visual = sheet.indexOf("t('filters.visual')");
  const photoOnly = sheet.indexOf("kind === 'image' && (");
  assert.ok(visual !== -1 && photoOnly !== -1);
  assert.ok(visual < photoOnly, 'visual search must not be inside the photo-only branch');
});

test('an album search is CONFINED to the album, never answered from the library', async () => {
  // The whole point of the server gaining an albumId: the alternative was
  // either hiding the control in an album or returning library-wide results
  // that read as if they were the album's.
  const hook = await sourceOf('../media/useMediaFilters.ts');
  assert.match(hook, /albumId: albumId \?\? undefined,/);
  const semanticCall = hook.slice(hook.indexOf('searchSemanticMedia({'));
  assert.match(semanticCall.slice(0, 700), /albumId/);
});
