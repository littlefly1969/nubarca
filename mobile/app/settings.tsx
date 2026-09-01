// Settings: the one place the app keeps choices that are not about media.
//
// It exists because there was no such place. Sign-out was an icon in the Photos
// header, and there was nowhere at all to choose a theme — which is how a
// preference that the web has had all along was simply unreachable on the
// phone. Appearance lives here; the account section starts with sign-out and is
// where the password change will join it.
import React from 'react';
import { Alert, Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { Redirect, router } from 'expo-router';
import { Screen, AppHeader, HeaderButton, SectionTitle } from '../src/ui/components';
import { useSession } from '../src/session/SessionProvider';
import { useI18n } from '../src/i18n';
import { radii, spacing, touch, type } from '../src/ui/tokens';
import { themed, useColors, useTheme } from '../src/ui/theme.ts';
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

export default function Settings(): React.JSX.Element {
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
        title={t('settings.title')}
        actions={<HeaderButton label={t('common.back')} onPress={() => router.back()} />}
      />
      <ScrollView contentContainerStyle={styles.content}>
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
      group: {
        marginHorizontal: spacing.l,
        borderRadius: radii.l,
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
      pressed: { backgroundColor: colors.surfaceMuted },
      rowLabel: { ...type.body, color: colors.textPrimary, flex: 1 },
      rowLabelOn: { fontWeight: '600', color: colors.accent },
      destructive: { color: colors.danger },
      hint: {
        ...type.secondary,
        color: colors.textTertiary,
        paddingHorizontal: spacing.l,
        marginTop: spacing.s,
      },
  }),
);
