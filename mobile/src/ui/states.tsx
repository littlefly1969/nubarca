// State primitives: empty, error, loading, and the one primary action.
//
// Normalised onto the Component Language (BRAND-APP-02 §G). An empty state is a
// heading, one sentence and at most ONE dominant action; an error is clearly
// destructive without turning the screen red; loading is one restrained
// indicator rather than a piece of theatre.
//
// The actions here are the shared `Button`, not local Pressables: three
// different ideas of what a primary action looks like is how a product stops
// looking like one product.

import React from 'react';
import { ActivityIndicator, StyleSheet, Text, View } from 'react-native';
import { spacing, typography } from './tokens';
import { useI18n } from '../i18n';
import { Button } from './components';
import { themed, useColors } from '../ui/theme';

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
        <View style={styles.action}>
          <Button label={t('common.retry')} onPress={onRetry} />
        </View>
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

/**
 * One accent-filled action per view.
 *
 * Kept as a name so its call sites do not have to change in this slice, but it
 * is now the shared `Button` — there is only one primary action in the product.
 */
export function PrimaryButton({
  label,
  onPress,
  disabled = false,
}: {
  label: string;
  onPress: () => void;
  disabled?: boolean;
}): React.JSX.Element {
  return <Button label={label} onPress={onPress} disabled={disabled} />;
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
    // The heading is a real heading role, so an empty screen reads like part of
    // the product rather than like a paragraph that lost its page.
    emptyTitle: {
      ...typography.sectionTitle,
      color: colors.textPrimary,
      textAlign: 'center',
    },
    emptyHint: {
      ...typography.secondary,
      color: colors.textSecondary,
      marginTop: spacing.s,
      textAlign: 'center',
    },
    action: { marginTop: spacing.l },
  }),
);
