// Native (APK) release discovery for the TV app — pure parsing and policy, no
// I/O, so every rule below is testable with `node --test`.
//
// WHY A STATIC DESCRIPTOR AND NOT AN API
// --------------------------------------
// The APK already has one canonical public publication path
// (`/download/tv/…`, published by deploy/publish-tv-apk.sh). Describing those
// same bytes does not need a database model, an endpoint or an auth model — it
// needs one small file published beside them. The descriptor is the ACTIVATION
// POINTER: it is written last, only after the bytes it names are uploaded and
// their SHA-256 is verified on the server.
//
// TRUST MODEL
// -----------
// This file treats the descriptor as UNTRUSTED input even though it arrives
// over TLS from the pinned production origin. It never yields a URL: the
// descriptor carries a bare FILE NAME, and the caller composes the URL from the
// base URL the app is already pinned to. A descriptor therefore cannot redirect
// the device to another host, another path, or another package — the worst a
// tampered descriptor can do is name a file that fails the download or the
// native gates.
//
// It is also NOT the security boundary. Matching `apkSha256` proves the bytes
// are the ones the descriptor described; it proves nothing about what they
// contain. Package identity, versionCode monotonicity and signer identity are
// re-verified natively against the RUNNING install before an install session is
// created, and Android verifies the signature again after that.

/** The one public path the release descriptor is published at. */
const RELEASE_DESCRIPTOR_PATH = '/download/tv/nubarca-tv.release.json';

/** The directory the immutable APKs are published in, on the same origin. */
const RELEASE_DOWNLOAD_PREFIX = '/download/tv/';

export interface NativeRelease {
  readonly schemaVersion: 1;
  readonly package: string;
  readonly version: string;
  readonly versionCode: number;
  readonly runtimeVersion: string;
  readonly channel: string;
  readonly apkFile: string;
  readonly apkSha256: string;
  readonly apkBytes: number;
}

export interface ExpectedIdentity {
  /** The running applicationId. A descriptor for anything else is not ours. */
  readonly package: string;
  /** The running release channel. */
  readonly channel: string;
}

const FIELDS = [
  'schemaVersion', 'package', 'version', 'versionCode', 'runtimeVersion',
  'channel', 'apkFile', 'apkSha256', 'apkBytes',
] as const;

const SHA256_HEX = /^[0-9a-f]{64}$/;
const SEMVER = /^\d+\.\d+\.\d+$/;
const SAFE_SEGMENT = /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/;

/** Sanitized, user-presentable reason a descriptor was refused. */
export type ReleaseRejection =
  | 'malformed'
  | 'wrong-package'
  | 'wrong-channel'
  | 'invalid-version'
  | 'invalid-hash'
  | 'invalid-file'
  | 'invalid-size';

export type ParseResult =
  | { ok: true; release: NativeRelease }
  | { ok: false; reason: ReleaseRejection };

function isPositiveInteger(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value > 0;
}

/**
 * The APK file name a descriptor for `versionCode` is REQUIRED to carry.
 *
 * Binding the name to the version code is what makes the published artifact
 * immutable in practice: publishing v7 can never overwrite the bytes an older
 * device is still being offered as v6.
 */
export function expectedApkFileName(versionCode: number): string {
  return `nubarca-tv-v${versionCode}.apk`;
}

/**
 * Parse an untrusted release descriptor.
 *
 * Accepts the raw text (not a parsed object) on purpose: a request for a
 * missing descriptor can be answered by an SPA fallback with HTTP 200 and an
 * HTML body, and that must land in `malformed` rather than anywhere near an
 * install path.
 */
