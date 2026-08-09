import { BackHandler, NativeModules, Platform } from 'react-native';
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

interface NubArcaTvPlatformNative {
  // Resolves true when an Activity was found and finished, false when there was
  // none to finish (so the caller can fall back rather than hang).
  exitAndRemoveTask(): Promise<boolean>;
}

const native = (NativeModules as { NubArcaTvPlatform?: NubArcaTvPlatformNative })
  .NubArcaTvPlatform;

export function hasNativeTaskExit(): boolean {
  return Platform.OS === 'android' && typeof native?.exitAndRemoveTask === 'function';
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
