// The ONE expo-updates lifecycle for the TV app. Two callers, two very
// different contracts, one set of operations underneath:
//
//   A. STARTUP PREFETCH  (startBackgroundUpdateCheck)
//      Runs at most once per JS process, never blocks the UI, and NEVER
//      reloads. A downloaded update stays pending until a later cold launch —
//      an automatic reload would interrupt a slideshow or a Beauty Lab run.
//
//   B. EXPLICIT USER CHECK  (checkForUpdateNow / applyPendingUpdate)
//      Driven by the Updates screen. May run again after an earlier no-update
//      result — the user pressing "check" while nothing was available at boot
//      must not be answered with the stale boot answer. It is the only path
//      that may reload, and only because the user asked for it.
//
// Both share ONE in-flight operation. expo-updates does not tolerate
// overlapping check/fetch calls, and a user arriving on the Updates screen
// while the startup check is still running must join it rather than start a
// second one.

export type OtaDiagnostics = {
  applicationVersion: string | null;
  versionCode: number | null;
  channel: string | null;
  runtimeVersion: string | null;
  runningUpdateId: string | null;
  embeddedUpdateId: string | null;
  isEmbedded: boolean;
  pending: boolean;
  lastResult: 'idle' | 'disabled' | 'checking' | 'no-update' | 'downloaded' | 'already-downloaded' | 'error';
  lastError: string | null;
};

export type ReleaseMetadata = {
  applicationVersion: string | null;
  versionCode: number | null;
  channel: string | null;
};

export type UpdatesApi = {
  isEnabled: boolean;
  runtimeVersion: string | null;
  updateId: string | null;
  isEmbeddedLaunch: boolean;
  checkForUpdateAsync(): Promise<{ isAvailable: boolean }>;
  fetchUpdateAsync(): Promise<{ isNew: boolean }>;
  // Restarts the JS runtime onto the downloaded update. Only ever called from
  // the explicit user action below.
  reloadAsync(): Promise<void>;
};

/** What the Updates screen may show after an explicit check. */
export type ManualCheckResult =
  | { state: 'disabled' }
  | { state: 'up-to-date' }
  | { state: 'ota-ready' }
  | { state: 'error'; message: string };

/**
 * `reloading` means the runtime accepted the restart; nothing after it is
 * guaranteed to run. `error` is retryable.
 */
export type ApplyResult = { state: 'reloading' } | { state: 'error'; message: string };

let started = false;
let inFlight: Promise<OtaDiagnostics> | null = null;
// Held for the lifetime of the process once a reload is accepted: the runtime
// is being torn down, so a second press must not fire a second reload.
let applying: Promise<ApplyResult> | null = null;
let diagnostics: OtaDiagnostics = {
  applicationVersion: null,
  versionCode: null,
  channel: null,
  runtimeVersion: null,
  runningUpdateId: null,
  embeddedUpdateId: null,
  isEmbedded: false,
  pending: false,
  lastResult: 'idle',
  lastError: null,
};

export function getOtaDiagnostics(): OtaDiagnostics {
  return { ...diagnostics };
}

function message(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/**
 * One check/fetch cycle against expo-updates, recorded into the shared
 * diagnostics. Never throws and never reloads.
 */
function runUpdateCycle(api: UpdatesApi): Promise<OtaDiagnostics> {
  if (inFlight) return inFlight;
  inFlight = (async () => {
    try {
      const check = await api.checkForUpdateAsync();
      if (!check.isAvailable) {
        diagnostics = { ...diagnostics, lastResult: 'no-update' };
        return getOtaDiagnostics();
      }

      const fetched = await api.fetchUpdateAsync();
      const pending = fetched.isNew === true;
      diagnostics = {
        ...diagnostics,
        pending,
        lastResult: pending ? 'downloaded' : 'already-downloaded',
      };
      return getOtaDiagnostics();
    } catch (error) {
      diagnostics = {
        ...diagnostics,
        lastResult: 'error',
        lastError: message(error),
      };
      return getOtaDiagnostics();
    } finally {
      console.info('[OTA]', diagnostics);
      inFlight = null;
    }
  })();
  return inFlight;
}

/** Starts at most one check for this JS process and never reloads the app. */
export function startBackgroundUpdateCheck(
  api: UpdatesApi,
  release: ReleaseMetadata = { applicationVersion: null, versionCode: null, channel: null },
): Promise<OtaDiagnostics> {
  if (inFlight) return inFlight;
  if (started) return Promise.resolve(getOtaDiagnostics());
  started = true;

  diagnostics = {
    ...diagnostics,
    ...release,
    runtimeVersion: api.runtimeVersion,
    runningUpdateId: api.updateId,
    embeddedUpdateId: api.isEmbeddedLaunch ? api.updateId : null,
    isEmbedded: api.isEmbeddedLaunch,
    lastResult: api.isEnabled ? 'checking' : 'disabled',
    lastError: null,
  };

  console.info('[TV_BOOT]', {
    applicationVersion: diagnostics.applicationVersion,
    versionCode: diagnostics.versionCode,
    runtimeVersion: diagnostics.runtimeVersion,
    channel: diagnostics.channel,
    updateId: diagnostics.runningUpdateId,
    embeddedLaunch: diagnostics.isEmbedded,
  });

  if (!api.isEnabled) {
    console.info('[OTA]', diagnostics);
    return Promise.resolve(getOtaDiagnostics());
  }

  return runUpdateCycle(api);
}

function resultFromDiagnostics(current: OtaDiagnostics): ManualCheckResult {
  if (current.pending) return { state: 'ota-ready' };
  if (current.lastResult === 'error') {
    return { state: 'error', message: current.lastError ?? 'update check failed' };
  }
  return { state: 'up-to-date' };
}

/**
 * The user pressed "check for updates".
 *
 * Deliberately NOT gated by `started`: a no-update answer from boot is not an
 * answer to a question asked minutes later. What it IS gated by is the shared
 * in-flight operation — a check running right now is joined, never duplicated.
 * An already-downloaded update short-circuits: expo-updates has the bytes, and
 * fetching them twice buys nothing.
 */
export async function checkForUpdateNow(api: UpdatesApi): Promise<ManualCheckResult> {
  if (!api.isEnabled) return { state: 'disabled' };
  if (inFlight) return resultFromDiagnostics(await inFlight);
  if (diagnostics.pending) return { state: 'ota-ready' };
  return resultFromDiagnostics(await runUpdateCycle(api));
}

/**
 * The user pressed "install now". The ONLY reload in the application.
 *
 * A successful `reloadAsync()` is TERMINAL for this JS execution path: the
 * runtime is restarting and no continuation is guaranteed to run, so nothing
 * meaningful happens after the await. `applying` is never cleared on success,
 * which is what makes a repeated press a no-op rather than a second reload.
 */
export function applyPendingUpdate(api: UpdatesApi): Promise<ApplyResult> {
  if (applying) return applying;
  applying = (async (): Promise<ApplyResult> => {
    try {
      await api.reloadAsync();
      return { state: 'reloading' };
    } catch (error) {
      // The runtime refused to restart, so this path is still live and the user
      // may retry.
      applying = null;
      return { state: 'error', message: message(error) };
    }
  })();
  return applying;
}

/** Test-only reset; not used by application code. */
export function resetUpdateLifecycleForTests(): void {
  started = false;
  inFlight = null;
  applying = null;
  diagnostics = {
    applicationVersion: null, versionCode: null, channel: null,
    runtimeVersion: null, runningUpdateId: null, embeddedUpdateId: null,
    isEmbedded: false, pending: false, lastResult: 'idle', lastError: null,
  };
}
