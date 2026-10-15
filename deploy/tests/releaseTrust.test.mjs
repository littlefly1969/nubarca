import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import {
  chmodSync, cpSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

// Signing trust changes only with the old trust's consent. CI is played by
// `previous` + `assemble` exactly as release.yml runs them; GHCR and cosign are
// fakes: a release "image" is a directory, and a "signature" names its signer
// and the SHA-256 of the bytes it signs, so only the right signer verifies.

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../..');
const tool = resolve(here, '../release-manifest.py');
const updater = readFileSync(resolve(here, '../update-production.sh'), 'utf8');
const releaseWorkflow = readFileSync(resolve(root, '.github/workflows/release.yml'), 'utf8');
const owner = 'littlefly1969';
const digest = (c) => `sha256:${c.repeat(64)}`;
const IDENTITY = `https://github.com/${owner}/nubarca/.github/workflows/release.yml@refs/heads/main`;

const pem = (fill) => `-----BEGIN PUBLIC KEY-----\n${fill.repeat(64)}\n${fill.repeat(24)}==\n-----END PUBLIC KEY-----\n`;
const KEY_A = pem('A');
const KEY_B = pem('B');

const FAKE_DOCKER = `#!/usr/bin/env python3
import os, shutil, sys
registry = os.environ["FAKE_REGISTRY"]
args = sys.argv[1:]
def published(image):
    return os.path.isdir(os.path.join(registry, image.rsplit(":", 1)[1]))
if args[:2] == ["manifest", "inspect"]:
    sys.exit(0 if published(args[2]) else 1)
if args[0] == "pull":
    sys.exit(0 if published(args[-1]) else 1)
if args[0] == "create":
    print(args[1].rsplit(":", 1)[1]); sys.exit(0)
if args[0] == "cp":
    container, _ = args[1].split(":", 1)
    shutil.copytree(os.path.join(registry, container), args[2], dirs_exist_ok=True); sys.exit(0)
if args[0] == "rm":
    sys.exit(0)
sys.exit(2)
`;

const FAKE_COSIGN = `#!/usr/bin/env python3
import hashlib, json, sys
args = sys.argv[1:]
assert args[0] == "verify-blob"
options, blob = {}, args[-1]
i = 1
while i < len(args) - 1:
    if "=" in args[i]:
        key, value = args[i].split("=", 1); options[key] = value; i += 1
    else:
        options[args[i]] = args[i + 1]; i += 2
if "--key" in options:
    assert options.get("--insecure-ignore-tlog") == "true"
    expected = "".join(open(options["--key"]).read().split())
else:
    assert options["--certificate-identity"] == "${IDENTITY}"
    assert options["--certificate-oidc-issuer"] == "https://token.actions.githubusercontent.com"
    expected = "keyless"
bundle = json.load(open(options["--bundle"]))
ok = bundle["signer"] == expected and bundle["sha256"] == hashlib.sha256(open(blob, "rb").read()).hexdigest()
sys.exit(0 if ok else 1)
`;

function git(cwd, ...args) {
  return execFileSync('git', args, { cwd, encoding: 'utf8' }).trim();
}

const signerId = (key) => (key ? key.split(/\s+/).join('') : 'keyless');
const signature = (key, bytes) => JSON.stringify({
  signer: signerId(key), sha256: createHash('sha256').update(bytes).digest('hex'),
});

/**
 * A repository, a registry and a CI. commit() makes one push to main (with
 * the given signing key committed, or none for keyless) and release()
 * publishes it the way release.yml does, signing with the keys it is given.
 */
function world() {
  const base = mkdtempSync(join(tmpdir(), 'nubarca-release-trust-'));
  const cwd = join(base, 'repo');
  const registry = join(base, 'registry');
  const bin = join(base, 'bin');
  for (const dir of [cwd, registry, bin]) mkdirSync(dir, { recursive: true });
  writeFileSync(join(bin, 'docker'), FAKE_DOCKER);
  writeFileSync(join(bin, 'cosign'), FAKE_COSIGN);
  chmodSync(join(bin, 'docker'), 0o755);
  chmodSync(join(bin, 'cosign'), 0o755);
  const env = { ...process.env, PATH: `${bin}:${process.env.PATH}`, FAKE_REGISTRY: registry };
  const run = (...args) => spawnSync('python3', [tool, ...args], { encoding: 'utf8', env });

  git(cwd, 'init', '-q', '-b', 'main');
  git(cwd, 'config', 'user.name', 'NubArca Test');
  git(cwd, 'config', 'user.email', 'test@example.invalid');
  mkdirSync(join(cwd, 'deploy'));
  writeFileSync(join(cwd, 'deploy/release-components.json'),
    readFileSync(resolve(root, 'deploy/release-components.json')));
  for (const dir of ['src/NubArca.Api', 'frontend', 'src/NubArca.PrintAgent']) {
    mkdirSync(join(cwd, dir), { recursive: true });
    writeFileSync(join(cwd, dir, 'file'), '0\n');
  }
  let step = 0;

  function commit(key) {
    step += 1;
    writeFileSync(join(cwd, 'src/NubArca.Api/file'), `${step}\n`);
    if (key) writeFileSync(join(cwd, 'deploy/release-signing.pub'), key);
    else rmSync(join(cwd, 'deploy/release-signing.pub'), { force: true });
    git(cwd, 'add', '-A');
    git(cwd, 'commit', '-qm', `step ${step}`);
    return git(cwd, 'rev-parse', 'HEAD');
  }

  /** CI for <sha>: find the previous release, plan, assemble, sign, publish. */
  function release(sha, { signWith, transitionWith, omitTransition = false } = {}) {
    const work = mkdtempSync(join(base, 'ci-'));
    const found = run('previous', cwd, sha, join(work, 'previous'));
    assert.equal(found.status, 0, found.stderr);
    const previous = found.stdout.trim() ? join(work, 'previous/release.json') : '-';
    const planned = JSON.parse(run('plan', cwd, sha, previous).stdout);
    const built = Object.fromEntries(planned.build.map((name) => [name, {
      api: { runtime: `ghcr.io/${owner}/nubarca-api@${digest('a')}`,
        openvino: `ghcr.io/${owner}/nubarca-api-openvino@${digest('b')}` },
      frontend: { default: `ghcr.io/${owner}/nubarca-frontend@${digest('c')}` },
      'print-agent': { 'linux-x64': `ghcr.io/${owner}/nubarca-print-agent@${digest('d')}` },
    }[name]]));
    writeFileSync(join(work, 'built.json'), JSON.stringify(built));
    const assembled = run('assemble', cwd, sha, '1.0.0', join(work, 'built.json'), previous);
    assert.equal(assembled.status, 0, assembled.stderr);
    const manifest = JSON.parse(assembled.stdout);
    const image = join(registry, sha);
    mkdirSync(image);
    writeFileSync(join(image, 'release.json'), assembled.stdout);
    const own = signWith === undefined ? keyOf(manifest.signer) : signWith;
    writeFileSync(join(image, 'release.sigstore.json'), signature(own, assembled.stdout));
    if (manifest.transition && !omitTransition) {
      const old = transitionWith === undefined ? keyOf(manifest.transition.fromSigner) : transitionWith;
      writeFileSync(join(image, 'release.transition.sigstore.json'), signature(old, assembled.stdout));
    }
    return manifest;
  }

  /** The installation: its checkout at <installed>, the candidate fetched. */
  function install(installed, candidate) {
    const checkout = join(base, `install-${installed.slice(0, 7)}-${Math.random().toString(36).slice(2)}`);
    cpSync(cwd, checkout, { recursive: true });
    git(checkout, 'checkout', '-q', installed);
    const into = mkdtempSync(join(base, 'fetched-'));
    const result = run('fetch-verified', candidate, checkout, into);
    return { ...result, into };
  }

  return { cwd, registry, run, commit, release, install };
}

const keyOf = (signer) => (signer.mode === 'key' ? signer.publicKey : null);

test('an installation on keyless signatures accepts a NubArca key only through a release keyless signed', () => {
  const w = world();
  const c1 = w.commit(null);
  w.release(c1);
  const c2 = w.commit(KEY_A);
  const transition = w.release(c2);
  assert.deepEqual(transition.transition, { fromSigner: { mode: 'keyless' }, fromTrustSince: c1 });
  assert.equal(transition.trustSince, c2);
  const c3 = w.commit(KEY_A);
  const after = w.release(c3);
  assert.equal(after.trustSince, c2);
  assert.equal(after.transition, null);

  const result = w.install(c1, c3);
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /NubArca release key, reached from keyless .* through 1 signed trust transition/);
  assert.equal(JSON.parse(readFileSync(join(result.into, 'release.json'), 'utf8')).sourceSha, c3);
  // An installation already on the key needs no transition.
  assert.equal(w.install(c2, c3).status, 0);
});

