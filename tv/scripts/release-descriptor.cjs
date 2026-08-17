// Builds the NubArca TV native release descriptor — the small static file
// published beside the APK that tells an installed TV a newer native release
// exists.
//
// WHY IT IS GENERATED AND NEVER HAND-MAINTAINED
// ---------------------------------------------
// Every value in it is already the truth somewhere else: the identity comes
// from tv/release-contract.json (the one tracked native contract), and the size
// and hash come from the actual APK bytes being published. A hand-written
// descriptor is a second place for the version to be wrong, and the failure it
// produces — a device told to install bytes that are not what was described —
// is exactly what the client and native gates then have to reject.
//
// WHAT IT IS NOT
// --------------
// It is not a security boundary and it carries NO URL. It names one file, and
// that name is pinned to the versionCode, so the client can compose a
// same-origin URL under the existing public download path and nothing else.
// Package identity, versionCode monotonicity and signer identity are re-checked
// natively against the running install before anything is installed.

const { createHash } = require('node:crypto');
const { createReadStream, statSync, writeFileSync } = require('node:fs');
const { resolve } = require('node:path');
const { readReleaseContract } = require('./release-contract.cjs');

const SCHEMA_VERSION = 1;

/**
 * The immutable published name for one versionCode.
 *
 * Binding the file name to the version code is what keeps published artifacts
 * immutable in practice: publishing v8 can never overwrite the bytes a device
 * is still being offered as v7. The client requires this exact name, so the two
 * sides cannot drift apart silently.
 */
function apkFileName(versionCode) {
  return `nubarca-tv-v${versionCode}.apk`;
}

/**
 * The descriptor for a contract plus the measured bytes of one APK.
 *
 * Field order is fixed so a published descriptor diffs cleanly between releases.
 */
function buildReleaseDescriptor(contract, apkSha256, apkBytes) {
  if (typeof apkSha256 !== 'string' || !/^[0-9a-f]{64}$/.test(apkSha256)) {
    throw new Error('Release descriptor apkSha256 must be 64 lowercase hexadecimal characters');
  }
  if (!Number.isSafeInteger(apkBytes) || apkBytes < 1) {
    throw new Error('Release descriptor apkBytes must be a positive integer');
  }
  return {
    schemaVersion: SCHEMA_VERSION,
    package: contract.package,
    version: contract.version,
    versionCode: contract.versionCode,
    runtimeVersion: contract.runtimeVersion,
    channel: contract.channel,
    apkFile: apkFileName(contract.versionCode),
    apkSha256,
    apkBytes,
  };
}

function sha256File(path) {
  return new Promise((resolveHash, rejectHash) => {
    const hash = createHash('sha256');
    createReadStream(path)
      .on('error', rejectHash)
      .on('data', (chunk) => hash.update(chunk))
      .on('end', () => resolveHash(hash.digest('hex')));
  });
}

/** Measure a real APK and describe it under the tracked release contract. */
async function describeApk(apkPath, contractPath) {
  const contract = contractPath ? readReleaseContract(contractPath) : readReleaseContract();
  const stats = statSync(apkPath);
  if (!stats.isFile()) throw new Error(`Not a regular file: ${apkPath}`);
  return buildReleaseDescriptor(contract, await sha256File(apkPath), stats.size);
}

module.exports = { SCHEMA_VERSION, apkFileName, buildReleaseDescriptor, describeApk };

// CLI: `release-descriptor.cjs <apk> [descriptor-output]`
//
// Writes the descriptor JSON to the output path when given one, and always
// prints the three values the publisher needs, one per line:
//   immutable APK file name / APK SHA-256 / APK byte count
if (require.main === module) {
  const [apkPath, outputPath] = process.argv.slice(2);
  if (!apkPath) {
    console.error('usage: release-descriptor.cjs <apk> [descriptor-output]');
    process.exit(2);
  }
  describeApk(resolve(apkPath))
    .then((descriptor) => {
      if (outputPath) {
        writeFileSync(resolve(outputPath), `${JSON.stringify(descriptor, null, 2)}\n`, 'utf8');
      }
      console.log(descriptor.apkFile);
      console.log(descriptor.apkSha256);
      console.log(descriptor.apkBytes);
    })
    .catch((error) => {
      console.error(error.message);
      process.exit(1);
    });
}
