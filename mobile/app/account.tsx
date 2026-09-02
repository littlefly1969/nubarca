// Account: the personal hub behind every gallery (NUBARCA-UX-01 §11).
//
// The hierarchy is `gallery -> account -> preferences`. A gallery should offer
// a person, not a cog: what sits behind it is who you are signed in as, and the
// settings are one of the things that follow from that — not the other way
// round.
//
// This is where account identity, appearance, synchronisation, security and
// sign-out progressively live. It began as the Settings screen and keeps that
// content; the entry point and the framing are what changed.
import React from 'react';
import { Alert, Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { Redirect, router } from 'expo-router';
import { Screen, AppHeader, HeaderButton, SectionTitle } from '../src/ui/components';
import { useSession } from '../src/session/SessionProvider';
import { useI18n } from '../src/i18n';
import { iconSizes, radius, spacing, touch, typography } from '../src/ui/tokens';
import { themed, useColors, useTheme } from '../src/ui/theme';
import { THEME_PREFERENCES, type ThemePreference } from '../src/ui/themePreference.ts';

const THEME_LABELS: Record<ThemePreference, 'settings.theme.dark' | 'settings.theme.light' | 'settings.theme.system'> = {
  dark: 'settings.theme.dark',
  light: 'settings.theme.light',
  system: 'settings.theme.system',
};

const THEME_ICONS: Record<ThemePreference, 'moon-outline' | 'sunny-outline' | 'phone-portrait-outline'> = {
  dark: 'moon-outline',
  light: 'sunny-outline',
  system: 'phone-portrait-outline',
};

export default function Account(): React.JSX.Element {
  const styles = useStyles();
  const colors = useColors();
  const { t } = useI18n();
  const session = useSession();
  const { preference, setPreference } = useTheme();

  if (session.status !== 'authed') {
    return <Redirect href="/login" />;
  }

  return (
    <Screen>
      <AppHeader
        title={t('account.title')}
        actions={<HeaderButton label={t('common.back')} onPress={() => router.back()} />}
      />
      <ScrollView contentContainerStyle={styles.content}>
        {/* Who you are signed in as. The hub's subject, stated first. */}
        <View style={styles.identity}>
          <Ionicons name="person-circle-outline" size={iconSizes.l * 2} color={colors.accent} />
          <View style={styles.identityText}>
            <Text style={styles.identityName} numberOfLines={1}>
              {session.user?.displayName ?? ''}
            </Text>
            <Text style={styles.identityMail} numberOfLines={1}>
              {session.user?.email ?? ''}
            </Text>
          </View>
        </View>

        <SectionTitle text={t('settings.appearance')} />
        {/* Three radio rows rather than a switch: `system` is a real third
            answer, not the absence of a choice, and a two-state control cannot
            say it. The chosen row is announced to the screen reader through
            accessibilityState, not only by the tick. */}
        <View style={styles.group}>
          {THEME_PREFERENCES.map((option) => {
            const selected = option === preference;
            return (
              <Pressable
                key={option}
                accessibilityRole="radio"
                accessibilityState={{ selected }}
                accessibilityLabel={t(THEME_LABELS[option])}
                onPress={() => setPreference(option)}
                style={({ pressed }) => [styles.row, pressed && styles.pressed]}
              >
                <Ionicons
                  name={THEME_ICONS[option]}
                  size={20}
                  color={selected ? colors.accent : colors.textSecondary}
                />
                <Text style={[styles.rowLabel, selected && styles.rowLabelOn]}>
                  {t(THEME_LABELS[option])}
                </Text>
                {selected && (
                  <Ionicons name="checkmark" size={20} color={colors.accent} />
                )}
              </Pressable>
            );
          })}
        </View>
        <Text style={styles.hint}>{t('settings.themeSystemHint')}</Text>

        <SectionTitle text={t('settings.account')} />
        <View style={styles.group}>
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t('common.signOut')}
            onPress={() => {
              Alert.alert(t('common.signOut'), t('common.signOutConfirmBody'), [
                { text: t('albums.cancel'), style: 'cancel' },
                {
                  text: t('common.signOut'),
                  style: 'destructive',
                  onPress: () => void session.logout(),
                },
              ]);
            }}
            style={({ pressed }) => [styles.row, pressed && styles.pressed]}
          >
            <Ionicons name="log-out-outline" size={20} color={colors.danger} />
            <Text style={[styles.rowLabel, styles.destructive]}>{t('common.signOut')}</Text>
          </Pressable>
        </View>
      </ScrollView>
    </Screen>
  );
}

const useStyles = themed((colors) =>
  StyleSheet.create({
      content: { paddingBottom: spacing.xxl },
    identity: {
      flexDirection: 'row',
      alignItems: 'center',
      gap: spacing.m,
      paddingHorizontal: spacing.l,
      paddingTop: spacing.m,
    },
    identityText: { flex: 1 },
    identityName: { ...typography.sectionTitle, color: colors.textPrimary },
    identityMail: { ...typography.secondary, color: colors.textSecondary },
      group: {
        marginHorizontal: spacing.l,
        borderRadius: radius.card,
        backgroundColor: colors.surface,
        borderWidth: StyleSheet.hairlineWidth,
        borderColor: colors.separator,
        overflow: 'hidden',
      },
      row: {
        minHeight: touch.minSize,
        paddingHorizontal: spacing.l,
        paddingVertical: spacing.m,
        flexDirection: 'row',
        alignItems: 'center',
        gap: spacing.m,
      },
      pressed: { backgroundColor: colors.surfaceSubtle },
      rowLabel: { ...typography.body, color: colors.textPrimary, flex: 1 },
      rowLabelOn: { ...typography.label, color: colors.accent },
      destructive: { color: colors.danger },
      hint: {
        ...typography.secondary,
        color: colors.textTertiary,
        paddingHorizontal: spacing.l,
        marginTop: spacing.s,
      },
  }),
);
