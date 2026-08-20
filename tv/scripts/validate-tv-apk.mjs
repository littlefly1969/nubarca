#!/usr/bin/env node
// BINARY + TV-PLATFORM validation of a NubArca TV release APK.
//
// This complements deploy/validate-tv-apk.sh rather than replacing it. That
// script owns IDENTITY and TRUST — signer fingerprint, embedded origin, OTA
// certificate. This one owns what the bytes have to be to run correctly on an
// Android TV / Fire TV device:
//
//   * 16 KB page-size compatibility of every packaged native library. Android
//     is moving to 16 KB pages; a .so whose LOAD segments are aligned for 4 KB
//     will not load. This is a RELEASE GATE, and it fails on the exact
//     offending library and ABI rather than reporting a summary.
//   * the ABI set actually shipped, so a release cannot silently go out with
//     only an emulator architecture.
//   * the TV manifest contract: leanback declared, touchscreen NOT required,
//     both launcher categories, both banners, landscape, and no accidental
//     hardware requirement for something NubArca does not use.
//   * minSdk / targetSdk, reported rather than assumed — those two numbers ARE
//     the device support floor, and guessing them is how a store listing comes
//     to claim hardware the app cannot run on.
//
// It is deliberately fail-closed and deliberately silent about how to "fix"
// a 16 KB failure by hiding it: `pageSizeCompat` is a compatibility mode, not
// compliance, and this gate must not be satisfiable by switching it on.
//
//   node scripts/validate-tv-apk.mjs <apk>
//   node scripts/validate-tv-apk.mjs <apk> --json

import { createRequire } from 'node:module';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, rmSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const tvRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const { readReleaseContract } = require(resolve(tvRoot, 'scripts/release-contract.cjs'));

// Android TV/Fire TV ship 32- and 64-bit ARM. x86 families are emulator-only
// for this product and are not required, but shipping ONLY them would mean the
// release runs on no real device — which is what REQUIRED_ABIS catches.
export const REQUIRED_ABIS = ['armeabi-v7a', 'arm64-v8a'];
export const PAGE_SIZE_16K = 16384;

// 16 KB pages are a SIXTY-FOUR-BIT platform change. A 32-bit ABI runs on 4 KB
// pages and is not subject to it, so gating armeabi-v7a/x86 on 16 KB alignment
// reports a compliant build as broken — which is exactly what the first version
// of this script did, with eighteen false failures. The alignment of 32-bit
// libraries is still REPORTED, because it is useful, but it is not a gate.
export const SIXTY_FOUR_BIT_ABIS = ['arm64-v8a', 'x86_64'];

// Hardware NubArca genuinely does not use. Any of these declared as REQUIRED
// would exclude perfectly good televisions from the store listing.
const FORBIDDEN_REQUIRED_FEATURES = [
  'android.hardware.touchscreen',
  'android.hardware.camera',
  'android.hardware.telephony',
  'android.hardware.location',
  'android.hardware.location.gps',
  'android.hardware.microphone',
  'android.hardware.sensor.accelerometer',
  'android.hardware.bluetooth',
];

const FORBIDDEN_PERMISSIONS = [
  'android.permission.INSTALL_PACKAGES',
  'android.permission.UPDATE_PACKAGES_WITHOUT_USER_ACTION',
];

function sdkTool(...candidates) {
  const home = process.env.ANDROID_HOME ?? process.env.ANDROID_SDK_ROOT
    ?? join(process.env.HOME ?? '', 'Android/Sdk');
  for (const relative of candidates) {
    try {
      const found = execFileSync('bash', ['-lc',
        `ls -d ${home}/${relative} 2>/dev/null | sort -V | tail -1`], { encoding: 'utf8' }).trim();
      if (found && existsSync(found)) return found;
    } catch { /* try the next candidate */ }
  }
  return null;
}

