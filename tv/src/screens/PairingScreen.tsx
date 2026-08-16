import { useEffect, useRef, useState } from 'react';
import { ActivityIndicator, Image, StyleSheet, Text, View } from 'react-native';
import { colors, font, spacing } from '../theme';
import { ApiError, ensureSessionPersisted } from '../api/client';
import {
  startTvPairing,
  getTvPairingStatus,
  getTvSession,
  type TvPairingStarted,
  type TvSessionStatus,
} from '../api/tv';
import { QrCode } from '../components/QrCode';
import { FocusableButton } from '../components/FocusableButton';
import { useI18n } from '../i18n';

type State =
  | { kind: 'starting' }
  | { kind: 'pairing'; pairing: TvPairingStarted }
  | { kind: 'expired' }
  | { kind: 'error' };

const PAIRING_REQUEST_TIMEOUT_MS = 10_000;

function timedRequest<T>(operation: (signal: AbortSignal) => Promise<T>) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), PAIRING_REQUEST_TIMEOUT_MS);
  return {
    controller,
    promise: operation(controller.signal).finally(() => clearTimeout(timer)),
  };
}

interface Props {
  onPaired: (session: TvSessionStatus) => void;
  // Shown above the pairing UI, e.g. the "pairing is incomplete" recovery
  // notice when a legacy paired session had no owner PIN.
  notice?: string | null;
}

// Landing/pairing screen: starts a pairing request and polls until the phone
// approves it. QR rendering is a documented follow-up — for the spike we show
// the short code + the approval URL the phone opens.
export function PairingScreen({ onPaired, notice = null }: Props) {
  const { t } = useI18n();
  const [state, setState] = useState<State>({ kind: 'starting' });
  const startController = useRef<AbortController | null>(null);

  const begin = () => {
    startController.current?.abort();
    const request = timedRequest(startTvPairing);
    startController.current = request.controller;
    setState({ kind: 'starting' });
    request.promise
      .then((pairing) => {
        if (startController.current === request.controller) setState({ kind: 'pairing', pairing });
      })
      .catch(() => {
        if (startController.current === request.controller) setState({ kind: 'error' });
      })
      .finally(() => {
        if (startController.current === request.controller) startController.current = null;
      });
  };

  useEffect(() => {
    begin();
    return () => {
      const controller = startController.current;
      startController.current = null;
      controller?.abort();
    };
  }, []);

  useEffect(() => {
    if (state.kind !== 'pairing') return;
    const { publicCode, pairingSecret, expiresAt } = state.pairing;
    const deadline = Date.parse(expiresAt);
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let activeRequest: AbortController | null = null;
    const remainingAtStart = deadline - Date.now();
    if (!Number.isFinite(deadline) || remainingAtStart <= 0) {
      setState({ kind: 'expired' });
      return;
    }
    const withTimeout = async <T,>(operation: (signal: AbortSignal) => Promise<T>): Promise<T> => {
      const request = timedRequest(operation);
      activeRequest = request.controller;
      try {
        return await request.promise;
      } finally {
        if (activeRequest === request.controller) activeRequest = null;
      }
    };
    const expire = () => {
      if (stopped) return;
      stopped = true;
      activeRequest?.abort();
      setState({ kind: 'expired' });
    };
    const deadlineTimer = setTimeout(expire, remainingAtStart);
    const poll = async () => {
      if (stopped) return;
      if (Date.now() >= deadline) {
        expire();
        return;
      }
      try {
        const status = await withTimeout((signal) => (
          getTvPairingStatus(publicCode, pairingSecret, signal)
        ));
        if (stopped) return;
        if (status.status === 'expired') {
          expire();
          return;
        }
        if (status.status === 'paired') {
          const session = await withTimeout((signal) => getTvSession(signal));
          const stillPersisted = await ensureSessionPersisted();
          if (stopped) return;
          if (!stillPersisted()) {
            throw new Error('TV session changed before pairing completed');
          }
          onPaired(session);
          return;
        }
      } catch (error) {
        // A 401 means the one-shot claim cookie was genuinely lost; network or
        // storage failures can reuse this approved pairing until its deadline.
        if (!stopped && error instanceof ApiError && error.status === 401) {
          setState({ kind: 'error' });
          return;
        }
      }
      if (!stopped) timer = setTimeout(poll, 2000);
    };
    timer = setTimeout(poll, 1000);
    return () => {
      stopped = true;
      activeRequest?.abort();
      if (timer) clearTimeout(timer);
      clearTimeout(deadlineTimer);
    };
  }, [state, onPaired]);

  return (
    <View style={styles.container}>
      {/* The approved transparent NubArca TV lockup. Transparent, so it sits
          on the screen's own Midnight Navy with no card edge or seam. The
          product name travels as the accessibility label rather than a
          second visible copy. */}
      <Image
        source={require('../../assets/brand/nubarca-tv-lockup-transparent-1280w.png')}
        style={styles.lockup}
        resizeMode="contain"
        accessible
        accessibilityRole="image"
        accessibilityLabel={t('pairing.title')}
      />
      {notice !== null && <Text style={styles.notice}>{notice}</Text>}

      {state.kind === 'starting' && (
        <>
          <ActivityIndicator size="large" color={colors.accent} />
          <Text style={styles.body}>{t('pairing.preparing')}</Text>
        </>
      )}

      {state.kind === 'pairing' && (
        <>
          <Text style={styles.body}>{t('pairing.scan')}</Text>
          <QrCode value={state.pairing.approvalUrl} size={320} style={styles.qr} />
          <Text style={styles.url}>{state.pairing.approvalUrl.split('#')[0]}</Text>
          <Text style={styles.codeLabel}>{t('pairing.code')}</Text>
          <Text style={styles.code}>{state.pairing.publicCode}</Text>
          <Text style={styles.muted}>{t('pairing.expiresWaiting')}</Text>
        </>
      )}

      {(state.kind === 'expired' || state.kind === 'error') && (
        <>
          <Text style={styles.body}>
            {state.kind === 'expired' ? t('pairing.expired') : t('pairing.unavailable')}
          </Text>
          <View style={styles.retry}>
            <FocusableButton label={t('common.tryAgain')} onPress={begin} hasTVPreferredFocus />
          </View>
        </>
      )}
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
  },
  // 640x148 keeps the approved 4.31:1 lockup proportions exactly; never stretched.
  lockup: { width: 640, height: 148, marginBottom: spacing.lg },
  notice: {
    color: colors.danger,
    fontSize: font.body,
    textAlign: 'center',
    marginBottom: spacing.md,
    maxWidth: 640,
  },
  body: { color: colors.text, fontSize: font.body, marginTop: spacing.md, textAlign: 'center' },
  qr: { marginTop: spacing.md, borderRadius: 12 },
  url: { color: colors.accent, fontSize: font.caption, marginTop: spacing.sm },
  codeLabel: { color: colors.muted, fontSize: font.caption, marginTop: spacing.lg },
  code: { color: colors.text, fontSize: font.code, fontWeight: '700', letterSpacing: 8 },
  muted: { color: colors.muted, fontSize: font.caption, marginTop: spacing.md },
  retry: { marginTop: spacing.lg },
});
