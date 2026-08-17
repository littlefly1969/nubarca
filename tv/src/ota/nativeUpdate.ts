// Fetching the native release descriptor and staging the APK it names.
//
// Everything that decides ANYTHING lives in nativeRelease.ts (pure, tested) or
// in the native installer (fail-closed, authoritative). This file is the I/O
// between them: one same-origin GET, one same-origin download, one bridge call.
//
// It uses plain `fetch` and expo-file-system rather than the TV API client on
// purpose. The release descriptor and the APK are the SAME public artifacts a
// person would sideload by hand; they carry no session, no grant and nothing
// owner-private, and they are deliberately not reachable through /api/tv.

import { Directory, File, Paths } from 'expo-file-system';
import { tvDebug } from '../debug';
import {
  hasNativeInstaller,
  requestPackageUpdate,
  type PackageUpdateFailure,
} from '../lib/tvPlatform';
import {
  apkDownloadUrl,
  parseReleaseDescriptor,
  releaseDescriptorUrl,
  type ExpectedIdentity,
  type NativeRelease,
  type ReleaseRejection,
} from './nativeRelease';

// One directory, wiped before every attempt. This is the whole cache policy:
// there is never more than one staged APK, and a failed attempt cannot leave a
// second one behind.
const UPDATE_CACHE_DIRNAME = 'tv-update';

const DESCRIPTOR_TIMEOUT_MS = 15_000;

/** A network/HTTP failure is separate from a refused descriptor. */
export type DescriptorResult =
  | { ok: true; release: NativeRelease }
  | { ok: false; reason: ReleaseRejection | 'unavailable' };

export type NativeUpdateFailure = PackageUpdateFailure | 'download-failed';

export type NativeUpdateResult =
  | { ok: true; outcome: 'installer-launched' | 'installed' }
  | { ok: false; code: NativeUpdateFailure };

/**
 * Reads the published release descriptor.
 *
 * A missing descriptor is not an error the user needs to see — it simply means
 * there is no native release to offer, and the OTA path takes over.
 */
export async function fetchNativeRelease(
  baseUrl: string,
  expected: ExpectedIdentity,
): Promise<DescriptorResult> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), DESCRIPTOR_TIMEOUT_MS);
  let text: string;
  try {
    const response = await fetch(releaseDescriptorUrl(baseUrl), {
      method: 'GET',
      headers: { accept: 'application/json' },
      signal: controller.signal,
    });
    if (!response.ok) return { ok: false, reason: 'unavailable' };
    text = await response.text();
  } catch {
    return { ok: false, reason: 'unavailable' };
  } finally {
    clearTimeout(timer);
  }

  // Note the raw TEXT: a request for a descriptor that does not exist can be
  // answered by the SPA fallback with HTTP 200 and an HTML body. Parsing here
  // means that lands in 'malformed', nowhere near an install path.
  const parsed = parseReleaseDescriptor(text, expected);
  if (!parsed.ok) {
    tvDebug('update', 'descriptor-rejected', parsed.reason);
    return { ok: false, reason: parsed.reason };
  }
  return { ok: true, release: parsed.release };
}

function stagingDirectory(): Directory {
  return new Directory(Paths.cache, UPDATE_CACHE_DIRNAME);
}

function discardStaged(dir: Directory): void {
  try {
    if (dir.exists) dir.delete();
  } catch {
    // A cache directory that refuses to be deleted is not worth failing an
    // update over; the next attempt overwrites it.
  }
}

/**
 * Downloads the APK the descriptor names and hands it to the platform
 * installer.
 *
 * The URL is composed here from the pinned base URL and a file name that has
 * already been proven equal to `nubarca-tv-v<versionCode>.apk` — the descriptor
 * never supplies a URL, so it cannot point the device at another host.
 *
 * The byte-count check below is a cheap early exit, NOT a security gate. The
 * authoritative SHA-256, package, versionCode and signer checks all run
 * natively against the running install before an install session is created.
 */
export async function downloadAndInstallNativeRelease(
  baseUrl: string,
  release: NativeRelease,
  installedVersionCode: number,
  onProgress?: (fraction: number | null) => void,
): Promise<NativeUpdateResult> {
  // Never download for a version we would refuse to install anyway.
  if (release.versionCode <= installedVersionCode) return { ok: false, code: 'not-newer' };
  if (!hasNativeInstaller()) return { ok: false, code: 'installer-unavailable' };

  const dir = stagingDirectory();
  discardStaged(dir);

  let staged: File | null;
  try {
    // The directory was just dropped, so the destination cannot already exist.
    dir.create({ intermediates: true, idempotent: true });
    const destination = new File(dir, release.apkFile);
    const task = File.createDownloadTask(apkDownloadUrl(baseUrl, release), destination, {
      onProgress: ({ bytesWritten, totalBytes }) => {
        // The server may omit Content-Length (totalBytes -1); the descriptor
        // already told us the size, so progress survives that.
        const total = totalBytes > 0 ? totalBytes : release.apkBytes;
        onProgress?.(total > 0 ? Math.min(1, bytesWritten / total) : null);
      },
    });
    // Resolves null when the transfer was paused rather than completed.
    staged = await task.downloadAsync();
  } catch {
    discardStaged(dir);
    return { ok: false, code: 'download-failed' };
  }
  if (staged === null) {
    discardStaged(dir);
    return { ok: false, code: 'download-failed' };
  }

  if ((staged.size ?? 0) !== release.apkBytes) {
    discardStaged(dir);
    return { ok: false, code: 'invalid-file' };
  }

  const result = await requestPackageUpdate(staged.uri, release.apkSha256, release.versionCode);
  // On the success path the native side has already deleted the staged file:
  // the install session owns those bytes and a second copy of a whole APK is
  // exactly the unbounded cache growth to avoid. Everything else is cleaned up
  // here.
  if (!result.ok) discardStaged(dir);
  return result;
}

/** Drops any staged APK — used when the user leaves the update screen. */
export function discardStagedUpdate(): void {
  discardStaged(stagingDirectory());
}
