import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

// The checks an installation makes on a signed release manifest before it
// installs anything it names: the images come from NubArca's own GHCR
// namespace by digest, the manifest is for exactly the candidate commit, and
// an image built from an older commit is accepted only when nothing of its
// component changed since.

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../..');
const tool = resolve(here, '../release-manifest.py');
const updater = readFileSync(resolve(here, '../update-production.sh'), 'utf8');
const releaseWorkflow = readFileSync(resolve(root, '.github/workflows/release.yml'), 'utf8');
const owner = 'littlefly1969';
const digest = (c) => `sha256:${c.repeat(64)}`;

function run(...args) {
  return spawnSync('python3', [tool, ...args], { encoding: 'utf8' });
}

function git(cwd, ...args) {
  return execFileSync('git', args, { cwd, encoding: 'utf8' }).trim();
}

/** A checkout with the real components file and three commits touching different components. */
function checkout() {
  const cwd = mkdtempSync(join(tmpdir(), 'nubarca-release-checkout-'));
  git(cwd, 'init', '-q', '-b', 'main');
  git(cwd, 'config', 'user.name', 'NubArca Test');
  git(cwd, 'config', 'user.email', 'test@example.invalid');
  mkdirSync(join(cwd, 'deploy'));
  writeFileSync(join(cwd, 'deploy/release-components.json'),
    readFileSync(resolve(root, 'deploy/release-components.json')));
  for (const dir of ['src/NubArca.Api', 'frontend', 'src/NubArca.PrintAgent']) {
    mkdirSync(join(cwd, dir), { recursive: true });
    writeFileSync(join(cwd, dir, 'file'), '1\n');
  }
  git(cwd, 'add', '.');
  git(cwd, 'commit', '-qm', 'base');
  const base = git(cwd, 'rev-parse', 'HEAD');
  writeFileSync(join(cwd, 'frontend/file'), '2\n');
  git(cwd, 'commit', '-qam', 'frontend');
  const frontend = git(cwd, 'rev-parse', 'HEAD');
  writeFileSync(join(cwd, 'README'), 'docs\n');
  git(cwd, 'add', '.');
  git(cwd, 'commit', '-qm', 'docs');
  const head = git(cwd, 'rev-parse', 'HEAD');
  return { cwd, base, frontend, head };
}

function manifest(source, components, overrides = {}) {
  return {
    schemaVersion: 1,
    artifact: 'nubarca-release',
    repository: `${owner}/nubarca`,
    sourceSha: source,
    version: '0.3.0',
    components: {
      api: {
        sourceSha: components.api,
        version: '0.3.0',
        images: {
          runtime: `ghcr.io/${owner}/nubarca-api@${digest('a')}`,
          openvino: `ghcr.io/${owner}/nubarca-api-openvino@${digest('b')}`,
        },
      },
      frontend: {
        sourceSha: components.frontend,
        images: { default: `ghcr.io/${owner}/nubarca-frontend@${digest('c')}` },
      },
      'print-agent': {
        sourceSha: components.agent,
        version: '0.8.0',
        images: { 'linux-x64': `ghcr.io/${owner}/nubarca-print-agent@${digest('d')}` },
      },
    },
    ...overrides,
  };
}

function write(document) {
  const dir = mkdtempSync(join(tmpdir(), 'nubarca-release-manifest-'));
  const path = join(dir, 'release.json');
  writeFileSync(path, JSON.stringify(document));
  return path;
}

test('trust constants are fixed to NubArca and its Release workflow on main', () => {
  assert.equal(run('trust', 'owner').stdout.trim(), owner);
  assert.equal(run('trust', 'release-image').stdout.trim(), `ghcr.io/${owner}/nubarca-release`);
  assert.equal(run('trust', 'identity').stdout.trim(),
    `https://github.com/${owner}/nubarca/.github/workflows/release.yml@refs/heads/main`);
  assert.equal(run('trust', 'issuer').stdout.trim(), 'https://token.actions.githubusercontent.com');
  assert.equal(run('trust', 'anything').status, 1);
});

test('plan names the last commit that changed each component', () => {
  const { cwd, base, frontend, head } = checkout();
  const result = run('plan', cwd, head);
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(JSON.parse(result.stdout), { api: base, frontend, 'print-agent': base });
});

test('verify accepts older images whose component did not change, and prints one line per image', () => {
  const { cwd, base, frontend, head } = checkout();
  const result = run('verify', write(manifest(head, { api: base, frontend, agent: base })), head, cwd);
  assert.equal(result.status, 0, result.stderr);
  const lines = result.stdout.trim().split('\n').map((l) => l.split('\t'));
  assert.deepEqual(lines.map((l) => `${l[0]}/${l[1]}`),
    ['api/openvino', 'api/runtime', 'frontend/default', 'print-agent/linux-x64']);
  assert.equal(lines[3][3], base);
  assert.equal(lines[3][4], '0.8.0');
});

