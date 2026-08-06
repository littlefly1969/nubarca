import assert from 'node:assert/strict';
import { createHash, randomUUID, sign } from 'node:crypto';
import {
  mkdtempSync, mkdirSync, readFileSync, readdirSync, rmSync, symlinkSync, writeFileSync,
} from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import test from 'node:test';
import { spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import {
  activate, cleanup, paths, publish, readPointer, rollback, validatePublication, validateReleaseGitSha,
} from './ota.mjs';

const require = createRequire(import.meta.url);
const { validateCodeSigningCertificate } = require('./code-signing-certificate.cjs');

const runtime = 'nubarca-tv-native-2';
const channel = 'production';
const gitSha = '1234567890abcdef1234567890abcdef12345678';
// The public origin is operator configuration; these tests supply their own.
const host = 'nubarca.example.com';
const updateUrl = `https://${host}/api/tv-app/updates`;
let root;
let key;
let certificate;
let env;

function createCertificate(directory, name) {
  const privateKey = join(directory, `${name}-key.pem`);
  const publicCertificate = join(directory, `${name}-certificate.pem`);
  for (const args of [
    ['genpkey', '-quiet', '-algorithm', 'RSA', '-pkeyopt', 'rsa_keygen_bits:2048', '-out', privateKey],
    ['req', '-x509', '-new', '-key', privateKey, '-out', publicCertificate, '-days', '1',
      '-subj', `/CN=${name}`, '-addext', 'basicConstraints=critical,CA:FALSE',
      '-addext', 'keyUsage=critical,digitalSignature', '-addext', 'extendedKeyUsage=critical,codeSigning'],
  ]) {
    const result = spawnSync('openssl', args, { encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
  }
  return { key: privateKey, certificate: publicCertificate };
}

test.beforeEach(() => {
  root = mkdtempSync(join(tmpdir(), 'nubarca-ota-test-'));
  ({ key, certificate } = createCertificate(root, 'NubArca OTA Test'));
  env = {
    TV_OTA_STORAGE_ROOT: root,
    NUBARCA_TV_RUNTIME_VERSION: runtime,
    NUBARCA_TV_OTA_CHANNEL: channel,
    NUBARCA_TV_OTA_CERTIFICATE: certificate,
    NUBARCA_TV_OTA_UPDATE_URL: updateUrl,
    TV_OTA_RELEASE_GIT_SHA: gitSha,
  };
});
test.afterEach(() => rmSync(root, { recursive: true, force: true }));

function publication(id = randomUUID(), createdAt = new Date().toISOString(), overrides = {}) {
  const publicationRuntime = overrides.runtime ?? runtime;
  const publicationChannel = overrides.channel ?? channel;
  const directory = join(root, 'publications', 'android', publicationRuntime, id);
  const relative = '_expo/static/js/android/index.hbc';
  const file = join(directory, 'files', relative);
  mkdirSync(join(directory, 'files', '_expo/static/js/android'), { recursive: true });
  writeFileSync(file, `bundle-${id}`);
  const hash = createHash('sha256').update(readFileSync(file)).digest('base64url');
  const url = `https://nubarca.example.com/api/tv-app/updates/assets/${publicationRuntime}/${id}/${relative}`;
  const manifest = {
    id, createdAt, runtimeVersion: publicationRuntime,
    launchAsset: { hash, key: hash, contentType: 'application/octet-stream', url },
    assets: [], metadata: { channel: publicationChannel, platform: 'android', gitSha },
    extra: { release: { gitSha } },
  };
  const manifestText = JSON.stringify(manifest);
  const signingKey = overrides.key ?? key;
  const signature = overrides.unsigned
    ? null
    : `sig="${sign('RSA-SHA256', Buffer.from(manifestText), readFileSync(signingKey)).toString('base64')}", keyid="main", alg="rsa-v1_5-sha256"`;
  writeFileSync(join(directory, 'manifest.json'), manifestText);
  writeFileSync(join(directory, 'publication.json'), JSON.stringify({
    id, createdAt, runtimeVersion: publicationRuntime, platform: 'android',
    channel: publicationChannel, gitSha, signature,
  }));
  return { id, directory, file };
}

function config(overrides = {}) {
  return {
    storage: root, runtime, channel, certificatePath: certificate, gitSha, updateUrl,
    publications: join(root, 'publications', 'android', runtime),
    pointer: join(root, 'channels', channel, 'android', `${runtime}.json`),
    ...overrides,
  };
}

// The public origin is operator configuration rather than a source constant, so
// the shape validation is what keeps a publication's asset URLs pinned. These
// cases prove externalising it did not turn the pin into a free-text field.
test('the operator-supplied OTA update URL is pinned to one exact shape', () => {
  assert.throws(() => paths({ ...env, NUBARCA_TV_OTA_UPDATE_URL: undefined }), /is required/i);
  assert.throws(() => paths({ ...env, NUBARCA_TV_OTA_UPDATE_URL: 'not-a-url' }), /absolute URL/i);
  assert.throws(() => paths({ ...env, NUBARCA_TV_OTA_UPDATE_URL: `http://${host}/api/tv-app/updates` }), /https/i);
  assert.throws(() => paths({ ...env, NUBARCA_TV_OTA_UPDATE_URL: `https://${host}/api/other` }), /must be exactly/i);
  assert.throws(() => paths({ ...env, NUBARCA_TV_OTA_UPDATE_URL: `https://u:p@${host}/api/tv-app/updates` }), /must be exactly/i);
  assert.throws(() => paths({ ...env, NUBARCA_TV_OTA_UPDATE_URL: `https://${host}/api/tv-app/updates?x=1` }), /must be exactly/i);
  // A trailing slash is normalised rather than rejected.
  assert.equal(paths({ ...env, NUBARCA_TV_OTA_UPDATE_URL: `${updateUrl}/` }).updateUrl, updateUrl);
});

// An asset URL on any other origin must be rejected even when everything else
// about the publication is valid and correctly signed.
test('a publication whose assets point at another origin is rejected', () => {
  const item = publication();
  assert.throws(
    () => validatePublication(item.directory, config({ updateUrl: 'https://somewhere-else.example.com/api/tv-app/updates' })),
    /immutable|another update/i,
  );
});

test('activation is atomic, retains previous, and rollback swaps pointers', () => {
  const first = publication('11111111-1111-4111-8111-111111111111', '2026-01-01T00:00:00Z');
  const second = publication('22222222-2222-4222-8222-222222222222', '2026-01-02T00:00:00Z');
  activate(first.id, config());
  activate(second.id, config());
  assert.equal(readPointer(config().pointer).current, second.id);
  assert.equal(readPointer(config().pointer).previous, first.id);
  rollback(env);
  assert.equal(readPointer(config().pointer).current, first.id);
  assert.equal(readPointer(config().pointer).previous, second.id);
  assert.equal(validatePublication(first.directory, config()).manifest.id, first.id);
  assert.equal(readdirSync(join(root, 'channels', channel, 'android')).filter((x) => x.endsWith('.tmp')).length, 0);
});

test('missing signature and tampered manifest are rejected cryptographically', () => {
  const unsigned = publication(randomUUID(), undefined, { unsigned: true });
  assert.throws(() => validatePublication(unsigned.directory, config()), /signature/i);
  const item = publication();
  const manifestPath = join(item.directory, 'manifest.json');
  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  manifest.createdAt = '2030-01-01T00:00:00Z';
  writeFileSync(manifestPath, JSON.stringify(manifest));
  assert.throws(() => validatePublication(item.directory, config()), /signature verification/i);
});

test('tampered or missing assets and unsafe symlinks are rejected', () => {
  const tampered = publication();
  writeFileSync(tampered.file, 'tampered');
  assert.throws(() => validatePublication(tampered.directory, config()), /hash mismatch/i);

  const missing = publication();
  rmSync(missing.file);
  assert.throws(() => validatePublication(missing.directory, config()), /missing asset/i);

  const linked = publication();
  rmSync(linked.file);
  const outside = join(root, 'outside');
  writeFileSync(outside, 'outside');
  symlinkSync(outside, linked.file);
  assert.throws(() => validatePublication(linked.directory, config()), /symlink|regular|hash/i);
});

test('wrong certificate and wrong private key are rejected', () => {
  const wrong = createCertificate(root, 'Wrong OTA Test');
  const item = publication();
  assert.throws(() => validatePublication(item.directory, config({ certificatePath: wrong.certificate })), /signature verification/i);
  assert.throws(() => publish({
    ...env,
    NUBARCA_TV_OTA_UPDATE_URL: 'https://nubarca.example.com/api/tv-app/updates',
    TV_OTA_PRIVATE_KEY_PATH: wrong.key,
  }), /does not match/i);
});

test('wrong runtime, wrong channel, runtime 1 and cross-runtime publications are rejected', () => {
  assert.throws(() => paths({ ...env, NUBARCA_TV_RUNTIME_VERSION: 'nubarca-tv-native-1' }), /runtime.*exactly/i);
  assert.throws(() => paths({ ...env, NUBARCA_TV_RUNTIME_VERSION: 'tv-native-3' }), /runtime.*exactly/i);
  assert.throws(() => paths({ ...env, NUBARCA_TV_OTA_CHANNEL: 'staging' }), /channel.*exactly/i);

  const wrongRuntime = publication(randomUUID(), undefined, { runtime: 'nubarca-tv-native-1' });
  assert.throws(() => validatePublication(wrongRuntime.directory, config()), /runtime|immutable/i);
  const wrongChannel = publication(randomUUID(), undefined, { channel: 'staging' });
  assert.throws(() => validatePublication(wrongChannel.directory, config()), /runtime|identity/i);
});

test('malformed and traversing publications and pointers are rejected', () => {
  const item = publication();
  const manifestPath = join(item.directory, 'manifest.json');
  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  manifest.launchAsset.url = `https://nubarca.example.com/api/tv-app/updates/assets/${runtime}/${item.id}/../secret`;
  writeFileSync(manifestPath, JSON.stringify(manifest));
  assert.throws(() => validatePublication(item.directory, config()), /unsafe|immutable|missing|signature/i);

  mkdirSync(join(root, 'channels', channel, 'android'), { recursive: true });
  writeFileSync(config().pointer, JSON.stringify({ current: '../escape' }));
  assert.throws(() => readPointer(config().pointer), /malformed|unsupported/i);
});

test('cleanup protects active, previous, newest, and other-channel references', () => {
  const ids = [1, 2, 3, 4].map((day) => publication(
    `00000000-0000-4000-8000-00000000000${day}`, `2026-01-0${day}T00:00:00Z`,
  ));
  activate(ids[0].id, config());
  activate(ids[1].id, config());
  const otherPointerDir = join(root, 'channels', 'staging', 'android');
  mkdirSync(otherPointerDir, { recursive: true });
  writeFileSync(join(otherPointerDir, `${runtime}.json`), JSON.stringify({ current: ids[0].id, previous: null }));
  activate(ids[2].id, config());
  activate(ids[3].id, config());
  cleanup({ ...env, TV_OTA_RETENTION_COUNT: '2', TV_OTA_CLEANUP_DRY_RUN: 'false' });
  assert.deepEqual(new Set(readdirSync(join(root, 'publications', 'android', runtime))), new Set([ids[0].id, ids[2].id, ids[3].id]));
});

test('publication requires signing material before export and cannot opt out', () => {
  assert.throws(() => publish({
    ...env, NUBARCA_TV_OTA_UPDATE_URL: 'https://nubarca.example.com/api/tv-app/updates',
  }), /private.key/i);
  assert.throws(() => publish({
    ...env, NUBARCA_TV_OTA_UPDATE_URL: 'https://nubarca.example.com/api/tv-app/updates',
    TV_OTA_PRIVATE_KEY_PATH: key, TV_OTA_SIGNING_REQUIRED: 'false',
  }), /unsigned.*forbidden/i);
});

test('release SHA must be full, clean, on main, and equal HEAD plus origin/main', () => {
  const outputs = new Map([
    ['rev-parse HEAD', gitSha], ['rev-parse origin/main', gitSha],
    ['branch --show-current', 'main'], ['status --porcelain', ''],
  ]);
  const runner = (args) => outputs.get(args.join(' '));
  assert.equal(validateReleaseGitSha(gitSha, runner), gitSha);
  assert.throws(() => validateReleaseGitSha('short', runner), /40-character/i);
  outputs.set('rev-parse origin/main', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa');
  assert.throws(() => validateReleaseGitSha(gitSha, runner), /origin\/main/i);
});

test('certificate validation enforces the Android expo-updates signing purpose', () => {
  assert.doesNotThrow(() => validateCodeSigningCertificate(certificate));
  const combined = join(root, 'combined.pem');
  writeFileSync(combined, `${readFileSync(certificate, 'utf8')}\n${readFileSync(key, 'utf8')}`);
  assert.throws(() => validateCodeSigningCertificate(combined), /must not contain a private key/i);
  const invalid = join(root, 'invalid.pem');
  const result = spawnSync('openssl', ['req', '-x509', '-new', '-key', key, '-out', invalid, '-days', '1', '-subj', '/CN=Invalid OTA']);
  assert.equal(result.status, 0);
  assert.throws(() => validateCodeSigningCertificate(invalid), /Code Signing/i);
});
