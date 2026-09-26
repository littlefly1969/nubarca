// What one remote press does in the viewer — a PORT of tv/src/video/remoteMap.ts.
//
//   PHOTO — LEFT/RIGHT previous/next, SELECT (and play/pause) start, pause or
//           resume the slideshow, rewind/fast-forward are accelerators.
//   VIDEO — SELECT/play-pause toggle playback, LEFT/RIGHT seek ±10 s,
//           rewind/fast-forward seek, UP/DOWN change item.
//   MENU  — shows or hides the overlay, whatever is on screen.
//
// The browser reads its remote through the DisplayPlatform's TvKey names; the
// only browser-specific step is `remoteEventFor`, which spells a TvKey the way
// the app's remote events are spelled, so the mapping itself is the app's.

import type { TvKey } from '../platform/displayPlatform';

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
      case 'rewind':
        return 'prev';
      case 'right':
      case 'longRight':
      case 'fastForward':
        return 'next';
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

/** A browser key, spelled as the app's remote event. */
export function remoteEventFor(key: TvKey): string {
  switch (key) {
    case 'next': return 'fastForward';
    case 'prev': return 'rewind';
    default: return key;
  }
}