function run(binary, args) {
  return execFileSync(binary, args, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
}

/** Everything aapt2 badging knows, parsed once. */
function readBadging(aapt2, apk) {
  const badging = run(aapt2, ['dump', 'badging', apk]);
  const pkg = /package: name='([^']+)' versionCode='(\d+)' versionName='([^']+)'/.exec(badging);
  const sdk = /minSdkVersion:'(\d+)'/.exec(badging);
  const target = /targetSdkVersion:'(\d+)'/.exec(badging);
  return {
    raw: badging,
    package: pkg?.[1] ?? null,
    versionCode: pkg ? Number(pkg[2]) : null,
    versionName: pkg?.[3] ?? null,
    minSdk: sdk ? Number(sdk[1]) : null,
    targetSdk: target ? Number(target[1]) : null,
    abis: [...badging.matchAll(/native-code: (.+)/g)]
      .flatMap((m) => [...m[1].matchAll(/'([^']+)'/g)].map((a) => a[1])),
    launchable: /^launchable-activity:/m.test(badging),
    leanbackLaunchable: /^leanback-launchable-activity:/m.test(badging),
    permissions: [...badging.matchAll(/uses-permission: name='([^']+)'/g)].map((m) => m[1]),
    // "required" features. aapt2 prints uses-feature for required ones and
    // uses-feature-not-required for the others, which is exactly the
    // distinction that matters for store device filtering.
    // aapt2 INDENTS these lines; anchoring to column 0 silently matched
    // nothing and reported a perfectly correct manifest as broken.
    requiredFeatures: [...badging.matchAll(/^\s*uses-feature: name='([^']+)'/gm)].map((m) => m[1]),
    notRequiredFeatures: [...badging.matchAll(/^\s*uses-feature-not-required: name='([^']+)'/gm)].map((m) => m[1]),
  };
}

/**
 * ELF LOAD-segment alignment for one library.
 *
 * A 16 KB-compatible library has every PT_LOAD segment aligned to at least
 * 16384. readelf reports the alignment as hex (0x4000 = 16384).
 */
export function maxLoadAlignment(readelfOutput) {
  const alignments = [...readelfOutput.matchAll(/LOAD\s+.*?(0x[0-9a-f]+)\s*$/gm)]
    .map((m) => Number.parseInt(m[1], 16))
    .filter((value) => Number.isFinite(value) && value > 0);
  return alignments.length === 0 ? 0 : Math.min(...alignments);
}

function checkNativeLibraries(apk, problems, report) {
  const readelf = sdkTool('ndk/*/toolchains/llvm/prebuilt/*/bin/llvm-readelf')
    ?? (() => { try { return run('bash', ['-lc', 'command -v llvm-readelf || command -v readelf']).trim(); } catch { return null; } })();
  if (!readelf) {
    problems.push('no readelf available — 16 KB compliance CANNOT be proven, so this gate fails closed');
    return;
  }
  const listing = run('unzip', ['-Z1', apk]).split('\n').filter((n) => n.endsWith('.so'));
  const workdir = mkdtempSync(join(tmpdir(), 'nubarca-tv-so-'));
  try {
    for (const entry of listing) {
      run('unzip', ['-o', '-q', apk, entry, '-d', workdir]);
      const file = join(workdir, entry);
      const alignment = maxLoadAlignment(run(readelf, ['-lW', file]));
      const abi = entry.split('/')[1] ?? 'unknown';
      const gated = SIXTY_FOUR_BIT_ABIS.includes(abi);
      report.nativeLibraries.push({ entry, abi, alignment, gated });
      if (gated && alignment < PAGE_SIZE_16K) {
        problems.push(
          `16 KB INCOMPATIBLE: ${entry} (${abi}) has LOAD alignment ${alignment}, needs >= ${PAGE_SIZE_16K}`);
      }
    }
  } finally {
    rmSync(workdir, { recursive: true, force: true });
  }
}

export function validateTvApk(apkPath) {
  const apk = resolve(apkPath);
  const contract = readReleaseContract();
  const problems = [];
  const report = {
    apk, contract: { ...contract }, nativeLibraries: [],
    sizeBytes: statSync(apk).size,
  };

  const aapt2 = sdkTool('build-tools/*/aapt2');
  const apksigner = sdkTool('build-tools/*/apksigner');
  const zipalign = sdkTool('build-tools/*/zipalign');
  if (!aapt2) throw new Error('aapt2 not found; cannot inspect the APK.');

  const badging = readBadging(aapt2, apk);
  Object.assign(report, {
    package: badging.package,
    versionName: badging.versionName,
    versionCode: badging.versionCode,
    minSdk: badging.minSdk,
    targetSdk: badging.targetSdk,
    abis: badging.abis,
    permissions: badging.permissions,
    requiredFeatures: badging.requiredFeatures,
    notRequiredFeatures: badging.notRequiredFeatures,
  });

  // --- identity, against the tracked contract ------------------------------
  if (badging.package !== contract.package) {
    problems.push(`package is ${badging.package}, contract says ${contract.package}`);
  }
  if (badging.versionName !== contract.version) {
    problems.push(`versionName is ${badging.versionName}, contract says ${contract.version}`);
  }
  if (badging.versionCode !== contract.versionCode) {
    problems.push(`versionCode is ${badging.versionCode}, contract says ${contract.versionCode}`);
  }

  // --- signer ---------------------------------------------------------------
  if (apksigner) {
    const certs = run(apksigner, ['verify', '--print-certs', apk]);
    const signer = /Signer #1 certificate SHA-256 digest: ([0-9a-f]+)/.exec(certs)?.[1] ?? null;
    report.signerSha256 = signer;
    if (signer !== contract.apkSignerSha256) {
      problems.push(`signer is ${signer}, contract says ${contract.apkSignerSha256}`);
    }
  } else {
    problems.push('apksigner not found — the signer CANNOT be proven');
  }

  // --- TV manifest contract, from the PACKAGED manifest ---------------------
  // aapt2 badging summarises; the launcher/banner/orientation contract needs
  // the decoded manifest itself. Fail closed if it cannot be read: an
  // uninspectable manifest is not a passing one.
  const apkanalyzer = sdkTool('cmdline-tools/*/bin/apkanalyzer');
  if (apkanalyzer) {
    let decoded;
    try {
      decoded = run(apkanalyzer, ['manifest', 'print', apk]);
    } catch {
      decoded = null;
    }
    if (decoded === null) {
      problems.push('the packaged manifest could not be decoded — the TV contract is unproven');
    } else {
      const activity = /<activity[^>]*MainActivity[\s\S]*?<\/activity>/.exec(decoded)?.[0] ?? '';
      const mainFilters = [...activity.matchAll(/<intent-filter>[\s\S]*?<\/intent-filter>/g)]
        .map((m) => m[0]).filter((f) => f.includes('android.intent.action.MAIN'));
      const count = (haystack, name) => haystack.split(`"${name}"`).length - 1;
      report.launcher = {
        mainFilters: mainFilters.length,
        main: mainFilters[0] ? count(mainFilters[0], 'android.intent.action.MAIN') : 0,
        launcher: mainFilters[0] ? count(mainFilters[0], 'android.intent.category.LAUNCHER') : 0,
        leanback: mainFilters[0]
          ? count(mainFilters[0], 'android.intent.category.LEANBACK_LAUNCHER') : 0,
        exported: /android:exported="true"/.test(activity),
        activityBanner: /android:banner=/.test(activity),
        applicationBanner: (decoded.match(/android:banner=/g) ?? []).length >= 2,
        // Deliberately absent for a leanback build — the TV toolchain removes
        // it on purpose, so its PRESENCE would be the surprise.
        pinnedOrientation: /android:screenOrientation/.test(activity),
      };
      const l = report.launcher;
      if (l.mainFilters !== 1) problems.push(`expected exactly one MAIN intent-filter, found ${l.mainFilters}`);
      if (l.main !== 1) problems.push(`ACTION_MAIN appears ${l.main} times`);
      if (l.launcher !== 1) problems.push(`CATEGORY_LAUNCHER appears ${l.launcher} times`);
      if (l.leanback !== 1) problems.push(`CATEGORY_LEANBACK_LAUNCHER appears ${l.leanback} times`);
      if (!l.exported) problems.push('the launcher activity is not exported');
      if (!l.activityBanner) problems.push('the launcher activity declares no banner');
      if (!l.applicationBanner) problems.push('the application declares no banner');
    }
  } else {
    problems.push('apkanalyzer not found — the packaged TV manifest CANNOT be inspected');
  }

  // --- TV feature contract (badging) ----------------------------------------
  if (!badging.leanbackLaunchable) problems.push('no LEANBACK_LAUNCHER activity');
  if (!badging.launchable) problems.push('no ordinary LAUNCHER activity (accepted 1.0.7 registration)');
  if (!badging.notRequiredFeatures.includes('android.hardware.touchscreen')) {
    problems.push('android.hardware.touchscreen is not declared required=false');
  }
  if (!/uses-feature.*android\.software\.leanback/.test(badging.raw)) {
    problems.push('android.software.leanback is not declared');
  }
  for (const feature of FORBIDDEN_REQUIRED_FEATURES) {
    if (badging.requiredFeatures.includes(feature)) {
      problems.push(`hardware requirement NubArca does not need: ${feature}`);
    }
  }
  for (const permission of FORBIDDEN_PERMISSIONS) {
    if (badging.permissions.includes(permission)) {
      problems.push(`privileged install permission present: ${permission}`);
    }
  }
  if (!/REQUEST_INSTALL_PACKAGES/.test(badging.raw)) {
    problems.push('REQUEST_INSTALL_PACKAGES missing — the in-app updater cannot work');
  }

  // --- ABIs -----------------------------------------------------------------
  for (const abi of REQUIRED_ABIS) {
    if (!badging.abis.includes(abi)) problems.push(`missing required ABI: ${abi}`);
  }
  if (badging.abis.length === 0) problems.push('the APK packages no native code at all');

  // --- 16 KB page size ------------------------------------------------------
  if (zipalign) {
    try {
      run(zipalign, ['-v', '-c', '-P', '16', '4', apk]);
      report.zipalign16k = 'ok';
    } catch {
      report.zipalign16k = 'FAILED';
      problems.push('zipalign -P 16 verification failed: the APK is not 16 KB aligned');
    }
  } else {
    problems.push('zipalign not found — alignment CANNOT be proven');
  }
  checkNativeLibraries(apk, problems, report);

  report.problems = problems;
  report.ok = problems.length === 0;
  return report;
}

if (import.meta.url === `file://${process.argv[1]}`) {
  const [apkPath, ...flags] = process.argv.slice(2);
  if (!apkPath) {
    console.error('usage: validate-tv-apk.mjs <apk> [--json]');
    process.exit(2);
  }
  let report;
  try {
    report = validateTvApk(apkPath);
  } catch (error) {
    console.error(error.message);
    process.exit(1);
  }
  if (flags.includes('--json')) {
    console.log(JSON.stringify(report, null, 2));
  } else {
    const line = (k, v) => console.log(`  ${k.padEnd(22)} ${v}`);
    console.log('NubArca TV release-candidate validation');
    line('package', report.package);
    line('versionName', report.versionName);
    line('versionCode', report.versionCode);
    line('signer SHA-256', report.signerSha256 ?? '(unproven)');
    line('minSdk', report.minSdk);
    line('targetSdk', report.targetSdk);
    line('ABIs', report.abis.join(', ') || '(none)');
    line('native libraries', report.nativeLibraries.length);
    line('zipalign -P 16', report.zipalign16k ?? '(unproven)');
    line('touchscreen', report.notRequiredFeatures.includes('android.hardware.touchscreen')
      ? 'not required (correct)' : 'REQUIRED');
    line('APK bytes', report.sizeBytes);
    if (report.launcher) {
      const l = report.launcher;
      line('MAIN / LAUNCHER / LEANBACK', `${l.main} / ${l.launcher} / ${l.leanback}`);
      line('activity exported', l.exported);
      line('banners', l.applicationBanner && l.activityBanner ? 'application + activity' : 'INCOMPLETE');
      line('pinned orientation', l.pinnedOrientation
        ? 'declared' : 'absent (correct for leanback)');
    }
    const gatedLibs = report.nativeLibraries.filter((l) => l.gated);
    const worst = gatedLibs.reduce(
      (low, l) => (low === null || l.alignment < low.alignment ? l : low), null);
    if (worst) {
      line('64-bit min alignment', `${worst.alignment} (${worst.entry})`);
      line('16 KB gate', worst.alignment >= PAGE_SIZE_16K ? 'PASS' : 'FAIL');
    }
    const thirtyTwo = report.nativeLibraries.filter((l) => !l.gated);
    if (thirtyTwo.length > 0) {
      const min = Math.min(...thirtyTwo.map((l) => l.alignment));
      line('32-bit alignment', `${min} (informational — 4 KB pages, not gated)`);
    }
  }
  if (!report.ok) {
    console.error('\nFAILED:');
    for (const problem of report.problems) console.error(`  - ${problem}`);
    process.exit(1);
  }
  console.log('\nCANDIDATE VALID');
}
