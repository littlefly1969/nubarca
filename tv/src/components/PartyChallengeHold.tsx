import { Image, StyleSheet, Text, View } from 'react-native';
import type { TvPartyChallenge } from '../api/tv';
import { resolveTvMediaUrl } from '../api/client';
import { colors, spacing } from '../theme';
import { useI18n } from '../i18n';

export function PartyChallengeHold({ challenge }: { challenge: TvPartyChallenge | null }) {
  const { t } = useI18n();
  if (!challenge) return null;
  const kind = {
    dare: t('challenge.kindDare'), penalty: t('challenge.kindPenalty'),
    guess: t('challenge.kindGuess'), custom: t('challenge.kindCustom'),
  }[challenge.kind];
  return (
    <View style={styles.overlay} pointerEvents="none" testID="party-challenge-hold">
      <View style={styles.card}>
        {challenge.mediaUrl && <Image source={{ uri: resolveTvMediaUrl(challenge.mediaUrl) }} style={styles.image} />}
        <View style={styles.copy}>
          <Text style={styles.eyebrow}>{kind}</Text>
          <Text style={styles.title} numberOfLines={2}>{challenge.title}</Text>
          <Text style={styles.body} numberOfLines={6}>{challenge.body}</Text>
          <Text style={styles.hint}>{t('challenge.nextHint')}</Text>
        </View>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  overlay: { position: 'absolute', top: 0, right: 0, bottom: 0, left: 0,
    zIndex: 40, backgroundColor: '#070a12',
    alignItems: 'center', justifyContent: 'center', paddingHorizontal: 72, paddingVertical: 48 },
  card: { width: '92%', maxWidth: 1420, minHeight: '72%', flexDirection: 'row',
    overflow: 'hidden', borderRadius: 32, borderWidth: 2, borderColor: '#6f8dd8',
    backgroundColor: '#141b2b' },
  image: { width: '42%', height: '100%', resizeMode: 'cover' },
  copy: { flex: 1, padding: spacing.xl * 2, justifyContent: 'center' },
  eyebrow: { color: '#9db8ff', fontSize: 24, fontWeight: '800', letterSpacing: 3,
    textTransform: 'uppercase', marginBottom: spacing.md },
  title: { color: colors.text, fontSize: 58, lineHeight: 66, fontWeight: '900', marginBottom: spacing.lg },
  body: { color: '#e4e9f5', fontSize: 36, lineHeight: 46, fontWeight: '500' },
  hint: { color: '#9ca9bf', fontSize: 22, marginTop: spacing.xl * 2 },
});
