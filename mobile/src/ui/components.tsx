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
import { colors, radii, spacing, touch, type } from './tokens';

// ---------------------------------------------------------------------------
// Screen: the standard page surface with safe-area padding.
export function Screen({
  children,
  style,
}: {
  children: React.ReactNode;
  style?: StyleProp<ViewStyle>;
}): React.JSX.Element {
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
  return (
    <Text style={[type.sectionTitle, styles.sectionTitle]}>{text}</Text>
  );
}

const styles = StyleSheet.create({
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
    ...type.title,
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
    borderRadius: radii.m,
    justifyContent: 'center',
    backgroundColor: colors.surfaceMuted,
  },
  headerBtnPressed: { opacity: 0.7 },
  headerBtnText: {
    fontSize: 14,
    fontWeight: '600',
    color: colors.accent,
  },
  headerBtnDestructive: { color: colors.danger },
  headerBtnDisabled: { color: colors.textTertiary },
  sectionTitle: {
    paddingHorizontal: spacing.l,
    marginTop: spacing.l,
    marginBottom: spacing.s,
  },
});
