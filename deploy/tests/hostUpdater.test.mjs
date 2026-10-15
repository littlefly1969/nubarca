import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { chmodSync, existsSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

// The host updater turns one mailbox request from the api into one run of the
// guided update script, and nothing else: two actions, validated arguments,
// and a sanitized outcome back — never a path or a log line.

const here = dirname(fileURLToPath(import.meta.url));
const updater = resolve(here, '../updater/nubarca-updater.py');
const installer = readFileSync(resolve(here, '../updater/install-updater.sh'), 'utf8');
const id = '0f8fad5b-d9cb-469f-a165-70867728950e';
const sha = 'a'.repeat(40);

/** A mailbox, and a fake update script that records its arguments and writes the report it is told to. */
function setup(report, exitCode = 0) {
  const dir = mkdtempSync(join(tmpdir(), 'nubarca-updater-'));
  const mailbox = join(dir, 'mailbox');
  const logs = join(dir, 'logs');
  spawnSync('mkdir', ['-p', mailbox]);
  const script = join(dir, 'update.sh');
  writeFileSync(script, `#!/usr/bin/env bash
printf '%s\\n' "$@" > "${dir}/args"
while [[ $# -gt 0 ]]; do
  if [[ "$1" == --report-json ]]; then printf '%s' '${JSON.stringify(report)}' > "$2"; fi
  shift
done
exit ${exitCode}
`);
  chmodSync(script, 0o755);
  const run = () => spawnSync('python3', [updater, '--mailbox', mailbox, '--checkout', dir,
    '--env-file', join(dir, '.env'), '--script', script, '--log-dir', logs,
    '--auto-check-hours', '0', '--poll-seconds', '0.05', '--once'], { encoding: 'utf8', timeout: 20000 });
  const request = (body) => writeFileSync(join(mailbox, 'request.json'), JSON.stringify(body));
  const status = () => JSON.parse(readFileSync(join(mailbox, 'status.json'), 'utf8'));
  const args = () => (existsSync(join(dir, 'args')) ? readFileSync(join(dir, 'args'), 'utf8').trim().split('\n') : null);
  return { dir, mailbox, run, request, status, args };
}

test('a check request runs the script once and publishes its report', () => {
  const box = setup({ schemaVersion: 1, mode: 'check', outcome: 'ready', candidate: { sha } });
  box.request({ id, action: 'check' });
  const result = box.run();
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(box.args().slice(0, 2), ['check', '--env-file']);
  assert.ok(!box.args().includes('--confirm'));
  const status = box.status();
  assert.equal(status.state, 'idle');
  assert.equal(status.last.requestId, id);
  assert.equal(status.last.outcome, 'ready');
  assert.ok(status.lastCheckAt);
  assert.ok(status.heartbeatAt);
  assert.equal(JSON.parse(readFileSync(join(box.mailbox, 'report.json'), 'utf8')).candidate.sha, sha);
  assert.ok(!existsSync(join(box.mailbox, 'request.json')));
});

test('an install request passes exactly the confirmed commit and the migration confirmation', () => {
  const box = setup({ schemaVersion: 1, mode: 'apply', outcome: 'applied' });
  writeFileSync(join(box.mailbox, 'report.json'), '{"outcome":"ready"}');
  box.request({ id, action: 'apply', sha, confirmMigrations: true });
  box.run();
  const args = box.args();
  assert.equal(args[0], 'apply');
  assert.equal(args[args.indexOf('--confirm') + 1], sha);
  assert.ok(args.includes('--confirm-migrations'));
  assert.equal(box.status().last.outcome, 'applied');
  // What was installed is the new baseline: the old check report is gone.
  assert.ok(!existsSync(join(box.mailbox, 'report.json')));
});

test('a request that names anything but the two actions is refused without running the script', () => {
  for (const body of [
    { id, action: 'shell', command: 'rm -rf /' },
    { id, action: 'apply', sha: 'main', confirmMigrations: false },
    { id, action: 'apply', sha },
    { id: '../../etc', action: 'check' },
  ]) {
    const box = setup({ outcome: 'ready' });
    box.request(body);
    box.run();
    assert.equal(box.args(), null, JSON.stringify(body));
    assert.equal(box.status().last.outcome, 'rejected');
  }
});

test('a failure reaches the api as one sanitized line, without paths or credentials', () => {
  const box = setup({
    schemaVersion: 1, mode: 'apply', outcome: 'failed',
    message: 'pg_dump failed writing /srv/backups/pre-update.sql.gz with password=hunter2',
  }, 1);
  box.request({ id, action: 'apply', sha, confirmMigrations: false });
  box.run();
  const { last } = box.status();
  assert.equal(last.outcome, 'failed');
  assert.doesNotMatch(last.message, /\/srv|hunter2/);
  assert.match(last.message, /pg_dump failed/);
});

test('a script that dies without a report is a failure, not a success', () => {
  const box = setup(null, 9);
  writeFileSync(join(box.dir, 'update.sh'), '#!/usr/bin/env bash\nexit 9\n');
  box.request({ id, action: 'check' });
  box.run();
  assert.equal(box.status().last.outcome, 'failed');
});

test('a run interrupted by a restart is reported as interrupted', () => {
  const box = setup({ outcome: 'ready' });
  writeFileSync(join(box.mailbox, 'status.json'), JSON.stringify({
    state: 'applying', current: { requestId: id, action: 'apply', sha, startedAt: '2026-10-15T10:00:00+00:00' },
  }));
  box.request({ id, action: 'check' });
  box.run();
  // The interrupted apply was recorded first, then the new check replaced it as the last outcome.
  assert.equal(box.status().last.action, 'check');
  const fresh = setup({ outcome: 'ready' });
  writeFileSync(join(fresh.mailbox, 'status.json'), JSON.stringify({
    state: 'applying', current: { requestId: id, action: 'apply', sha, startedAt: '2026-10-15T10:00:00+00:00' },
  }));
  spawnSync('timeout', ['2', 'python3', updater, '--mailbox', fresh.mailbox, '--checkout', fresh.dir,
    '--env-file', join(fresh.dir, '.env'), '--auto-check-hours', '0', '--poll-seconds', '0.1']);
  assert.equal(fresh.status().last.outcome, 'interrupted');
  assert.equal(fresh.status().state, 'idle');
});

test('the installer pins cosign by checksum and never runs the updater as root', () => {
  assert.match(installer, /COSIGN_VERSION="v2\.6\.1"/);
  assert.match(installer, /sha256sum -c -/);
  assert.match(installer, /User=\$OWNER/);
  assert.match(installer, /the checkout is owned by root/);
  assert.match(installer, /--mailbox/);
  assert.match(installer, /Restart=always/);
});
