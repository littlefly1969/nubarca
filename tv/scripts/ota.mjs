#!/usr/bin/env node
import { createHash, createPublicKey, randomUUID, sign, verify } from 'node:crypto';
import {
  cpSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync,
  realpathSync, renameSync, rmSync, statSync, writeFileSync,
} from 'node:fs';
import { dirname, join, resolve, sep } from 'node:path';
import { tmpdir } from 'node:os';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { validateCodeSigningCertificate } = require('./code-signing-certificate.cjs');
const { normalizePublicOrigin, readReleaseContract } = require('./release-contract.cjs');

const here = dirname(fileURLToPath(import.meta.url));
const tvRoot = resolve(here, '..');
const release = readReleaseContract();
const SAFE = /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/;
const GIT_SHA = /^[0-9a-f]{40}$/;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const SIGNATURE = /^sig="([A-Za-z0-9+/]+={0,2})", keyid="main", alg="rsa-v1_5-sha256"$/;
const BUNDLE_SCHEMA_VERSION = 1;
const BUNDLE_ARTIFACT = 'nubarca-tv-ota';

export function safeSegment(value, name) {
  if (!SAFE.test(value ?? '')) throw new Error(`${name} contains unsupported characters`);
  return value;
}

export function sha256Base64Url(file) {
  return createHash('sha256').update(readFileSync(file)).digest('base64url');
}

function sha256Hex(value) {
  return createHash('sha256').update(value).digest('hex');
}

function contentType(file) {
  const ext = file.toLowerCase().split('.').pop();
  return ({ hbc: 'application/octet-stream', js: 'application/javascript', json: 'application/json',
    png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg', webp: 'image/webp',
    gif: 'image/gif', svg: 'image/svg+xml', ttf: 'font/ttf', otf: 'font/otf',
    mp4: 'video/mp4', webm: 'video/webm' })[ext] ?? 'application/octet-stream';
}

function requireEnv(name, env = process.env) {
  const value = env[name]?.trim();
  if (!value) throw new Error(`${name} is required`);
  return value;
}

export function assertNodeVersion(version = process.versions.node) {
  if (Number.parseInt(version.split('.')[0], 10) !== 22) {
    throw new Error(`Node 22.x is required for TV OTA release tooling; found ${version}`);
  }
  return version;
}

export function resolveReleaseContext(env = process.env, { requireStorage = true, requirePrivateKey = false } = {}) {
  const origin = normalizePublicOrigin(requireEnv('NUBARCA_PUBLIC_ORIGIN', env));
  const certificatePath = resolve(requireEnv('NUBARCA_TV_OTA_CERTIFICATE', env));
  const privateKeyPath = requirePrivateKey
    ? resolve(requireEnv('TV_OTA_PRIVATE_KEY_PATH', env))
    : (env.TV_OTA_PRIVATE_KEY_PATH ? resolve(env.TV_OTA_PRIVATE_KEY_PATH) : null);
  const storage = requireStorage ? resolve(requireEnv('TV_OTA_STORAGE_ROOT', env)) : null;
  return {
    ...release,
    origin,
    updateUrl: `${origin}${release.updatePath}`,
    certificatePath,
    privateKeyPath,
    storage,
    publications: storage ? join(storage, 'publications', 'android', release.runtimeVersion) : null,
    pointer: storage ? join(storage, 'channels', release.channel, 'android', `${release.runtimeVersion}.json`) : null,
  };
}

// Backwards-compatible internal name used by publication validation tests.
export function paths(env = process.env) {
  return resolveReleaseContext(env);
}

export function readPointer(file) {
  if (!existsSync(file)) return { current: null, previous: null };
  if (lstatSync(file).isSymbolicLink() || !statSync(file).isFile()) throw new Error('channel pointer must be a regular file');
  const value = JSON.parse(readFileSync(file, 'utf8'));
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('channel pointer is malformed');
  for (const key of ['current', 'previous']) {
    if (!(key in value) || (value[key] !== null && typeof value[key] !== 'string')) {
      throw new Error(`pointer ${key} is malformed`);
    }
    if (value[key] !== null) safeSegment(value[key], `pointer ${key}`);
  }
  if (value.current !== null && value.current === value.previous) throw new Error('channel pointer current and previous must differ');
  return value;
}

