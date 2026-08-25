import { useCallback, type ReactNode } from 'react';
import { Modal, ScrollView, StyleSheet, Text, View } from 'react-native';
import { colors, font, spacing } from '../../theme';

// A FULL-SCREEN BODY DECLARES WHETHER IT SCROLLS.
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
//   * A focusable TV chooser must not hide controls outside its declared body
//     geometry. The People chooser is explicitly paged, so all of its mounted
//     rows fit and no list viewport is needed.
//
// So the body mode is now declared:
//   'scroll' — the shell scrolls. For genuinely variable row collections.
//   'fixed'  — no scrolling at all. For bounded editors that must fit.
export type PanelBodyMode = 'scroll' | 'fixed';

// Full-screen panel container for the Personal Gallery MENU actions (filters /
// sort / search / people / albums / details).
//
// This is a real native Modal rather than an absolutely-positioned sibling of
// the media grid. Android may promote focused/elevated descendants from a
// virtualized grid above an ordinary React sibling even when that sibling has a
// larger zIndex/elevation. That failure was reproduced on a physical Fire Stick:
// DPAD focus moved through the People picker, but the user kept seeing the cover
// underneath it. A Modal owns a separate native window, so its opaque surface
// and its focus tree are unconditionally above the library.
//
// While an Android Modal is open, BACK is delivered through onRequestClose (not
// BackHandler). The deepest panel is still the one mounted in the modal and
// therefore closes first.
interface Props {
  title: string;
  onBack: () => void;
  children: ReactNode;
  // Custom BACK behavior (e.g. the keyboard deletes a character first). Return
  // true when handled; false falls through to onBack.
  onBackOverride?: () => boolean;
  // Defaults to 'scroll' so existing filter-row panels are unchanged; the
  // bounded editors and the explicitly paged People picker opt out.
  body?: PanelBodyMode;
}

export function PanelShell({
  title, onBack, children, onBackOverride, body = 'scroll',
}: Props) {
  const requestClose = useCallback(() => {
    if (onBackOverride?.()) return;
    onBack();
  }, [onBack, onBackOverride]);

  return (
    <Modal
      animationType="none"
      hardwareAccelerated
      navigationBarTranslucent
      onRequestClose={requestClose}
      presentationStyle="fullScreen"
      statusBarTranslucent
      visible
    >
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
          // A fixed body promises that every mounted control fits. If a large
          // data set is needed, the child must page it into a bounded set.
          <View style={[styles.scroll, styles.content]}>{children}</View>
        )}
      </View>
    </Modal>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
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
