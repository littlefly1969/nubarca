import { StyleSheet, Text, View } from 'react-native';
import { colors, font, spacing } from '../theme';
import { AuthedImage } from './AuthedImage';
import { useI18n } from '../i18n';

// Shared face-filter indicator, shown inside the MENU overlay of BOTH the album
// grid and the slideshow while face-filter mode is active:
//
//   [face thumbnail]  Photos with this person
//                     Album name
//
// Fixed thumbnail size, never focusable (pointerEvents none), long album names
// ellipsized. The thumbnail is the small detected-face crop served through the
// TV-scoped endpoint — never the guest's full selfie, never an original, and no
// names/scores/identity data.
const THUMB_SIZE = 56;

export function FaceFilterIndicator({
  faceThumbnailUrl,
  albumName,
}: {
  faceThumbnailUrl: string | null;
  albumName: string;
}) {
  const { t } = useI18n();
  return (
    <View style={styles.pill} pointerEvents="none">
      {faceThumbnailUrl && (
        <AuthedImage path={faceThumbnailUrl} style={styles.thumb} />
      )}
      <View style={styles.textCol}>
        <Text style={styles.title} numberOfLines={1}>{t('items.facePerson')}</Text>
        <Text style={styles.album} numberOfLines={1}>{albumName}</Text>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  pill: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: spacing.md,
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.xs,
    borderRadius: 14,
    backgroundColor: 'rgba(0,0,0,0.78)',
    maxWidth: '46%',
  },
  thumb: { width: THUMB_SIZE, height: THUMB_SIZE, borderRadius: 10 },
  textCol: { flexShrink: 1 },
  title: { color: colors.text, fontSize: font.body, fontWeight: '700' },
  album: { color: colors.muted, fontSize: font.caption },
});
