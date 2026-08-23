import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import test from 'node:test';

const repoRoot = resolve(import.meta.dirname, '../..');
const validator = resolve(repoRoot, 'deploy/validate-tv-apk-bundle.py');
const contractPath = resolve(repoRoot, 'tv/release-contract.json');
const contract = JSON.parse(readFileSync(contractPath, 'utf8'));

function makeBundle() {
  const directory = mkdtempSync(join(tmpdir(), 'nubarca-tv-bundle-test-'));
  const apkName = `nubarca-tv-v${contract.versionCode}.apk`;
  const apk = Buffer.from('synthetic APK bytes for bundle validation');
  const hash = createHash('sha256').update(apk).digest('hex');
  writeFileSync(join(directory, apkName), apk);
  writeFileSync(join(directory, `${apkName}.sha256`), `${hash}  ${apkName}\n`);
  writeFileSync(
    join(directory, 'nubarca-tv.release.json'),
    `${JSON.stringify({
      schemaVersion: 1,
      package: contract.package,
      version: contract.version,
      versionCode: contract.versionCode,
      runtimeVersion: contract.runtimeVersion,
      channel: contract.channel,
      apkFile: apkName,
      apkSha256: hash,
      apkBytes: apk.length,
    }, null, 2)}\n`,
  );
  return { directory, apkName };
}

function validate(directory, ...extraArgs) {
  return execFileSync('python3', [validator, directory, contractPath, ...extraArgs], {
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
  });
}

test('accepts an exact bundle and emits publication values', () => {
  const { directory, apkName } = makeBundle();
  try {
    assert.match(validate(directory), /TV APK BUNDLE VALID/);
    const values = validate(directory, '--values').trim().split('\t');
    assert.equal(values[0], apkName);
    assert.match(values[1], /^[0-9a-f]{64}$/);
    assert.equal(values[3], String(contract.versionCode));
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('rejects changed APK bytes and unexpected files', () => {
  const first = makeBundle();
  try {
    writeFileSync(join(first.directory, first.apkName), 'changed');
    assert.throws(() => validate(first.directory));
  } finally {
    rmSync(first.directory, { recursive: true, force: true });
  }

  const second = makeBundle();
  try {
    writeFileSync(join(second.directory, 'unexpected.txt'), 'no');
    assert.throws(() => validate(second.directory));
  } finally {
    rmSync(second.directory, { recursive: true, force: true });
  }
});