test('a key rotation, and a return to keyless, are followed hop by hop from the installed signer', () => {
  const w = world();
  const c1 = w.commit(null);
  w.release(c1);
  const c2 = w.commit(KEY_A);
  w.release(c2);
  const c3 = w.commit(KEY_B);
  const rotation = w.release(c3);
  assert.equal(rotation.transition.fromSigner.publicKey, KEY_A);
  assert.equal(rotation.transition.fromTrustSince, c2);
  const c4 = w.commit(null);
  w.release(c4);
  const c5 = w.commit(null);
  w.release(c5);

  const toB = w.install(c1, c3);
  assert.equal(toB.status, 0, toB.stderr);
  assert.match(toB.stdout, /through 2 signed trust transition/);
  // Still on keyless itself, it needs no hand-over to accept keyless again.
  assert.match(w.install(c1, c5).stdout, /^signed by keyless \(Release workflow on main\)$/m);
  const fromA = w.install(c2, c5);
  assert.equal(fromA.status, 0, fromA.stderr);
  assert.match(fromA.stdout, /through 2 signed trust transition/);
  assert.equal(w.install(c3, c4).status, 0);
});

test('a new signer the old one never authorized is refused', () => {
  const w = world();
  const c1 = w.commit(null);
  w.release(c1);
  const c2 = w.commit(KEY_A);
  // Whoever holds only the new key signs the hand-over with it.
  w.release(c2, { transitionWith: KEY_A });
  const c3 = w.commit(KEY_A);
  w.release(c3);
  const result = w.install(c1, c3);
  assert.equal(result.status, 1);
  assert.match(result.stderr, /trust transition at .* is not signed by the signer this installation trusts/);
  assert.throws(() => readFileSync(join(result.into, 'release.json')), 'nothing is handed to the updater');
});

