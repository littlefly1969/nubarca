import { useEffect, type ReactNode } from 'react';
import { BackHandler, ScrollView, StyleSheet, Text, View } from 'react-native';
import { colors, font, spacing } from '../../theme';

// EXACTLY ONE COMPONENT MAY OWN VERTICAL SCROLLING.
//
// This shell used to wrap its children in a ScrollView unconditionally, which
// made that impossible to honour. Two things went wrong, both on real
// televisions:
//
//   * BOUNDED editors — the keyboard, the numeric pad, the date pad — have a
//     known number of key rows. Where the constants did not fit the viewport
//     the panel simply scrolled, so the bottom key row went off-screen: a
//     focusable the remote can reach and the viewer cannot see. A scroll is the
//     wrong answer to "this does not fit"; sizing it to fit is (see
//     lib/panelLayout.ts).
//   * A VIRTUALIZED list cannot live inside a ScrollView. A FlatList given
//     unbounded height renders every row, which defeats the virtualization
//     entirely, and the two scroll containers then fight over the same gesture
//     and the same focus.
//
// So the body mode is now declared:
//   'scroll' — the shell scrolls. For genuinely variable row collections.
//   'fixed'  — no scrolling at all. For bounded editors that must fit.
//   'custom' — the CHILD owns scrolling (a FlatList). The shell must not.
export type PanelBodyMode = 'scroll' | 'fixed' | 'custom';

// Full-screen panel container for the Personal Gallery MENU actions (filters /
// sort / search / people / albums / details). Opaque, so the grid behind it is
// fully covered; the grid's tiles are made non-focusable by the parent while a
// panel is open, so DPAD focus cannot escape underneath. Registers its OWN
// hardware-Back handler on mount — handlers are LIFO, so the deepest open panel
// always wins BACK, closing itself first (spec: BACK closes the deepest panel).
interface Props {
  title: string;
  onBack: () => void;
  children: ReactNode;
  // Custom BACK behavior (e.g. the keyboard deletes a character first). Return
  // true when handled; false falls through to onBack.
  onBackOverride?: () => boolean;
  // Defaults to 'scroll' so existing filter-row panels are unchanged; the
  // editors and the People picker opt out explicitly.
  body?: PanelBodyMode;
}

export function PanelShell({
  title, onBack, children, onBackOverride, body = 'scroll',
}: Props) {
  useEffect(() => {
    const onBackPress = () => {
      if (onBackOverride?.()) return true;
      onBack();
      return true;
    };
    const sub = BackHandler.addEventListener('hardwareBackPress', onBackPress);
    return () => sub.remove();
  }, [onBack, onBackOverride]);

  return (
    <View style={styles.container} accessibilityViewIsModal>
      <Text style={styles.title}>{title}</Text>
      {body === 'scroll' ? (
        <ScrollView
          style={styles.scroll}
          contentContainerStyle={styles.content}
          // The DPAD moves between focusables; the ScrollView follows the focused
          // child automatically on TV. No nested horizontal scrolling.
          showsVerticalScrollIndicator={false}
        >
          {children}
        </ScrollView>
      ) : (
        // 'fixed' and 'custom' differ in INTENT, not in what this shell does:
        // in both cases the shell must not scroll. 'fixed' promises the content
        // fits; 'custom' promises the child scrolls itself.
        <View style={[styles.scroll, styles.content]}>{children}</View>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    position: 'absolute',
    top: 0,
    left: 0,
    right: 0,
    bottom: 0,
    // Full-screen panels are rendered after the gallery, but Android can still
    // promote focused/elevated tile descendants above a later sibling. Give
    // the opaque focus scope its own explicit native layer so neither the
    // People list nor its fixed Done action can be painted "under" the grid.
    zIndex: 100,
    elevation: 24,
    backgroundColor: colors.bg,
    paddingVertical: spacing.xl,
    paddingHorizontal: spacing.xl * 2,
  },
  title: {
    color: colors.text,
    fontSize: font.heading,
    fontWeight: '800',
    marginBottom: spacing.lg,
    textAlign: 'center',
  },
  scroll: { flex: 1 },
  content: { gap: spacing.sm, paddingBottom: spacing.xl },
});
