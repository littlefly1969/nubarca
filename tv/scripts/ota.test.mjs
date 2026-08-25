import assert from 'node:assert/strict';
import { createHash, randomUUID, sign } from 'node:crypto';
import {
  existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, symlinkSync, writeFileSync,
} from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import test from 'node:test';
import { spawnSync } from 'node:child_process';
import {
  activate, assertNodeVersion, bundle, cleanup, importBundle, parseCleanupArguments, readPointer,
  refreshAndValidateGitState, resolveReleaseContext, rollbackPointer, status, validate,
  validateBundle, validatePublication, verifyRemote,
} from './ota.mjs';

const gitSha = '1234567890abcdef1234567890abcdef12345678';
const origin = 'https://nubarca.example.com';
let root;
let key;
let certificate;
let env;

function createCertificate(directory, name, { validForCodeSigning = true } = {}) {
  const privateKey = join(directory, `${name}-key.pem`);
  const publicCertificate = join(directory, `${name}-certificate.pem`);
  const extensions = validForCodeSigning
    ? ['-addext', 'basicConstraints=critical,CA:FALSE', '-addext', 'keyUsage=critical,digitalSignature',
      '-addext', 'extendedKeyUsage=critical,codeSigning']
    : [];
  for (const args of [
    ['genpkey', '-quiet', '-algorithm', 'RSA', '-pkeyopt', 'rsa_keygen_bits:2048', '-out', privateKey],
    ['req', '-x509', '-new', '-key', privateKey, '-out', publicCertificate, '-days', '1',
      '-subj', `/CN=${name}`, ...extensions],
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
    NUBARCA_PUBLIC_ORIGIN: origin,
    NUBARCA_TV_OTA_CERTIFICATE: certificate,
    TV_OTA_PRIVATE_KEY_PATH: key,
    TV_OTA_STORAGE_ROOT: root,
  };
});
test.afterEach(() => rmSync(root, { recursive: true, force: true }));

function gitRunner(overrides = {}) {
  const outputs = new Map([
    ['fetch origin +refs/heads/main:refs/remotes/origin/main', ''],
    ['rev-parse HEAD', gitSha], ['rev-parse origin/main', gitSha],
    ['branch --show-current', 'main'], ['status --porcelain', ''],
  ]);
  for (const [command, value] of Object.entries(overrides)) outputs.set(command, value);
  return (args) => {
    const value = outputs.get(args.join(' '));
    if (value instanceof Error) throw value;
    if (value === undefined) throw new Error(`unexpected git command: ${args.join(' ')}`);
    return value;
  };
}

function fakeExport(output) {
  const bundle = '_expo/static/js/android/index.hbc';
  const asset = 'assets/icon.png';
  mkdirSync(join(output, '_expo/static/js/android'), { recursive: true });
  mkdirSync(join(output, 'assets'), { recursive: true });
  writeFileSync(join(output, bundle), 'bundle');
  writeFileSync(join(output, asset), 'asset');
  writeFileSync(join(output, 'metadata.json'), JSON.stringify({
    fileMetadata: { android: { bundle, assets: [{ path: asset }] } },
  }));
}

const dependencies = () => ({
  nodeVersion: '22.22.0',
  gitRunner: gitRunner(),
  runExport: fakeExport,
  readPublicExpoConfig: () => ({ version: '1.0.10' }),
});

