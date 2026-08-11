import { useState } from 'react';
import {
  Pressable,
  StyleSheet,
  View,
  type StyleProp,
  type ViewStyle,
} from 'react-native';
import { colors } from '../theme';

interface Props {
  onSelect: () => void;
  children: React.ReactNode;
  style?: StyleProp<ViewStyle>;
  accessibilityLabel?: string;
  hasTVPreferredFocus?: boolean;
  onFocusChange?: (focused: boolean) => void;
  focusable?: boolean;
}

// Media-only focus surface. Unlike the generic FocusableTile, its focus rings
// overlay the preview instead of reserving twelve layout pixels around every
// unfocused item, so adjacent photos/posters use almost all of their tile box.
//
// Directional focus deliberately stays native. react-native-tvos wraps each
// virtualized list in a focus guide and keeps the last-focused viewport alive;
// attaching JS-managed native tags here races row virtualization under key
// repeat and creates stale destinations.
export function FocusableMediaTile({
  onSelect,
  children,
  style,
  accessibilityLabel,
  hasTVPreferredFocus,
  onFocusChange,
  focusable = true,
}: Props) {
  const [focused, setFocused] = useState(false);

  // A tile that is not a focus destination must not keep painting a focus ring.
  // While a command rail or panel owns focus the whole grid is switched to
  // non-focusable, and exactly one element on screen may look focused.
  const showFocusRing = focused && focusable;

  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={accessibilityLabel}
      focusable={focusable}
      hasTVPreferredFocus={hasTVPreferredFocus}
      onFocus={() => { setFocused(true); onFocusChange?.(true); }}
      onBlur={() => { setFocused(false); onFocusChange?.(false); }}
      onPress={onSelect}
      style={[styles.tile, style, showFocusRing && styles.tileFocused]}
    >
      {children}
      {showFocusRing && (
        <View pointerEvents="none" style={styles.outerRing}>
          <View style={styles.innerRing} />
        </View>
      )}
    </Pressable>
  );
}

const styles = StyleSheet.create({
  tile: {
    position: 'relative',
    borderRadius: 10,
    overflow: 'visible',
    backgroundColor: colors.panel,
  },
  tileFocused: {
    backgroundColor: colors.panelFocused,
    zIndex: 2,
  },
  outerRing: {
    position: 'absolute',
    top: 0,
    right: 0,
    bottom: 0,
    left: 0,
    borderRadius: 10,
    borderWidth: 3,
    borderColor: '#ffffff',
  },
  innerRing: {
    position: 'absolute',
    top: 3,
    right: 3,
    bottom: 3,
    left: 3,
    borderRadius: 7,
    borderWidth: 2,
    borderColor: colors.accent,
  },
});
