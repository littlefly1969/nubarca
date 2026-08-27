import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { resolve } from 'node:path';
import test from 'node:test';

const require = createRequire(import.meta.url);
const mobileRoot = resolve(import.meta.dirname, '..');
const packageJson = require(resolve(mobileRoot, 'package.json'));
const bundled = require(resolve(mobileRoot, 'node_modules/expo/bundledNativeModules.json'));
const semver = require(resolve(mobileRoot, 'node_modules/semver'));

function resolvedAndroidModules() {
  const cli = resolve(
    mobileRoot,
    'node_modules/expo-modules-autolinking/bin/expo-modules-autolinking.js',
  );
  return JSON.parse(
    execFileSync(process.execPath, [cli, 'resolve', '--platform', 'android', '--json'], {
      cwd: mobileRoot,
      encoding: 'utf8',
    }),
  ).modules;
}

test('Expo font is pinned as an SDK-owned native dependency', () => {
  assert.equal(packageJson.dependencies['expo-font'], bundled['expo-font']);
});

test('every autolinked Expo Android module matches the SDK 54 ABI range', () => {
  const mismatches = resolvedAndroidModules()
    .filter(({ packageName }) => bundled[packageName] !== undefined)
    .filter(({ packageName, packageVersion }) =>
      !semver.satisfies(packageVersion, bundled[packageName]),
    )
    .map(({ packageName, packageVersion }) =>
      `${packageName}@${packageVersion} (SDK expects ${bundled[packageName]})`,
    );

  assert.deepEqual(mismatches, []);
});
