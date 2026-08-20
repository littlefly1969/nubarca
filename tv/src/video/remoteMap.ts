// Video-hls slice 4: pure mapping from a TV remote event to a viewer action,
// branched on whether the CURRENT item is a video. Extracted from
// ViewerScreen so the (user-approved) video remote semantics are unit-tested:
//
//   photos:  LEFT/RIGHT = prev/next photo · SELECT and the play/pause key =
//     start / pause / resume the slideshow · REWIND / FAST_FORWARD = prev/next
//     · MENU = overlay
//
//   videos:  SELECT and the play/pause key = play/pause · REWIND /
//     FAST_FORWARD = seek −/+10 s (the TV media convention) · LEFT/RIGHT =
//     seek −/+10 s as well · UP/DOWN = prev/next item · MENU = overlay ·
//     advancing to the next item on video end is the player's job, not a
//     remote event.
//
// SELECT used to be RESERVED (a no-op) on photos, and that was a five-way
// accessibility defect rather than a design choice: on a remote with no
// dedicated play/pause key — which is most generic Android TV remotes — there
// was NO way to start a slideshow from inside the viewer. The rule is that
// every product function must be reachable with UP/DOWN/LEFT/RIGHT/SELECT/BACK
// alone, so SELECT now carries the same meaning the transport key does.
//
// D-pad LEFT/RIGHT may control seek ONLY because the viewer owns the ENTIRE
// remote while it is up: it is a full-screen surface with no focusable grid
// underneath, so the same event cannot also drive focus navigation. That is the
// condition under which the double meaning is safe, and it is why the grid and
// the viewer are separate input-ownership modes rather than one blended one.
//
// BACK is deliberately absent from this map. It is a NAVIGATION key handled by
// the viewer's own BackHandler (leave playback → return to the grid), so it can
// never be spent as a playback control. Same for HOME, which is a system action
// the app must not intercept at all.

export type ViewerRemoteAction =
  | 'prev'
  | 'next'
  | 'toggle-overlay'
  | 'toggle-play'
  | 'seek-back'
  | 'seek-forward'
  | 'none';

export const VIDEO_SEEK_SECONDS = 10;

export function mapViewerRemoteEvent(eventType: string, isVideo: boolean): ViewerRemoteAction {
  if (eventType === 'menu') return 'toggle-overlay';

  if (!isVideo) {
    switch (eventType) {
      case 'left':
      case 'longLeft':
      // A dedicated transport key means the same thing it means on a video
      // player: go back one. It is an accelerator for LEFT, never a second
      // feature.
      case 'rewind':
        return 'prev';
      case 'right':
      case 'longRight':
      case 'fastForward':
        return 'next';
      // SELECT is the FIVE-WAY route to the slideshow; playPause is the
      // accelerator for the remotes that have it. One semantic action, two
      // keys — the viewer decides whether that means start, pause or resume.
      case 'select':
      case 'playPause':
        return 'toggle-play';
      default:
        return 'none';
    }
  }

  switch (eventType) {
    case 'select':
    case 'playPause':
      return 'toggle-play';
    // The dedicated transport keys are the Fire TV convention and must work on
    // a remote that has them, independently of the D-pad mapping below.
    case 'rewind':
    case 'left':
    case 'longLeft':
      return 'seek-back';
    case 'fastForward':
    case 'right':
    case 'longRight':
      return 'seek-forward';
    case 'up':
    case 'longUp':
      return 'prev';
    case 'down':
    case 'longDown':
      return 'next';
    default:
      return 'none';
  }
}
