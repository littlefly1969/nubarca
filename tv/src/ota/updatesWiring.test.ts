// Regression coverage for the in-app updater's WIRING: the Android manifest and
// native source the config plugin generates, the top-level flow transitions, and
// the mode selector.
//
// The manifest and Kotlin assertions run against what the plugin EMITS, not
// against its file text, so a comment mentioning a permission can never satisfy
// them — the failure mode a previous version of the back-navigation test
// actually had.

import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { tvFlowReducer, isPersonalState, type TvFlowState } from '../personal/flow.ts';

const require = createRequire(import.meta.url);
const plugin = require(new URL('../../plugins/withTvPlatformModule.js', import.meta.url).pathname);
const pluginSource = readFileSync(
  new URL('../../plugins/withTvPlatformModule.js', import.meta.url), 'utf8');
const app = readFileSync(new URL('../../App.tsx', import.meta.url), 'utf8');
const modeSelect = readFileSync(
  new URL('../screens/ModeSelectScreen.tsx', import.meta.url), 'utf8');
const updateScreen = readFileSync(new URL('../screens/UpdateScreen.tsx', import.meta.url), 'utf8');
const packageJson = JSON.parse(readFileSync(new URL('../../package.json', import.meta.url), 'utf8'));

const INSTALL_PERMISSION = 'android.permission.REQUEST_INSTALL_PACKAGES';

function code(source: string): string {
  return source
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .split('\n')
    .filter((line) => !line.trimStart().startsWith('//'))
    .join('\n');
}

/** Run the plugin's manifest mods over a minimal generated-manifest fixture. */
async function applyManifest(existingPermissions: string[] = []) {
  const manifest = {
    manifest: {
      'uses-permission': existingPermissions.map((name) => ({ $: { 'android:name': name } })),
      application: [{
        $: { 'android:name': '.MainApplication' },
        activity: [{
          $: { 'android:name': '.MainActivity' },
          'intent-filter': [{
            action: [{ $: { 'android:name': 'android.intent.action.MAIN' } }],
            category: [{ $: { 'android:name': 'android.intent.category.LAUNCHER' } }],
          }],
        }],
      }],
    },
  };
  const { mods } = plugin({});
  const applied = await mods.android.manifest({ modResults: manifest });
  return applied.modResults.manifest;
}

function permissionNames(manifest: {
  'uses-permission'?: { $: Record<string, string> }[];
}): string[] {
  return (manifest['uses-permission'] ?? []).map((entry) => entry.$['android:name']);
}

/** Capture the Kotlin the plugin writes, without a generated android/ project. */
async function generatedNativeSources(): Promise<Record<string, string>> {
  const written: Record<string, string> = {};
  const fs = require('node:fs');
  const originals = { mkdirSync: fs.mkdirSync, writeFileSync: fs.writeFileSync };
  fs.mkdirSync = () => undefined;
  fs.writeFileSync = (file: string, contents: string) => {
    written[String(file).split('/').pop() ?? ''] = contents;
  };
  try {
    const { mods } = plugin({});
    await mods.android.dangerous({
      modRequest: { platformProjectRoot: '/tmp/nubarca-tv-plugin-fixture' },
    });
  } finally {
    Object.assign(fs, originals);
  }
  return written;
}

// --- the Android manifest ----------------------------------------------------

test('the generated manifest declares the install permission exactly once', async () => {
  const names = permissionNames(await applyManifest());
  assert.deepEqual(names.filter((name) => name === INSTALL_PERMISSION), [INSTALL_PERMISSION]);
});

test('applying the plugin over a manifest that already has it does not duplicate it', async () => {
  const names = permissionNames(await applyManifest([INSTALL_PERMISSION]));
  assert.deepEqual(names.filter((name) => name === INSTALL_PERMISSION), [INSTALL_PERMISSION]);
});

test('the privileged install permissions are build failures, not omissions', async () => {
  // REQUEST_INSTALL_PACKAGES grants the right to ASK. These grant the right to
  // install without asking, and NubArca TV must never hold them — the user
  // confirming on a Fire OS screen is the design.
  for (const forbidden of [
    'android.permission.INSTALL_PACKAGES',
    'android.permission.UPDATE_PACKAGES_WITHOUT_USER_ACTION',
  ]) {
    await assert.rejects(() => applyManifest([forbidden]), /must never be declared/);
  }
  const names = permissionNames(await applyManifest());
  assert.equal(names.includes('android.permission.INSTALL_PACKAGES'), false);
  assert.equal(names.includes('android.permission.UPDATE_PACKAGES_WITHOUT_USER_ACTION'), false);
});

