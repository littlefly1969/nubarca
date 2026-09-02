// The bottom navigation, in NubArca's own language (BRAND-APP-02 §D).
//
// A custom bar rather than a pile of styling exceptions on the default one:
// the selected state this brand wants — a thin luminous edge, an accent label,
// a quiet surface — is not reachable by overriding tint colours, and every
// attempt to get there through options is a rule written where nobody looks.
//
// WHAT IT DOES NOT DO is the important part. It holds no navigation state of
// its own: the focused index, the labels, the icons and the accessibility
// options all come from React Navigation's own descriptors, and a press emits
// the ordinary `tabPress` event and respects `preventDefault`. A tab bar that
// tracked its own selection would disagree with the router the first time a
// route changed from anywhere else.

import React from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import type { BottomTabBarProps } from '@react-navigation/bottom-tabs';
import { iconSizes, spacing, touch, typography } from './tokens';
import { themed, useColors } from './theme';

export function BrandTabBar({
  state,
  descriptors,
  navigation,
}: BottomTabBarProps): React.JSX.Element {
  const styles = useTabStyles();
  const colors = useColors();
  const insets = useSafeAreaInsets();

  return (
    <View style={[styles.bar, { paddingBottom: insets.bottom }]}>
      {state.routes.map((route, index) => {
        const { options } = descriptors[route.key];
        const focused = state.index === index;
        const label =
          typeof options.tabBarLabel === 'string'
            ? options.tabBarLabel
            : (options.title ?? route.name);
        const tint = focused ? colors.accent : colors.textTertiary;

        return (
          <Pressable
            key={route.key}
            accessibilityRole="tab"
            // `selected` is what a screen reader announces; the colour is only
            // how it looks. Both are required, and neither substitutes.
            accessibilityState={{ selected: focused }}
            accessibilityLabel={options.tabBarAccessibilityLabel ?? label}
            onPress={() => {
              const event = navigation.emit({
                type: 'tabPress',
                target: route.key,
                canPreventDefault: true,
              });
              if (!focused && !event.defaultPrevented) {
                navigation.navigate(route.name);
              }
            }}
            onLongPress={() => {
              navigation.emit({ type: 'tabLongPress', target: route.key });
            }}
            style={({ pressed }) => [styles.tab, pressed && styles.tabPressed]}
          >
            {/* The signature: a thin edge on the selected destination. Not a
                filled capsule, not a glow, not a floating panel. */}
            <View style={[styles.edge, focused && styles.edgeActive]} />
            {options.tabBarIcon?.({
              focused,
              color: tint,
              size: iconSizes.l,
            })}
            <Text
              style={[styles.label, { color: tint }, focused && styles.labelActive]}
              numberOfLines={1}
            >
              {label}
            </Text>
          </Pressable>
        );
      })}
    </View>
  );
}

const useTabStyles = themed((colors) =>
  StyleSheet.create({
    bar: {
      flexDirection: 'row',
      backgroundColor: colors.surface,
      // Separated from the content by surface hierarchy and one hairline, not
      // by a shadow: the brand does not use heavy dashboard elevation.
      borderTopWidth: StyleSheet.hairlineWidth,
      borderTopColor: colors.separator,
    },
    tab: {
      flex: 1,
      minHeight: touch.minSize,
      paddingTop: spacing.s,
      paddingBottom: spacing.s,
      alignItems: 'center',
      justifyContent: 'center',
      gap: 2,
    },
    tabPressed: { opacity: 0.7 },
    // A BORDER, not a fill. `accent` is the text-and-border role: it is the
    // legibility tint, and white on it fails AA — which is why the fill role
    // exists separately and why the invariant check refuses it as a background.
    // A 2 px rule is a border, and it matches the accent label above it exactly,
    // which `accentStrong` would not.
    edge: {
      position: 'absolute',
      top: 0,
      left: spacing.l,
      right: spacing.l,
      height: 0,
      borderTopWidth: 2,
      borderTopColor: 'transparent',
    },
    edgeActive: { borderTopColor: colors.accent },
    label: { ...typography.badge },
    labelActive: { color: colors.accent },
  }),
);
