import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import {
  chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

// The Print Box update service (src/NubArca.PrintAgent/linux/update-print-box.sh)
// run against a scratch tree with a fake systemctl and agent. Once it has moved
// the installed bundle, every way it can end short of the new agent reaching
// the server — no heartbeat, a failing unit install or daemon-reload, a signal —
// must leave the previous bundle installed and running, and report "failed".

const here = dirname(fileURLToPath(import.meta.url));
const script = resolve(here, '../../src/NubArca.PrintAgent/linux/update-print-box.sh');
const REQUEST_ID = '1700000000000';

function agentScript(version) {
  return `#!/usr/bin/env bash
set -euo pipefail
case "$1" in
  version) echo ${version} ;;
  self-update-fetch)
    out="$5"
    cp "$FAKE_ZIP" "$out/nubarca-print-agent-0.9.0-linux-x64.zip"
    printf '{"version":"0.9.0","fileName":"nubarca-print-agent-0.9.0-linux-x64.zip","sha256":"%s","requestId":"${REQUEST_ID}"}' x > "$out/offer.json"
    ;;
esac
`;
}

// Bundles before 0.9.1 carry no Wi-Fi setting.
function bundle(dir, version, { wifi = true } = {}) {
  mkdirSync(join(dir, 'linux'), { recursive: true });
  writeFileSync(join(dir, 'NubArca.PrintAgent'), agentScript(version));
  chmodSync(join(dir, 'NubArca.PrintAgent'), 0o755);
  for (const name of ['nubarca-print-agent@.service', 'nubarca-print-agent-update.path',
    'nubarca-print-agent-update.service', 'nubarca-sim', 'nubarca-print-box.rules',
    ...(wifi ? ['nubarca-wifi-powersave.conf'] : [])]) {
    writeFileSync(join(dir, 'linux', name), `${name} ${version}\n`);
  }
  writeFileSync(join(dir, 'linux', 'update-print-box.sh'), '#!/usr/bin/env bash\n');
}

const SYSTEMCTL = `#!/usr/bin/env bash
echo "$*" >> "$FAKE_ROOT/systemctl.log"
case "$1" in
  daemon-reload)
    if [[ -f "$FAKE_ROOT/fail-reload" ]]; then rm -f "$FAKE_ROOT/fail-reload"; exit 1; fi ;;
  start)
    echo running > "$FAKE_ROOT/agent-state"
    if [[ "$(cat "$FAKE_ROOT/healthy")" == yes ]]; then
      "$FAKE_ROOT/opt/nubarca-print-agent/NubArca.PrintAgent" version > "$FAKE_ROOT/var/lib/nubarca-print-agent/box/heartbeat-ok"
    fi ;;
  stop) echo stopped > "$FAKE_ROOT/agent-state" ;;
  is-failed) exit 1 ;;
esac
exit 0
`;

const RUNUSER = `#!/usr/bin/env bash
shift 3
exec "$@"
`;

// provisioned: the installer manages this box's network (it has the polkit rule).
function box({ healthy = true, failReload = false, stagedVersion = '0.9.0', provisioned = false,
  request = { version: '0.9.0', requestId: REQUEST_ID } } = {}) {
  const root = mkdtempSync(join(tmpdir(), 'nubarca-print-box-update-'));
  const bin = join(root, 'fakebin');
  mkdirSync(bin);
  writeFileSync(join(bin, 'systemctl'), SYSTEMCTL);
  writeFileSync(join(bin, 'runuser'), RUNUSER);
  chmodSync(join(bin, 'systemctl'), 0o755);
  chmodSync(join(bin, 'runuser'), 0o755);
  bundle(join(root, 'opt/nubarca-print-agent'), '0.8.0', { wifi: false });
  if (provisioned) {
    mkdirSync(join(root, 'etc/polkit-1/rules.d'), { recursive: true });
    writeFileSync(join(root, 'etc/polkit-1/rules.d/49-nubarca-print-box.rules'), 'nubarca-print-box.rules 0.8.0\n');
  }
  const state = join(root, 'var/lib/nubarca-print-agent/box');
  mkdirSync(state, { recursive: true });
  if (request) writeFileSync(join(state, 'update-request.json'), JSON.stringify(request));
  mkdirSync(join(root, 'etc/nubarca-print-agent'), { recursive: true });
  writeFileSync(join(root, 'etc/nubarca-print-agent/box.json'), '{}');
  mkdirSync(join(root, 'etc/systemd/system'), { recursive: true });
  mkdirSync(join(root, 'usr/lib/cups/backend'), { recursive: true });
  writeFileSync(join(root, 'healthy'), healthy ? 'yes' : 'no');
  if (failReload) writeFileSync(join(root, 'fail-reload'), '');

  const staged = join(root, 'staged-bundle');
  bundle(staged, stagedVersion);
  const zip = join(root, 'bundle.zip');
  const zipped = spawnSync('python3', ['-c', `
import os, sys, zipfile
src, out = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, "w") as z:
    for base, _, files in os.walk(src):
        for name in files:
            path = os.path.join(base, name)
            z.write(path, os.path.relpath(path, src))
`, staged, zip]);
  assert.equal(zipped.status, 0, String(zipped.stderr));

  const env = {
    ...process.env,
    PATH: `${bin}:${process.env.PATH}`,
    FAKE_ROOT: root,
    FAKE_ZIP: zip,
    NUBARCA_PRINT_UPDATE_TEST_ROOT: root,
    NUBARCA_PRINT_UPDATE_HEALTH_SECONDS: '3',
    NUBARCA_PRINT_UPDATE_POLL_SECONDS: '1',
  };
  return { root, state, env };
}

const installed = (root) => spawnSync(join(root, 'opt/nubarca-print-agent/NubArca.PrintAgent'), ['version'],
  { encoding: 'utf8' }).stdout.trim();
const result = (state) => readFileSync(join(state, 'update-result'), 'utf8').trim();

function assertFinished(root, state) {
  assert.equal(existsSync(join(state, 'update-request.json')), false, 'the request is always taken down');
  assert.equal(existsSync(join(state, 'update-request.claimed')), false, 'and so is its claim');
  assert.equal(existsSync(join(root, 'run/nubarca-print-agent-update/in-progress')), false,
    'the in-progress marker is always removed');
  assert.equal(existsSync(join(root, 'opt/nubarca-print-agent.new')), false);
  assert.equal(existsSync(join(root, 'opt/nubarca-print-agent.failed')), false);
}

function assertRestored(root, state) {
  assert.equal(installed(root), '0.8.0', 'the previous agent is installed again');
  assert.equal(readFileSync(join(root, 'etc/systemd/system/nubarca-print-agent@.service'), 'utf8').trim(),
    'nubarca-print-agent@.service 0.8.0', 'and its own unit with it');
  assert.equal(readFileSync(join(root, 'agent-state'), 'utf8').trim(), 'running', 'and running');
  assert.equal(result(state), `failed 0.9.0 ${REQUEST_ID}`);
  assertFinished(root, state);
}

test('a new agent that reaches the server stays, and the outcome is ok', () => {
  const { root, state, env } = box();
  const run = spawnSync('bash', [script], { env, encoding: 'utf8' });
  assert.equal(run.status, 0, run.stdout + run.stderr);
  assert.equal(installed(root), '0.9.0');
  assert.equal(result(state), `ok 0.9.0 ${REQUEST_ID}`);
  assert.equal(spawnSync(join(root, 'opt/nubarca-print-agent.previous/NubArca.PrintAgent'), ['version'],
    { encoding: 'utf8' }).stdout.trim(), '0.8.0', 'the previous bundle is kept for a manual rollback');
  assertFinished(root, state);
});

const WIFI_SETTING = 'etc/NetworkManager/conf.d/zz-nubarca-wifi-powersave.conf';

test('a box whose network the installer manages gets the Wi-Fi setting with the update', () => {
  const { root, state, env } = box({ provisioned: true });
  const run = spawnSync('bash', [script], { env, encoding: 'utf8' });
  assert.equal(run.status, 0, run.stdout + run.stderr);
  assert.equal(readFileSync(join(root, WIFI_SETTING), 'utf8').trim(), 'nubarca-wifi-powersave.conf 0.9.0');
  assert.equal(readFileSync(join(root, 'etc/polkit-1/rules.d/49-nubarca-print-box.rules'), 'utf8').trim(),
    'nubarca-print-box.rules 0.9.0');
  assert.equal(result(state), `ok 0.9.0 ${REQUEST_ID}`);
});

test('a box that manages its own network gets no Wi-Fi setting', () => {
  const { root, env } = box();
  const run = spawnSync('bash', [script], { env, encoding: 'utf8' });
  assert.equal(run.status, 0, run.stdout + run.stderr);
  assert.equal(existsSync(join(root, WIFI_SETTING)), false);
});

test('going back to a bundle with no Wi-Fi setting leaves the setting in place', () => {
  const { root, state, env } = box({ provisioned: true, healthy: false });
  const run = spawnSync('bash', [script], { env, encoding: 'utf8' });
  assert.equal(run.status, 1);
  assertRestored(root, state);
  assert.equal(readFileSync(join(root, WIFI_SETTING), 'utf8').trim(), 'nubarca-wifi-powersave.conf 0.9.0');
});

test('a new agent that never reaches the server is replaced by the previous one', () => {
  const { root, state, env } = box({ healthy: false });
  const run = spawnSync('bash', [script], { env, encoding: 'utf8' });
  assert.equal(run.status, 1);
  assertRestored(root, state);
});

test('a failure installing the new system files after the swap restores the previous agent', () => {
  const { root, state, env } = box({ failReload: true });
  const run = spawnSync('bash', [script], { env, encoding: 'utf8' });
  assert.notEqual(run.status, 0);
  assertRestored(root, state);
});

test('a stop signal after the swap restores the previous agent', async () => {
  const { root, state, env } = box({ healthy: false });
  env.NUBARCA_PRINT_UPDATE_HEALTH_SECONDS = '60';
  const child = spawn('bash', [script], { env, stdio: 'ignore' });
  const exited = new Promise((done) => child.on('exit', (code, signal) => done({ code, signal })));
  // Wait until the new bundle is in place and the health check is waiting.
  for (let i = 0; i < 100 && installed(root) !== '0.9.0'; i++) await new Promise((r) => setTimeout(r, 100));
  assert.equal(installed(root), '0.9.0');
  child.kill('SIGTERM');
  const { code } = await exited;
  assert.notEqual(code, 0);
  assertRestored(root, state);
});

test('a staged bundle that is not the offered version is never swapped in', () => {
  const { root, state, env } = box({ stagedVersion: '0.9.1' });
  const run = spawnSync('bash', [script], { env, encoding: 'utf8' });
  assert.equal(run.status, 1);
  assert.equal(installed(root), '0.8.0');
  assert.equal(existsSync(join(root, 'agent-state')), false, 'the running agent was never stopped');
  assert.equal(result(state), `failed 0.9.0 ${REQUEST_ID}`);
  assertFinished(root, state);
});

function assertUntouched(root, state) {
  assert.equal(installed(root), '0.8.0');
  assert.equal(existsSync(join(root, 'agent-state')), false, 'the running agent was never stopped');
  assert.equal(existsSync(join(root, 'run/nubarca-print-agent-update/in-progress')), false);
  assert.equal(existsSync(join(state, 'update-request.claimed')), false);
}

test('a request the agent already took back is never installed', () => {
  // The agent renamed its request away before this service could claim it.
  const { root, state, env } = box({ request: null });
  const run = spawnSync('bash', [script], { env, encoding: 'utf8' });
  assert.equal(run.status, 0, run.stdout + run.stderr);
  assert.match(run.stdout, /no update request to pick up/);
  assertUntouched(root, state);
  assert.equal(existsSync(join(state, 'update-result')), false);
});

test('a request that is not valid installs nothing', () => {
  const { root, state, env } = box({ request: { version: '../0.9', requestId: 'x' } });
  const run = spawnSync('bash', [script], { env, encoding: 'utf8' });
  assert.equal(run.status, 1);
  assertUntouched(root, state);
});

test('an offer that is not what the agent asked for installs nothing', () => {
  const { root, state, env } = box({ request: { version: '0.9.0', requestId: '42' } });
  const run = spawnSync('bash', [script], { env, encoding: 'utf8' });
  assert.equal(run.status, 1);
  assertUntouched(root, state);
  assert.equal(result(state), 'failed 0.9.0 42');
});