test('the launcher activity keeps its clean-relaunch behaviour', async () => {
  const manifest = await applyManifest();
  assert.equal(manifest.application[0].activity[0].$['android:clearTaskOnLaunch'], 'true');
});

// --- the generated native sources --------------------------------------------

test('the platform package is registered exactly once', async () => {
  const contents = `
        override fun getPackages(): List<ReactPackage> =
            PackageList(this).packages.apply {
              // add(MyReactNativePackage())
            }
  `;
  const { mods } = plugin({});
  const once = await mods.android.mainApplication({ modResults: { contents } });
  const twice = await mods.android.mainApplication({ modResults: { ...once.modResults } });
  const occurrences = twice.modResults.contents.match(/NubArcaTvPlatformPackage\(\)/g) ?? [];
  assert.deepEqual(occurrences, ['NubArcaTvPlatformPackage()']);
});

test('the generated Kotlin drives PackageInstaller and never installs silently', async () => {
  const sources = await generatedNativeSources();
  const installer = code(sources['NubArcaTvInstaller.kt'] ?? '');
  assert.match(installer, /PackageInstaller\.SessionParams\(\s*PackageInstaller\.SessionParams\.MODE_FULL_INSTALL/);
  assert.match(installer, /installer\.createSession\(params\)/);
  assert.match(installer, /session\.openWrite\(/);
  assert.match(installer, /session\.commit\(pending\.intentSender\)/);
  assert.match(installer, /STATUS_PENDING_USER_ACTION/);
  assert.match(installer, /setRequireUserAction\(PackageInstaller\.SessionParams\.USER_ACTION_REQUIRED\)/);
  assert.doesNotMatch(installer, /USER_ACTION_NOT_REQUIRED/);
  assert.doesNotMatch(installer, /\bsu\b|Runtime\.getRuntime\(\)\.exec|DevicePolicyManager/);
});

test('the generated Kotlin refuses an APK before an install session exists', async () => {
  const installer = code((await generatedNativeSources())['NubArcaTvInstaller.kt'] ?? '');
  // A matching hash proves the bytes are the described bytes and nothing more,
  // so package, versionCode and SIGNER are read out of the archive itself and
  // compared against the RUNNING install.
  for (const gate of [
    /MessageDigest\.getInstance\("SHA-256"\)/,
    /getPackageArchiveInfo\(/,
    /candidate\.packageName != context\.packageName/,
    /candidateCode <= versionCodeOf\(installed\)/,
    /installedSigners != candidateSigners/,
    /GET_SIGNING_CERTIFICATES/,
    /apkContentsSigners/,
    /canonicalFile/,
  ]) {
    assert.match(installer, gate, `missing install gate ${gate}`);
  }
  // Validation runs before startInstall, never after.
  assert.ok(installer.indexOf('validate(context, staged') < installer.indexOf('startInstall(context, staged'));
  // Failures cross to JavaScript as sanitized codes only.
  for (const codeName of [
    'permission-required', 'invalid-file', 'hash-mismatch', 'wrong-package',
    'not-newer', 'signer-mismatch', 'installer-rejected', 'installer-unavailable',
  ]) {
    assert.match(installer, new RegExp(`"${codeName}"`), `missing sanitized code ${codeName}`);
  }
});

test('the permission request targets this package and nothing more', async () => {
  const installer = code((await generatedNativeSources())['NubArcaTvInstaller.kt'] ?? '');
  assert.match(installer, /Settings\.ACTION_MANAGE_UNKNOWN_APP_SOURCES/);
  assert.match(installer, /"package:" \+ context\.packageName/);
  assert.match(installer, /packageManager\.canRequestPackageInstalls\(\)/);
});

test('the existing finishAndRemoveTask behaviour is untouched', async () => {
  const module = code((await generatedNativeSources())['NubArcaTvPlatformModule.kt'] ?? '');
  assert.match(module, /activity\.finishAndRemoveTask\(\)/);
  assert.doesNotMatch(module, /System\.exit|killProcess|Runtime\.getRuntime\(\)\.exit/);
});

test('the updater introduced no new native or JavaScript dependency', () => {
  // The bridge is the one already in the app, and the download reuses the
  // expo-file-system dependency the media cache already requires.
  assert.equal(Object.keys(packageJson.dependencies).length, 11);
  assert.ok(packageJson.dependencies['expo-file-system']);
  assert.equal(packageJson.dependencies['expo-updates'] !== undefined, true);
  assert.doesNotMatch(pluginSource, /require\('(?!node:|expo\/config-plugins)/);
});

// --- the top-level flow ------------------------------------------------------

const mode: TvFlowState = { name: 'mode', notice: null };
const updates: TvFlowState = { name: 'updates' };

test('mode selection opens the update surface and BACK returns to it', () => {
  assert.deepEqual(tvFlowReducer(mode, { type: 'CHOOSE_UPDATES' }), updates);
  assert.deepEqual(tvFlowReducer(updates, { type: 'UPDATES_BACK' }), mode);
});

test('a revoked session tears the update surface down like any other state', () => {
  assert.deepEqual(tvFlowReducer(updates, { type: 'SESSION_INVALID' }),
    { name: 'pairing', incomplete: false });
  assert.deepEqual(tvFlowReducer(updates, { type: 'ASSOCIATION_INCOMPLETE' }),
    { name: 'pairing', incomplete: true });
});

test('the update surface is not a personal state', () => {
  // No PIN, no grant, no owner-private API. Treating it as personal would make
  // BACK revoke a grant it never held and re-validate a grant that is absent.
  assert.equal(isPersonalState(updates), false);
  assert.equal(isPersonalState({ name: 'beautyLab', home: { displayName: 'x', galleryAvailable: true } }), true);
});

test('the update surface cannot be entered from anywhere but mode selection', () => {
  for (const from of [
    { name: 'loading' } as TvFlowState,
    { name: 'pairing', incomplete: false } as TvFlowState,
    { name: 'party' } as TvFlowState,
    { name: 'pin', target: 'personal' } as TvFlowState,
  ]) {
    assert.deepEqual(tvFlowReducer(from, { type: 'CHOOSE_UPDATES' }), from);
  }
});

test('the existing mode transitions are unchanged', () => {
  assert.deepEqual(tvFlowReducer(mode, { type: 'CHOOSE_PARTY' }), { name: 'party' });
  assert.deepEqual(tvFlowReducer(mode, { type: 'CHOOSE_PERSONAL' }), { name: 'pin', target: 'personal' });
  assert.deepEqual(tvFlowReducer(mode, { type: 'CHOOSE_BEAUTY_LAB' }), { name: 'pin', target: 'beautyLab' });
  assert.deepEqual(tvFlowReducer({ name: 'party' }, { type: 'PARTY_EXIT' }), mode);
});

// --- the mode selector and the screen ----------------------------------------

test('the mode selector has a fourth entry that does not steal the initial focus', () => {
  const source = code(modeSelect);
  const buttons = [...source.matchAll(/<FocusableButton\s+label=\{t\('([^']+)'\)\}/g)]
    .map((match) => match[1]);
  assert.deepEqual(buttons, ['mode.party', 'mode.personal', 'mode.beautyLab', 'mode.updates']);
  // Party is what this TV is normally opened for; Updates must never take the
  // remote's first press.
  assert.match(source, /label=\{t\('mode\.party'\)\}[^/]*hasTVPreferredFocus/);
  assert.doesNotMatch(source, /label=\{t\('mode\.updates'\)\}[^/]*hasTVPreferredFocus/);
});

test('App wires the update surface into the one flow reducer', () => {
  const source = code(app);
  assert.match(source, /rawDispatch\(\{ type: 'CHOOSE_UPDATES' \}\)/);
  assert.match(source, /rawDispatch\(\{ type: 'UPDATES_BACK' \}\)/);
  assert.match(source, /flow\.name === 'updates' &&/);
  // No second navigation state beside the reducer.
  assert.doesNotMatch(source, /useState<[^>]*[Uu]pdate[^>]*>/);
});

test('the update screen evaluates the native release before offering an OTA', () => {
  // Sliced past the imports: the order that matters is the order of the CALLS,
  // and an import block would otherwise decide this assertion.
  const source = code(updateScreen).slice(code(updateScreen).indexOf('export function UpdateScreen'));
  const descriptor = source.indexOf('fetchNativeRelease');
  const decision = source.indexOf('decideUpdatePath');
  const ota = source.indexOf('checkForOtaUpdateNow');
  assert.ok(descriptor > 0 && decision > descriptor && ota > decision,
    'the native descriptor and its decision must both precede the OTA check');
  // And the native branch returns rather than falling through to the OTA check.
  assert.match(source, /setState\(\{ name: 'native-available', release: published \}\);\s*\n\s*return;/);
});

test('the update screen never shows a path, a hash or a native message', () => {
  const source = code(updateScreen);
  assert.doesNotMatch(source, /apkSha256\}|apkFile\}|\.uri\}/);
  assert.doesNotMatch(source, /error\.message|String\(error\)/);
  // Every failure the screen can render comes from the sanitized code map.
  assert.match(source, /function errorMessage\(code: NativeUpdateFailure\)/);
});
