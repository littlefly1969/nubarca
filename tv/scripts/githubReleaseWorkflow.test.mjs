import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { resolve } from 'node:path';

const tvRoot = resolve(import.meta.dirname, '..');
const repoRoot = resolve(tvRoot, '..');
const workflow = readFileSync(
  resolve(repoRoot, '.github/workflows/tv-native-release.yml'),
  'utf8',
);
const pullPublisher = readFileSync(
  resolve(repoRoot, 'deploy/pull-publish-tv-apk-image.sh'),
  'utf8',
);

test('native release workflow is manual and production-environment scoped', () => {
  assert.match(workflow, /workflow_dispatch:/);
  assert.doesNotMatch(workflow, /^\s+(?:push|pull_request|schedule):/m);
  assert.match(workflow, /environment: tv-production/);
  assert.match(workflow, /cancel-in-progress: false/);
});

test('JDK setup does not require the generated Android tree to exist', () => {
  const setupJdk = workflow.match(/- name: Set up JDK[\s\S]*?(?=\n\s+- name:)/)?.[0] ?? '';
  assert.doesNotMatch(setupJdk, /cache:\s*gradle/);
});

test('native releases are gated to main and publishing confirms versionCode', () => {
  assert.match(workflow, /GITHUB_REF.*refs\/heads\/main/);
  assert.match(workflow, /CONFIRMED_VERSION_CODE.*version_code/);
  assert.match(workflow, /confirm_version_code:/);
});

test('only APK signing inputs come from environment secrets', () => {
  for (const secret of [
    'NUBARCA_TV_RELEASE_KEYSTORE_BASE64',
    'NUBARCA_TV_RELEASE_STORE_PASSWORD',
    'NUBARCA_TV_RELEASE_KEY_ALIAS',
    'NUBARCA_TV_RELEASE_KEY_PASSWORD',
    'NUBARCA_TV_OTA_CERTIFICATE_BASE64',
  ]) {
    assert.ok(workflow.includes(`secrets.${secret}`), `${secret} must be environment-scoped`);
  }
  assert.doesNotMatch(workflow, /TV_OTA_PRIVATE_KEY_PATH/);
  assert.doesNotMatch(workflow, /NUBARCA_TV_DEPLOY_SSH_PRIVATE_KEY/);
  assert.doesNotMatch(workflow, /PRIVATE KEY-----/);
  assert.ok(
    workflow.indexOf('Run TV tests') < workflow.indexOf('Materialize signing inputs'),
    'private signing files must not exist while repository tests execute',
  );
});

test('validated bytes are published to GHCR before server-side activation', () => {
  const build = workflow.indexOf('./gradlew --no-daemon assembleRelease');
  const validate = workflow.indexOf('./deploy/validate-tv-apk.sh');
  const bundleValidate = workflow.indexOf('deploy/validate-tv-apk-bundle.py');
  const upload = workflow.indexOf('actions/upload-artifact@v4');
  const imageVerify = workflow.indexOf('Verify APK bundle image before publication');
  const push = workflow.indexOf("docker push '${{ steps.image.outputs.image }}'");
  assert.ok(build >= 0 && build < validate);
  assert.ok(validate < bundleValidate);
  assert.ok(bundleValidate < upload);
  assert.ok(upload < imageVerify);
  assert.ok(imageVerify < push);
  assert.match(workflow, /nubarca-tv-apk:\$\{GITHUB_SHA\}/);
  assert.match(workflow, /Manifest\.Digest/);
});

test('GitHub never contacts production and the server pulls only by digest', () => {
  assert.doesNotMatch(workflow, /ssh(?:-keyscan|-add)?\b/);
  assert.doesNotMatch(workflow, /NUBARCA_PRODUCTION_SSH/);
  assert.match(pullPublisher, /nubarca-tv-apk@sha256:/);
  assert.match(pullPublisher, /source_sha.*head_sha/);
  assert.match(pullPublisher, /validate-tv-apk-bundle\.py/);
  assert.ok(
    pullPublisher.indexOf('nubarca-tv.apk.sha256') <
      pullPublisher.indexOf('nubarca-tv.release.json.tmp'),
    'descriptor must be activated after APK and checksum',
  );
});