test('a hand-over without the old signer\'s signature is refused', () => {
  const w = world();
  const c1 = w.commit(null);
  w.release(c1);
  const c2 = w.commit(KEY_A);
  w.release(c2, { omitTransition: true });
  const result = w.install(c1, c2);
  assert.equal(result.status, 1);
  assert.match(result.stderr, /not signed by the signer this installation trusts/);
});

test('a rotated-out key cannot sign for an installation that already moved on', () => {
  const w = world();
  const c1 = w.commit(KEY_A);
  w.release(c1);
  const c2 = w.commit(KEY_B);
  w.release(c2);
  const c3 = w.commit(KEY_B);
  // The old key signs the newest release.
  w.release(c3, { signWith: KEY_A });
  const result = w.install(c2, c3);
  assert.equal(result.status, 1);
  assert.match(result.stderr, /signature does not verify/);
});

test('a manifest cannot claim its signer started fresh to skip the hand-over', () => {
  const w = world();
  const c1 = w.commit(null);
  w.release(c1);
  const c2 = w.commit(KEY_A);
  w.release(c2);
  const c3 = w.commit(KEY_A);
  // A forged release names itself the start of KEY_A's trust, without a transition.
  const image = join(w.registry, c3);
  mkdirSync(image);
  const forged = JSON.parse(readFileSync(join(w.registry, c2, 'release.json'), 'utf8'));
  Object.assign(forged, { sourceSha: c3, trustSince: c3, previousRelease: null, transition: null });
  const bytes = JSON.stringify(forged);
  writeFileSync(join(image, 'release.json'), bytes);
  writeFileSync(join(image, 'release.sigstore.json'), signature(KEY_A, bytes));
  const result = w.install(c1, c3);
  assert.equal(result.status, 1);
  assert.match(result.stderr, /never handed over/);
});

