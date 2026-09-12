import { ActivityIndicator, StyleSheet, Text, View } from 'react-native';
import { colors, font, spacing } from '../theme';
import { useI18n } from '../i18n';

// The shell's own picture of a party, drawn natively.
//
// It is what the room sees whenever the canonical stage is not allowed to be
// seen: while a takeover is bootstrapping, while a renderer is being replaced,
// while a grant is being renewed, while the fallback waits for a probe to prove
// itself, and when the assigned party cannot be shown at all. One component for
// all of them, on the stage's own dark ground, so a change of programme looks
// like a change of programme — never a white page, a black one, or a browser
// error — and the words say something true.

interface Props {
  /** The assigned party's name, when the server gave one. */
  readonly albumName?: string | null;
  /** One honest sentence about what is happening, or nothing. */
  readonly message?: string | null;
  /** Show the activity indicator: something is actively being tried. */
  readonly busy?: boolean;
  /** Cover whatever is behind (a loading WebView) rather than taking layout space. */
  readonly overlay?: boolean;
  readonly testID?: string;
}

export const PARTY_SURFACE_BACKGROUND = '#070a12';

export function PartyNativeSurface({
  albumName = null, message = null, busy = false, overlay = false, testID,
}: Props) {
  const { t } = useI18n();
  return (
    <View
      style={[styles.surface, overlay && styles.overlay]}
      testID={testID}
      pointerEvents="none"
    >
      <Text style={styles.brand}>{t('partyDisplay.brand')}</Text>
      {albumName ? <Text style={styles.album} numberOfLines={2}>{albumName}</Text> : null}
      {message ? <Text style={styles.message}>{message}</Text> : null}
      {busy ? <ActivityIndicator size="large" color={colors.accent} style={styles.spinner} /> : null}
    </View>
  );
}

const styles = StyleSheet.create({
  surface: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: PARTY_SURFACE_BACKGROUND,
    padding: spacing.xl,
    gap: spacing.md,
  },
  overlay: { position: 'absolute', top: 0, right: 0, bottom: 0, left: 0 },
  brand: {
    color: '#9db8ff', fontSize: font.body, fontWeight: '800',
    letterSpacing: 4, textTransform: 'uppercase',
  },
  album: {
    color: colors.text, fontSize: font.heading, fontWeight: '700', textAlign: 'center', maxWidth: 1000,
  },
  message: {
    color: colors.muted, fontSize: font.body, textAlign: 'center', maxWidth: 900,
  },
  spinner: { marginTop: spacing.md },
});
