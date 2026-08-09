import { useCallback, useLayoutEffect, useReducer, useState } from 'react';
import {
  Pressable,
  StyleSheet,
  View,
  type StyleProp,
  type ViewStyle,
} from 'react-native';
import { colors } from '../theme';
import type { TvFixedGridTargets } from '../lib/useTvFixedGridFocus';
import { tvDebug } from '../debug';

interface Props {
  onSelect: () => void;
  children: React.ReactNode;
  style?: StyleProp<ViewStyle>;
  accessibilityLabel?: string;
  hasTVPreferredFocus?: boolean;
  onFocusChange?: (focused: boolean) => void;
  focusable?: boolean;
  focusTargets?: TvFixedGridTargets;
  // Flat index, for the development-only unresolved-link diagnostic below.
  index?: number;
}

// Media-only focus surface. Unlike the generic FocusableTile, its focus rings
// overlay the preview instead of reserving twelve layout pixels around every
// unfocused item, so adjacent photos/posters use almost all of their tile box.
//
// TARGET RESOLUTION. `nextFocus*` wants a mounted View, and a virtualized row
// may mount after its neighbour renders. The previous version copied the
// resolved Views into component STATE inside a layout effect, which put a
// second render pass between "a neighbour mounted" and "the native link is
// correct" — one of the two renders a fast auto-repeat could outrun (see
// tvFixedGrid.ts).
//
// Now the Views are read straight from the ref objects during render. A tile
// re-resolves when its row re-renders, when it receives focus, and once per
// layout pass; nothing is copied into state, so no navigation-time render is
// introduced. If a link is still unresolved at press time, the uniform grid's
// geometric fallback names the SAME tile the link would have — which is exactly
// what the fixed-column layout buys and what the justified wall could not offer.
export function FocusableMediaTile({
  onSelect,
  children,
  style,
  accessibilityLabel,
  hasTVPreferredFocus,
  onFocusChange,
  focusable = true,
  focusTargets,
  index,
}: Props) {
  const [focused, setFocused] = useState(false);
  // A bare re-render request. Bumped when this tile takes focus so its links
  // pick up neighbours mounted since the last render. It carries no value, so
  // it can never become a source of stale derived state.
  const [, requestResolve] = useReducer((n: number) => n + 1, 0);

  // One resolve pass after mount, so a row that mounts alongside its neighbours
  // commits real links on the very next frame.
  useLayoutEffect(() => {
    requestResolve();
  }, []);

  const onFocus = useCallback(() => {
    requestResolve();
    setFocused(true);
    onFocusChange?.(true);
    if (__DEV__ && focusTargets !== undefined) {
      // Development diagnostic: an unresolved link is an INVARIANT breach of the
      // render-window sizing, not something to shrug at. It is reported rather
      // than silently tolerated, and it names positions only — never an item id.
      const missing = (['left', 'right', 'up', 'down'] as const).filter(
        (d) => focusTargets[d] !== undefined && focusTargets[d]!.current === null,
      );
      if (missing.length > 0) {
        tvDebug('grid nav', 'unmounted link target at index', index ?? -1, missing.join(','));
      }
    }
  }, [onFocusChange, focusTargets, index]);

  // A tile that is not a focus destination must not keep painting a focus ring.
  // While a command rail or panel owns focus the whole grid is switched to
  // non-focusable, and exactly one element on screen may look focused.
  const showFocusRing = focused && focusable;

  return (
    <Pressable
      ref={focusTargets?.self}
      accessibilityRole="button"
      accessibilityLabel={accessibilityLabel}
      focusable={focusable}
      hasTVPreferredFocus={hasTVPreferredFocus}
      nextFocusLeft={focusTargets?.left?.current ?? undefined}
      nextFocusRight={focusTargets?.right?.current ?? undefined}
      nextFocusUp={focusTargets?.up?.current ?? undefined}
      nextFocusDown={focusTargets?.down?.current ?? undefined}
      onFocus={onFocus}
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
