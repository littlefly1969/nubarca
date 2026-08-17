// The publisher half of the native update path: what the release descriptor
// contains, and — just as importantly — WHEN it becomes visible.
//
// The ordering assertions read the publisher script rather than running it,
// because running it means an SSH target and a signed APK. What they protect is
// a property of the script's TEXT and is exactly the property that breaks
// silently: a descriptor written before the bytes it names would advertise an
// install that cannot succeed, and no test that only checks JSON fields would
// notice.

import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const tvRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const {
  apkFileName, buildReleaseDescriptor, describeApk,
} = require(resolve(tvRoot, 'scripts/release-descriptor.cjs'));
const { readReleaseContract } = require(resolve(tvRoot, 'scripts/release-contract.cjs'));
const publisher = readFileSync(resolve(tvRoot, '../deploy/publish-tv-apk.sh'), 'utf8');
const release = readReleaseContract();

test('every descriptor value derives from the release contract and the real APK', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'nubarca-tv-descriptor-'));
  try {
    const bytes = Buffer.from('not really an APK, but really these bytes');
    const apk = join(directory, 'app-release.apk');
    writeFileSync(apk, bytes);
    const descriptor = await describeApk(apk);
    assert.deepEqual(descriptor, {
      schemaVersion: 1,
      package: release.package,
      version: release.version,
      versionCode: release.versionCode,
      runtimeVersion: release.runtimeVersion,
      channel: release.channel,
      apkFile: `nubarca-tv-v${release.versionCode}.apk`,
      apkSha256: createHash('sha256').update(bytes).digest('hex'),
      apkBytes: bytes.length,
    });
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('the immutable APK name carries the exact versionCode', () => {
  // Publishing v8 must never be able to overwrite the bytes a device is still
  // being offered as v7.
  assert.equal(apkFileName(7), 'nubarca-tv-v7.apk');
  assert.equal(apkFileName(release.versionCode), `nubarca-tv-v${release.versionCode}.apk`);
  assert.equal(buildReleaseDescriptor(release, 'b'.repeat(64), 1).apkFile,
    `nubarca-tv-v${release.versionCode}.apk`);
});

test('the descriptor builder refuses values it cannot have measured', () => {
  assert.throws(() => buildReleaseDescriptor(release, 'not-a-hash', 1), /apkSha256/);
  assert.throws(() => buildReleaseDescriptor(release, 'B'.repeat(64), 1), /apkSha256/);
  assert.throws(() => buildReleaseDescriptor(release, 'b'.repeat(64), 0), /apkBytes/);
  assert.throws(() => buildReleaseDescriptor(release, 'b'.repeat(64), 1.5), /apkBytes/);
});

test('the descriptor is generated, never hand-maintained, by the publisher', () => {
  assert.match(publisher, /release-descriptor\.cjs/);
  // No literal identity typed into the shell script: one contract, one source.
  assert.doesNotMatch(publisher, new RegExp(`"?${release.version.replace(/\./g, '\\.')}"?`));
  assert.doesNotMatch(publisher, /schemaVersion/);
});

test('the release descriptor is published LAST, after the bytes it names', () => {
  const versionedUpload = publisher.indexOf('$versioned_temporary');
  const shaVerification = publisher.indexOf('remote_versioned_sha');
  const canonicalUpload = publisher.indexOf('$temporary_name');
  const descriptorPublish = publisher.indexOf('$descriptor_temporary');
  for (const [name, index] of Object.entries({
    versionedUpload, shaVerification, canonicalUpload, descriptorPublish,
  })) {
    assert.ok(index > 0, `${name} must appear in the publisher`);
  }
  assert.ok(versionedUpload < shaVerification,
    'the immutable APK must be uploaded before its remote hash is verified');
  assert.ok(shaVerification < canonicalUpload,
    'the remote hash must be verified before the canonical artifact is replaced');
  assert.ok(canonicalUpload < descriptorPublish,
    'the descriptor is the activation pointer and must be published last');
});

test('a publication that fails before activation cannot change the active descriptor', () => {
  // `set -e` is what makes the ordering above a guarantee rather than a habit:
  // any failure in steps 1-3 aborts before step 4 runs, so devices keep being
  // offered the release that is still fully published.
  assert.match(publisher, /^set -euo pipefail$/m);
  const descriptorPublish = publisher.indexOf('$descriptor_temporary');
  const failures = [...publisher.matchAll(/^\s*exit 1$/gm)].map((match) => match.index ?? -1);
  assert.ok(failures.length >= 2, 'the publisher must fail closed on a hash mismatch');
  for (const index of failures) {
    assert.ok(index < descriptorPublish,
      'every mismatch guard must run before the descriptor is activated');
  }
  // Activation is a rename over a fully uploaded temporary file, never a
  // truncating write in place.
  assert.match(publisher, /mv -f '\$remote_dir\/\$descriptor_temporary' '\$remote_dir\/\$descriptor_name'/);
});

test('the existing canonical APK publication remains available', () => {
  // The public sideload contract does not change: /tv.apk, the canonical
  // nubarca-tv.apk and its checksum keep working exactly as before.
  assert.match(publisher, /remote_name="nubarca-tv\.apk"/);
  assert.match(publisher, /\$\{remote_name\}\.sha256/);
  assert.match(publisher, /\/tv\.apk/);
  assert.match(publisher, /deploy\/validate-tv-apk\.sh/);
  const validation = publisher.indexOf('deploy/validate-tv-apk.sh');
  assert.ok(validation > 0 && validation < publisher.indexOf('release-descriptor.cjs'),
    'the existing local validator still runs first and stays authoritative');
});

test('the descriptor and the immutable APK are actually served', () => {
  // The publisher writing a file the reverse proxy will not serve is a silent
  // failure: the device would fetch the SPA fallback and see HTTP 200 HTML.
  const nginx = readFileSync(resolve(tvRoot, '../frontend/nginx.conf'), 'utf8');
  assert.match(nginx, /location = \/download\/tv\/nubarca-tv\.release\.json/);
  assert.match(nginx, /location ~ \^\/download\/tv\/nubarca-tv-v\[0-9\]\+\\\.apk\$/);
  // The activation pointer must never be served stale.
  const descriptorBlock = nginx.slice(nginx.indexOf('location = /download/tv/nubarca-tv.release.json'));
  assert.match(descriptorBlock.slice(0, 400), /no-cache, no-store, must-revalidate/);
});
