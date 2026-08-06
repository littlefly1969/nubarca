import { useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import { colors, font, spacing } from '../theme';

// The one TV button. Every focusable button in the app (screen headers, pairing
// retry, slideshow overlay controls) uses this so the focus treatment is
// consistent and NOT color-only:
//  - scale-up
//  - thick WHITE outer border + inner ACCENT ring (double ring, nested views —
//    reliable on Android TV, unlike shadow*/outline*)
//  - brighter background
//  - a ▸ caret marker and a bolder label
interface Props {
  label: string;
  onPress: () => void;
  disabled?: boolean;
  hasTVPreferredFocus?: boolean;
  // Called on focus/interaction — e.g. the slideshow overlay uses it to re-arm
  // its auto-hide timer.
  onFocusChange?: (focused: boolean) => void;
}

export function FocusableButton({
  label, onPress, disabled = false, hasTVPreferredFocus = false, onFocusChange,
}: Props) {
  const [focused, setFocused] = useState(false);
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={label}
      focusable={!disabled}
      disabled={disabled}
      hasTVPreferredFocus={hasTVPreferredFocus}
      onFocus={() => { setFocused(true); onFocusChange?.(true); }}
      onBlur={() => { setFocused(false); onFocusChange?.(false); }}
      onPress={onPress}
      style={[styles.outer, disabled && styles.disabled, focused && styles.outerFocused]}
    >
      <View style={[styles.inner, focused && styles.innerFocused]}>
        <Text style={[styles.label, focused && styles.labelFocused]} numberOfLines={1}>
          {focused ? '▸ ' : ''}{label}
        </Text>
      </View>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  outer: {
    borderRadius: 12,
    // Border box reserved in both states so focus never shifts layout.
    borderWidth: 3,
    borderColor: 'transparent',
    backgroundColor: colors.panel,
  },
  outerFocused: {
    borderColor: '#ffffff',
    backgroundColor: colors.panelFocused,
    transform: [{ scale: 1.1 }],
  },
  inner: {
    borderRadius: 8,
    borderWidth: 2,
    borderColor: 'transparent',
    paddingVertical: spacing.xs,
    paddingHorizontal: spacing.md,
  },
  innerFocused: {
    borderColor: colors.accent,
  },
  disabled: { opacity: 0.35 },
  // font.button (20) — overlay commands must read from couch distance.
  label: { color: colors.text, fontSize: font.button, fontWeight: '600' },
  labelFocused: { fontWeight: '800' },
});
