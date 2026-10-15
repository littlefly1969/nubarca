import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import {
  chmodSync, copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

// A failed install puts the whole previous release back, not only its images.
// The real deploy/update-production.sh runs in a production checkout of a
// fixture repository; Docker, GHCR, cosign, the health endpoints and the disk
// are fakes. The candidate changes docker-compose.prod.yml, so an install that
// fails and comes back with the candidate's Compose files is visible.

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../..');
const owner = 'littlefly1969';
const IDENTITY = `https://github.com/${owner}/nubarca/.github/workflows/release.yml@refs/heads/main`;
const image = (repo, fill) => `ghcr.io/${owner}/${repo}@sha256:${fill.repeat(64)}`;

// Compose: -f files merged service by service (docker-compose.prod*.yml are
// JSON, which is YAML; the release pin is the updater's own small YAML). `up`
// records what it started, and the images now run; `inspect` answers for the
// running containers. GHCR: a release "image" is a directory.
const FAKE_DOCKER = `#!/usr/bin/env python3
import json, os, re, shutil, sys
state_path = os.environ["FAKE_STATE"]
registry = os.environ["FAKE_REGISTRY"]
state = json.load(open(state_path))
args = sys.argv[1:]

def save():
    with open(state_path, "w") as handle:
        json.dump(state, handle)

def load(path):
    text = open(path).read()
    if text.lstrip().startswith("{"):
        return json.loads(text)
    services, current = {}, None
    for line in text.splitlines():
        if m := re.fullmatch(r"  ([a-z]+):", line):
            current = services.setdefault(m.group(1), {})
        elif m := re.fullmatch(r'    image: "(.*)"', line):
            current["image"] = m.group(1)
    return {"services": services}

def effective(files):
    model = {"services": {}}
    for path in files:
        for name, service in load(path).get("services", {}).items():
            model["services"].setdefault(name, {}).update(service)
    return model

if args[0] == "compose":
    files, rest, i = [], [], 1
    while i < len(args):
        if args[i] in ("-f", "--env-file", "--profile"):
            if args[i] == "-f":
                files.append(args[i + 1])
            i += 2
        else:
            rest.append(args[i]); i += 1
    model = effective(files)
    if rest[:1] == ["config"]:
        print(json.dumps(model)); sys.exit(0)
    if rest[:1] == ["up"]:
        started = [a for a in rest[1:] if not a.startswith("-")]
        for name in started:
            state["running"][name] = model["services"][name]["image"]
        state["ups"].append({"services": started, "model": model,
                             "prodYml": open("docker-compose.prod.yml").read(),
                             "head": os.popen("git rev-parse HEAD").read().strip()})
        save(); sys.exit(0)
    sys.exit(2)
if args[0] == "inspect":
    container, fmt = args[1], args[args.index("--format") + 1]
    service = container.removeprefix("nubarca-")
    source = state["sources"].get(state["running"].get(service, ""), "")
    if ".Mounts" in fmt: print("")
    elif ".Config.Env" in fmt: print("NUBARCA_GIT_SHA=" + source)
    elif "image.revision" in fmt: print(source)
    sys.exit(0)
if args[:2] == ["manifest", "inspect"]:
    sys.exit(0 if os.path.isdir(os.path.join(registry, args[-1].rsplit(":", 1)[1])) else 1)
if args[0] == "pull":
    sys.exit(0)
if args[0] == "create":
    print(args[1].rsplit(":", 1)[1]); sys.exit(0)
if args[0] == "cp":
    container, _ = args[1].split(":", 1)
    shutil.copytree(os.path.join(registry, container), args[2], dirs_exist_ok=True); sys.exit(0)
if args[0] == "rm":
    sys.exit(0)
sys.exit(2)
`;

// Healthy unless the running api is the image this test declares broken.
const FAKE_CURL = `#!/usr/bin/env python3
import json, os, sys
state = json.load(open(os.environ["FAKE_STATE"]))
sys.exit(22 if state["running"].get("api") == os.environ.get("FAKE_UNHEALTHY_IMAGE") else 0)
`;

const FAKE_COSIGN = `#!/usr/bin/env python3
import hashlib, json, sys
args = sys.argv[1:]
if args == ["version"]:
    print("GitVersion:    v2.6.1"); sys.exit(0)
assert args[0] == "verify-blob"
options, blob, i = {}, args[-1], 1
while i < len(args) - 1:
    if "=" in args[i]:
        key, value = args[i].split("=", 1); options[key] = value; i += 1
    else:
        options[args[i]] = args[i + 1]; i += 2
assert options["--certificate-identity"] == "${IDENTITY}"
bundle = json.load(open(options["--bundle"]))
ok = bundle["signer"] == "keyless" and bundle["sha256"] == hashlib.sha256(open(blob, "rb").read()).hexdigest()
sys.exit(0 if ok else 1)
`;

const FAKE_DF = `#!/bin/sh
echo "Filesystem 1024-blocks Used Available Capacity Mounted"
echo "fake 1000000000 100000000 900000000 10% /"
`;

function git(cwd, ...args) {
  return execFileSync('git', args, { cwd, encoding: 'utf8' }).trim();
}

function executable(path, text) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, text);
  chmodSync(path, 0o755);
}

