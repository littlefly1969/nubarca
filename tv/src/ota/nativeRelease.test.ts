import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import {
  apkDownloadUrl,
  decideUpdatePath,
  expectedApkFileName,
  parseReleaseDescriptor,
  releaseDescriptorUrl,
  type NativeRelease,
} from './nativeRelease.ts';

const expected = { package: 'it.littlefly.nubarca.tv', channel: 'production' };
const sha = 'a'.repeat(64);

const valid = {
  schemaVersion: 1,
  package: 'it.littlefly.nubarca.tv',
  version: '1.0.5',
  versionCode: 7,
  runtimeVersion: 'nubarca-tv-native-6',
  channel: 'production',
  apkFile: 'nubarca-tv-v7.apk',
  apkSha256: sha,
  apkBytes: 84_000_000,
};

function parse(overrides: Record<string, unknown> = {}) {
  return parseReleaseDescriptor(JSON.stringify({ ...valid, ...overrides }), expected);
}

function release(versionCode: number): NativeRelease {
  const parsed = parseReleaseDescriptor(
    JSON.stringify({ ...valid, versionCode, apkFile: expectedApkFileName(versionCode) }),
    expected,
  );
  assert.equal(parsed.ok, true);
  return (parsed as { ok: true; release: NativeRelease }).release;
}

// --- precedence --------------------------------------------------------------

test('a higher published versionCode is a native update', () => {
  assert.equal(decideUpdatePath(release(7), 6), 'native');
});

test('an equal versionCode leaves the OTA flow in charge', () => {
  // Same native contract: nothing to install, and a compatible JS update is
  // exactly what OTA is for.
  assert.equal(decideUpdatePath(release(6), 6), 'ota');
});

test('a lower published versionCode never downgrades the device', () => {
  // A rolled-back pointer, or a device that already moved ahead. Android would
  // refuse the install anyway; offering it would just be a broken button.
  assert.equal(decideUpdatePath(release(5), 6), 'ota');
  assert.equal(decideUpdatePath(null, 6), 'ota');
});

test('a native release wins over an OTA that is already downloaded', () => {
  // That OTA belongs to the runtime the device is LEAVING. Reloading into it
  // first would be a pointless restart into the old native contract.
  assert.equal(decideUpdatePath(release(7), 6, true), 'native');
  assert.equal(decideUpdatePath(release(6), 6, true), 'ota');
});

test('a differing runtime string alone never authorizes a native install', () => {
  // versionCode plus package/signer are the Android upgrade authority. Runtime
  // strings are the OTA compatibility authority and nothing more.
  const parsed = parse({ runtimeVersion: 'nubarca-tv-native-99' });
  assert.equal(parsed.ok, true);
  assert.equal(decideUpdatePath((parsed as { ok: true; release: NativeRelease }).release, 7), 'ota');
});

// --- descriptor validation ---------------------------------------------------

test('a well-formed descriptor for this app parses', () => {
  const parsed = parse();
  assert.equal(parsed.ok, true);
  assert.deepEqual((parsed as { ok: true; release: NativeRelease }).release, valid);
});

test('a descriptor for another package is refused', () => {
  assert.deepEqual(parse({ package: 'it.littlefly.nubarca' }), { ok: false, reason: 'wrong-package' });
  assert.deepEqual(parse({ package: 'com.example.tv' }), { ok: false, reason: 'wrong-package' });
});

test('a descriptor for another channel is refused', () => {
  assert.deepEqual(parse({ channel: 'beta' }), { ok: false, reason: 'wrong-channel' });
});

test('a malformed hash is refused', () => {
  for (const apkSha256 of ['', 'zz', sha.toUpperCase(), sha.slice(1), `${sha}a`, 123]) {
    assert.deepEqual(parse({ apkSha256 }), { ok: false, reason: apkSha256 === 123 ? 'malformed' : 'invalid-hash' });
  }
});

