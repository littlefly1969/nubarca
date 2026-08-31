import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import {
  mkdtempSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import test from 'node:test';

const planner = resolve(
  import.meta.dirname,
  '../../deploy/production-migration-plan.py',
);
const migrationRoot = 'src/NubArca.Api/Data/Migrations';
const migrationId = '20260102030405_AddSafeThing';
const repositoryPolicy = resolve(
  import.meta.dirname,
  '../../deploy/migration-policy.json',
);
const repositoryMigrations = resolve(
  import.meta.dirname,
  '../../src/NubArca.Api/Data/Migrations',
);

function git(cwd, ...args) {
  return execFileSync('git', args, { cwd, encoding: 'utf8' }).trim();
}

function write(cwd, path, contents) {
  const absolute = join(cwd, path);
  mkdirSync(resolve(absolute, '..'), { recursive: true });
  writeFileSync(absolute, contents);
}

function fixture({ policy = true, compatible = true, mutateOld = false } = {}) {
  const cwd = mkdtempSync(join(tmpdir(), 'nubarca-migration-plan-'));
  git(cwd, 'init', '-q');
  git(cwd, 'config', 'user.name', 'NubArca Test');
  git(cwd, 'config', 'user.email', 'test@example.invalid');
  write(cwd, `${migrationRoot}/20250101000000_Initial.cs`, '// initial\n');
  write(cwd, `${migrationRoot}/20250101000000_Initial.Designer.cs`, '// initial designer\n');
  write(cwd, `${migrationRoot}/AppDbContextModelSnapshot.cs`, '// snapshot 1\n');
  write(cwd, 'deploy/migration-policy.json', '{"schemaVersion":1,"migrations":{}}\n');
  git(cwd, 'add', '.');
  git(cwd, 'commit', '-qm', 'base');
  const base = git(cwd, 'rev-parse', 'HEAD');

  write(cwd, `${migrationRoot}/${migrationId}.cs`, '// additive migration\n');
  write(cwd, `${migrationRoot}/${migrationId}.Designer.cs`, '// additive designer\n');
  write(cwd, `${migrationRoot}/AppDbContextModelSnapshot.cs`, '// snapshot 2\n');
  if (mutateOld) {
    write(cwd, `${migrationRoot}/20250101000000_Initial.cs`, '// rewritten history\n');
  }
  const migrations = policy
    ? { [migrationId]: {
        automated: true,
        previousApplicationCompatible: compatible,
        reason: 'Additive test migration.',
      } }
    : {};
  write(cwd, 'deploy/migration-policy.json', `${JSON.stringify({ schemaVersion: 1, migrations })}\n`);
  git(cwd, 'add', '.');
  git(cwd, 'commit', '-qm', 'candidate');
  return { cwd, base, candidate: git(cwd, 'rev-parse', 'HEAD') };
}

function plan(fixtureRepo) {
  return spawnSync('python3', [planner, fixtureRepo.base, fixtureRepo.candidate], {
    cwd: fixtureRepo.cwd,
    encoding: 'utf8',
  });
}

test('accepts an additive, declared, previous-application-compatible migration', () => {
  const result = plan(fixture());
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout.trim(), migrationId);
});

test('rejects a migration without an automation policy', () => {
  const result = plan(fixture({ policy: false }));
  assert.equal(result.status, 2);
  assert.match(result.stderr, /has no production automation policy/);
});

test('rejects a migration that cannot roll back to the previous application', () => {
  const result = plan(fixture({ compatible: false }));
  assert.equal(result.status, 2);
  assert.match(result.stderr, /does not permit application-image rollback/);
});

test('rejects rewritten migration history even beside an approved additive migration', () => {
  const result = plan(fixture({ mutateOld: true }));
  assert.equal(result.status, 2);
  assert.match(result.stderr, /only additive migration files are automatic/);
});

test('repository policy explicitly classifies every governed migration', () => {
  const policy = JSON.parse(readFileSync(repositoryPolicy, 'utf8'));
  assert.match(
    policy.classificationRequiredFrom,
    /^\d{14}_[A-Za-z0-9_]+$/,
  );

  const governed = readdirSync(repositoryMigrations)
    .map((name) => /^(\d{14}_[A-Za-z0-9_]+)\.cs$/.exec(name)?.[1])
    .filter((id) => id && id >= policy.classificationRequiredFrom)
    .sort();
  const classified = Object.keys(policy.migrations)
    .filter((id) => id >= policy.classificationRequiredFrom)
    .sort();

  assert.deepEqual(classified, governed);
  for (const id of governed) {
    const rule = policy.migrations[id];
    assert.equal(typeof rule.automated, 'boolean', `${id} automated`);
    assert.equal(
      typeof rule.previousApplicationCompatible,
      'boolean',
      `${id} previousApplicationCompatible`,
    );
    assert.equal(
      typeof rule.reason,
      'string',
      `${id} compatibility reason`,
    );
    assert.ok(rule.reason.trim(), `${id} compatibility reason`);
  }
});