test('verify refuses an image whose component changed after it was built', () => {
  const { cwd, base, head } = checkout();
  const result = run('verify', write(manifest(head, { api: base, frontend: base, agent: base })), head, cwd);
  assert.equal(result.status, 1);
  assert.match(result.stderr, /component frontend changed after/);
});

test('verify refuses an API image older than a documentation change its help corpus is built from', () => {
  const { cwd, base, frontend, head } = checkout();
  mkdirSync(join(cwd, 'docs'), { recursive: true });
  writeFileSync(join(cwd, 'docs/guide.md'), 'new help\n');
  git(cwd, 'add', '.');
  git(cwd, 'commit', '-qm', 'docs only');
  const docs = git(cwd, 'rev-parse', 'HEAD');
  const plan = JSON.parse(run('plan', cwd, docs).stdout);
  assert.equal(plan.api, docs, 'a docs-only commit rebuilds the API image');
  const result = run('verify', write(manifest(docs, { api: base, frontend, agent: base })), docs, cwd);
  assert.equal(result.status, 1);
  assert.match(result.stderr, /component api changed after/);
  assert.ok(head);
});

test('verify refuses a manifest for another commit', () => {
  const { cwd, base, frontend, head } = checkout();
  const result = run('verify', write(manifest(frontend, { api: base, frontend, agent: base })), head, cwd);
  assert.equal(result.status, 1);
  assert.match(result.stderr, /not the candidate/);
});

test('verify refuses images outside NubArca\'s namespace or without a digest', () => {
  const { cwd, base, frontend, head } = checkout();
  const foreign = manifest(head, { api: base, frontend, agent: base });
  foreign.components.frontend.images.default = `ghcr.io/somebody/nubarca-frontend@${digest('c')}`;
  assert.match(run('verify', write(foreign), head, cwd).stderr, /must come from ghcr\.io\/littlefly1969\/nubarca-frontend/);
  const tagged = manifest(head, { api: base, frontend, agent: base });
  tagged.components.api.images.runtime = `ghcr.io/${owner}/nubarca-api:latest`;
  assert.match(run('verify', write(tagged), head, cwd).stderr, /immutable sha256 digest/);
});

test('verify refuses a component source that is not an ancestor of the release', () => {
  const { cwd, base, frontend, head } = checkout();
  git(cwd, 'checkout', '-q', '-b', 'side', base);
  writeFileSync(join(cwd, 'src/NubArca.Api/file'), 'side\n');
  git(cwd, 'commit', '-qam', 'side');
  const side = git(cwd, 'rev-parse', 'HEAD');
  git(cwd, 'checkout', '-q', 'main');
  const result = run('verify', write(manifest(head, { api: side, frontend, agent: base })), head, cwd);
  assert.equal(result.status, 1);
  assert.match(result.stderr, /not an ancestor/);
});

