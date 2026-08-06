// Video-hls slice 4: pure mapping from a TV remote event to a viewer action,
// branched on whether the CURRENT item is a video. Extracted from
// ViewerScreen so the (user-approved) video remote semantics are unit-tested:
//
//   photos (unchanged):  LEFT/RIGHT = prev/next photo · play/pause key =
//     toggle slideshow · SELECT reserved (no-op) · MENU = overlay
//   videos:              SELECT and the play/pause key = play/pause ·
//     LEFT/RIGHT = seek −/+10 s · UP/DOWN = prev/next item (navigation moves
//     off LEFT/RIGHT while seeking owns them) · MENU = overlay · advancing to
//     the next item on video end is the player's job, not a remote event.

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
    case 'left':
    case 'longLeft':
      return 'seek-back';
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
