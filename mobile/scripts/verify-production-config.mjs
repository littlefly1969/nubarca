// CI GATE for the production Expo config (acceptance §11).
//
// The fail-closed rules in app.config.js are VERIFIED through the REAL Expo
// pipeline — `expo config` evaluates the file exactly like a build would:
//   * production without NUBARCA_PUBLIC_ORIGIN must FAIL;
//   * production with an http:// origin must FAIL;
//   * production with an https:// origin must PASS and ship with Android
//     cleartext DISABLED and iOS arbitrary loads OFF.
// Any deviation exits non-zero and fails the mobile lane.

import { spawnSync } from 'node:child_process';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const mobileDir = new URL('..', import.meta.url).pathname;
const require = createRequire(import.meta.url);
const release = require('../release-contract.json');

function runExpoConfig(nodeEnv, baseUrl) {
  const env = { ...process.env, NODE_ENV: nodeEnv };
  delete env.NUBARCA_PUBLIC_ORIGIN;
  delete env.EXPO_PUBLIC_NUBARCA_API_BASE_URL;
  delete env.NUBARCA_MOBILE_API_BASE_URL;
  if (baseUrl !== undefined) env.NUBARCA_PUBLIC_ORIGIN = baseUrl;

  const res = spawnSync(
    'npx',
    ['expo', 'config', '--type', 'introspect', '--json'],
    { env, encoding: 'utf8', cwd: mobileDir },
  );
  return {
    ok: res.status === 0,
    stdout: res.stdout ?? '',
    stderr: res.stderr ?? '',
  };
}

let failures = 0;
function check(name, fn) {
  try {
    fn();
    console.log(`✔ ${name}`);
  } catch (err) {
    failures += 1;
    console.error(`✖ ${name}`);
    console.error(err?.message ?? err);
  }
}

check('production WITHOUT an API origin fails closed', () => {
  const r = runExpoConfig('production', undefined);
  assert.notEqual(r.ok, true, `expo config should have failed.\n${r.stderr}`);
});

check('production with an http:// origin fails closed', () => {
  const r = runExpoConfig('production', 'http://insecure.example');
  assert.notEqual(r.ok, true, `cleartext production should have failed.\n${r.stderr}`);
});

check('production with an https:// origin passes with cleartext OFF', () => {
  const r = runExpoConfig('production', 'https://example.invalid');
  assert.equal(r.ok, true, `expo config failed:\n${r.stderr}`);
  const cfg = JSON.parse(r.stdout);
  assert.equal(cfg.extra.apiBaseUrl, 'https://example.invalid');
  assert.equal(cfg.extra.releaseVersion, release.version);
  assert.equal(cfg.extra.releaseVersionCode, release.versionCode);
  assert.equal(cfg.android?.package, release.package);
  assert.equal(cfg.android?.versionCode, release.versionCode);
  assert.equal(
    cfg.android?.usesCleartextTraffic,
    false,
    'Android cleartext MUST be disabled in production',
  );
  const ats = cfg.ios?.infoPlist?.NSAppTransportSecurity;
  assert.equal(
    ats?.NSAllowsArbitraryLoads,
    false,
    'iOS arbitrary loads MUST be disabled in production',
  );
});

check('production rejects a URL that is not exactly an origin', () => {
  for (const value of [
    'https://example.invalid/path',
    'https://example.invalid?query=yes',
    'https://user@example.invalid',
  ]) {
    const r = runExpoConfig('production', value);
    assert.notEqual(r.ok, true, `${value} should have failed`);
  }
});

check('development still allows the loopback default over cleartext', () => {
  const r = runExpoConfig('development', undefined);
  assert.equal(r.ok, true, `dev config failed:\n${r.stderr}`);
  const cfg = JSON.parse(r.stdout);
  assert.equal(cfg.android?.usesCleartextTraffic, true);
});

if (failures > 0) {
  console.error(`\n${failures} production-config gate check(s) failed`);
  process.exit(1);
}
console.log('\nProduction config gate: all checks passed');
