import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import test from 'node:test';

const mobileRoot = resolve(import.meta.dirname, '..');
const read = (relativePath) => readFileSync(resolve(mobileRoot, relativePath), 'utf8');

test('a signed-out cold start does not construct authenticated sync storage', () => {
  const layout = read('app/_layout.tsx');
  const signedOutBranch = layout.slice(
    layout.indexOf("if (session.status === 'unauthed')"),
    layout.indexOf('const userId = session.user.id'),
  );

  assert.ok(signedOutBranch.includes('<Redirect href="/login" />'));
  assert.doesNotMatch(signedOutBranch, /SyncProvider|accountId/);
  assert.doesNotMatch(layout, /accountId=['"]anon['"]|\?\? ['"]anon['"]/);
});

test('the native ledger uses an Expo database filename, not a file URI', () => {
  const storage = read('src/sync/ledgerStorage.ts');

  assert.match(storage, /const databaseName = `sync-ledger-\$\{namespace\}\.db`/);
  assert.match(storage, /SQLite\.openDatabaseSync\(databaseName\)/);
  assert.doesNotMatch(storage, /FileSystem\.|makeDirectoryAsync\(/);
});

test('optional sync initialization cannot take down the gallery', () => {
  const provider = read('src/sync/SyncProvider.tsx');

  assert.match(provider, /try \{[\s\S]*openAccountLedgerConnection\(accountId\)/);
  assert.match(provider, /catch \(error\) \{[\s\S]*Mobile sync initialization failed/);
});