test('an APK file name that is anything but a bare pinned name is refused', () => {
  for (const apkFile of [
    '../../etc/passwd',
    '/download/tv/nubarca-tv-v7.apk',
    'https://elsewhere.example.com/evil.apk',
    'nubarca-tv-v7.apk?x=1',
    'nubarca-tv-v7.apk#frag',
    'sub/nubarca-tv-v7.apk',
    'nubarca-tv-v7.apk\\..\\evil',
    'evil.apk',
    '',
  ]) {
    assert.deepEqual(parse({ apkFile }), { ok: false, reason: 'invalid-file' }, apkFile);
  }
});

test('an APK file name that disagrees with the versionCode is refused', () => {
  // This is the mismatch that would otherwise let a descriptor advertise v8
  // while pointing at the bytes of v7.
  assert.deepEqual(parse({ apkFile: 'nubarca-tv-v6.apk' }), { ok: false, reason: 'invalid-file' });
  assert.deepEqual(parse({ versionCode: 8 }), { ok: false, reason: 'invalid-file' });
});

test('a malformed schema is refused', () => {
  assert.deepEqual(parseReleaseDescriptor('not json', expected), { ok: false, reason: 'malformed' });
  // The SPA fallback answers a missing file with HTTP 200 and index.html.
  assert.deepEqual(parseReleaseDescriptor('<!doctype html><html></html>', expected),
    { ok: false, reason: 'malformed' });
  assert.deepEqual(parseReleaseDescriptor('null', expected), { ok: false, reason: 'malformed' });
  assert.deepEqual(parseReleaseDescriptor('[]', expected), { ok: false, reason: 'malformed' });
  assert.deepEqual(parse({ schemaVersion: 2 }), { ok: false, reason: 'malformed' });
  assert.deepEqual(parseReleaseDescriptor(
    JSON.stringify({ ...valid, unexpected: true }), expected), { ok: false, reason: 'malformed' });
  const { apkBytes: _dropped, ...missing } = valid;
  assert.deepEqual(parseReleaseDescriptor(JSON.stringify(missing), expected),
    { ok: false, reason: 'malformed' });
  assert.deepEqual(parse({ runtimeVersion: '../escape' }), { ok: false, reason: 'malformed' });
});

test('invalid versions and sizes are refused', () => {
  assert.deepEqual(parse({ version: '1.0' }), { ok: false, reason: 'invalid-version' });
  assert.deepEqual(parse({ version: 'latest' }), { ok: false, reason: 'invalid-version' });
  for (const versionCode of [0, -1, 1.5, '7', null]) {
    assert.deepEqual(parse({ versionCode }), { ok: false, reason: 'invalid-version' },
      String(versionCode));
  }
  for (const apkBytes of [0, -1, 1.5]) {
    assert.deepEqual(parse({ apkBytes }), { ok: false, reason: 'invalid-size' }, String(apkBytes));
  }
});

// --- URL composition ---------------------------------------------------------

test('URLs are composed from the pinned origin, never from the descriptor', () => {
  const origin = 'https://nubarca.example.com';
  assert.equal(releaseDescriptorUrl(origin), `${origin}/download/tv/nubarca-tv.release.json`);
  assert.equal(apkDownloadUrl(origin, release(7)), `${origin}/download/tv/nubarca-tv-v7.apk`);
  assert.equal(apkDownloadUrl(`${origin}/`, release(7)), `${origin}/download/tv/nubarca-tv-v7.apk`);
});

// --- publisher/client agreement ----------------------------------------------

test('a descriptor generated by the publisher is accepted by the client parser', () => {
  // The two sides validate independently on purpose (one produces, one treats
  // the result as untrusted input). This is what keeps them from drifting.
  const require = createRequire(import.meta.url);
  const here = fileURLToPath(new URL('.', import.meta.url));
  const { buildReleaseDescriptor } = require(`${here}../../scripts/release-descriptor.cjs`);
  const { readReleaseContract } = require(`${here}../../scripts/release-contract.cjs`);
  const contract = readReleaseContract();
  const generated = buildReleaseDescriptor(contract, sha, 84_000_000);
  const parsed = parseReleaseDescriptor(JSON.stringify(generated), {
    package: contract.package, channel: contract.channel,
  });
  assert.equal(parsed.ok, true, `publisher output rejected: ${JSON.stringify(parsed)}`);
  assert.equal((parsed as { ok: true; release: NativeRelease }).release.versionCode,
    contract.versionCode);
});
