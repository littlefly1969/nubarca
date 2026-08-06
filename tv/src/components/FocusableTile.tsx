import { useState } from 'react';
import { Pressable, StyleSheet, View, type ViewStyle } from 'react-native';
import { colors } from '../theme';

// A D-pad-friendly tile. On TV, the native focus engine moves focus with the
// remote's arrow keys; we track onFocus/onBlur to draw the focus state and fire
// onSelect on Enter/tap.
//
// The focused state must be unmistakable from couch distance WITHOUT relying on
// color alone (color-blind safe), so it stacks several redundant cues built only
// from properties that reliably render on Android TV:
//  - scale-up (the tile visibly grows)
//  - a thick WHITE outer border
//  - a second inner ACCENT ring (double ring, drawn by a nested view)
//  - a brighter panel background
// (No shadow*/outline* props: shadows are iOS-only and outline support on the TV
// fork is unverified — nested borders always render.)
interface Props {
  onSelect: () => void;
  children: React.ReactNode;
  style?: ViewStyle;
  accessibilityLabel?: string;
  // On the TV runtime, requests initial D-pad focus for this tile so the remote
  // lands on a sensible element when a screen opens.
  hasTVPreferredFocus?: boolean;
  // Lets a parent react to focus (e.g. the album grid shows a "7 / 42" position
  // badge on the focused tile only).
  onFocusChange?: (focused: boolean) => void;
  // Set false while a full-screen panel/viewer covers the grid, so DPAD focus
  // cannot escape to a tile hidden underneath. Defaults to focusable.
  focusable?: boolean;
}

export function FocusableTile({
  onSelect,
  children,
  style,
  accessibilityLabel,
  hasTVPreferredFocus,
  onFocusChange,
  focusable = true,
}: Props) {
  const [focused, setFocused] = useState(false);
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={accessibilityLabel}
      focusable={focusable}
      hasTVPreferredFocus={hasTVPreferredFocus}
      onFocus={() => { setFocused(true); onFocusChange?.(true); }}
      onBlur={() => { setFocused(false); onFocusChange?.(false); }}
      onPress={onSelect}
      style={[styles.tile, style, focused && styles.tileFocused]}
    >
      <View style={[styles.inner, focused && styles.innerFocused]}>{children}</View>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  tile: {
    borderRadius: 14,
    // Reserve the border box in both states so focus never shifts layout.
    borderWidth: 4,
    borderColor: 'transparent',
    backgroundColor: colors.panel,
  },
  tileFocused: {
    borderColor: '#ffffff',
    backgroundColor: colors.panelFocused,
    transform: [{ scale: 1.07 }],
  },
  inner: {
    borderRadius: 10,
    borderWidth: 2,
    borderColor: 'transparent',
  },
  innerFocused: {
    borderColor: colors.accent,
  },
});