function publication(id = randomUUID(), createdAt = new Date().toISOString(), overrides = {}) {
  const config = resolveReleaseContext(env);
  const publicationRuntime = overrides.runtimeVersion ?? config.runtimeVersion;
  const publicationChannel = overrides.channel ?? config.channel;
  const directory = join(root, 'publications', 'android', publicationRuntime, id);
  const relative = '_expo/static/js/android/index.hbc';
  const file = join(directory, 'files', relative);
  mkdirSync(join(directory, 'files', '_expo/static/js/android'), { recursive: true });
  writeFileSync(file, `bundle-${id}`);
  const hash = createHash('sha256').update(readFileSync(file)).digest('base64url');
  const url = `${origin}/api/tv-app/updates/assets/${publicationRuntime}/${id}/${relative}`;
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

test('release context derives runtime, channel, and update URL from the tracked contract', () => {
  const context = resolveReleaseContext(env);
  assert.equal(context.runtimeVersion, 'nubarca-tv-native-11');
  assert.equal(context.channel, 'production');
  assert.equal(context.updateUrl, `${origin}/api/tv-app/updates`);
  assert.throws(() => resolveReleaseContext({ ...env, NUBARCA_PUBLIC_ORIGIN: 'http://nubarca.example.com' }), /https/i);
  assert.throws(() => resolveReleaseContext({ ...env, NUBARCA_PUBLIC_ORIGIN: `${origin}/path` }), /without path/i);
});

test('Node 22 is an explicit release-tooling gate', () => {
  assert.equal(assertNodeVersion('22.22.0'), '22.22.0');
  assert.throws(() => assertNodeVersion('24.1.0'), /Node 22/i);
});

test('Git state refreshes origin/main before deriving the verified HEAD', () => {
  const calls = [];
  const runner = gitRunner();
  assert.equal(refreshAndValidateGitState((args) => { calls.push(args.join(' ')); return runner(args); }), gitSha);
  assert.equal(calls[0], 'fetch origin +refs/heads/main:refs/remotes/origin/main');
  assert.throws(() => refreshAndValidateGitState(gitRunner({ 'status --porcelain': ' M App.tsx' })), /clean/i);
  assert.throws(() => refreshAndValidateGitState(gitRunner({ 'branch --show-current': 'feature' })), /main branch/i);
  assert.throws(() => refreshAndValidateGitState(gitRunner({ 'rev-parse origin/main': 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' })), /freshly fetched/i);
  assert.throws(() => refreshAndValidateGitState(gitRunner({
    'fetch origin +refs/heads/main:refs/remotes/origin/main': new Error('offline'),
  })), /offline/i);
});

test('validate uses the real candidate pipeline without storage or APK keystore inputs', () => {
  const validationEnv = {
    NUBARCA_PUBLIC_ORIGIN: env.NUBARCA_PUBLIC_ORIGIN,
    NUBARCA_TV_OTA_CERTIFICATE: env.NUBARCA_TV_OTA_CERTIFICATE,
    TV_OTA_PRIVATE_KEY_PATH: env.TV_OTA_PRIVATE_KEY_PATH,
  };
  const result = validate(validationEnv, dependencies());
  assert.equal(result.gitSha, gitSha);
  assert.equal(result.candidate.metadata.gitSha, gitSha);
  assert.equal(result.candidate.manifest.assets.length, 1);
  assert.equal(existsSync(result.candidate.publication), false, 'temporary candidate must be removed');
  assert.equal(existsSync(join(root, 'publications')), false, 'validation must not touch publication storage');
});

test('GitHub bundle and server import keep signing separate from activation', () => {
  const output = join(root, 'ci-bundle');
  const built = bundle(output, env, dependencies());
  assert.equal(built.gitSha, gitSha);
  const context = resolveReleaseContext(env);
  const packaged = validateBundle(output, context, gitSha);
  assert.equal(packaged.bundleMetadata.updateId, built.id);
  assert.equal(existsSync(join(root, 'publications')), false, 'CI bundle must not touch publication storage');

  const importEnv = { ...env };
  delete importEnv.TV_OTA_PRIVATE_KEY_PATH;
  const id = importBundle(output, gitSha, importEnv, { nodeVersion: '22.22.0' });
  const config = resolveReleaseContext(env);
  const pointer = readPointer(config.pointer);
  assert.equal(pointer.current, id);
  const item = validatePublication(join(config.publications, id), config);
  assert.equal(item.metadata.gitSha, gitSha);
  assert.equal(readFileSync(join(root, '.nubarca-tv-ota.source'), 'utf8'), `${gitSha}\n`);

  assert.equal(importBundle(output, gitSha, importEnv, { nodeVersion: '22.22.0' }), id,
    'retrying the exact immutable bundle is idempotent');
  assert.throws(() => importBundle(output, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', importEnv,
    { nodeVersion: '22.22.0' }), /verified checkout HEAD/i);
});

test('bundle import rejects metadata tampering and immutable byte replacement', () => {
  const output = join(root, 'ci-bundle');
  bundle(output, env, dependencies());
  const importEnv = { ...env };
  delete importEnv.TV_OTA_PRIVATE_KEY_PATH;
  const id = importBundle(output, gitSha, importEnv, { nodeVersion: '22.22.0' });

  const metadataFile = join(output, 'bundle.json');
  const metadata = JSON.parse(readFileSync(metadataFile, 'utf8'));
  metadata.channel = 'other';
  writeFileSync(metadataFile, JSON.stringify(metadata));
  assert.throws(() => importBundle(output, gitSha, importEnv, { nodeVersion: '22.22.0' }), /identity/i);

  metadata.channel = 'production';
  writeFileSync(metadataFile, JSON.stringify(metadata));
  writeFileSync(join(root, 'publications', 'android', 'nubarca-tv-native-11', id, 'unexpected.txt'), 'different');
  assert.throws(() => importBundle(output, gitSha, importEnv, { nodeVersion: '22.22.0' }), /different bytes/i);
});

test('wrong private key and invalid certificate usage fail before export', () => {
  const wrong = createCertificate(root, 'Wrong OTA Test');
  assert.throws(() => validate({ ...env, TV_OTA_PRIVATE_KEY_PATH: wrong.key }, dependencies()), /does not match/i);
  const invalid = createCertificate(root, 'Invalid OTA Test', { validForCodeSigning: false });
  assert.throws(() => validate({ ...env, NUBARCA_TV_OTA_CERTIFICATE: invalid.certificate,
    TV_OTA_PRIVATE_KEY_PATH: invalid.key }, dependencies()), /Code Signing|Digital Signature/i);
});

test('missing signature, tampered manifests/assets, wrong origin, and symlinks are rejected', () => {
  const config = resolveReleaseContext(env);
  const unsigned = publication(randomUUID(), undefined, { unsigned: true });
  assert.throws(() => validatePublication(unsigned.directory, config), /signature/i);

  const tamperedManifest = publication();
  const manifestPath = join(tamperedManifest.directory, 'manifest.json');
  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  manifest.createdAt = '2030-01-01T00:00:00Z';
  writeFileSync(manifestPath, JSON.stringify(manifest));
  assert.throws(() => validatePublication(tamperedManifest.directory, config), /signature verification/i);

  const tamperedAsset = publication();
  writeFileSync(tamperedAsset.file, 'tampered');
  assert.throws(() => validatePublication(tamperedAsset.directory, config), /hash mismatch/i);

  const linked = publication();
  rmSync(linked.file);
  const outside = join(root, 'outside');
  writeFileSync(outside, 'outside');
  symlinkSync(outside, linked.file);
  assert.throws(() => validatePublication(linked.directory, config), /symlink|regular|hash/i);

  const wrongOriginConfig = { ...config, origin: 'https://another.example.com' };
  const wrongOrigin = publication();
  assert.throws(() => validatePublication(wrongOrigin.directory, wrongOriginConfig), /another update/i);
});

test('read-only and mutating storage commands reject symlinked storage paths', () => {
  const outside = mkdtempSync(join(tmpdir(), 'nubarca-ota-outside-'));
  try {
    const linkedStorage = join(root, 'linked-storage');
    symlinkSync(outside, linkedStorage);
    const linkedEnv = { ...env, TV_OTA_STORAGE_ROOT: linkedStorage };
    assert.throws(() => status(linkedEnv), /without symlinks/i);
    assert.throws(() => cleanup([], linkedEnv), /without symlinks/i);

    const channels = join(root, 'channels');
    symlinkSync(outside, channels);
    assert.throws(() => status(env), /symlink/i);
  } finally {
    rmSync(outside, { recursive: true, force: true });
  }
});

test('activation is atomic and rollback-pointer changes only distribution state', () => {
  const config = resolveReleaseContext(env);
  const first = publication('11111111-1111-4111-8111-111111111111', '2026-01-01T00:00:00Z');
  const second = publication('22222222-2222-4222-8222-222222222222', '2026-01-02T00:00:00Z');
  activate(first.id, config);
  activate(second.id, config);
  const beforeFirst = readFileSync(join(first.directory, 'manifest.json'), 'utf8');
  const beforeSecond = readFileSync(join(second.directory, 'manifest.json'), 'utf8');
  rollbackPointer(first.id, env);
  assert.equal(readPointer(config.pointer).current, first.id);
  assert.equal(readFileSync(join(first.directory, 'manifest.json'), 'utf8'), beforeFirst);
  assert.equal(readFileSync(join(second.directory, 'manifest.json'), 'utf8'), beforeSecond);
  assert.throws(() => rollbackPointer(first.id, env), /already the current/i);
  assert.throws(() => rollbackPointer('not-a-uuid', env), /UUID/i);
  assert.throws(() => rollbackPointer('33333333-3333-4333-8333-333333333333', env), /incomplete/i);
});

test('status accepts an empty channel and validates referenced publications', () => {
  assert.deepEqual(status(env), { current: null, previous: null });
  const item = publication();
  activate(item.id, resolveReleaseContext(env));
  assert.equal(status(env).current, item.id);
});

test('cleanup is dry-run by default and protects current, previous, and newest releases', () => {
  assert.deepEqual(parseCleanupArguments([]), { apply: false, keep: 5 });
  assert.deepEqual(parseCleanupArguments(['--apply', '--keep', '2']), { apply: true, keep: 2 });
  assert.throws(() => parseCleanupArguments(['--keep', '1']), />= 2/);
  assert.throws(() => parseCleanupArguments(['--unknown']), /unknown/);

  const config = resolveReleaseContext(env);
  const items = [1, 2, 3, 4].map((day) => publication(
    `00000000-0000-4000-8000-00000000000${day}`, `2026-01-0${day}T00:00:00Z`,
  ));
  activate(items[0].id, config);
  activate(items[1].id, config);
  activate(items[2].id, config);
  activate(items[3].id, config);
  const dryRun = cleanup(['--keep', '2'], env);
  assert.deepEqual(new Set(dryRun), new Set([items[0].id, items[1].id]));
  assert.equal(existsSync(join(config.publications, dryRun[0])), true);
  cleanup(['--apply', '--keep', '2'], env);
  assert.equal(existsSync(join(config.publications, dryRun[0])), false);
  assert.deepEqual(new Set(readdirSync(config.publications)), new Set([items[2].id, items[3].id]));
});

test('remote verification reports an intentional no-publication response', async () => {
  const response = new Response(null, { status: 204 });
  assert.equal(await verifyRemote(env, async () => response), null);
});

test('remote verification validates a signed manifest and every immutable asset', async () => {
  const context = resolveReleaseContext(env, { requireStorage: false });
  const id = randomUUID();
  const bytes = Buffer.from('remote asset');
  const hash = createHash('sha256').update(bytes).digest('base64url');
  const assetUrl = `${context.updateUrl}/assets/${context.runtimeVersion}/${id}/index.hbc`;
  const manifest = {
    id,
    runtimeVersion: context.runtimeVersion,
    launchAsset: { url: assetUrl, hash, contentType: 'application/octet-stream' },
    assets: [],
    metadata: { channel: context.channel, gitSha },
  };
  const manifestText = JSON.stringify(manifest);
  const signature = `sig="${sign('RSA-SHA256', Buffer.from(manifestText), readFileSync(key)).toString('base64')}", keyid="main", alg="rsa-v1_5-sha256"`;
  const seen = [];
  const fetcher = async (input, options) => {
    seen.push({ input: String(input), options });
    if (String(input) === context.updateUrl) {
      return new Response(manifestText, { status: 200, headers: {
        'content-type': 'application/expo+json',
        'expo-signature': signature,
      } });
    }
    assert.equal(String(input), assetUrl);
    return new Response(bytes, { status: 200, headers: { 'content-type': 'application/octet-stream' } });
  };
  assert.equal((await verifyRemote(env, fetcher)).id, id);
  assert.equal(seen[0].options.headers['Expo-Runtime-Version'], context.runtimeVersion);
  assert.equal(seen[0].options.headers['expo-channel-name'], context.channel);
  assert.equal(seen.length, 2);
});