test('verify refuses a missing component or a foreign repository', () => {
  const { cwd, base, frontend, head } = checkout();
  const partial = manifest(head, { api: base, frontend, agent: base });
  delete partial.components['print-agent'];
  assert.match(run('verify', write(partial), head, cwd).stderr, /exactly the release's components/);
  const other = manifest(head, { api: base, frontend, agent: base }, { repository: 'somebody/nubarca' });
  assert.match(run('verify', write(other), head, cwd).stderr, /not for littlefly1969\/nubarca/);
});

test('the production updater installs only from a verified, signed manifest', () => {
  assert.match(updater, /cosign verify-blob/);
  assert.match(updater, /--certificate-identity "\$SIGNER_IDENTITY"/);
  assert.match(updater, /--certificate-oidc-issuer "\$SIGNER_ISSUER"/);
  assert.match(updater, /release-manifest\.py verify/);
  // Signature first, content second, and both before any image is pulled.
  const signature = updater.indexOf('cosign verify-blob');
  const content = updater.indexOf('release-manifest.py verify');
  const pull = updater.indexOf('docker pull "$NEW_API_IMAGE"');
  assert.ok(signature > 0 && signature < content && content < pull);
  // Trust is never derived from the checkout's remote.
  assert.doesNotMatch(updater, /GHCR_OWNER="\$\(printf/);
  assert.match(updater, /GHCR_OWNER="\$\(python3 deploy\/release-manifest\.py trust owner\)"/);
});

test('the production updater publishes the Print Box bundle only after the server is healthy', () => {
  const healthy = updater.indexOf('rollback_needed=false');
  const publish = updater.indexOf('deploy/pull-publish-print-agent-image.sh "$PRINT_AGENT_IMAGE"');
  assert.ok(healthy > 0 && publish > healthy);
});

test('the production updater supports a lean installation and an installation without TV', () => {
  assert.match(updater, /NUBARCA_API_VARIANT/);
  assert.match(updater, /\[\[ "\$API_VARIANT" == "openvino" \]\] && COMPOSE_FILES\+=\(-f docker-compose\.facedirect-api\.yml\)/);
  assert.match(updater, /NUBARCA_TV_ENABLED/);
  assert.match(updater, /--report-json/);
});

test('the Release workflow signs keylessly from main with a pinned cosign', () => {
  assert.match(releaseWorkflow, /branches:\s*\n\s*- main/);
  assert.match(releaseWorkflow, /id-token: write/);
  assert.match(releaseWorkflow, /COSIGN_SHA256: [0-9a-f]{64}/);
  assert.match(releaseWorkflow, /sha256sum -c -/);
  assert.match(releaseWorkflow, /cosign" sign-blob --yes/);
  assert.match(releaseWorkflow, /cancel-in-progress: false/);
});

// A component's paths decide whether its image is rebuilt and whether an
// older image may stand for a release, so they must contain EVERY build
// input: anything the Dockerfile copies, every file the project pulls in
// from outside its directory, the Dockerfile, the .dockerignore that shapes
// its context, and the workflow that runs the build.
const componentsFile = JSON.parse(readFileSync(resolve(root, 'deploy/release-components.json'), 'utf8'));

function covered(paths, file) {
  const normal = file.replace(/\/+$/, '');
  return paths.some((p) => normal === p || normal.startsWith(`${p}/`));
}

function dockerfileSources(text) {
  const joined = text.replace(/\\\r?\n/g, ' ');
  const sources = [];
  for (const line of joined.split(/\r?\n/)) {
    const match = line.match(/^\s*(COPY|ADD)\s+(.*)$/i);
    if (!match) continue;
    const words = match[2].trim().split(/\s+/);
    if (words.some((w) => /^--from=/i.test(w))) continue;
    const args = words.filter((w) => !w.startsWith('--'));
    sources.push(...args.slice(0, -1).map((s) => s.replace(/^\.\//, '')));
  }
  return sources;
}

function projectInputs(project) {
  const text = readFileSync(resolve(root, project), 'utf8');
  const dir = dirname(project);
  const inputs = [];
  for (const match of text.matchAll(/(?:Include|Update|Project)="([^"]+)"/g)) {
    const value = match[1].replace(/\\/g, '/');
    if (!value.startsWith('../')) continue;
    inputs.push(join(dir, value).replace(/\\/g, '/'));
  }
  return inputs;
}

for (const [name, component] of Object.entries(componentsFile.components)) {
  test(`component ${name} declares every input of its build`, () => {
    const build = component.buildInputs;
    assert.ok(build, `${name} names its build inputs`);
    const required = [...build.workflows];
    for (const dockerfile of build.dockerfiles) {
      required.push(dockerfile, '.dockerignore');
      const sources = dockerfileSources(readFileSync(resolve(root, dockerfile), 'utf8'));
      assert.ok(sources.length > 0, `${dockerfile} copies something`);
      required.push(...sources);
    }
    for (const project of build.projects) {
      required.push(project, ...projectInputs(project));
    }
    const missing = [...new Set(required)].filter((file) => !covered(component.paths, file));
    assert.deepEqual(missing, [], `${name} paths miss build inputs`);
  });
}

test('the API image declares the documentation its help corpus is built from', () => {
  const sources = dockerfileSources(readFileSync(resolve(root, 'src/NubArca.Api/Dockerfile'), 'utf8'));
  for (const doc of ['README.md', 'ARCHITECTURE.md', 'docs/']) assert.ok(sources.includes(doc), doc);
  const paths = componentsFile.components.api.paths;
  for (const doc of ['README.md', 'ARCHITECTURE.md', 'docs/guide.md']) assert.ok(covered(paths, doc), doc);
});

test('the build-input check catches an undeclared input', () => {
  const sources = dockerfileSources('FROM x AS a\nCOPY --from=a /x /y\nCOPY README.md \\\n  other.md ./\nCOPY docs/ docs/\n');
  assert.deepEqual(sources, ['README.md', 'other.md', 'docs/']);
  assert.equal(covered(['src/NubArca.Api'], 'README.md'), false);
});