export function writePointerAtomic(file, value) {
  mkdirSync(dirname(file), { recursive: true });
  const temp = `${file}.${process.pid}.${randomUUID()}.tmp`;
  writeFileSync(temp, `${JSON.stringify(value, null, 2)}\n`, { flag: 'wx', mode: 0o644 });
  renameSync(temp, file);
}

function assertNoSymlinkPath(file, root) {
  const absoluteRoot = resolve(root);
  const absolute = resolve(file);
  if (absolute !== absoluteRoot && !absolute.startsWith(`${absoluteRoot}${sep}`)) throw new Error('path escaped its trusted root');
  const relative = absolute.slice(absoluteRoot.length).split(sep).filter(Boolean);
  let cursor = absoluteRoot;
  if (existsSync(cursor) && lstatSync(cursor).isSymbolicLink()) throw new Error('unsafe symlink in publication path');
  for (const part of relative) {
    cursor = join(cursor, part);
    if (existsSync(cursor) && lstatSync(cursor).isSymbolicLink()) throw new Error('unsafe symlink in publication path');
  }
}

function assertSafeStorageContext(config) {
  if (existsSync(config.storage)) {
    if (lstatSync(config.storage).isSymbolicLink() || realpathSync(config.storage) !== config.storage) {
      throw new Error('TV_OTA_STORAGE_ROOT must be a real path without symlinks');
    }
  }
  assertNoSymlinkPath(config.publications, config.storage);
  assertNoSymlinkPath(config.pointer, config.storage);
}

function parseSignature(value) {
  const match = SIGNATURE.exec(value ?? '');
  if (!match) throw new Error('missing or invalid signature metadata');
  return Buffer.from(match[1], 'base64');
}

export function certificateIdentity(certificatePath) {
  const certificate = validateCodeSigningCertificate(certificatePath);
  return {
    certificate,
    certificateSha256: sha256Hex(certificate.raw),
    publicKeySha256: sha256Hex(certificate.publicKey.export({ type: 'spki', format: 'der' })),
  };
}

export function validateSigningMaterial(config) {
  if (!existsSync(config.certificatePath)) throw new Error('NUBARCA_TV_OTA_CERTIFICATE is unavailable');
  if (!config.privateKeyPath || !existsSync(config.privateKeyPath)) throw new Error('TV_OTA_PRIVATE_KEY_PATH is unavailable');
  const identity = certificateIdentity(config.certificatePath);
  const privatePublic = createPublicKey(readFileSync(config.privateKeyPath)).export({ type: 'spki', format: 'der' });
  const certificatePublic = identity.certificate.publicKey.export({ type: 'spki', format: 'der' });
  if (!privatePublic.equals(certificatePublic)) throw new Error('OTA private key does not match the signing certificate');
  return identity;
}

