import { StyleSheet, Text, View } from 'react-native';
import { colors, font, spacing } from '../theme';
import { FocusableButton } from '../components/FocusableButton';
import { useI18n } from '../i18n';
import type { TvDisplayAssignment } from '../api/tv';

interface Props {
  onChooseParty: () => void;
  onChoosePersonal: () => void;
  onChooseBeautyLab: () => void;
  onChooseUpdates: () => void;
  // e.g. "The PIN was changed. Enter the new PIN." after a stale-grant lock.
  notice?: string | null;
  // What the owner has this television set to. Displayed and nothing more: the
  // assignment does not change what any button here does yet.
  assignment?: TvDisplayAssignment | null;
}

// Mode selection, shown on EVERY app start after pairing (the previous mode is
// never auto-reopened). Initial focus is EXPLICITLY on Party; the lock glyph is
// part of the Personal label (not a separate focusable element). BACK here
// falls through to the OS default (exit app) — it must never enter a mode.
//
// Updates is the fourth entry and deliberately the LAST one: it carries no lock
// glyph because it needs no PIN, and it must not take the initial focus away
// from Party, which is what this TV is normally opened for.
export function ModeSelectScreen({
  onChooseParty, onChoosePersonal, onChooseBeautyLab, onChooseUpdates, notice = null,
  assignment = null,
}: Props) {
  const { t } = useI18n();
  // A television the owner pointed at a party says so. It is a LINE, not a
  // behaviour: nothing here reads the assignment to decide what to show, which
  // stays the server's job and a later slice's work.
  const assignedTo = assignment?.kind === 'party'
    ? (assignment.partyAvailable
      ? t('mode.assignedParty', { name: assignment.albumName ?? t('mode.assignedPartyFallback') })
      : t('mode.assignedPartyGone'))
    : null;
  return (
    <View style={styles.container}>
      <Text style={styles.title}>{t('mode.title')}</Text>
      {assignedTo !== null && <Text style={styles.assignment}>{assignedTo}</Text>}
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
  assignment: { color: colors.text, fontSize: font.body, textAlign: 'center', maxWidth: 640 },
  options: { gap: spacing.md, minWidth: 420 },
});
