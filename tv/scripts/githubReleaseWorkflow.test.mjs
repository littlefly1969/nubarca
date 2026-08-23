import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { resolve } from 'node:path';

const tvRoot = resolve(import.meta.dirname, '..');
const workflow = readFileSync(
  resolve(tvRoot, '../.github/workflows/tv-native-release.yml'),
  'utf8',
);

test('native release workflow is manual and production-environment scoped', () => {
  assert.match(workflow, /workflow_dispatch:/);
  assert.doesNotMatch(workflow, /^\s+(?:push|pull_request|schedule):/m);
  assert.match(workflow, /environment: tv-production/);
  assert.match(workflow, /cancel-in-progress: false/);
});

test('native releases are gated to main and publishing confirms versionCode', () => {
  assert.match(workflow, /GITHUB_REF.*refs\/heads\/main/);
  assert.match(workflow, /CONFIRMED_VERSION_CODE.*version_code/);
  assert.match(workflow, /confirm_version_code:/);
});

test('private signing material comes only from GitHub environment secrets', () => {
  for (const secret of [
    'NUBARCA_TV_RELEASE_KEYSTORE_BASE64',
    'NUBARCA_TV_RELEASE_STORE_PASSWORD',
    'NUBARCA_TV_RELEASE_KEY_ALIAS',
    'NUBARCA_TV_RELEASE_KEY_PASSWORD',
    'NUBARCA_TV_OTA_CERTIFICATE_BASE64',
    'NUBARCA_TV_DEPLOY_SSH_PRIVATE_KEY',
  ]) {
    assert.ok(workflow.includes(`secrets.${secret}`), `${secret} must be environment-scoped`);
  }
  assert.doesNotMatch(workflow, /TV_OTA_PRIVATE_KEY_PATH/);
  assert.doesNotMatch(workflow, /PRIVATE KEY-----/);
  assert.ok(
    workflow.indexOf('Run TV tests') < workflow.indexOf('Materialize signing inputs'),
    'private signing files must not exist while repository tests execute',
  );
});

test('the validated artifact exists before production activation', () => {
  const build = workflow.indexOf('./gradlew --no-daemon assembleRelease');
  const validate = workflow.indexOf('./deploy/validate-tv-apk.sh');
  const upload = workflow.indexOf('actions/upload-artifact@v4');
  const publish = workflow.indexOf('./deploy/publish-tv-apk.sh');
  const verify = workflow.indexOf('Verify published bytes over HTTPS');
  assert.ok(build >= 0 && build < validate);
  assert.ok(validate < upload);
  assert.ok(upload < publish);
  assert.ok(publish < verify);
});

test('SSH publication pins known hosts instead of discovering trust at release time', () => {
  assert.match(workflow, /NUBARCA_TV_DEPLOY_KNOWN_HOSTS/);
  assert.match(workflow, /ssh-add -/);
  assert.doesNotMatch(workflow, /ssh-keyscan/);
});
