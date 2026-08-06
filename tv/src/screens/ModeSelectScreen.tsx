import { StyleSheet, Text, View } from 'react-native';
import { colors, font, spacing } from '../theme';
import { FocusableButton } from '../components/FocusableButton';
import { useI18n } from '../i18n';

interface Props {
  onChooseParty: () => void;
  onChoosePersonal: () => void;
  onChooseBeautyLab: () => void;
  // e.g. "The PIN was changed. Enter the new PIN." after a stale-grant lock.
  notice?: string | null;
}

// Mode selection, shown on EVERY app start after pairing (the previous mode is
// never auto-reopened). Initial focus is EXPLICITLY on Party; the lock glyph is
// part of the Personal label (not a separate focusable element). BACK here
// falls through to the OS default (exit app) — it must never enter a mode.
export function ModeSelectScreen({ onChooseParty, onChoosePersonal, onChooseBeautyLab, notice = null }: Props) {
  const { t } = useI18n();
  return (
    <View style={styles.container}>
      <Text style={styles.title}>{t('mode.title')}</Text>
      {notice !== null && <Text style={styles.notice}>{notice}</Text>}
      <View style={styles.options}>
        <FocusableButton label={t('mode.party')} onPress={onChooseParty} hasTVPreferredFocus />
        <FocusableButton label={t('mode.personal')} onPress={onChoosePersonal} />
        <FocusableButton label={t('mode.beautyLab')} onPress={onChooseBeautyLab} />
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
  notice: { color: colors.muted, fontSize: font.body, textAlign: 'center', maxWidth: 640 },
  options: { gap: spacing.md, minWidth: 420 },
});
