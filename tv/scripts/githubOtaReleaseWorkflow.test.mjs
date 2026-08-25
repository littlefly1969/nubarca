import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import test from 'node:test';

const repoRoot = resolve(import.meta.dirname, '../..');
const workflow = readFileSync(resolve(repoRoot, '.github/workflows/tv-ota-release.yml'), 'utf8');
const pullPublisher = readFileSync(resolve(repoRoot, 'deploy/pull-publish-tv-ota-image.sh'), 'utf8');
const packageJson = readFileSync(resolve(repoRoot, 'tv/package.json'), 'utf8');

test('OTA release is manual, main-only, confirmed, and production-environment scoped', () => {
  assert.match(workflow, /workflow_dispatch:/);
  assert.doesNotMatch(workflow, /^\s+(?:push|pull_request|schedule):/m);
  assert.match(workflow, /environment: tv-production/);
  assert.match(workflow, /GITHUB_REF.*refs\/heads\/main/);
  assert.match(workflow, /CONFIRMED_GIT_SHA.*GITHUB_SHA/);
  assert.match(workflow, /cancel-in-progress: false/);
});

test('GitHub alone receives OTA private signing material after source tests pass', () => {
  assert.match(workflow, /secrets\.NUBARCA_TV_OTA_PRIVATE_KEY_BASE64/);
  assert.match(workflow, /secrets\.NUBARCA_TV_OTA_CERTIFICATE_BASE64/);
  assert.ok(
    workflow.indexOf('Run TV tests before exposing signing material') < workflow.indexOf('Materialize OTA signing inputs'),
  );
  assert.doesNotMatch(pullPublisher, /NUBARCA_TV_OTA_PRIVATE_KEY_BASE64|BEGIN PRIVATE KEY/);
  assert.match(pullPublisher, /unset TV_OTA_PRIVATE_KEY_PATH/);
  assert.doesNotMatch(packageJson, /publish:ota/);
});

test('validated OTA bytes are published immutably and GitHub never contacts production', () => {
  const bundle = workflow.indexOf('Build and validate signed OTA bundle');
  const upload = workflow.indexOf('actions/upload-artifact@v4');
  const imageVerify = workflow.indexOf('Verify OTA image contains the exact validated bundle');
  const push = workflow.indexOf("docker push '${{ steps.image.outputs.image }}'");
  assert.ok(bundle >= 0 && bundle < upload && upload < imageVerify && imageVerify < push);
  assert.match(workflow, /image=\$repository:\$\{GITHUB_SHA\}/);
  assert.match(workflow, /Manifest\.Digest/);
  assert.match(workflow, /steps\.image\.outputs\.repository.*steps\.digest\.outputs\.digest/);
  assert.doesNotMatch(workflow, /steps\.image\.outputs\.image.*steps\.digest\.outputs\.digest/);
  assert.doesNotMatch(workflow, /ssh(?:-keyscan|-add)?\b/);
  assert.doesNotMatch(workflow, /NUBARCA_PRODUCTION_SSH/);
});

test('production pulls only by digest and verifies provenance before atomic import', () => {
  assert.match(pullPublisher, /nubarca-tv-ota@sha256:/);
  assert.match(pullPublisher, /source_sha.*head_sha/);
  assert.match(pullPublisher, /runtime_label.*expected_runtime/);
  assert.match(pullPublisher, /ota\.mjs import-bundle/);
  assert.ok(
    pullPublisher.indexOf('source_sha.*head_sha') < pullPublisher.indexOf('ota.mjs import-bundle')
      || pullPublisher.indexOf('[[ "$source_sha" == "$head_sha" ]]') < pullPublisher.indexOf('ota.mjs import-bundle'),
  );
});
