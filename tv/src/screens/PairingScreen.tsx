import { useEffect, useRef, useState } from 'react';
import { ActivityIndicator, Image, StyleSheet, Text, View } from 'react-native';
import { colors, font, spacing } from '../theme';
import { startTvPairing, getTvPairingStatus, type TvPairingStarted } from '../api/tv';
import { QrCode } from '../components/QrCode';
import { FocusableButton } from '../components/FocusableButton';
import { useI18n } from '../i18n';

type State =
  | { kind: 'starting' }
  | { kind: 'pairing'; pairing: TvPairingStarted }
  | { kind: 'expired' }
  | { kind: 'error' };

interface Props {
  onPaired: () => void;
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
  const paired = useRef(false);

  const begin = () => {
    setState({ kind: 'starting' });
    startTvPairing()
      .then((pairing) => setState({ kind: 'pairing', pairing }))
      .catch(() => setState({ kind: 'error' }));
  };

  useEffect(() => {
    begin();
  }, []);

  useEffect(() => {
    if (state.kind !== 'pairing') return;
    const { publicCode, pairingSecret } = state.pairing;
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const poll = async () => {
      try {
        const status = await getTvPairingStatus(publicCode, pairingSecret);
        if (stopped) return;
        if (status.status === 'paired') {
          paired.current = true;
          onPaired();
          return;
        }
        if (status.status === 'expired') {
          setState({ kind: 'expired' });
          return;
        }
      } catch {
        // Transient poll failure: the server deadline stays authoritative.
      }
      if (!stopped) timer = setTimeout(poll, 2000);
    };
    timer = setTimeout(poll, 1000);
    return () => {
      stopped = true;
      if (timer) clearTimeout(timer);
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