export function validatePublication(directory, options) {
  if (!options.certificatePath || !existsSync(options.certificatePath)) throw new Error('OTA verification certificate is unavailable');
  const certificate = validateCodeSigningCertificate(options.certificatePath);
  assertNoSymlinkPath(directory, dirname(directory));
  const metadataFile = join(directory, 'publication.json');
  const manifestFile = join(directory, 'manifest.json');
  if (!existsSync(metadataFile) || !existsSync(manifestFile)) throw new Error('publication metadata is incomplete');
  assertNoSymlinkPath(metadataFile, directory);
  assertNoSymlinkPath(manifestFile, directory);
  const metadata = JSON.parse(readFileSync(metadataFile, 'utf8'));
  const manifestText = readFileSync(manifestFile, 'utf8');
  const manifest = JSON.parse(manifestText);
  if (manifest.id !== metadata.id || manifest.runtimeVersion !== options.runtimeVersion || metadata.runtimeVersion !== options.runtimeVersion
      || metadata.platform !== 'android' || metadata.channel !== options.channel
      || manifest.metadata?.platform !== 'android' || manifest.metadata?.channel !== options.channel) {
    throw new Error('publication identity or runtime does not match');
  }
  if (!GIT_SHA.test(metadata.gitSha ?? '') || metadata.gitSha !== manifest.metadata?.gitSha) {
    throw new Error('publication Git SHA is missing or does not match');
  }
  if (options.gitSha && metadata.gitSha !== options.gitSha) throw new Error('publication Git SHA is not the verified HEAD');
  if (!UUID.test(manifest.id)) throw new Error('update id must be a UUID');
  for (const asset of [manifest.launchAsset, ...manifest.assets]) {
    if (!asset || typeof asset.url !== 'string' || typeof asset.hash !== 'string') throw new Error('invalid manifest asset');
    const prefix = `${release.updatePath}/assets/${encodeURIComponent(options.runtimeVersion)}/${manifest.id}/`;
    const parsedAssetUrl = new URL(asset.url);
    if (parsedAssetUrl.origin !== options.origin || parsedAssetUrl.username || parsedAssetUrl.password
        || parsedAssetUrl.search || parsedAssetUrl.hash || !parsedAssetUrl.pathname.startsWith(prefix)) {
      throw new Error('asset URL is not immutable or belongs to another update');
    }
    const encoded = parsedAssetUrl.pathname.slice(prefix.length);
    const parts = encoded.split('/').map(decodeURIComponent);
    if (parts.some((part) => !part || part === '.' || part === '..' || part.includes('/') || part.includes('\\'))) {
      throw new Error('unsafe asset path');
    }
    const file = resolve(directory, 'files', ...parts);
    const root = `${resolve(directory, 'files')}${sep}`;
    if (!file.startsWith(root) || !existsSync(file) || !statSync(file).isFile()) throw new Error(`missing asset: ${encoded}`);
    assertNoSymlinkPath(file, directory);
    if (sha256Base64Url(file) !== asset.hash) throw new Error(`asset hash mismatch: ${encoded}`);
  }
  const signature = parseSignature(metadata.signature);
  if (!verify('RSA-SHA256', Buffer.from(manifestText), certificate.publicKey, signature)) {
    throw new Error('manifest signature verification failed');
  }
  return { metadata, manifest, manifestText };
}

export function validateBundle(directory, config, expectedGitSha = null) {
  const root = resolve(directory);
  assertNoSymlinkPath(root, dirname(root));
  const metadataFile = join(root, 'bundle.json');
  const publication = join(root, 'publication');
  if (!existsSync(metadataFile) || !existsSync(publication)) throw new Error('OTA bundle is incomplete');
  assertNoSymlinkPath(metadataFile, root);
  assertNoSymlinkPath(publication, root);
  if (!statSync(metadataFile).isFile() || !statSync(publication).isDirectory()) {
    throw new Error('OTA bundle entries have invalid types');
  }
  const metadata = JSON.parse(readFileSync(metadataFile, 'utf8'));
  const keys = Object.keys(metadata).sort();
  const expectedKeys = [
    'artifact', 'certificateSha256', 'channel', 'createdAt', 'gitSha', 'publicKeySha256',
    'runtimeVersion', 'schemaVersion', 'updateId',
  ].sort();
  if (JSON.stringify(keys) !== JSON.stringify(expectedKeys)) throw new Error('OTA bundle metadata schema is invalid');
  if (metadata.schemaVersion !== BUNDLE_SCHEMA_VERSION || metadata.artifact !== BUNDLE_ARTIFACT
      || !GIT_SHA.test(metadata.gitSha ?? '') || !UUID.test(metadata.updateId ?? '')
      || metadata.runtimeVersion !== config.runtimeVersion || metadata.channel !== config.channel
      || typeof metadata.createdAt !== 'string'
      || !/^[0-9a-f]{64}$/.test(metadata.certificateSha256 ?? '')
      || !/^[0-9a-f]{64}$/.test(metadata.publicKeySha256 ?? '')) {
    throw new Error('OTA bundle identity is invalid');
  }
  if (expectedGitSha && metadata.gitSha !== expectedGitSha) {
    throw new Error('OTA bundle Git SHA is not the verified checkout HEAD');
  }
  const identity = certificateIdentity(config.certificatePath);
  if (metadata.certificateSha256 !== identity.certificateSha256
      || metadata.publicKeySha256 !== identity.publicKeySha256) {
    throw new Error('OTA bundle signing identity does not match the authoritative certificate');
  }
  const validated = validatePublication(publication, { ...config, gitSha: metadata.gitSha });
  if (validated.metadata.id !== metadata.updateId || validated.metadata.createdAt !== metadata.createdAt) {
    throw new Error('OTA bundle metadata does not match its publication');
  }
  return { ...validated, bundleMetadata: metadata, publication };
}

