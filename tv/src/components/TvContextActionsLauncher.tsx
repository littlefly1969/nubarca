import { StyleSheet, View } from 'react-native';
import { spacing } from '../theme';
import { FocusableButton } from './FocusableButton';
import { useI18n } from '../i18n';

// The FIVE-WAY entry to a screen's command surface.
//
// WHY IT EXISTS
// -------------
// Several screens put real product functions — the media-kind tabs, Filters,
// Slideshow, "show all photos" after a face search — exclusively inside a MENU
// overlay. That is fine on a Fire TV remote and broken everywhere else: plenty
// of Android TV and Google TV remotes have no MENU key at all, and a gamepad
// certainly does not. On those devices the functions were simply unreachable.
//
// The rule this restores is that every product function must have a route using
// only UP / DOWN / LEFT / RIGHT / SELECT / BACK. MENU is not removed — it stays
// as a shortcut for the remotes that have it.
//
// WHAT IT IS NOT
// --------------
// It is NOT a second menu. It opens the SAME command surface MENU opens, by
// calling the same function, so there is one action model with two entrances.
// Copying the buttons into a second rail would be two implementations of
// "start the slideshow", and the second one is the one that rots.
//
// It is also not a toolbar. A permanent full-width bar above every row would
// tax every screen forever to solve a problem that needs one small control, so
// this is a compact affordance in the normal layout flow — which is also what
// makes it reachable: native focus geometry finds it with UP from the first
// content row, with no focus graph, no key handler and no timers.
interface Props {
  // Opens the shared command surface. The SAME callback MENU is wired to.
  onOpen: () => void;
  // False while the command surface owns focus: the launcher must not remain a
  // focus destination behind its own modal, or a direction could escape to it.
  focusable?: boolean;
  hasTVPreferredFocus?: boolean;
}

export function TvContextActionsLauncher({
  onOpen, focusable = true, hasTVPreferredFocus = false,
}: Props) {
  const { t } = useI18n();
  // Rendered but inert while the rail is up: unmounting it instead would reflow
  // the grid underneath the overlay, and a grid that moves while a modal is
  // open is how focus restoration loses its place.
  return (
    <View style={styles.row} pointerEvents={focusable ? 'auto' : 'none'}>
      <FocusableButton
        label={t('actions.open')}
        onPress={onOpen}
        disabled={!focusable}
        hasTVPreferredFocus={hasTVPreferredFocus}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  // Compact and leading-aligned: it takes one button's height from the grid,
  // not a full-width bar, and it sits inside the caller's overscan padding.
  row: {
    flexDirection: 'row',
    alignItems: 'center',
    alignSelf: 'flex-start',
    marginBottom: spacing.sm,
  },
});
