import { useEffect } from 'react';
import { BackHandler, StyleSheet, Text, View } from 'react-native';
import { colors, font, spacing } from '../theme';
import { FocusableButton } from '../components/FocusableButton';
import { useI18n } from '../i18n';
import type { PersonalHomeInfo } from '../personal/flow';

interface Props {
  home: PersonalHomeInfo;
  onOpenLibrary: () => void;
  onOpenAlbums: () => void;
  // BACK from the Personal Area root: LOCK immediately (revoke the grant) and
  // return to mode selection. Also triggered by the explicit lock button.
  onLock: () => void;
}

// Personal Area home shell:
//
//     Library   — All / Photos / Videos, one unified surface
//     Albums    — the owner's own albums
//     Lock
//
// It used to offer "Gallery" and "Videos" as two separate destinations, which
// was the navigation shape of two independent browsing implementations. One
// Library entry replaces both; the kind tabs live inside it, where they belong.
// Initial focus is EXPLICITLY on Library.
export function PersonalHomeScreen({ home, onOpenLibrary, onOpenAlbums, onLock }: Props) {
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
          <FocusableButton label={t('personal.library')} onPress={onOpenLibrary} hasTVPreferredFocus />
        )}
        {home.galleryAvailable && (
          <FocusableButton label={t('personal.albums')} onPress={onOpenAlbums} />
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