export function activate(publicationId, config = paths()) {
  safeSegment(publicationId, 'update id');
  assertSafeStorageContext(config);
  validatePublication(join(config.publications, publicationId), config);
  const old = readPointer(config.pointer);
  if (old.current === publicationId) return old;
  const next = { current: publicationId, previous: old.current, activatedAt: new Date().toISOString() };
  writePointerAtomic(config.pointer, next);
  return next;
}

function expoEnvironment(env) {
  const clean = { ...process.env, ...env, NODE_ENV: 'production' };
  for (const apkOnly of [
    'NUBARCA_TV_RELEASE_STORE_FILE', 'NUBARCA_TV_RELEASE_STORE_PASSWORD',
    'NUBARCA_TV_RELEASE_KEY_ALIAS', 'NUBARCA_TV_RELEASE_KEY_PASSWORD',
  ]) delete clean[apkOnly];
  return clean;
}

function runExport(output, env) {
  const result = spawnSync(process.platform === 'win32' ? 'npx.cmd' : 'npx',
    ['expo', 'export', '--platform', 'android', '--output-dir', output, '--clear'],
    { cwd: tvRoot, stdio: 'inherit', env: expoEnvironment(env) });
  if (result.error) throw new Error(`Unable to start Expo export: ${result.error.message}`);
  if (result.status !== 0) throw new Error(`Expo export failed with status ${result.status}`);
}

function readPublicExpoConfig(env) {
  const result = spawnSync(process.platform === 'win32' ? 'npx.cmd' : 'npx',
    ['expo', 'config', '--type', 'public', '--json'],
    { cwd: tvRoot, encoding: 'utf8', env: expoEnvironment(env) });
  if (result.error) throw new Error(`Unable to start Expo config export: ${result.error.message}`);
  if (result.status !== 0) throw new Error(`Expo config export failed: ${result.stderr || result.status}`);
  if (!result.stdout.trim()) throw new Error('Expo config export returned no JSON');
  const config = JSON.parse(result.stdout);
  if (config.updates) delete config.updates.codeSigningCertificate;
  return config;
}

function assetDescriptor(source, relativePath, context, id) {
  const hash = sha256Base64Url(source);
  const urlPath = relativePath.split('/').map(encodeURIComponent).join('/');
  const ext = relativePath.includes('.') ? relativePath.split('.').pop() : undefined;
  return {
    hash, key: hash, contentType: contentType(relativePath),
    ...(ext ? { fileExtension: `.${ext}` } : {}),
    url: `${context.updateUrl}/assets/${encodeURIComponent(context.runtimeVersion)}/${id}/${urlPath}`,
  };
}

function git(args) {
  const result = spawnSync('git', args, { cwd: tvRoot, encoding: 'utf8' });
  if (result.error || result.status !== 0) {
    throw new Error(`Git release check failed (${args.join(' ')}): ${(result.stderr || result.error?.message || result.status).toString().trim()}`);
  }
  return result.stdout.trim();
}

export function refreshAndValidateGitState(gitRunner = git) {
  gitRunner(['fetch', 'origin', '+refs/heads/main:refs/remotes/origin/main']);
  const head = gitRunner(['rev-parse', 'HEAD']);
  const remoteMain = gitRunner(['rev-parse', 'origin/main']);
  const branch = gitRunner(['branch', '--show-current']);
  const status = gitRunner(['status', '--porcelain']);
  if (!GIT_SHA.test(head)) throw new Error('Git HEAD must be a full 40-character SHA');
  if (head !== remoteMain) throw new Error('OTA release HEAD must equal freshly fetched origin/main');
  if (branch !== 'main') throw new Error('OTA release must run from the main branch');
  if (status) throw new Error('OTA release requires a clean working tree');
  return head;
}

