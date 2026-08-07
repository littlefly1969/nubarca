import { TVFocusGuideView, type StyleProp, type ViewStyle } from 'react-native';

interface Props {
  children: React.ReactNode;
  style?: StyleProp<ViewStyle>;
}

// The MENU overlay's command bar as a real FOCUS SCOPE.
//
// The bar used to be a plain absolutely-positioned View whose first button
// asked for focus at mount time. That only decides where focus STARTS: the
// media grid underneath stayed a live focus destination, so a D-pad press could
// walk out of the menu geometrically, and a virtualized row mounting underneath
// could take focus back.
//
//  - `autoFocus` makes the rail redirect an incoming focus to its first
//    focusable child rather than leaving it to spatial luck;
//  - the four traps make the native focus search run inside the rail ONLY
//    (react-native-tvos intercepts `focusSearch` and restricts `FocusFinder` to
//    this subtree), so no direction can leave the menu while it is open.
//
// The overlay is unmounted while hidden, so the rail is remounted on every MENU
// press and always starts at the FIRST command — never at whichever command was
// used last.
export function MenuCommandRail({ children, style }: Props) {
  return (
    <TVFocusGuideView
      style={style}
      autoFocus
      trapFocusUp
      trapFocusDown
      trapFocusLeft
      trapFocusRight
    >
      {children}
    </TVFocusGuideView>
  );
}
