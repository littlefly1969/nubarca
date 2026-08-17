// The application's binding to expo-updates. Everything with a rule in it lives
// in updateLifecycle.ts behind the testable UpdatesApi; this file only supplies
// the real module and the release identity Expo already knows.

import * as Updates from 'expo-updates';
import Constants from 'expo-constants';
import {
  applyPendingUpdate as applyUpdate,
  checkForUpdateNow as checkNow,
  getOtaDiagnostics,
  startBackgroundUpdateCheck as startCheck,
  type ApplyResult,
  type ManualCheckResult,
} from './updateLifecycle';

export interface RunningRelease {
  version: string | null;
  versionCode: number | null;
  // The running applicationId. A release descriptor naming anything else is not
  // describing this app and is refused.
  packageName: string | null;
  runtimeVersion: string | null;
  channel: string | null;
  updateId: string | null;
}

function extra(): { otaChannel?: string; releaseVersionCode?: number } {
  return (Constants.expoConfig?.extra ?? {}) as { otaChannel?: string; releaseVersionCode?: number };
}

/**
 * The release identity of the RUNNING app.
 *
 * `versionCode` is only ever used to decide whether a published native release
 * is newer. It is not the security gate: the native installer re-reads the
 * really-installed versionCode from PackageManager and refuses anything that is
 * not strictly higher.
 */
export function getRunningRelease(): RunningRelease {
  const diagnostics = getOtaDiagnostics();
  return {
    version: Constants.expoConfig?.version ?? null,
    versionCode: Constants.expoConfig?.android?.versionCode ?? extra().releaseVersionCode ?? null,
    packageName: Constants.expoConfig?.android?.package ?? null,
    runtimeVersion: diagnostics.runtimeVersion ?? Updates.runtimeVersion ?? null,
    channel: extra().otaChannel ?? null,
    updateId: diagnostics.runningUpdateId ?? Updates.updateId ?? null,
  };
}

/** Startup: one non-blocking check per JS process. Never reloads. */
export function startBackgroundUpdateCheck(): void {
  void startCheck(Updates, {
    applicationVersion: Constants.expoConfig?.version ?? null,
    versionCode: Constants.expoConfig?.android?.versionCode ?? null,
    channel: extra().otaChannel ?? null,
  });
}

/** Whether an OTA has already been downloaded and is waiting for a reload. */
export function isOtaUpdatePending(): boolean {
  return getOtaDiagnostics().pending;
}

/** The user asked. Joins the startup check when one is still running. */
export function checkForOtaUpdateNow(): Promise<ManualCheckResult> {
  return checkNow(Updates);
}

/** The user pressed "install now" — the only reload in the application. */
export function applyDownloadedOtaUpdate(): Promise<ApplyResult> {
  return applyUpdate(Updates);
}
