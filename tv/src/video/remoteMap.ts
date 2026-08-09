// Video-hls slice 4: pure mapping from a TV remote event to a viewer action,
// branched on whether the CURRENT item is a video. Extracted from
// ViewerScreen so the (user-approved) video remote semantics are unit-tested:
//
//   photos (unchanged):  LEFT/RIGHT = prev/next photo · play/pause key =
//     toggle slideshow · SELECT reserved (no-op) · MENU = overlay
//   videos:              SELECT and the play/pause key = play/pause ·
//     REWIND / FAST_FORWARD = seek −/+10 s (the Fire TV media convention) ·
//     LEFT/RIGHT = seek −/+10 s as well · UP/DOWN = prev/next item · MENU =
//     overlay · advancing to the next item on video end is the player's job,
//     not a remote event.
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
        return 'prev';
      case 'right':
      case 'longRight':
        return 'next';
      case 'playPause':
        return 'toggle-play';
      default:
        return 'none'; // SELECT stays reserved for photos
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
