import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import test from 'node:test';

// The trust checks deploy/pull-publish-print-agent-image.sh runs before it
// publishes a Print Agent bundle: the image must come from NubArca's own GHCR
// repository by digest — whatever its labels and manifest claim — and inside
// it the manifest, the labels and the zip must agree, from a commit on main.

const validator = resolve(import.meta.dirname, '../../deploy/print-agent-bundle.py');
const publisher = resolve(import.meta.dirname, '../../deploy/pull-publish-print-agent-image.sh');
const trusted = 'ghcr.io/littlefly1969/nubarca-print-agent';
const digest = `sha256:${'a'.repeat(64)}`;

function run(...args) {
  return spawnSync('python3', [validator, ...args], { encoding: 'utf8' });
}

function git(cwd, ...args) {
  return execFileSync('git', args, { cwd, encoding: 'utf8' }).trim();
}

/** A checkout whose main has one commit, and a commit that is NOT on main. */
function checkout() {
  const cwd = mkdtempSync(join(tmpdir(), 'nubarca-print-agent-checkout-'));
  git(cwd, 'init', '-q', '-b', 'main');
  git(cwd, 'config', 'user.name', 'NubArca Test');
  git(cwd, 'config', 'user.email', 'test@example.invalid');
  writeFileSync(join(cwd, 'README'), 'main\n');
  git(cwd, 'add', '.');
  git(cwd, 'commit', '-qm', 'main');
  const onMain = git(cwd, 'rev-parse', 'HEAD');
  git(cwd, 'checkout', '-q', '-b', 'elsewhere');
  writeFileSync(join(cwd, 'README'), 'elsewhere\n');
  git(cwd, 'commit', '-qam', 'elsewhere');
  const offMain = git(cwd, 'rev-parse', 'HEAD');
  git(cwd, 'checkout', '-q', 'main');
  return { cwd, onMain, offMain };
}

/** The bundle as the release workflow packages it: zip, then a manifest naming it. */
function bundle(commit, { tamper = false } = {}) {
  const dir = mkdtempSync(join(tmpdir(), 'nubarca-print-agent-bundle-'));
  const name = 'nubarca-print-agent-0.7.0-linux-x64.zip';
  const bytes = Buffer.from('PK-print-agent-bundle');
  writeFileSync(join(dir, name), bytes);
  const sha256 = createHash('sha256').update(bytes).digest('hex');
  writeFileSync(join(dir, 'nubarca-print-agent.release.json'), JSON.stringify({
    artifact: 'print-agent', version: '0.7.0', runtime: 'linux-x64', fileName: name,
    sha256, sizeBytes: bytes.length, sourceCommit: commit,
  }));
  if (tamper) writeFileSync(join(dir, name), Buffer.from('PK-print-agent-bundlX'));
  return { dir, name, sha256, size: bytes.length };
}

test('a trusted image by digest, with a manifest that checks out, is accepted', () => {
  const repo = checkout();
  const image = run('image', `${trusted}@${digest}`);
  assert.equal(image.status, 0, image.stderr);
  assert.equal(image.stdout.trim(), `${trusted}@${digest}`);

  const published = bundle(repo.onMain);
  const result = run('validate', published.dir, 'print-agent', repo.onMain, repo.cwd);
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout.trim(), `${published.name}\t${published.sha256}\t0.7.0\t${published.size}`);
});

test('another owner or repository is refused, whatever commit its image claims', () => {
  // The image could carry a genuine NubArca commit in its labels and manifest:
  // the repository is checked first, from the reference alone.
  for (const reference of [
    `ghcr.io/attacker/nubarca-print-agent@${digest}`,
    `ghcr.io/littlefly1969/nubarca-print-agent-evil@${digest}`,
    `ghcr.io/littlefly1969/nubarca-print-agen@${digest}`,
    `ghcr.io/LITTLEFLY1969/nubarca-print-agent@${digest}`,
    `docker.io/littlefly1969/nubarca-print-agent@${digest}`,
    `evil.example/ghcr.io/littlefly1969/nubarca-print-agent@${digest}`,
  ]) {
    const result = run('image', reference);
    assert.equal(result.status, 1, reference);
    assert.match(result.stderr, /refused/);
  }
});

test('a malformed or mutable reference is refused', () => {
  for (const reference of [
    `${trusted}:latest`,
    `${trusted}@sha256:${'a'.repeat(63)}`,
    `${trusted}@sha256:${'A'.repeat(64)}`,
    `${trusted}@sha512:${'a'.repeat(64)}`,
    `${trusted}@${digest}@${digest}`,
    trusted,
    '',
  ]) {
    assert.equal(run('image', reference).status, 1, reference);
  }
});

test('a source commit that is not on the deployed main is refused', () => {
  const repo = checkout();
  const published = bundle(repo.offMain);
  const result = run('validate', published.dir, 'print-agent', repo.offMain, repo.cwd);
  assert.equal(result.status, 1);
  assert.match(result.stderr, /not on the deployed main/);
});

test('a zip that does not hash to its manifest is refused', () => {
  const repo = checkout();
  const published = bundle(repo.onMain, { tamper: true });
  const result = run('validate', published.dir, 'print-agent', repo.onMain, repo.cwd);
  assert.equal(result.status, 1);
  assert.match(result.stderr, /does not hash/);
});

test('labels and manifest must agree, and a missing manifest is no bundle', () => {
  const repo = checkout();
  const published = bundle(repo.onMain);
  assert.match(run('validate', published.dir, 'tv-apk', repo.onMain, repo.cwd).stderr, /not a NubArca Print Agent/);
  assert.match(run('validate', published.dir, 'print-agent', '', repo.cwd).stderr, /no source commit/);
  assert.match(run('validate', published.dir, 'print-agent', 'b'.repeat(40), repo.cwd).stderr, /commit differs/);
  const empty = mkdtempSync(join(tmpdir(), 'nubarca-print-agent-empty-'));
  assert.match(run('validate', empty, 'print-agent', repo.onMain, repo.cwd).stderr, /manifest is missing/);
});

test('the publisher checks the reference before pulling, and switches the pointer last', () => {
  const script = readFileSync(publisher, 'utf8');
  const check = script.indexOf('"$VALIDATOR" image');
  assert.ok(check > 0, 'the publisher validates the image reference');
  assert.ok(check < script.indexOf('docker pull'), 'nothing is pulled before the repository is trusted');
  assert.ok(script.indexOf('"$VALIDATOR" validate') < script.indexOf('docker run'),
    'nothing is written before the bundle is validated');
  assert.ok(script.indexOf('/out/$ZIP_NAME"') < script.indexOf('current-linux-x64.json'),
    'the zip is written before the pointer that names it');
});

test('the release workflow pushes to the one trusted repository', () => {
  const workflow = readFileSync(resolve(import.meta.dirname, '../../.github/workflows/print-agent-release.yml'), 'utf8');
  assert.match(workflow, /image=ghcr\.io\/\$\{owner\}\/nubarca-print-agent:/);
  assert.equal(trusted, 'ghcr.io/littlefly1969/nubarca-print-agent');
});
