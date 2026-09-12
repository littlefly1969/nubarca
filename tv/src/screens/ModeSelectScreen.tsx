import { StyleSheet, Text, View } from 'react-native';
import { colors, font, spacing } from '../theme';
import { FocusableButton } from '../components/FocusableButton';
import { useI18n } from '../i18n';

interface Props {
  onChooseParty: () => void;
  onChoosePersonal: () => void;
  onChooseBeautyLab: () => void;
  onChooseUpdates: () => void;
  // e.g. "The PIN was changed. Enter the new PIN." after a stale-grant lock.
  notice?: string | null;
}

// Mode selection, shown after pairing on a GENERAL television (the previous mode
// is never auto-reopened). A television the owner assigned to a party never
// reaches it: the server's assignment takes the screen instead (flow.ts).
// Initial focus is EXPLICITLY on Party; the lock glyph is part of the Personal
// label (not a separate focusable element). BACK here closes the app — it must
// never enter a mode.
//
// Updates is the fourth entry and deliberately the LAST one: it carries no lock
// glyph because it needs no PIN, and it must not take the initial focus away
// from Party, which is what this TV is normally opened for.
export function ModeSelectScreen({
  onChooseParty, onChoosePersonal, onChooseBeautyLab, onChooseUpdates, notice = null,
}: Props) {
  const { t } = useI18n();
  return (
    <View style={styles.container}>
      <Text style={styles.title}>{t('mode.title')}</Text>
      {notice !== null && <Text style={styles.notice}>{notice}</Text>}
      <View style={styles.options}>
        <FocusableButton label={t('mode.party')} onPress={onChooseParty} hasTVPreferredFocus />
        <FocusableButton label={t('mode.personal')} onPress={onChoosePersonal} />
        <FocusableButton label={t('mode.beautyLab')} onPress={onChooseBeautyLab} />
        <FocusableButton label={t('mode.updates')} onPress={onChooseUpdates} />
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
