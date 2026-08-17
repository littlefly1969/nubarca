import { useCallback, useEffect, useRef, useState } from 'react';
import { ActivityIndicator, StyleSheet, Text, View } from 'react-native';
import { colors, font, spacing } from '../theme';
import { FocusableButton } from '../components/FocusableButton';
import { useI18n, type TvMessageKey } from '../i18n';
import {
  applyDownloadedOtaUpdate,
  checkForOtaUpdateNow,
  getRunningRelease,
  isOtaUpdatePending,
} from '../ota/expoUpdate';
import { decideUpdatePath, type NativeRelease } from '../ota/nativeRelease';
import {
  discardStagedUpdate,
  downloadAndInstallNativeRelease,
  fetchNativeRelease,
  type NativeUpdateFailure,
} from '../ota/nativeUpdate';
import { canRequestPackageInstalls, openPackageInstallSettings } from '../lib/tvPlatform';

// The ONE update surface, reached from mode selection. Two update kinds behind
// one decision:
//
//   * a compatible JS/React update, applied by the existing expo-updates system
//     with a user-triggered reload;
//   * a new native release, downloaded from the same public path a person would
//     sideload from and handed to the platform installer, which asks the user to
//     confirm.
//
// A published native release ALWAYS wins over an OTA: an OTA belongs to the
// runtime the device is leaving. See decideUpdatePath.
//
// Nothing here is owner-private: no PIN, no grant, no /api/tv/personal call, no
// media cache. BACK simply returns to mode selection.

type ScreenState =
  | { name: 'checking' }
  | { name: 'up-to-date' }
  | { name: 'ota-ready' }
  | { name: 'ota-applying' }
  | { name: 'native-available'; release: NativeRelease }
  | { name: 'native-downloading'; release: NativeRelease; fraction: number | null }
  | { name: 'native-needs-permission'; release: NativeRelease }
  | { name: 'native-installer' }
  | { name: 'error'; message: TvMessageKey };

interface Props {
  baseUrl: string;
  onBack: () => void;
}

/** Sanitized failure code → one user-readable line. Never a native message. */
function errorMessage(code: NativeUpdateFailure): TvMessageKey {
  switch (code) {
    case 'permission-required':
      return 'updates.error.permission';
    case 'download-failed':
      return 'updates.error.download';
    case 'not-newer':
      return 'updates.error.notNewer';
    case 'invalid-file':
    case 'hash-mismatch':
    case 'wrong-package':
    case 'signer-mismatch':
      return 'updates.error.invalid';
    case 'installer-rejected':
      return 'updates.error.installer';
    case 'installer-unavailable':
      return 'updates.error.unavailable';
  }
}

