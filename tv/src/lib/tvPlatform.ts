import { BackHandler, NativeEventEmitter, NativeModules, Platform } from 'react-native';
import { tvDebug } from '../debug.ts';

// JavaScript side of the NubArcaTvPlatform native bridge (see
// plugins/withTvPlatformModule.js).
//
// `BackHandler.exitApp()` maps to `Activity.moveTaskToBack(true)` on Android: it
// BACKGROUNDS the task. On a physical Fire Stick that showed the launcher but
// left NubArca in the task list, and relaunching resumed the old Activity —
// which is the reported "final BACK does not close the app" defect.
// `finishAndRemoveTask()` is the API that actually finishes the Activity and
// removes the task; it has no JavaScript binding, so the plugin adds one.
//
// "Closed" here means: Activity finished, task removed, launcher visible,
// nothing still playing, and a relaunch creating a new Activity. It does NOT
// mean the Linux process is gone — Android keeping a cached process is normal.
// Nothing in this path calls System.exit or killProcess.

// The second capability on the same bridge is the user-approved self-update:
// NubArca TV installing its OWN next official APK, so a native upgrade no
// longer needs ADB, a PC or a file manager. Fire OS still shows its own
// confirmation — that is the design, not a limitation being worked around.
//
// Every failure crosses this boundary as one of the sanitized codes below.
// Native exception text, stack traces and filesystem paths never reach the UI.

interface NubArcaTvPlatformNative {
  startOutputObserver(): Promise<boolean>;
  stopOutputObserver(): Promise<boolean>;
  // Resolves true when an Activity was found and finished, false when there was
  // none to finish (so the caller can fall back rather than hang).
  exitAndRemoveTask(): Promise<boolean>;
  canRequestPackageInstalls(): Promise<boolean>;
  openPackageInstallSettings(): Promise<boolean>;
  requestPackageUpdate(
    localApkPath: string,
    expectedSha256: string,
    expectedVersionCode: number,
  ): Promise<string>;
}

const native = (NativeModules as { NubArcaTvPlatform?: NubArcaTvPlatformNative })
  .NubArcaTvPlatform;

/** Whether this build can install its own updates at all (release Fire TV only). */
export function hasNativeInstaller(): boolean {
  return Platform.OS === 'android' && typeof native?.requestPackageUpdate === 'function';
}

// Every failure the NATIVE bridge can report. Anything it cannot classify
// collapses to 'installer-unavailable' rather than leaking a native message.
// The update screen explains one more — 'download-failed' — which happens
// before the bridge is reached; see NativeUpdateFailure.
const PACKAGE_UPDATE_FAILURES = [
  'permission-required',
  'invalid-file',
  'hash-mismatch',
  'wrong-package',
  'not-newer',
  'signer-mismatch',
  'installer-rejected',
  'installer-unavailable',
] as const;

export type PackageUpdateFailure = (typeof PACKAGE_UPDATE_FAILURES)[number];

export type PackageUpdateResult =
  // 'installer-launched': Fire OS is showing its confirmation screen.
  // 'installed': the platform reported success before we stopped listening.
  | { ok: true; outcome: 'installer-launched' | 'installed' }
  | { ok: false; code: PackageUpdateFailure };

function toFailure(value: unknown): PackageUpdateFailure {
  const code = (value as { code?: unknown } | null)?.code;
  return (PACKAGE_UPDATE_FAILURES as readonly string[]).includes(code as string)
    ? (code as PackageUpdateFailure)
    : 'installer-unavailable';
}

/** Has the user already allowed NubArca TV to request installs? */
export async function canRequestPackageInstalls(): Promise<boolean> {
  if (!native?.canRequestPackageInstalls) return false;
  try {
    return await native.canRequestPackageInstalls();
  } catch {
    return false;
  }
}

/** Opens the system screen granting that permission, scoped to this package. */
export async function openPackageInstallSettings(): Promise<boolean> {
  if (!native?.openPackageInstallSettings) return false;
  try {
    return await native.openPackageInstallSettings();
  } catch {
    return false;
  }
}

/**
 * Hands a downloaded APK to the platform installer.
 *
 * The native side re-validates hash, package, versionCode and signer against
 * the RUNNING install before a session exists — the values passed here are what
 * it checks the file AGAINST, never a claim it takes on trust.
 */
export async function requestPackageUpdate(
  localApkPath: string,
  expectedSha256: string,
  expectedVersionCode: number,
): Promise<PackageUpdateResult> {
  if (!native?.requestPackageUpdate) return { ok: false, code: 'installer-unavailable' };
  try {
    const outcome = await native.requestPackageUpdate(
      localApkPath, expectedSha256, expectedVersionCode,
    );
    tvDebug('update', 'install', outcome);
    return outcome === 'installed'
      ? { ok: true, outcome: 'installed' }
      : { ok: true, outcome: 'installer-launched' };
  } catch (error) {
    const code = toFailure(error);
    tvDebug('update', 'install-failed', code);
    return { ok: false, code };
  }
}

// Close the app at the navigation root.
//
// The fallback exists for the development client and for iOS (used only for a
// phone-form-factor smoke test), where the module is absent. It is deliberately
// the OLD, weaker behaviour rather than a hard failure: on those targets
// backgrounding is the best available outcome and is not a product requirement.
// On a release Fire TV build the native path is always present — a build where
// it is missing failed the plugin's own registration gate at prebuild time.
export async function exitTvApp(): Promise<void> {
  if (native?.exitAndRemoveTask) {
    try {
      const finished = await native.exitAndRemoveTask();
      tvDebug('app', 'exit', finished ? 'task-removed' : 'no-activity');
      if (finished) return;
    } catch {
      // Fall through to the platform default rather than trapping the user on
      // a screen whose BACK appears to do nothing.
      tvDebug('app', 'exit', 'native-failed');
    }
  } else {
    tvDebug('app', 'exit', 'native-absent');
  }
  BackHandler.exitApp();
}

// --- audio output route ------------------------------------------------------

// The event the native observer emits when the playback output disappears —
// HDMI unplugged, a receiver switched away, a Bluetooth speaker gone.
const OUTPUT_LOST_EVENT = 'NubArcaTvOutputLost';

/**
 * Subscribe to output-route loss while a playback context exists.
 *
 * The native side is deliberately dumb: it reports, it never acts. Deciding
 * what loss MEANS — pause, keep the position, do not auto-resume — belongs to
 * the JavaScript playback authority, because expo-video already owns the
 * player, the audio focus and the MediaSession and a second owner of any of
 * those is the defect this whole audit set out to avoid.
 *
 * Registration is scoped to the subscription: outside a playback context there
 * is nothing to react to, so nothing is registered and NubArca is not sitting
 * on a broadcast receiver for the life of the process.
 */
export function subscribeOutputLost(onLost: () => void): () => void {
  if (!native?.startOutputObserver) return () => {};
  const emitter = new NativeEventEmitter(native as never);
  const subscription = emitter.addListener(OUTPUT_LOST_EVENT, () => {
    tvDebug('audio', 'output-lost');
    onLost();
  });
  void native.startOutputObserver().catch(() => {});
  return () => {
    subscription.remove();
    void native.stopOutputObserver?.().catch(() => {});
  };
}