const prodYml = (marker) => `${JSON.stringify({
  services: {
    api: { image: 'unpinned', environment: { RELEASE_MARKER: marker } },
    worker: { image: 'unpinned', environment: { RELEASE_MARKER: marker } },
    frontend: { image: 'unpinned' },
    postgres: { image: 'postgres', environment: { POSTGRES_USER: 'nubarca', POSTGRES_DB: 'nubarca' } },
  },
}, null, 2)}\n`;

const pin = (api, frontend) =>
  `services:\n  api:\n    image: "${api}"\n  worker:\n    image: "${api}"\n  frontend:\n    image: "${frontend}"\n`;

/**
 * Release C0 installed and running; C1 on origin/main changes the API and
 * docker-compose.prod.yml, and CI published both releases.
 */
function installation() {
  const base = mkdtempSync(join(tmpdir(), 'nubarca-rollback-'));
  const dev = join(base, 'dev');
  const origin = join(base, 'github.com', owner, 'nubarca.git');
  const checkout = join(base, 'production');
  const registry = join(base, 'registry');
  const bin = join(base, 'bin');
  const statePath = join(base, 'state.json');
  const envFile = join(base, 'production.env');
  mkdirSync(dev, { recursive: true });
  mkdirSync(registry);

  executable(join(bin, 'docker'), FAKE_DOCKER);
  executable(join(bin, 'curl'), FAKE_CURL);
  executable(join(bin, 'cosign'), FAKE_COSIGN);
  executable(join(bin, 'df'), FAKE_DF);
  executable(join(bin, 'sleep'), '#!/bin/sh\nexit 0\n');
  const env = {
    ...process.env,
    PATH: `${bin}:${process.env.PATH}`,
    FAKE_STATE: statePath,
    FAKE_REGISTRY: registry,
  };
  const manifestTool = (...args) => spawnSync('python3', [join(dev, 'deploy/release-manifest.py'), ...args],
    { encoding: 'utf8', env });

  git(dev, 'init', '-q', '-b', 'main');
  git(dev, 'config', 'user.name', 'NubArca Test');
  git(dev, 'config', 'user.email', 'test@example.invalid');
  for (const file of ['deploy/update-production.sh', 'deploy/release-manifest.py', 'deploy/release-components.json']) {
    mkdirSync(dirname(join(dev, file)), { recursive: true });
    copyFileSync(resolve(root, file), join(dev, file));
  }
  chmodSync(join(dev, 'deploy/update-production.sh'), 0o755);
  // No migrations, and image provenance is proved by the fake Docker instead.
  executable(join(dev, 'deploy/production-migration-plan.py'), '#!/usr/bin/env python3\n');
  executable(join(dev, 'scripts/verify-production-image.sh'),
    '#!/bin/sh\n[ -z "$FAKE_VERIFY_FAILS" ] || { echo "image verification failed" >&2; exit 1; }\n');
  executable(join(dev, 'scripts/verify-production-frontend-image.sh'), '#!/bin/sh\n');
  for (const dir of ['src/NubArca.Api', 'frontend', 'src/NubArca.PrintAgent']) {
    mkdirSync(join(dev, dir), { recursive: true });
    writeFileSync(join(dev, dir, 'file'), '0\n');
  }
  writeFileSync(join(dev, 'src/NubArca.Api/NubArca.Api.csproj'),
    '<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>\n');
  writeFileSync(join(dev, 'docker-compose.prod.yml'), prodYml('c0'));
  writeFileSync(join(dev, '.gitignore'),
    'docker-compose.prod.local.yml\ndocker-compose.release.local.yml\ndocker-compose.release.local.yml.tmp\n/deploy-history/\n');
  git(dev, 'add', '-A');
  git(dev, 'commit', '-qm', 'release c0');
  const c0 = git(dev, 'rev-parse', 'HEAD');

  writeFileSync(join(dev, 'src/NubArca.Api/file'), '1\n');
  writeFileSync(join(dev, 'docker-compose.prod.yml'), prodYml('c1'));
  git(dev, 'commit', '-qam', 'release c1');
  const c1 = git(dev, 'rev-parse', 'HEAD');

  // CI: previous + plan + assemble as release.yml runs them, then sign and publish.
  const apiImages = { [c0]: 'a', [c1]: 'e' };
  for (const sha of [c0, c1]) {
    const work = mkdtempSync(join(base, 'ci-'));
    const found = manifestTool('previous', dev, sha, join(work, 'previous'));
    assert.equal(found.status, 0, found.stderr);
    const previous = found.stdout.trim() ? join(work, 'previous/release.json') : '-';
    const planned = JSON.parse(manifestTool('plan', dev, sha, previous).stdout);
    const built = Object.fromEntries(planned.build.map((name) => [name, {
      api: { runtime: image('nubarca-api', apiImages[sha]), openvino: image('nubarca-api-openvino', 'b') },
      frontend: { default: image('nubarca-frontend', 'c') },
      'print-agent': { 'linux-x64': image('nubarca-print-agent', 'd') },
    }[name]]));
    writeFileSync(join(work, 'built.json'), JSON.stringify(built));
    const assembled = manifestTool('assemble', dev, sha, '1.0.0', join(work, 'built.json'), previous);
    assert.equal(assembled.status, 0, assembled.stderr);
    mkdirSync(join(registry, sha));
    writeFileSync(join(registry, sha, 'release.json'), assembled.stdout);
    writeFileSync(join(registry, sha, 'release.sigstore.json'), JSON.stringify({
      signer: 'keyless', sha256: createHash('sha256').update(assembled.stdout).digest('hex'),
    }));
  }

  execFileSync('git', ['clone', '-q', '--bare', dev, origin]);
  execFileSync('git', ['clone', '-q', `file://${origin}`, checkout]);
  git(checkout, 'reset', '-q', '--hard', c0);
  writeFileSync(join(checkout, 'docker-compose.prod.local.yml'), '{"services": {}}\n');
  const oldApi = image('nubarca-api', 'a');
  const newApi = image('nubarca-api', 'e');
  const frontend = image('nubarca-frontend', 'c');
  writeFileSync(join(checkout, 'docker-compose.release.local.yml'), pin(oldApi, frontend));
  writeFileSync(envFile, 'NUBARCA_API_VARIANT=runtime\nNUBARCA_TV_ENABLED=false\n');
  writeFileSync(statePath, JSON.stringify({
    running: { api: oldApi, worker: oldApi, frontend },
    sources: { [oldApi]: c0, [newApi]: c1, [frontend]: c0 },
    ups: [],
  }));

  const update = (args, extraEnv = {}) => spawnSync(join(checkout, 'deploy/update-production.sh'),
    [...args, '--env-file', envFile], { cwd: checkout, encoding: 'utf8', env: { ...env, ...extraEnv } });
  const state = () => JSON.parse(readFileSync(statePath, 'utf8'));

  function check() {
    const report = join(base, 'check.json');
    const result = update(['check', '--report-json', report]);
    assert.equal(result.status, 0, result.stdout + result.stderr);
    const parsed = JSON.parse(readFileSync(report, 'utf8'));
    assert.equal(parsed.outcome, 'ready');
    assert.equal(parsed.candidate.sha, c1);
    assert.equal(parsed.components.backend.changed, true);
    return parsed.candidate.releaseId;
  }

  const apply = (releaseId, extraEnv) => update(['apply', '--confirm', c1, '--confirm-release', releaseId], extraEnv);

  return { checkout, c0, c1, oldApi, newApi, frontend, state, check, apply };
}

