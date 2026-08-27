import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import test from 'node:test';

const repoRoot = resolve(import.meta.dirname, '..', '..');
const workflow = readFileSync(
  resolve(repoRoot, '.github/workflows/mobile-android-release.yml'),
  'utf8',
);

test('mobile native release is manual, main-only, and environment scoped', () => {
  assert.match(workflow, /workflow_dispatch:/);
  assert.doesNotMatch(workflow, /^\s+(?:push|pull_request|schedule):/m);
  assert.match(workflow, /GITHUB_REF.*refs\/heads\/main/);
  assert.match(workflow, /environment: mobile-production/);
  assert.match(workflow, /confirm_version_code:/);
  assert.match(workflow, /cancel-in-progress: false/);
});

test('source checks finish before private signing bytes are materialized', () => {
  for (const secret of [
    'NUBARCA_MOBILE_UPLOAD_KEYSTORE_BASE64',
    'NUBARCA_MOBILE_UPLOAD_STORE_PASSWORD',
    'NUBARCA_MOBILE_UPLOAD_KEY_ALIAS',
    'NUBARCA_MOBILE_UPLOAD_KEY_PASSWORD',
  ]) assert.ok(workflow.includes(`secrets.${secret}`), secret);
  assert.ok(workflow.indexOf('Run mobile tests') < workflow.indexOf('Materialize upload key'));
  assert.doesNotMatch(workflow, /PRIVATE KEY-----|storePassword\s+['"][^$]/);
});

test('one release run builds and validates both sideload APK and Play AAB', () => {
  const build = workflow.indexOf(':app:assembleRelease :app:bundleRelease');
  const bundleValidate = workflow.indexOf('"$BUNDLETOOL_JAR" validate');
  const bundleInstall = workflow.indexOf('"$BUNDLETOOL_JAR" build-apks');
  const validate = workflow.indexOf('validate-android-artifacts.mjs');
  const upload = workflow.indexOf('actions/upload-artifact@v4');
  assert.ok(build >= 0 && build < bundleValidate);
  assert.ok(bundleValidate < bundleInstall);
  assert.ok(bundleInstall < validate);
  assert.ok(validate < upload);
  assert.match(workflow, /a099cfa1543f55593bc2ed16a70a7c67fe54b1747bb7301f37fdfd6d91028e29/);
});

test('the public artifact has provenance but no production deployment path', () => {
  assert.match(workflow, /actions\/attest-build-provenance@v3/);
  assert.match(workflow, /attestations: write/);
  assert.match(workflow, /id-token: write/);
  assert.doesNotMatch(workflow, /ssh(?:-keyscan|-add)?\b|docker\s+push|packages:\s+write/);
  assert.doesNotMatch(workflow, /gh\s+release|softprops\/action-gh-release/);
});
