#!/usr/bin/env node

import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, join, resolve } from 'node:path';

const require = createRequire(import.meta.url);
const mobileRoot = resolve(import.meta.dirname, '..');
const { normalizePublicOrigin, readReleaseContract } = require('./release-contract.cjs');
const contract = readReleaseContract();

function run(binary, args, options = {}) {
  return execFileSync(binary, args, {
    encoding: options.encoding ?? 'utf8',
    maxBuffer: 128 * 1024 * 1024,
    ...options,
  });
}

function requiredTool(name) {
  try {
    return run('which', [name]).trim();
  } catch {
    throw new Error(`${name} is required to validate the Android release`);
  }
}

function shaFromCertificateReport(report) {
  const colonForm = /^\s*SHA256:\s*([0-9A-F:]+)\s*$/im.exec(report)?.[1];
  const digestForm = /certificate SHA-256 digest:\s*([0-9a-f]+)/i.exec(report)?.[1];
  return (colonForm ?? digestForm ?? '').replaceAll(':', '').toLowerCase();
}

function xmlAttribute(element, name) {
  return new RegExp(`(?:android:)?${name}="([^"]+)"`).exec(element)?.[1] ?? null;
}

function verifyNativeLibraries(apk, llvmReadelf) {
  const entries = run('unzip', ['-Z1', apk]).split('\n').filter((entry) => /^lib\/[^/]+\/[^/]+\.so$/.test(entry));
  const abis = [...new Set(entries.map((entry) => entry.split('/')[1]))].sort();
  for (const required of ['armeabi-v7a', 'arm64-v8a']) {
    assert.ok(abis.includes(required), `${basename(apk)} must contain ${required}`);
  }

  const work = mkdtempSync(join(tmpdir(), 'nubarca-mobile-so-'));
  try {
    for (const entry of entries) {
      const abi = entry.split('/')[1];
      if (!['arm64-v8a', 'x86_64'].includes(abi)) continue;
      const output = join(work, basename(entry));
      writeFileSync(output, run('unzip', ['-p', apk, entry], { encoding: 'buffer' }));
      const programHeaders = run(llvmReadelf, ['-lW', output]);
      const alignments = [...programHeaders.matchAll(/LOAD\s+.*?(0x[0-9a-f]+)\s*$/gmi)]
        .map((match) => Number.parseInt(match[1], 16));
      assert.ok(alignments.length > 0, `${entry} has no readable LOAD segment`);
      assert.ok(
        alignments.every((alignment) => alignment >= 16384),
        `${entry} is not compatible with 16 KB memory pages`,
      );
    }
  } finally {
    rmSync(work, { recursive: true, force: true });
  }
  return abis;
}