function assertPreviousRelease(w) {
  assert.equal(git(w.checkout, 'rev-parse', 'HEAD'), w.c0);
  assert.equal(git(w.checkout, 'status', '--porcelain'), '');
  assert.equal(readFileSync(join(w.checkout, 'docker-compose.prod.yml'), 'utf8'), prodYml('c0'));
  assert.equal(readFileSync(join(w.checkout, 'docker-compose.release.local.yml'), 'utf8'), pin(w.oldApi, w.frontend));
  assert.equal(existsSync(join(w.checkout, 'docker-compose.release.local.yml.tmp')), false);
}

test('a candidate that changes docker-compose.prod.yml and fails health is rolled back to the previous checkout, Compose model and pins', () => {
  const w = installation();
  const releaseId = w.check();
  const result = w.apply(releaseId, { FAKE_UNHEALTHY_IMAGE: w.newApi });
  const output = result.stdout + result.stderr;

  assert.notEqual(result.status, 0, output);
  assert.match(output, /smoke checks did not become healthy/);
  assert.match(output, /restoring the checkout to the previous release/);
  assert.match(output, /the previous release is running and healthy again/);
  assertPreviousRelease(w);

  const { running, ups } = w.state();
  assert.equal(ups.length, 2);
  // The candidate really started, with its own Compose model...
  assert.equal(ups[0].head, w.c1);
  assert.equal(ups[0].prodYml, prodYml('c1'));
  assert.equal(ups[0].model.services.api.image, w.newApi);
  // ...and the restart that followed ran entirely on the previous release.
  const restart = ups[1];
  assert.deepEqual(restart.services, ['api', 'worker']);
  assert.equal(restart.head, w.c0);
  assert.equal(restart.prodYml, prodYml('c0'));
  for (const name of ['api', 'worker']) {
    assert.equal(restart.model.services[name].image, w.oldApi);
    assert.deepEqual(restart.model.services[name].environment, { RELEASE_MARKER: 'c0' });
  }
  assert.equal(restart.model.services.frontend.image, w.frontend);
  assert.deepEqual(running, { api: w.oldApi, worker: w.oldApi, frontend: w.frontend });
});

test('a failure after the fast-forward but before any restart also restores the checkout', () => {
  const w = installation();
  const result = w.apply(w.check(), { FAKE_VERIFY_FAILS: '1' });
  assert.notEqual(result.status, 0, result.stdout + result.stderr);
  assert.match(result.stdout, /restoring the checkout to the previous release/);
  assertPreviousRelease(w);
  assert.deepEqual(w.state().ups, []);
});

test('a healthy candidate stays installed: checkout, Compose model and pins are the new release', () => {
  const w = installation();
  const result = w.apply(w.check());
  const output = result.stdout + result.stderr;
  assert.equal(result.status, 0, output);
  assert.doesNotMatch(output, /restoring the checkout/);
  assert.equal(git(w.checkout, 'rev-parse', 'HEAD'), w.c1);
  assert.equal(readFileSync(join(w.checkout, 'docker-compose.prod.yml'), 'utf8'), prodYml('c1'));
  assert.equal(readFileSync(join(w.checkout, 'docker-compose.release.local.yml'), 'utf8'), pin(w.newApi, w.frontend));
  const { running, ups } = w.state();
  assert.equal(ups.length, 1);
  assert.deepEqual(running, { api: w.newApi, worker: w.newApi, frontend: w.frontend });
});
