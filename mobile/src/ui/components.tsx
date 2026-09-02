// Small UI primitives shared by every screen. Deliberately minimal: tokens +
// a handful of repeated shapes. Platform-native behavior over bespoke
// animation; every icon-only control REQUIRES an accessibility label.

import React from 'react';
import {
  Pressable,
  StyleSheet,
  Text,
  View,
  type StyleProp,
  type ViewStyle,
} from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { radius, spacing, touch, typography } from './tokens';
import { themed, useColors } from '../ui/theme';

// ---------------------------------------------------------------------------
// Screen: the standard page surface with safe-area padding.
export function Screen({
  children,
  style,
}: {
  children: React.ReactNode;
  style?: StyleProp<ViewStyle>;
}): React.JSX.Element {
  const styles = useStyles();
  const insets = useSafeAreaInsets();
  return (
    <View
      style={[
        styles.screen,
        {
          paddingTop: insets.top,
          paddingBottom: insets.bottom,
          paddingLeft: insets.left,
          paddingRight: insets.right,
        },
        style,
      ]}
    >
      {children}
    </View>
  );
}

// AppHeader: title + optional trailing actions row.
export function AppHeader({
  title,
  actions,
}: {
  title: string;
  actions?: React.ReactNode;
}): React.JSX.Element {
  const styles = useStyles();
  return (
    <View style={styles.header}>
      <Text style={styles.headerTitle} numberOfLines={1} ellipsizeMode="tail">
        {title}
      </Text>
      {actions !== undefined && <View style={styles.headerActions}>{actions}</View>}
    </View>
  );
}

export function HeaderButton({
  label,
  onPress,
  disabled = false,
  destructive = false,
}: {
  label: string;
  onPress: () => void;
  disabled?: boolean;
  destructive?: boolean;
}): React.JSX.Element {
  const styles = useStyles();
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={label}
      onPress={onPress}
      disabled={disabled}
      style={({ pressed }) => [
        styles.headerBtn,
        (pressed || disabled) && styles.headerBtnPressed,
      ]}
      hitSlop={6}
    >
      <Text
        style={[
          styles.headerBtnText,
          destructive && styles.headerBtnDestructive,
          disabled && styles.headerBtnDisabled,
        ]}
      >
        {label}
      </Text>
    </Pressable>
  );
}

// SectionTitle for grouped content.
export function SectionTitle({ text }: { text: string }): React.JSX.Element {
  const styles = useStyles();
  return (
    <Text style={styles.sectionTitle}>{text}</Text>
  );
}

const useStyles = themed((colors) =>
  StyleSheet.create({
    screen: {
      flex: 1,
      backgroundColor: colors.canvas,
    },
    header: {
      minHeight: touch.minSize,
      paddingHorizontal: spacing.l,
      paddingVertical: spacing.s + spacing.xs,
      flexDirection: 'row',
      alignItems: 'center',
      justifyContent: 'space-between',
      backgroundColor: colors.canvas,
      borderBottomWidth: StyleSheet.hairlineWidth,
      borderBottomColor: colors.separator,
    },
    headerTitle: {
      ...typography.pageTitle,
      color: colors.textPrimary,
      flexShrink: 1,
    },
    headerActions: {
      flexDirection: 'row',
      alignItems: 'center',
      gap: spacing.s,
      marginLeft: spacing.m,
    },
    headerBtn: {
      minHeight: touch.minSize - 8,
      paddingHorizontal: spacing.m,
      borderRadius: radius.control,
      justifyContent: 'center',
      backgroundColor: colors.surfaceMuted,
    },
    headerBtnPressed: { opacity: 0.7 },
    headerBtnText: {
      ...typography.label,
      color: colors.accent,
    },
    headerBtnDestructive: { color: colors.danger },
    headerBtnDisabled: { color: colors.textTertiary },
    sectionTitle: {
      ...typography.sectionTitle,
      color: colors.textPrimary,
      paddingHorizontal: spacing.l,
      marginTop: spacing.l,
      marginBottom: spacing.s,
    },
  }),
);

// ---------------------------------------------------------------------------
// Component Language primitives.
//
// These exist so a screen expresses INTENT — primary, danger, connected — and
// the brand decides what that looks like. A component that received a colour
// would be a component the brand cannot change.
//
// This slice introduces them; it does not migrate the existing screens onto
// them. New and changed identity surfaces use these.

export type ButtonVariant = 'primary' | 'secondary' | 'quiet' | 'danger';

/**
 * One action, named by the part it plays.
 *
 * `primary` is the ONE dominant action of a local decision — it fills, and the
 * fill is Electric Blue itself rather than the lighter accent tint, because
 * white clears AA on the fill and does not on the tint (BRAND-CONTRAST-01).
 *
 * Disabled is not communicated by opacity alone: the label takes the muted
 * text role, which stays legible, instead of fading the whole control until it
 * reads as a rendering fault.
 */
