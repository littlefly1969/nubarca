import { useEffect } from 'react';
import { BackHandler, StyleSheet, Text, View } from 'react-native';
import { colors, font, spacing } from '../theme';
import { FocusableButton } from '../components/FocusableButton';
import { useI18n } from '../i18n';
import type { PersonalHomeInfo } from '../personal/flow';

interface Props {
  home: PersonalHomeInfo;
  onOpenGallery: () => void;
  onOpenVideos: () => void;
  // BACK from the Personal Area root: LOCK immediately (revoke the grant) and
  // return to mode selection. Also triggered by the explicit lock button.
  onLock: () => void;
}

// Personal Area home shell. This slice contains only the Gallery entry; it
// proves the authenticated boundary (reachable only with a live unlock grant)
// and the navigation/lock lifecycle. Initial focus is EXPLICITLY on Gallery.
export function PersonalHomeScreen({ home, onOpenGallery, onOpenVideos, onLock }: Props) {
  const { t } = useI18n();

  useEffect(() => {
    const onBackPress = () => {
      onLock();
      return true;
    };
    const sub = BackHandler.addEventListener('hardwareBackPress', onBackPress);
    return () => sub.remove();
  }, [onLock]);

  return (
    <View style={styles.container}>
      <Text style={styles.title}>{t('personal.title')}</Text>
      <Text style={styles.owner}>{home.displayName}</Text>
      <View style={styles.options}>
        {home.galleryAvailable && (
          <FocusableButton label={t('personal.gallery')} onPress={onOpenGallery} hasTVPreferredFocus />
        )}
        {home.galleryAvailable && (
          <FocusableButton label={t('personal.videos')} onPress={onOpenVideos} />
        )}
        <FocusableButton
          label={t('personal.lock')}
          onPress={onLock}
          hasTVPreferredFocus={!home.galleryAvailable}
        />
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
    gap: spacing.md,
  },
  title: { color: colors.text, fontSize: font.heading, fontWeight: '700' },
  owner: { color: colors.muted, fontSize: font.body, marginBottom: spacing.sm },
  options: { gap: spacing.md, minWidth: 420 },
});
