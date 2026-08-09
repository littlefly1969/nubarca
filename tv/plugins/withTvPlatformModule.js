// Expo config plugin: a tiny native module that really closes the app.
//
// WHY THIS EXISTS
// ---------------
// At the navigation root the remote's BACK button must CLOSE NubArca TV. React
// Native's `BackHandler.exitApp()` was used for that and did not produce the
// required behaviour on a physical Fire Stick: the launcher came back, but the
// NubArca task stayed in the recents/task list and relaunching resumed the old
// Activity instead of starting a fresh one. `exitApp()` maps to
// `Activity.moveTaskToBack(true)` on Android — it BACKGROUNDS the task by
// design, and no amount of JavaScript changes that.
//
// The correct API is `Activity.finishAndRemoveTask()`: it finishes every
// Activity of the task AND removes the task from recents. It has no JavaScript
// binding in React Native or Expo, so this plugin adds one.
//
// WHAT "CLOSED" MEANS, AND WHAT IT DOES NOT
// -----------------------------------------
// Success is: the Activity is finished, the task is removed, the Fire TV
// launcher is visible, nothing keeps playing, and relaunching creates a NEW
// Activity. Android may keep the process alive as a cached process afterwards —
// that is normal, healthy platform behaviour and is NOT a failure of this
// bridge. Which is why the implementation deliberately does NOT call
// `System.exit`, `Runtime.getRuntime().exit` or `Process.killProcess`: those
// kill the process out from under the platform, corrupt the saved instance
// state, and are the standard cause of a "reopens into a broken screen" bug.
//
// WHY A CONFIG PLUGIN AND NOT AN EDIT TO android/
// -----------------------------------------------
// `expo prebuild` regenerates android/ from the template, so a hand-edited file
// there is deleted on the next release build. The plugin is the tracked source
// of truth and re-applies on every prebuild — the same reasoning as
// withReleaseSigning.

const {
  AndroidConfig,
  withAndroidManifest,
  withDangerousMod,
  withMainApplication,
} = require('expo/config-plugins');
const fs = require('node:fs');
const path = require('node:path');

const PACKAGE_DIR = 'it/littlefly/nubarca/tv/platform';
const PACKAGE_NAME = 'it.littlefly.nubarca.tv.platform';

const MODULE_KT = `package ${PACKAGE_NAME}

import android.app.Activity
import com.facebook.react.bridge.Promise
import com.facebook.react.bridge.ReactApplicationContext
import com.facebook.react.bridge.ReactContextBaseJavaModule
import com.facebook.react.bridge.ReactMethod

/**
 * Closes NubArca TV for real.
 *
 * BackHandler.exitApp() maps to Activity.moveTaskToBack(true), which BACKGROUNDS
 * the task: on a Fire Stick the launcher appears but the task stays in recents
 * and a relaunch resumes the old Activity. finishAndRemoveTask() finishes every
 * Activity of the task and removes the task, which is what the product means by
 * "closed".
 *
 * Deliberately no System.exit / Process.killProcess. Killing the process is not
 * how an Android app exits: it skips orderly teardown and leaves the platform
 * holding stale saved state. A cached process surviving after the task is
 * removed is correct platform behaviour, not a leak.
 *
 * Runs on the UI thread because finishAndRemoveTask() is an Activity call.
 * Resolves false when there is no current Activity (nothing to finish) so the
 * JavaScript side can fall back rather than hang.
 */
class NubArcaTvPlatformModule(reactContext: ReactApplicationContext) :
    ReactContextBaseJavaModule(reactContext) {

    override fun getName(): String = "NubArcaTvPlatform"

    @ReactMethod
    fun exitAndRemoveTask(promise: Promise) {
        val activity: Activity? = currentActivity
        if (activity == null) {
            promise.resolve(false)
            return
        }
        activity.runOnUiThread {
            activity.finishAndRemoveTask()
        }
        promise.resolve(true)
    }
}
`;