function createCandidate(context, gitSha, workingRoot, env, dependencies = {}) {
  const exportRunner = dependencies.runExport ?? runExport;
  const configReader = dependencies.readPublicExpoConfig ?? readPublicExpoConfig;
  const exported = join(workingRoot, 'export');
  const publication = join(workingRoot, 'publication');
  mkdirSync(publication, { recursive: true });
  const expoClient = configReader(env);
  exportRunner(exported, env);
  const exportMetadata = JSON.parse(readFileSync(join(exported, 'metadata.json'), 'utf8'));
  const android = exportMetadata?.fileMetadata?.android;
  if (!android?.bundle || !Array.isArray(android.assets)) throw new Error('Expo export metadata is malformed');
  const exportedFiles = [android.bundle, ...android.assets.map((asset) => asset.path)];
  if (new Set(exportedFiles).size !== exportedFiles.length) throw new Error('Expo export contains duplicate asset paths');

  const id = randomUUID();
  const createdAt = new Date().toISOString();
  const filesRoot = join(publication, 'files');
  for (const relativePath of exportedFiles) {
    if (typeof relativePath !== 'string' || relativePath.startsWith('/')
        || relativePath.split('/').some((part) => !part || part === '.' || part === '..')) {
      throw new Error(`unsafe Expo export path: ${relativePath}`);
    }
    const source = resolve(exported, relativePath);
    if (!source.startsWith(`${resolve(exported)}${sep}`) || !existsSync(source)) throw new Error(`missing exported file: ${relativePath}`);
    assertNoSymlinkPath(source, exported);
    if (!lstatSync(source).isFile() || !realpathSync(source).startsWith(`${realpathSync(exported)}${sep}`)) {
      throw new Error(`exported asset is not an isolated regular file: ${relativePath}`);
    }
    const target = join(filesRoot, relativePath);
    mkdirSync(dirname(target), { recursive: true });
    cpSync(source, target, { errorOnExist: true, force: false });
  }

  const launchAsset = assetDescriptor(join(exported, android.bundle), android.bundle, context, id);
  delete launchAsset.fileExtension;
  const assets = android.assets.map((asset) => assetDescriptor(join(exported, asset.path), asset.path, context, id));
  const manifest = { id, createdAt, runtimeVersion: context.runtimeVersion, launchAsset, assets,
    metadata: { channel: context.channel, platform: 'android', gitSha },
    extra: { expoClient, release: { gitSha } } };
  const manifestText = JSON.stringify(manifest);
  const signature = `sig="${sign('RSA-SHA256', Buffer.from(manifestText), readFileSync(context.privateKeyPath)).toString('base64')}", keyid="main", alg="rsa-v1_5-sha256"`;
  writeFileSync(join(publication, 'manifest.json'), manifestText, { flag: 'wx', mode: 0o644 });
  writeFileSync(join(publication, 'publication.json'), `${JSON.stringify({ id, createdAt,
    runtimeVersion: context.runtimeVersion, platform: 'android', channel: context.channel, gitSha, signature }, null, 2)}\n`,
  { flag: 'wx', mode: 0o644 });
  const validated = validatePublication(publication, { ...context, gitSha });
  return { id, publication, ...validated };
}

function prepareRelease(env, dependencies = {}) {
  assertNodeVersion(dependencies.nodeVersion ?? process.versions.node);
  const context = resolveReleaseContext(env, { requireStorage: dependencies.requireStorage ?? false, requirePrivateKey: true });
  const signing = validateSigningMaterial(context);
  const gitSha = refreshAndValidateGitState(dependencies.gitRunner ?? git);
  return { context, signing, gitSha };
}

function printCandidateSummary(prepared, candidate) {
  console.log(`Git SHA: ${prepared.gitSha}`);
  console.log(`Runtime: ${prepared.context.runtimeVersion}`);
  console.log(`Channel: ${prepared.context.channel}`);
  console.log(`Origin: ${prepared.context.origin}`);
  console.log(`OTA certificate SHA-256: ${prepared.signing.certificateSha256}`);
  console.log(`OTA public-key SPKI SHA-256: ${prepared.signing.publicKeySha256}`);
  console.log(`Asset count: ${candidate.manifest.assets.length + 1}`);
}

export function validate(env = process.env, dependencies = {}) {
  const prepared = prepareRelease(env, { ...dependencies, requireStorage: false });
  const temporary = mkdtempSync(join(tmpdir(), 'nubarca-tv-ota-validate-'));
  try {
    const candidate = createCandidate(prepared.context, prepared.gitSha, temporary, env, dependencies);
    printCandidateSummary(prepared, candidate);
    console.log('Candidate validation: PASS');
    return { ...prepared, candidate };
  } finally {
    rmSync(temporary, { recursive: true, force: true });
  }
}