export function UpdateScreen({ baseUrl, onBack }: Props) {
  const { t } = useI18n();
  const [state, setState] = useState<ScreenState>({ name: 'checking' });
  const release = getRunningRelease();
  const installedVersionCode = release.versionCode ?? 0;
  // Live-guards every async continuation, so a screen the user has already left
  // never writes state — and so a download in progress is never deleted from
  // under itself on unmount.
  const mounted = useRef(true);
  const downloading = useRef(false);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      if (!downloading.current) discardStagedUpdate();
    };
  }, []);

  const check = useCallback(async () => {
    setState({ name: 'checking' });

    // The native descriptor is evaluated FIRST, and only when this build knows
    // its own package and channel: without both there is nothing to validate a
    // descriptor against, and guessing is not an option on an install path.
    const identity = release.packageName && release.channel
      ? { package: release.packageName, channel: release.channel }
      : null;
    const descriptor = identity ? await fetchNativeRelease(baseUrl, identity) : null;
    if (!mounted.current) return;

    // ONE decision, and it runs BEFORE any OTA is offered. An update already
    // downloaded for the runtime this device is leaving does not change it.
    const published = descriptor?.ok ? descriptor.release : null;
    if (decideUpdatePath(published, installedVersionCode, isOtaUpdatePending()) === 'native'
        && published) {
      setState({ name: 'native-available', release: published });
      return;
    }

    // Either the installed native release is current, or the descriptor is
    // stale/absent. Both leave the OTA flow for the RUNNING runtime.
    const result = await checkForOtaUpdateNow();
    if (!mounted.current) return;
    switch (result.state) {
      case 'ota-ready':
        setState({ name: 'ota-ready' });
        return;
      case 'up-to-date':
        setState({ name: 'up-to-date' });
        return;
      case 'disabled':
        setState({ name: 'error', message: 'updates.error.unavailable' });
        return;
      case 'error':
        setState({ name: 'error', message: 'updates.error.check' });
    }
  }, [baseUrl, installedVersionCode, release.channel, release.packageName]);

  useEffect(() => {
    void check();
  }, [check]);

  // "Installa ora". applyPendingUpdate is idempotent, so a repeated press while
  // the reload is being accepted cannot fire a second reload. Nothing meaningful
  // happens after a successful reload — the runtime is restarting.
  const installOta = useCallback(async () => {
    setState({ name: 'ota-applying' });
    const result = await applyDownloadedOtaUpdate();
    if (result.state === 'error' && mounted.current) {
      setState({ name: 'error', message: 'updates.error.check' });
    }
  }, []);

  const installNative = useCallback(async (target: NativeRelease) => {
    // Checked before spending a whole APK download on it. The native side
    // checks again and refuses regardless — this only saves the bytes.
    if (!(await canRequestPackageInstalls())) {
      if (mounted.current) setState({ name: 'native-needs-permission', release: target });
      return;
    }
    if (!mounted.current) return;

    downloading.current = true;
    setState({ name: 'native-downloading', release: target, fraction: null });
    const result = await downloadAndInstallNativeRelease(
      baseUrl, target, installedVersionCode,
      (fraction) => {
        if (mounted.current) setState({ name: 'native-downloading', release: target, fraction });
      },
    );
    downloading.current = false;
    if (!mounted.current) {
      discardStagedUpdate();
      return;
    }
    if (result.ok) {
      setState({ name: 'native-installer' });
      return;
    }
    setState(result.code === 'permission-required'
      ? { name: 'native-needs-permission', release: target }
      : { name: 'error', message: errorMessage(result.code) });
  }, [baseUrl, installedVersionCode]);

  const authorize = useCallback(async (target: NativeRelease) => {
    await openPackageInstallSettings();
    // The user grants the permission on a Fire OS screen we do not control and
    // comes back here; pressing install again re-checks it.
    if (mounted.current) setState({ name: 'native-available', release: target });
  }, []);

  const percent = state.name === 'native-downloading' && state.fraction !== null
    ? Math.round(state.fraction * 100)
    : null;

  return (
    <View style={styles.container}>
      <Text style={styles.title}>{t('updates.title')}</Text>

      <View style={styles.status}>
        {(state.name === 'checking' || state.name === 'ota-applying'
          || state.name === 'native-downloading') && (
          <ActivityIndicator size="large" color={colors.accent} />
        )}

        {state.name === 'checking' && <Text style={styles.line}>{t('updates.checking')}</Text>}
        {state.name === 'up-to-date' && <Text style={styles.line}>{t('updates.upToDate')}</Text>}
        {state.name === 'ota-ready' && <Text style={styles.line}>{t('updates.otaReady')}</Text>}
        {state.name === 'ota-applying' && <Text style={styles.line}>{t('updates.otaApplying')}</Text>}
        {state.name === 'native-installer' && (
          <Text style={styles.line}>{t('updates.installerHandoff')}</Text>
        )}
        {state.name === 'error' && <Text style={styles.error}>{t(state.message)}</Text>}

        {(state.name === 'native-available' || state.name === 'native-needs-permission') && (
          <>
            <Text style={styles.line}>{t('updates.nativeAvailable')}</Text>
            <Text style={styles.detail}>
              {t('updates.versionChange', {
                current: release.version ?? '—',
                available: state.release.version,
              })}
            </Text>
          </>
        )}
        {state.name === 'native-needs-permission' && (
          <Text style={styles.notice}>{t('updates.needsPermission')}</Text>
        )}
        {state.name === 'native-downloading' && (
          <Text style={styles.line}>
            {percent === null
              ? t('updates.downloading')
              : t('updates.downloadingPercent', { percent })}
          </Text>
        )}
      </View>

      <View style={styles.actions}>
        {state.name === 'ota-ready' && (
          <FocusableButton
            label={t('updates.installNow')}
            onPress={() => { void installOta(); }}
            hasTVPreferredFocus
          />
        )}
        {state.name === 'native-available' && (
          <FocusableButton
            label={t('updates.downloadAndInstall')}
            onPress={() => { void installNative(state.release); }}
            hasTVPreferredFocus
          />
        )}
        {state.name === 'native-needs-permission' && (
          <FocusableButton
            label={t('updates.authorize')}
            onPress={() => { void authorize(state.release); }}
            hasTVPreferredFocus
          />
        )}
        {state.name === 'error' && (
          <FocusableButton
            label={t('common.tryAgain')}
            onPress={() => { void check(); }}
            hasTVPreferredFocus
          />
        )}
        {(state.name === 'up-to-date' || state.name === 'native-installer') && (
          <FocusableButton
            label={t('updates.checkAgain')}
            onPress={() => { void check(); }}
            hasTVPreferredFocus
          />
        )}
        <FocusableButton label={t('updates.back')} onPress={onBack} />
      </View>

      {/* Non-secret release identity: what a support conversation actually
          needs, and nothing a share or a log could not already carry. */}
      <View style={styles.diagnostics}>
        <Text style={styles.diagnostic}>
          {t('updates.currentVersion')}: {release.version ?? '—'} ({release.versionCode ?? '—'})
        </Text>
        <Text style={styles.diagnostic}>
          {t('updates.runtime')}: {release.runtimeVersion ?? '—'}
        </Text>
        <Text style={styles.diagnostic}>
          {t('updates.updateId')}: {release.updateId ?? '—'}
        </Text>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: colors.bg,
    padding: spacing.xl,
    gap: spacing.lg,
  },
  title: { color: colors.text, fontSize: font.heading, fontWeight: '700', textAlign: 'center' },
  status: { alignItems: 'center', gap: spacing.sm, minHeight: 140, justifyContent: 'center' },
  line: { color: colors.text, fontSize: font.body, textAlign: 'center', maxWidth: 720 },
  detail: { color: colors.accent, fontSize: font.body, fontWeight: '700', textAlign: 'center' },
  notice: { color: colors.muted, fontSize: font.caption, textAlign: 'center', maxWidth: 720 },
  error: { color: colors.danger, fontSize: font.body, textAlign: 'center', maxWidth: 720 },
  actions: { gap: spacing.md, minWidth: 420 },
  diagnostics: { alignItems: 'center', gap: spacing.xs },
  diagnostic: { color: colors.muted, fontSize: font.caption },
});