export function parseReleaseDescriptor(text: string, expected: ExpectedIdentity): ParseResult {
  let value: unknown;
  try {
    value = JSON.parse(text);
  } catch {
    return { ok: false, reason: 'malformed' };
  }
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    return { ok: false, reason: 'malformed' };
  }

  const raw = value as Record<string, unknown>;
  const keys = Object.keys(raw).sort();
  if (keys.join('\n') !== [...FIELDS].sort().join('\n')) return { ok: false, reason: 'malformed' };
  if (raw.schemaVersion !== 1) return { ok: false, reason: 'malformed' };

  const {
    package: packageName, version, versionCode, runtimeVersion, channel, apkFile, apkSha256, apkBytes,
  } = raw;

  if (typeof packageName !== 'string' || typeof version !== 'string'
      || typeof runtimeVersion !== 'string' || typeof channel !== 'string'
      || typeof apkFile !== 'string' || typeof apkSha256 !== 'string') {
    return { ok: false, reason: 'malformed' };
  }
  if (packageName !== expected.package) return { ok: false, reason: 'wrong-package' };
  if (channel !== expected.channel) return { ok: false, reason: 'wrong-channel' };
  if (!SEMVER.test(version)) return { ok: false, reason: 'invalid-version' };
  if (!isPositiveInteger(versionCode)) return { ok: false, reason: 'invalid-version' };
  if (!SAFE_SEGMENT.test(runtimeVersion)) return { ok: false, reason: 'malformed' };
  if (!SHA256_HEX.test(apkSha256)) return { ok: false, reason: 'invalid-hash' };
  if (!isPositiveInteger(apkBytes)) return { ok: false, reason: 'invalid-size' };

  // Defence in depth: the exact-name comparison below already excludes every
  // one of these, but rejecting them by shape keeps the reason honest if the
  // naming rule ever changes.
  if (apkFile.includes('/') || apkFile.includes('\\') || apkFile.includes('..')
      || apkFile.includes(':') || apkFile.includes('?') || apkFile.includes('#')
      || !SAFE_SEGMENT.test(apkFile)) {
    return { ok: false, reason: 'invalid-file' };
  }
  if (apkFile !== expectedApkFileName(versionCode)) return { ok: false, reason: 'invalid-file' };

  return {
    ok: true,
    release: {
      schemaVersion: 1,
      package: packageName,
      version,
      versionCode,
      runtimeVersion,
      channel,
      apkFile,
      apkSha256,
      apkBytes,
    },
  };
}

/**
 * The ONE update-precedence decision, per the product rule:
 *
 *   published > installed → the native release wins outright. An OTA published
 *     for the OLDER runtime must not be offered or applied on top of a device
 *     that is about to move to a new native contract.
 *   published == installed → nothing native to do; the OTA flow for the running
 *     runtime is the update path.
 *   published < installed → the descriptor is stale (a rollback of the pointer,
 *     or a device that already moved ahead). Never downgrade; the OTA flow for
 *     the running runtime may still be checked.
 *
 * A missing or refused descriptor degrades to the OTA flow rather than blocking
 * updates entirely: not knowing about a native release is not a reason to stop
 * shipping compatible ones.
 *
 * `otaPending` is passed in and deliberately IGNORED when the native release
 * wins. An update already downloaded for the runtime the device is leaving is
 * not a reason to apply it — it would reload into the old native contract only
 * to be replaced moments later.
 *
 * Note what is deliberately NOT here: runtime strings. A differing
 * `runtimeVersion` never authorizes an install — versionCode is the Android
 * upgrade authority, and package/signer identity is the native gate.
 */
export function decideUpdatePath(
  release: NativeRelease | null,
  installedVersionCode: number,
  otaPending = false,
): 'native' | 'ota' {
  if (release && release.versionCode > installedVersionCode) return 'native';
  void otaPending;
  return 'ota';
}

/**
 * The same-origin URL the validated APK is fetched from. The descriptor never
 * supplies a URL — only the final path segment, which has already been proven
 * equal to `nubarca-tv-v<versionCode>.apk`.
 */
export function apkDownloadUrl(baseUrl: string, release: NativeRelease): string {
  return `${baseUrl.replace(/\/$/, '')}${RELEASE_DOWNLOAD_PREFIX}${release.apkFile}`;
}

/** The same-origin URL of the release descriptor itself. */
export function releaseDescriptorUrl(baseUrl: string): string {
  return `${baseUrl.replace(/\/$/, '')}${RELEASE_DESCRIPTOR_PATH}`;
}