export function bundle(outputDirectory, env = process.env, dependencies = {}) {
  if (!outputDirectory) throw new Error('bundle output directory is required');
  const output = resolve(outputDirectory);
  if (existsSync(output)) throw new Error(`bundle output already exists: ${output}`);
  const prepared = prepareRelease(env, { ...dependencies, requireStorage: false });
  const temporary = mkdtempSync(join(dirname(output), '.nubarca-tv-ota-bundle-'));
  try {
    const candidate = createCandidate(prepared.context, prepared.gitSha, temporary, env, dependencies);
    printCandidateSummary(prepared, candidate);
    const assembled = join(temporary, 'bundle');
    mkdirSync(assembled);
    renameSync(candidate.publication, join(assembled, 'publication'));
    writeFileSync(join(assembled, 'bundle.json'), `${JSON.stringify({
      schemaVersion: BUNDLE_SCHEMA_VERSION,
      artifact: BUNDLE_ARTIFACT,
      gitSha: prepared.gitSha,
      runtimeVersion: prepared.context.runtimeVersion,
      channel: prepared.context.channel,
      updateId: candidate.id,
      createdAt: candidate.metadata.createdAt,
      certificateSha256: prepared.signing.certificateSha256,
      publicKeySha256: prepared.signing.publicKeySha256,
    }, null, 2)}\n`, { flag: 'wx', mode: 0o644 });
    validateBundle(assembled, prepared.context, prepared.gitSha);
    renameSync(assembled, output);
    console.log(`Validated OTA bundle: ${output}`);
    return { ...prepared, id: candidate.id, output };
  } finally {
    rmSync(temporary, { recursive: true, force: true });
  }
}

