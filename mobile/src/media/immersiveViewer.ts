// Full-screen viewing: hide the system bars while media is open.
//
// THE DEFECT THIS ANSWERS, reported from a device: the Android navigation bar
// stayed over a full-screen photo or video, and stayed there indefinitely. A
// media viewer is the one screen where the system chrome is competing with the
// content rather than framing it.
//
// TWO RULES, both about not trapping the user:
//
//   * the bars come back with the CHROME. When the viewer's own controls are
//     visible — the back button, the title — the system bars belong on screen
//     too, or the user is in a room with a hidden exit;
//   * the bars are ALWAYS restored on the way out. A viewer that leaves the
//     phone immersive after it closes has broken the rest of the app, and an
//     error or a crash on this path must not be able to do that either.
//
// Android only: iOS has no navigation bar to hide, and its status bar is
// handled by the existing StatusBar component. The functions are safe to call
// on any platform.

export type SystemBarsMode = 'immersive' | 'visible';

export interface NavigationBarController {
  setVisibilityAsync(visibility: 'visible' | 'hidden'): Promise<unknown>;
  setBehaviorAsync?(behavior: string): Promise<unknown>;
}

/**
 * What the system bars should do, given whether media is open and whether the
 * viewer is currently showing its own chrome.
 *
 * Pure so the rule is testable without a device: the mistake it prevents is a
 * state where BOTH the app chrome and the system bars are hidden, which leaves
 * no visible way out of the screen.
 */
export function systemBarsFor(input: {
  viewerOpen: boolean;
  chromeVisible: boolean;
}): SystemBarsMode {
  if (!input.viewerOpen) return 'visible';
  return input.chromeVisible ? 'visible' : 'immersive';
}

/**
 * Apply a mode. Failures are swallowed on purpose: system-bar control is a
 * comfort, and a device that refuses it must not take the viewer down with it.
 */
export async function applySystemBars(
  controller: NavigationBarController | null,
  mode: SystemBarsMode,
): Promise<void> {
  if (controller === null) return;
  try {
    // Swipe-to-reveal rather than sticky-hidden: the user can always summon
    // the bars back with a gesture, whatever the app believes.
    await controller.setBehaviorAsync?.('overlay-swipe');
    await controller.setVisibilityAsync(mode === 'immersive' ? 'hidden' : 'visible');
  } catch {
    /* the viewer works with the bars showing; it must not fail because of them */
  }
}
