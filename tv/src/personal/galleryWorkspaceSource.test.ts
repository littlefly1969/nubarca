import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const galleryScreen = readFileSync(
  new URL('../screens/PersonalGalleryScreen.tsx', import.meta.url),
  'utf8',
);
const appConfig = readFileSync(new URL('../../app.config.js', import.meta.url), 'utf8');

test('native gallery routes search through one workspace and typed bulk panels', () => {
  assert.match(galleryScreen, /GalleryWorkspacePanel/);
  assert.match(galleryScreen, /GalleryDestinationPanel/);
  assert.match(galleryScreen, /GalleryTrashConfirmPanel/);
  assert.doesNotMatch(galleryScreen, /GalleryFiltersPanel/);
  assert.doesNotMatch(galleryScreen, /GallerySortPanel/);
  assert.doesNotMatch(galleryScreen, /GallerySearchPanel/);
  assert.doesNotMatch(galleryScreen, /GalleryCommandPanel/);
});

test('the native video build uses the current OTA runtime contract', () => {
  // The identity values themselves are pinned in scripts/appIdentity.test.mjs,
  // which reads the evaluated config rather than its source text. What matters
  // here is only the OTA launch contract.
  assert.match(appConfig, /NUBARCA_TV_RUNTIME_VERSION \|\| RELEASE_RUNTIME/);
  assert.match(appConfig, /versionCode: RELEASE_VERSION_CODE/);
  assert.match(appConfig, /checkAutomatically: 'NEVER'/);
  assert.match(appConfig, /fallbackToCacheTimeout: 0/);
});
