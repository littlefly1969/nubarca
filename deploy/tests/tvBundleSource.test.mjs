import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

// scripts/lib/tv-bundle-source.sh decides when a TV bundle CI built from an
// earlier commit is still the bundle of a later one. The updater uses it to
// find a reusable bundle, and both TV importers use it to accept one.

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(here, '../..');
const helper = resolve(repoRoot, 'scripts/lib/tv-bundle-source.sh');

function git(cwd, ...args) {
  return execFileSync('git', args, { cwd, encoding: 'utf8' }).trim();
}

function commit(cwd, path, content) {
  mkdirSync(dirname(join(cwd, path)), { recursive: true });
  writeFileSync(join(cwd, path), content);
  git(cwd, 'add', '.');
  git(cwd, 'commit', '-qm', path);
  return git(cwd, 'rev-parse', 'HEAD');
}

function bash(cwd, script, ...args) {
  return spawnSync('bash', ['-c', `. "${helper}"; ${script}`, 'bash', ...args], { cwd, encoding: 'utf8' });
}

/** main: tvChange (tv/) → server (api only) → tip (frontend only); off: a side commit. */
function history() {
  const cwd = mkdtempSync(join(tmpdir(), 'nubarca-tv-bundle-source-'));
  git(cwd, 'init', '-q', '-b', 'main');
  git(cwd, 'config', 'user.name', 'NubArca Test');
  git(cwd, 'config', 'user.email', 'test@example.invalid');
  const before = commit(cwd, 'tv/App.tsx', 'old\n');
  const tvChange = commit(cwd, 'tv/App.tsx', 'new\n');
  const server = commit(cwd, 'src/NubArca.Api/Program.cs', 'api\n');
  const tip = commit(cwd, 'frontend/src/main.tsx', 'web\n');
  git(cwd, 'checkout', '-qb', 'side', before);
  const off = commit(cwd, 'README', 'side\n');
  git(cwd, 'checkout', '-q', 'main');
  return { cwd, before, tvChange, server, tip, off };
}

test('a bundle stays reusable until a TV build input changes', () => {
  const h = history();
  const reusable = (source) => bash(h.cwd, 'tv_bundle_reusable "$1" "$2"', source, h.tip).status === 0;
  assert.equal(reusable(h.tip), true);
  assert.equal(reusable(h.server), true);
  assert.equal(reusable(h.tvChange), true);
  assert.equal(reusable(h.before), false, 'the TV changed after it');
  assert.equal(reusable(h.off), false, 'not an ancestor');
  assert.equal(reusable('f'.repeat(40)), false, 'unknown commit');
  assert.equal(reusable('main'), false, 'only full SHAs');
});

test('a TV workflow change also makes older bundles unusable', () => {
  const h = history();
  const workflow = commit(h.cwd, '.github/workflows/tv-ota-release.yml', 'name: x\n');
  assert.notEqual(bash(h.cwd, 'tv_bundle_reusable "$1" "$2"', h.tip, workflow).status, 0);
  assert.equal(bash(h.cwd, 'tv_bundle_reusable "$1" "$2"', workflow, workflow).status, 0);
});

test('candidate sources run newest first, back to the last TV change', () => {
  const h = history();
  const result = bash(h.cwd, 'tv_bundle_candidate_sources "$1"', h.tip);
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(result.stdout.trim().split('\n'), [h.tip, h.server, h.tvChange]);
});

test('updater and importers share the rule', () => {
  const updater = readFileSync(resolve(repoRoot, 'deploy/update-production.sh'), 'utf8');
  assert.match(updater, /scripts\/lib\/tv-bundle-source\.sh/);
  assert.match(updater, /tv_bundle_candidate_sources "\$CANDIDATE_SHA"/);
  assert.match(updater, /changed_between "\$TV_CURRENT_SHA" "\$\{TV_BUNDLE_PATHS\[@\]\}"/);
  for (const name of ['pull-publish-tv-ota-image.sh', 'pull-publish-tv-apk-image.sh']) {
    const importer = readFileSync(resolve(repoRoot, 'deploy', name), 'utf8');
    assert.match(importer, /scripts\/lib\/tv-bundle-source\.sh/);
    assert.match(importer, /tv_bundle_reusable "\$source_sha" "\$head_sha" \|\|/);
  }
  const ota = readFileSync(resolve(repoRoot, 'deploy/pull-publish-tv-ota-image.sh'), 'utf8');
  assert.match(ota, /ota\.mjs import-bundle "\$bundle_dir" "\$source_sha"/);
});