test('a manifest signed by a signer its commit does not declare is refused', () => {
  const w = world();
  const c1 = w.commit(null);
  w.release(c1);
  const c2 = w.commit(null);
  const image = join(w.registry, c2);
  mkdirSync(image);
  const forged = JSON.parse(readFileSync(join(w.registry, c1, 'release.json'), 'utf8'));
  Object.assign(forged, { sourceSha: c2, signer: { mode: 'key', publicKey: KEY_A }, trustSince: c2,
    previousRelease: null });
  writeFileSync(join(image, 'release.json'), JSON.stringify(forged));
  const result = w.install(c1, c2);
  assert.equal(result.status, 1);
  assert.match(result.stderr, /names a signer its commit does not declare/);
});

test('CI starts from the newest published release, and never from one that does not verify', () => {
  const w = world();
  const c1 = w.commit(null);
  w.release(c1);
  const c2 = w.commit(null); // its run failed: nothing published
  const c3 = w.commit(null);
  const third = w.release(c3);
  assert.equal(third.previousRelease, c1, 'an unpublished commit is skipped');
  assert.ok(c2);
  const c4 = w.commit(null);
  // The newest release's signature is broken: CI stops instead of reaching further back.
  writeFileSync(join(w.registry, c3, 'release.sigstore.json'), signature(KEY_A, 'other bytes'));
  const result = w.run('previous', w.cwd, c4, mkdtempSync(join(tmpdir(), 'nubarca-previous-')));
  assert.equal(result.status, 1);
  assert.match(result.stderr, /not signed by the signer its commit declares/);
});

test('the updater installs from the signature chain, and CI dual-signs a hand-over', () => {
  const chain = updater.indexOf('release-manifest.py fetch-verified');
  const content = updater.indexOf('release-manifest.py verify');
  const pull = updater.indexOf('docker pull "$NEW_API_IMAGE"');
  assert.ok(chain > 0 && chain < content && content < pull);
  assert.match(releaseWorkflow, /release\.transition\.sigstore\.json/);
  assert.match(releaseWorkflow, /check-signature "\$RUNNER_TEMP\/release\.json" "\$RUNNER_TEMP\/release\.transition\.sigstore\.json" previous/);
  assert.match(releaseWorkflow, /RELEASE_SIGNING_KEY_PREVIOUS/);
});

test('a published release is never replaced, and an install confirms the exact release its check found', () => {
  // CI: a commit already released is a no-op; one published meanwhile must be byte-identical.
  assert.match(releaseWorkflow, /already published; nothing to do/);
  assert.match(releaseWorkflow, /needs\.plan\.outputs\.published != 'true'/);
  assert.match(releaseWorkflow, /cmp -s "\$existing\/release\.json" "\$RUNNER_TEMP\/release\.json"/);
  assert.match(releaseWorkflow, /it is never replaced/);
  // The installation: the identity covers the signed manifest's bytes and both TV digests,
  // the check reports it, and apply refuses anything else before installing.
  assert.match(updater, /manifest:%s\\ntv-apk:%s\\ntv-ota:%s\\n/);
  assert.match(updater, /"releaseId": e\.get\("R_RELEASE_ID"\)/);
  assert.match(updater, /\[\[ "\$MODE" == "apply" && "\$CONFIRM_SHA" =~ \^\[0-9a-f\]\{40\}\$ && "\$CONFIRM_RELEASE" =~ \^\[0-9a-f\]\{64\}\$ \]\]/);
  const pinned = updater.indexOf('"$CONFIRM_RELEASE" == "$RELEASE_ID"');
  const tvResolved = updater.indexOf('TV_OTA_DIGEST="$(manifest_digest');
  const firstPull = updater.indexOf('docker pull "$NEW_API_IMAGE"');
  assert.ok(tvResolved > 0 && tvResolved < pinned && pinned < firstPull);
});
