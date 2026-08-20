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
  // SELECTED is not FOCUSED, and conflating them is a real TV defect: focus is
  // where the remote happens to be right now, selection is the state the screen
  // is actually in. The media-kind rail used to express the current kind ONLY
  // as initial preferred focus, so the moment the user moved the remote —
  // never mind closed and reopened the menu — nothing on screen said whether
  // they were looking at All, Photos or Videos.
  //
  // Marked with a ✓ AND weight, never colour alone: a washed-out television and
  // a viewer who cannot separate two blues both need this to survive.
  selected?: boolean;
  // Called on focus/interaction — e.g. the slideshow overlay uses it to re-arm
  // its auto-hide timer.
  onFocusChange?: (focused: boolean) => void;
}

export function FocusableButton({
  label, onPress, disabled = false, hasTVPreferredFocus = false, selected = false,
  onFocusChange,
}: Props) {
  const [focused, setFocused] = useState(false);
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={label}
      accessibilityState={{ selected }}
      focusable={!disabled}
      disabled={disabled}
      hasTVPreferredFocus={hasTVPreferredFocus}
      onFocus={() => { setFocused(true); onFocusChange?.(true); }}
      onBlur={() => { setFocused(false); onFocusChange?.(false); }}
      onPress={onPress}
      style={[
        styles.outer,
        disabled && styles.disabled,
        selected && styles.outerSelected,
        focused && styles.outerFocused,
      ]}
    >
      <View style={[styles.inner, focused && styles.innerFocused]}>
        <Text
          style={[styles.label, selected && styles.labelSelected, focused && styles.labelFocused]}
          numberOfLines={1}
        >
          {focused ? '▸ ' : ''}{selected ? '✓ ' : ''}{label}
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
  // Survives focus moving away — that is the whole point.
  outerSelected: { backgroundColor: colors.panelFocused },
  labelSelected: { color: colors.text, fontWeight: '800' },
  disabled: { opacity: 0.35 },
  // font.button (20) — overlay commands must read from couch distance.
  label: { color: colors.text, fontSize: font.button, fontWeight: '600' },
  labelFocused: { fontWeight: '800' },
});
