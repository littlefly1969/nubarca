// State primitives: empty, error, loading, and the one primary action.

import React from 'react';
import {
  ActivityIndicator,
  Pressable,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import { radii, spacing, touch, type } from './tokens';
import { useI18n } from '../i18n';
import { themed, useColors } from '../ui/theme.ts';

export function EmptyState({
  icon,
  title,
  hint,
}: {
  icon?: string;
  title: string;
  hint?: string;
}): React.JSX.Element {
  const styles = useStyles();
  return (
    <View style={styles.centered}>
      {icon !== undefined && <Text style={styles.emptyIcon}>{icon}</Text>}
      <Text style={styles.emptyTitle}>{title}</Text>
      {hint !== undefined && <Text style={styles.emptyHint}>{hint}</Text>}
    </View>
  );
}

export function ErrorState({
  title,
  message,
  onRetry,
}: {
  title: string;
  message?: string | null;
  onRetry?: () => void;
}): React.JSX.Element {
  const styles = useStyles();
  const { t } = useI18n();
  return (
    <View style={styles.centered}>
      <Text style={[styles.emptyIcon, styles.errorIcon]}>⚠️</Text>
      <Text style={styles.emptyTitle}>{title}</Text>
      {message != null && message.length > 0 && (
        <Text style={styles.emptyHint}>{message}</Text>
      )}
      {onRetry !== undefined && (
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t('common.retry')}
          onPress={onRetry}
          style={({ pressed }) => [styles.retryBtn, pressed && styles.pressed]}
        >
          <Text style={styles.retryText}>{t('common.retry')}</Text>
        </Pressable>
      )}
    </View>
  );
}

export function LoadingState(): React.JSX.Element {
  const styles = useStyles();
  const colors = useColors();
  return (
    <View style={styles.centered}>
      <ActivityIndicator size="large" color={colors.accent} />
    </View>
  );
}

// PrimaryButton: one accent-filled action per view.
export function PrimaryButton({
  label,
  onPress,
  disabled = false,
}: {
  label: string;
  onPress: () => void;
  disabled?: boolean;
}): React.JSX.Element {
  const styles = useStyles();
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={label}
      onPress={onPress}
      disabled={disabled}
      style={({ pressed }) => [
        styles.primaryBtn,
        pressed && styles.pressed,
        disabled && styles.primaryBtnDisabled,
      ]}
    >
      <Text
        style={[styles.primaryBtnText, disabled && styles.primaryBtnTextDisabled]}
      >
        {label}
      </Text>
    </Pressable>
  );
}

const useStyles = themed((colors) =>
  StyleSheet.create({
    centered: {
      flex: 1,
      alignItems: 'center',
      justifyContent: 'center',
      padding: spacing.xl,
    },
    emptyIcon: { fontSize: 40, marginBottom: spacing.m },
    errorIcon: { fontSize: 34 },
    emptyTitle: {
      ...type.body,
      color: colors.textPrimary,
      fontWeight: '600',
      textAlign: 'center',
    },
    emptyHint: {
      ...type.secondary,
      color: colors.textSecondary,
      marginTop: spacing.xs,
      textAlign: 'center',
    },
    retryBtn: {
      marginTop: spacing.l,
      backgroundColor: colors.accentStrong,
      borderRadius: radii.m,
      minHeight: touch.minSize - 4,
      justifyContent: 'center',
      paddingHorizontal: spacing.xl,
    },
    retryText: {
      color: colors.textOnAccent,
      fontWeight: '600',
      fontSize: 15,
    },
    pressed: { opacity: 0.75 },
    primaryBtn: {
      backgroundColor: colors.accentStrong,
      borderRadius: radii.m,
      minHeight: touch.minSize,
      justifyContent: 'center',
      alignItems: 'center',
      paddingHorizontal: spacing.xl,
    },
    primaryBtnDisabled: { backgroundColor: colors.accentDisabled },
    primaryBtnText: {
      color: colors.textOnAccent,
      fontWeight: '600',
      fontSize: 15,
    },
    primaryBtnTextDisabled: { color: colors.surface },
  }),
);
