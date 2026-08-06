import { useCallback, useLayoutEffect, useState } from 'react';
import {
  Pressable,
  StyleSheet,
  View,
  type StyleProp,
  type ViewStyle,
} from 'react-native';
import { colors } from '../theme';
import type { TvMediaFocusTargets } from '../lib/mediaGridFocus';

interface Props {
  onSelect: () => void;
  children: React.ReactNode;
  style?: StyleProp<ViewStyle>;
  accessibilityLabel?: string;
  hasTVPreferredFocus?: boolean;
  onFocusChange?: (focused: boolean) => void;
  focusable?: boolean;
  focusTargets?: TvMediaFocusTargets;
}

// Media-only focus surface. Unlike the generic FocusableTile, its focus rings
// overlay the preview instead of reserving twelve layout pixels around every
// unfocused item. This keeps the strong double-ring TV focus cue while allowing
// adjacent photos/posters to use almost all of their justified tile box.
export function FocusableMediaTile({
  onSelect,
  children,
  style,
  accessibilityLabel,
  hasTVPreferredFocus,
  onFocusChange,
  focusable = true,
  focusTargets,
}: Props) {
  const [focused, setFocused] = useState(false);
  const [resolvedTargets, setResolvedTargets] = useState<{
    left?: View;
    right?: View;
    up?: View;
    down?: View;
  }>({});

  const refreshFocusTargets = useCallback(() => {
    const next = {
      left: focusTargets?.left?.current ?? undefined,
      right: focusTargets?.right?.current ?? undefined,
      up: focusTargets?.up?.current ?? undefined,
      down: focusTargets?.down?.current ?? undefined,
    };
    setResolvedTargets((current) => (
      current.left === next.left
      && current.right === next.right
      && current.up === next.up
      && current.down === next.down
        ? current
        : next
    ));
  }, [focusTargets]);

  // Native refs are attached before layout effects. Resolve once after every
  // row mount and once more when this tile receives focus, so virtualized rows
  // mounted later are available before the next D-pad move.
  useLayoutEffect(refreshFocusTargets, [refreshFocusTargets]);

  return (
    <Pressable
      ref={focusTargets?.self}
      accessibilityRole="button"
      accessibilityLabel={accessibilityLabel}
      focusable={focusable}
      hasTVPreferredFocus={hasTVPreferredFocus}
      nextFocusLeft={resolvedTargets.left}
      nextFocusRight={resolvedTargets.right}
      nextFocusUp={resolvedTargets.up}
      nextFocusDown={resolvedTargets.down}
      onFocus={() => {
        refreshFocusTargets();
        setFocused(true);
        onFocusChange?.(true);
      }}
      onBlur={() => { setFocused(false); onFocusChange?.(false); }}
      onPress={onSelect}
      style={[styles.tile, style, focused && styles.tileFocused]}
    >
      {children}
      {focused && (
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