const PACKAGE_KT = `package ${PACKAGE_NAME}

import android.view.View
import com.facebook.react.ReactPackage
import com.facebook.react.bridge.NativeModule
import com.facebook.react.bridge.ReactApplicationContext
import com.facebook.react.uimanager.ReactShadowNode
import com.facebook.react.uimanager.ViewManager

/** Registers the single platform module. No view managers, no other natives. */
class NubArcaTvPlatformPackage : ReactPackage {
    override fun createNativeModules(
        reactContext: ReactApplicationContext
    ): List<NativeModule> = listOf(NubArcaTvPlatformModule(reactContext))

    override fun createViewManagers(
        reactContext: ReactApplicationContext
    ): List<ViewManager<View, ReactShadowNode<*>>> = emptyList()
}
`;

// Written into MainApplication's package list. The Expo template builds it as
// `PackageList(this).packages.apply { … }` with a commented `add(...)`
// placeholder inside the block; appending after that comment is the documented
// extension point.
const PACKAGE_LIST_ANCHOR = '// add(MyReactNativePackage())';
const PACKAGE_LIST_REPLACEMENT = `// add(MyReactNativePackage())
          // NubArca TV: the finishAndRemoveTask bridge, so the final BACK at
          // the navigation root really closes the task rather than
          // backgrounding it. See plugins/withTvPlatformModule.js.
          add(it.littlefly.nubarca.tv.platform.NubArcaTvPlatformPackage())`;

const withNativeSources = (config) =>
  withDangerousMod(config, [
    'android',
    (modConfig) => {
      const root = modConfig.modRequest.platformProjectRoot;
      const dir = path.join(root, 'app', 'src', 'main', 'java', PACKAGE_DIR);
      fs.mkdirSync(dir, { recursive: true });
      fs.writeFileSync(path.join(dir, 'NubArcaTvPlatformModule.kt'), MODULE_KT, 'utf8');
      fs.writeFileSync(path.join(dir, 'NubArcaTvPlatformPackage.kt'), PACKAGE_KT, 'utf8');
      return modConfig;
    },
  ]);

const withPackageRegistration = (config) =>
  withMainApplication(config, (mainConfig) => {
    const contents = mainConfig.modResults.contents;
    if (contents.includes('NubArcaTvPlatformPackage')) {
      return mainConfig; // already applied
    }
    if (!contents.includes(PACKAGE_LIST_ANCHOR)) {
      // Fail loudly rather than produce an APK whose BACK-at-root silently does
      // nothing: a missing bridge is exactly the defect this plugin fixes.
      throw new Error(
        'withTvPlatformModule: the package list in MainApplication no longer matches ' +
          'the expected template. Refusing to continue, because skipping registration ' +
          'would ship an app whose final BACK cannot close the task.',
      );
    }
    mainConfig.modResults.contents = contents.replace(
      PACKAGE_LIST_ANCHOR,
      PACKAGE_LIST_REPLACEMENT,
    );
    return mainConfig;
  });

// A task that is removed must not be resurrected from a stale snapshot on the
// next launch. `excludeFromRecents` is deliberately NOT set (the user should see
// NubArca in recents while it is running); `alwaysRetainTaskState` is left at
// its default so the platform is free to drop the removed task's state.
const withLauncherActivity = (config) =>
  withAndroidManifest(config, (manifestConfig) => {
    const activity = AndroidConfig.Manifest.getMainActivityOrThrow(manifestConfig.modResults);
    // Relaunching after finishAndRemoveTask must start a clean Activity rather
    // than restore a half-torn-down one.
    activity.$['android:clearTaskOnLaunch'] = 'true';
    return manifestConfig;
  });

/** @type {import('expo/config-plugins').ConfigPlugin} */
const withTvPlatformModule = (config) =>
  withLauncherActivity(withPackageRegistration(withNativeSources(config)));

module.exports = withTvPlatformModule;