function directoryDigest(directory) {
  const hash = createHash('sha256');
  const walk = (current, relative = '') => {
    for (const entry of readdirSync(current, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
      if (entry.isSymbolicLink()) throw new Error('unsafe symlink in OTA bundle');
      const childRelative = relative ? `${relative}/${entry.name}` : entry.name;
      const child = join(current, entry.name);
      hash.update(`${entry.isDirectory() ? 'd' : 'f'}\0${childRelative}\0`);
      if (entry.isDirectory()) walk(child, childRelative);
      else if (entry.isFile()) hash.update(readFileSync(child));
      else throw new Error('unsupported file type in OTA bundle');
    }
  };
  walk(directory);
  return hash.digest('hex');
}

export function importBundle(bundleDirectory, expectedGitSha, env = process.env, dependencies = {}) {
  assertNodeVersion(dependencies.nodeVersion ?? process.versions.node);
  if (!GIT_SHA.test(expectedGitSha ?? '')) throw new Error('expected Git SHA must be 40 lowercase hexadecimal characters');
  const config = resolveReleaseContext(env, { requireStorage: true, requirePrivateKey: false });
  mkdirSync(config.storage, { recursive: true });
  assertSafeStorageContext(config);
  const validated = validateBundle(bundleDirectory, config, expectedGitSha);
  mkdirSync(config.publications, { recursive: true });
  const stagingRoot = join(config.storage, '.staging');
  mkdirSync(stagingRoot, { recursive: true });
  assertNoSymlinkPath(stagingRoot, config.storage);
  const staging = join(stagingRoot, `${process.pid}-${randomUUID()}`);
  const stagedPublication = join(staging, 'publication');
  mkdirSync(staging);
  try {
    cpSync(validated.publication, stagedPublication, { recursive: true, errorOnExist: true, force: false });
    validatePublication(stagedPublication, { ...config, gitSha: expectedGitSha });
    const destination = join(config.publications, validated.bundleMetadata.updateId);
    if (existsSync(destination)) {
      validatePublication(destination, { ...config, gitSha: expectedGitSha });
      if (directoryDigest(destination) !== directoryDigest(stagedPublication)) {
        throw new Error(`immutable OTA publication exists with different bytes: ${validated.bundleMetadata.updateId}`);
      }
    } else {
      renameSync(stagedPublication, destination);
    }
    activate(validated.bundleMetadata.updateId, config);
    const marker = join(config.storage, '.nubarca-tv-ota.source');
    writeFileSync(`${marker}.${process.pid}.tmp`, `${expectedGitSha}\n`, { flag: 'wx', mode: 0o644 });
    renameSync(`${marker}.${process.pid}.tmp`, marker);
    console.log(`Imported and activated ${validated.bundleMetadata.updateId} for android/${config.runtimeVersion}/${config.channel}`);
    return validated.bundleMetadata.updateId;
  } finally {
    rmSync(staging, { recursive: true, force: true });
  }
}

export function status(env = process.env) {
  const config = resolveReleaseContext(env);
  assertSafeStorageContext(config);
  const identity = certificateIdentity(config.certificatePath);
  const pointer = readPointer(config.pointer);
  console.log(`Runtime: ${config.runtimeVersion}`);
  console.log(`Channel: ${config.channel}`);
  console.log(`OTA certificate SHA-256: ${identity.certificateSha256}`);
  console.log(`OTA public-key SPKI SHA-256: ${identity.publicKeySha256}`);
  for (const [label, id] of [['Current', pointer.current], ['Previous', pointer.previous]]) {
    if (!id) {
      console.log(`${label} publication: none`);
      continue;
    }
    const item = validatePublication(join(config.publications, id), config);
    console.log(`${label} publication: ${id} createdAt=${item.manifest.createdAt} gitSha=${item.metadata.gitSha}`);
  }
  return pointer;
}

export function rollbackPointer(target, env = process.env) {
  const config = resolveReleaseContext(env);
  assertSafeStorageContext(config);
  const pointer = readPointer(config.pointer);
  const selected = target || pointer.previous;
  if (!selected) throw new Error('no previous publication is available');
  if (!UUID.test(selected)) throw new Error('rollback target must be a publication UUID');
  if (selected === pointer.current) throw new Error('rollback target is already the current publication');
  validatePublication(join(config.publications, selected), config);
  const next = { current: selected, previous: pointer.current, activatedAt: new Date().toISOString() };
  writePointerAtomic(config.pointer, next);
  console.log('WARNING: This changes server distribution only.');
  console.log('It does not guarantee downgrade of devices that already downloaded a newer update.');
  console.log(`Server pointer changed to ${selected}`);
  return next;
}

export function parseCleanupArguments(args) {
  let apply = false;
  let keep = 5;
  for (let index = 0; index < args.length; index += 1) {
    if (args[index] === '--apply') apply = true;
    else if (args[index] === '--keep') {
      keep = Number.parseInt(args[index + 1] ?? '', 10);
      index += 1;
    } else throw new Error(`unknown cleanup option: ${args[index]}`);
  }
  if (!Number.isInteger(keep) || keep < 2) throw new Error('--keep must be an integer >= 2');
  return { apply, keep };
}

export function cleanup(args = [], env = process.env) {
  const { apply, keep } = parseCleanupArguments(args);
  const config = resolveReleaseContext(env);
  assertSafeStorageContext(config);
  const pointer = readPointer(config.pointer);
  if (!existsSync(config.publications)) return [];
  const entries = readdirSync(config.publications, { withFileTypes: true })
    .filter((entry) => entry.isDirectory() && SAFE.test(entry.name))
    .map((entry) => ({ id: entry.name, dir: join(config.publications, entry.name) }))
    .map((entry) => ({ ...entry, createdAt: validatePublication(entry.dir, config).manifest.createdAt }))
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt));
  const referenced = [pointer.current, pointer.previous];
  const channelsRoot = join(config.storage, 'channels');
  if (existsSync(channelsRoot)) {
    for (const channelEntry of readdirSync(channelsRoot, { withFileTypes: true })) {
      if (!channelEntry.isDirectory() || !SAFE.test(channelEntry.name)) continue;
      const candidate = join(channelsRoot, channelEntry.name, 'android', `${config.runtimeVersion}.json`);
      if (!existsSync(candidate)) continue;
      const channelPointer = readPointer(candidate);
      referenced.push(channelPointer.current, channelPointer.previous);
    }
  }
  const protectedIds = new Set([...referenced, ...entries.slice(0, keep).map((item) => item.id)].filter(Boolean));
  const removed = [];
  for (const entry of entries) {
    if (protectedIds.has(entry.id)) continue;
    console.log(`${apply ? 'Removing' : 'Would remove'} ${entry.dir}`);
    if (apply) rmSync(entry.dir, { recursive: true });
    removed.push(entry.id);
  }
  return removed;
}