function validateApk(apkPath, tools, expectedOrigin) {
  const apk = resolve(apkPath);
  assert.ok(statSync(apk).size > 0, `${apk} is empty`);

  const signerReport = run(tools.apksigner, ['verify', '--verbose', '--print-certs', apk]);
  assert.match(signerReport, /^Verified using v2 scheme .*: true$/m);
  assert.match(signerReport, /^Verified using v3 scheme .*: true$/m);
  assert.equal(shaFromCertificateReport(signerReport), contract.uploadSignerSha256);
  assert.doesNotMatch(signerReport, /Android Debug/i);

  run(tools.zipalign, ['-c', '-P', '16', '4', apk]);
  const badging = run(tools.aapt2, ['dump', 'badging', apk]);
  const packageLine = /^package: name='([^']+)' versionCode='(\d+)' versionName='([^']+)'/m.exec(badging);
  assert.ok(packageLine, 'aapt2 did not report package identity');
  assert.equal(packageLine[1], contract.package);
  assert.equal(Number(packageLine[2]), contract.versionCode);
  assert.equal(packageLine[3], contract.version);
  assert.equal(Number(/minSdkVersion:'(\d+)'/.exec(badging)?.[1]), contract.minSdk);
  assert.equal(Number(/targetSdkVersion:'(\d+)'/.exec(badging)?.[1]), contract.targetSdk);
  assert.match(badging, new RegExp(`application-label:'${contract.applicationName}'`));
  assert.match(badging, /^launchable-activity:/m);

  const manifest = run(tools.apkanalyzer, ['manifest', 'print', apk]);
  const application = /<application\b[^>]*>/s.exec(manifest)?.[0] ?? '';
  assert.equal(xmlAttribute(application, 'allowBackup'), 'false');
  // For targetSdk >= 28 Android's secure default is false. Expo omits the
  // attribute when false, so only an explicit true is a release failure.
  assert.notEqual(xmlAttribute(application, 'usesCleartextTraffic'), 'true');
  assert.notEqual(xmlAttribute(application, 'debuggable'), 'true');

  const embedded = run('unzip', ['-p', apk, 'assets/app.config']);
  const config = JSON.parse(embedded);
  assert.equal(config.extra?.apiBaseUrl, expectedOrigin);
  assert.equal(config.extra?.releaseVersion, contract.version);
  assert.equal(config.extra?.releaseVersionCode, contract.versionCode);
  assert.equal(config.android?.package, contract.package);
  assert.equal(config.android?.versionCode, contract.versionCode);

  return { apk, bytes: statSync(apk).size, abis: verifyNativeLibraries(apk, tools.llvmReadelf) };
}

function validateBundle(aabPath, bundletool, expectedOrigin, tools) {
  const aab = resolve(aabPath);
  assert.ok(statSync(aab).size > 0, `${aab} is empty`);
  // Upload keys are intentionally self-signed. `-strict` therefore returns 4
  // even for a correctly signed Android App Bundle because the certificate is
  // not rooted in the JVM trust store. Verify the JAR signature itself here;
  // the exact trusted upload-key fingerprint is enforced immediately below.
  const jarSignature = run('jarsigner', ['-verify', '-certs', aab]);
  assert.match(jarSignature, /jar verified\./i);
  assert.doesNotMatch(jarSignature, /jar is unsigned/i);
  const certificate = run('keytool', [
    '-J-Duser.language=en', '-J-Duser.country=US', '-printcert', '-jarfile', aab,
  ]);
  assert.equal(shaFromCertificateReport(certificate), contract.uploadSignerSha256);
  assert.doesNotMatch(certificate, /Android Debug/i);

  run('java', ['-jar', bundletool, 'validate', `--bundle=${aab}`]);
  const manifest = run('java', [
    '-jar', bundletool, 'dump', 'manifest', `--bundle=${aab}`, '--module=base',
  ]);
  const manifestTag = /<manifest\b[^>]*>/s.exec(manifest)?.[0] ?? '';
  const usesSdk = /<uses-sdk\b[^>]*>/s.exec(manifest)?.[0] ?? '';
  const application = /<application\b[^>]*>/s.exec(manifest)?.[0] ?? '';
  assert.equal(xmlAttribute(manifestTag, 'package'), contract.package);
  assert.equal(Number(xmlAttribute(manifestTag, 'versionCode')), contract.versionCode);
  assert.equal(xmlAttribute(manifestTag, 'versionName'), contract.version);
  assert.equal(Number(xmlAttribute(usesSdk, 'minSdkVersion')), contract.minSdk);
  assert.equal(Number(xmlAttribute(usesSdk, 'targetSdkVersion')), contract.targetSdk);
  assert.equal(xmlAttribute(application, 'allowBackup'), 'false');
  assert.notEqual(xmlAttribute(application, 'usesCleartextTraffic'), 'true');

  // The AAB's embedded Expo config is checked indirectly through the universal
  // APK generated from this exact bundle below; it must match the direct APK.
  return { aab, bytes: statSync(aab).size, expectedOrigin, tools };
}

const [apkArg, aabArg, universalArg, bundletoolArg] = process.argv.slice(2);
if (!apkArg || !aabArg || !universalArg || !bundletoolArg) {
  console.error('usage: validate-android-artifacts.mjs <apk> <aab> <universal-from-aab.apk> <bundletool.jar>');
  process.exit(2);
}

const expectedOrigin = normalizePublicOrigin(process.env.NUBARCA_PUBLIC_ORIGIN);
const tools = {
  aapt2: requiredTool('aapt2'),
  apkanalyzer: requiredTool('apkanalyzer'),
  apksigner: requiredTool('apksigner'),
  llvmReadelf: requiredTool('llvm-readelf'),
  zipalign: requiredTool('zipalign'),
};

const direct = validateApk(apkArg, tools, expectedOrigin);
const bundle = validateBundle(aabArg, resolve(bundletoolArg), expectedOrigin, tools);
const universal = validateApk(universalArg, tools, expectedOrigin);

console.log('MOBILE ANDROID RELEASE VALID');
console.log(`Package: ${contract.package}`);
console.log(`Version: ${contract.version} (${contract.versionCode})`);
console.log(`Target SDK: ${contract.targetSdk}; min SDK: ${contract.minSdk}`);
console.log(`Upload signer SHA-256: ${contract.uploadSignerSha256}`);
console.log(`Direct APK bytes: ${direct.bytes}`);
console.log(`AAB bytes: ${bundle.bytes}`);
console.log(`Bundle-derived APK bytes: ${universal.bytes}`);
console.log(`ABIs: ${direct.abis.join(', ')}`);
