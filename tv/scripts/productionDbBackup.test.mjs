import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { gzipSync } from 'node:zlib';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import test from 'node:test';

const verifier = resolve(
  import.meta.dirname,
  '../../deploy/verify-production-db-backup.py',
);

function verify(contents, { compressed = true } = {}) {
  const directory = mkdtempSync(join(tmpdir(), 'nubarca-db-backup-'));
  const path = join(directory, 'backup.sql.gz');
  writeFileSync(path, compressed ? gzipSync(contents) : contents);
  return spawnSync('python3', [verifier, path], { encoding: 'utf8' });
}

test('accepts a complete gzip pg_dump carrying migration history', () => {
  const result = verify([
    'CREATE TABLE public."__EFMigrationsHistory" ();',
    '-- PostgreSQL database dump complete',
    '\\unrestrict token',
    '',
  ].join('\n'));
  assert.equal(result.status, 0, result.stderr);
});

test('rejects bytes that are not a gzip stream', () => {
  const result = verify('not gzip', { compressed: false });
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /cannot read complete gzip stream/);
});

test('rejects a dump without the EF migration history', () => {
  const result = verify('-- PostgreSQL database dump complete\n');
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /does not contain __EFMigrationsHistory/);
});

test('rejects a dump without pg_dump completion', () => {
  const result = verify('CREATE TABLE public."__EFMigrationsHistory" ();\n');
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /no clean completion marker/);
});