export async function verifyRemote(env = process.env, fetcher = fetch) {
  const context = resolveReleaseContext(env, { requireStorage: false });
  const response = await fetcher(context.updateUrl, { headers: {
    Accept: 'application/expo+json',
    'Expo-Protocol-Version': '1',
    'Expo-Platform': 'android',
    'Expo-Runtime-Version': context.runtimeVersion,
    'expo-channel-name': context.channel,
    'expo-expect-signature': 'sig, keyid="main", alg="rsa-v1_5-sha256"',
  } });
  if (response.status === 204) {
    console.log('Remote OTA status: 204 No Content (no active publication)');
    return null;
  }
  if (response.status !== 200) throw new Error(`OTA endpoint returned HTTP ${response.status}`);
  const contentTypeHeader = response.headers.get('content-type') ?? '';
  if (!contentTypeHeader.toLowerCase().startsWith('application/expo+json')) throw new Error(`Unexpected OTA content type: ${contentTypeHeader}`);
  const manifestText = await response.text();
  const manifest = JSON.parse(manifestText);
  if (manifest.runtimeVersion !== context.runtimeVersion || manifest.metadata?.channel !== context.channel
      || !GIT_SHA.test(manifest.metadata?.gitSha ?? '') || !UUID.test(manifest.id ?? '')) {
    throw new Error('Remote OTA manifest identity is invalid');
  }
  if (!manifest.launchAsset || !Array.isArray(manifest.assets)) throw new Error('Remote OTA manifest assets are invalid');
  const identity = certificateIdentity(context.certificatePath);
  const signature = parseSignature(response.headers.get('expo-signature'));
  if (!verify('RSA-SHA256', Buffer.from(manifestText), identity.certificate.publicKey, signature)) {
    throw new Error('Remote OTA manifest signature verification failed');
  }
  const assets = [manifest.launchAsset, ...manifest.assets];
  const expectedAssetPrefix = `${context.updateUrl}/assets/${encodeURIComponent(context.runtimeVersion)}/${manifest.id}/`;
  const seenUrls = new Set();
  for (const asset of assets) {
    if (!asset || typeof asset.url !== 'string' || typeof asset.hash !== 'string'
        || !/^[A-Za-z0-9_-]{43}$/.test(asset.hash) || typeof asset.contentType !== 'string'
        || !asset.contentType.trim()) {
      throw new Error('Remote OTA asset descriptor is invalid');
    }
    const url = new URL(asset.url);
    if (!asset.url.startsWith(expectedAssetPrefix) || url.origin !== context.origin
        || url.username || url.password || url.search || url.hash || seenUrls.has(url.href)) {
      throw new Error('Remote OTA asset is not a unique immutable URL for this update');
    }
    seenUrls.add(url.href);
    const assetResponse = await fetcher(url);
    if (!assetResponse.ok) throw new Error(`Remote OTA asset returned HTTP ${assetResponse.status}`);
    const assetContentType = assetResponse.headers.get('content-type')?.split(';')[0]?.trim().toLowerCase();
    if (assetContentType !== asset.contentType.toLowerCase()) throw new Error('Remote OTA asset content type mismatch');
    const actual = createHash('sha256').update(Buffer.from(await assetResponse.arrayBuffer())).digest('base64url');
    if (actual !== asset.hash) throw new Error('Remote OTA asset hash mismatch');
  }
  console.log(`Remote OTA VALID: ${manifest.id} gitSha=${manifest.metadata.gitSha} assets=${assets.length}`);
  return manifest;
}

const command = process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url) ? process.argv[2] : null;
if (command) {
  try {
    if (command === 'validate') validate();
    else if (command === 'bundle') bundle(process.argv[3]);
    else if (command === 'import-bundle') importBundle(process.argv[3], process.argv[4]);
    else if (command === 'status') status();
    else if (command === 'rollback-pointer') rollbackPointer(process.argv[3]);
    else if (command === 'cleanup') cleanup(process.argv.slice(3));
    else if (command === 'verify') await verifyRemote();
    else throw new Error('usage: ota.mjs <validate|bundle|import-bundle|status|verify|rollback-pointer|cleanup>');
  } catch (error) {
    console.error(`OTA ${command} failed: ${error instanceof Error ? error.message : error}`);
    process.exitCode = 1;
  }
}