export function Button({
  label,
  onPress,
  variant = 'primary',
  disabled = false,
  accessibilityLabel,
}: {
  label: string;
  onPress: () => void;
  variant?: ButtonVariant;
  disabled?: boolean;
  accessibilityLabel?: string;
}): React.JSX.Element {
  const styles = useComponentStyles();
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={accessibilityLabel ?? label}
      accessibilityState={{ disabled }}
      disabled={disabled}
      onPress={onPress}
      style={({ pressed }) => [
        styles.button,
        variant === 'primary' && styles.buttonPrimary,
        variant === 'secondary' && styles.buttonSecondary,
        variant === 'danger' && styles.buttonDanger,
        pressed && !disabled && styles.buttonPressed,
        disabled && styles.buttonDisabled,
      ]}
    >
      <Text
        style={[
          styles.buttonLabel,
          variant === 'primary' && styles.buttonLabelOnFill,
          variant === 'danger' && styles.buttonLabelDanger,
          disabled && styles.buttonLabelDisabled,
        ]}
      >
        {label}
      </Text>
    </Pressable>
  );
}

/**
 * An icon-only control. The hit area is a full touch target whatever the icon
 * measures, and the accessible label is REQUIRED — an icon with no name is a
 * control that does not exist for a screen reader.
 */
export function IconButton({
  accessibilityLabel,
  onPress,
  children,
  selected = false,
  disabled = false,
}: {
  accessibilityLabel: string;
  onPress: () => void;
  children: React.ReactNode;
  selected?: boolean;
  disabled?: boolean;
}): React.JSX.Element {
  const styles = useComponentStyles();
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={accessibilityLabel}
      accessibilityState={{ selected, disabled }}
      disabled={disabled}
      onPress={onPress}
      hitSlop={6}
      style={({ pressed }) => [
        styles.iconButton,
        selected && styles.iconButtonSelected,
        pressed && !disabled && styles.buttonPressed,
      ]}
    >
      {children}
    </Pressable>
  );
}

/**
 * A raised surface. `hero` is for high-scale editorial or large-media
 * compositions only — an ordinary settings row is not a hero.
 */
export function Surface({
  children,
  hero = false,
  style,
}: {
  children: React.ReactNode;
  hero?: boolean;
  style?: StyleProp<ViewStyle>;
}): React.JSX.Element {
  const styles = useComponentStyles();
  return <View style={[styles.surface, hero && styles.surfaceHero, style]}>{children}</View>;
}

export type StatusTone = 'active' | 'connected' | 'intelligence' | 'success' | 'danger' | 'neutral';

/**
 * A status, told by TEXT as well as by colour.
 *
 * Colour alone is not a status (BRAND-FOCUS-01): the label is the status, and
 * the tone only reinforces it. `connected` is Cyan and `intelligence` is
 * Violet because those are their meanings — neither is available as decoration.
 */
export function StatusBadge({
  label,
  tone = 'neutral',
}: {
  label: string;
  tone?: StatusTone;
}): React.JSX.Element {
  const styles = useComponentStyles();
  const colors = useColors();
  const toneColor: Record<StatusTone, string> = {
    active: colors.accent,
    connected: colors.signalConnected,
    intelligence: colors.signalIntelligence,
    success: colors.signalSuccess,
    danger: colors.danger,
    neutral: colors.textTertiary,
  };
  return (
    <View style={styles.badge}>
      <View style={[styles.badgeDot, { backgroundColor: toneColor[tone] }]} />
      <Text style={[styles.badgeLabel, { color: toneColor[tone] }]}>{label}</Text>
    </View>
  );
}

const useComponentStyles = themed((colors) =>
  StyleSheet.create({
    button: {
      minHeight: touch.minSize,
      paddingHorizontal: spacing.l,
      borderRadius: radius.control,
      alignItems: 'center',
      justifyContent: 'center',
      flexDirection: 'row',
      gap: spacing.s,
    },
    // Electric Blue itself: the brand fill, where white clears AA.
    buttonPrimary: { backgroundColor: colors.accentStrong },
    buttonSecondary: {
      backgroundColor: colors.surface,
      borderWidth: StyleSheet.hairlineWidth,
      borderColor: colors.separator,
    },
    buttonDanger: { backgroundColor: colors.dangerSurface },
    // `quiet` deliberately has no container of its own until it is pressed.
    buttonPressed: { opacity: 0.7 },
    buttonDisabled: { backgroundColor: colors.surfaceSubtle },
    buttonLabel: { ...typography.button, color: colors.accent },
    buttonLabelOnFill: { color: colors.textOnAccent },
    buttonLabelDanger: { color: colors.danger },
    buttonLabelDisabled: { color: colors.textTertiary },

    iconButton: {
      minWidth: touch.minSize,
      minHeight: touch.minSize,
      borderRadius: radius.control,
      alignItems: 'center',
      justifyContent: 'center',
    },
    iconButtonSelected: { backgroundColor: colors.accentSubtle },

    surface: {
      backgroundColor: colors.surface,
      borderRadius: radius.card,
      borderWidth: StyleSheet.hairlineWidth,
      borderColor: colors.separator,
      overflow: 'hidden',
    },
    surfaceHero: { borderRadius: radius.hero },

    badge: {
      flexDirection: 'row',
      alignItems: 'center',
      gap: spacing.xs + 2,
      paddingHorizontal: spacing.m,
      paddingVertical: spacing.xs,
      borderRadius: radius.pill,
      backgroundColor: colors.surfaceSubtle,
      alignSelf: 'flex-start',
    },
    badgeDot: { width: 6, height: 6, borderRadius: radius.pill },
    badgeLabel: { ...typography.badge },
  }),
);
