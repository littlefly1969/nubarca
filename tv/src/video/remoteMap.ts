// Video-hls slice 4: pure mapping from a TV remote event to a viewer action,
// branched on whether the CURRENT item is a video. Extracted from
// ViewerScreen so the (user-approved) video remote semantics are unit-tested.
//
// What follows is the UNATTENDED wall (`selectable: false`). A viewer a person
// opened from a grid uses the selection contract further down instead.
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
  | 'toggle-select'
  | 'open-actions'
  | 'seek-back'
  | 'seek-forward'
  | 'none';

export const VIDEO_SEEK_SECONDS = 10;

// THE SELECTION CONTRACT (the definitive one, shared by Fire TV, Android TV and
// the browser display). It applies to every viewer a person OPENED from a grid,
// which is where a selection exists:
//
//   OK            — photo: select / deselect the photo on screen. During a
//                   slideshow it selects WITHOUT stopping it.
//   LEFT / RIGHT  — photo: previous / next. Manual navigation PAUSES a running
//                   slideshow (the viewer decides that; the map only says
//                   "prev"/"next").
//   PLAY/PAUSE    — start, pause and resume the slideshow (a video: playback).
//   UP            — open the action bar, navigable with the D-pad. The bar
//                   carries an on-screen Play/Pause, which is the five-way route
//                   to the slideshow on a remote (or browser) with no usable
//                   transport key.
//   BACK          — never mapped here: the viewer's own Back handler closes the
//                   action bar first, then the viewer, returning to the same
//                   tile with the selection intact.
//
// A video keeps SELECT/PLAY-PAUSE as play/pause and LEFT/RIGHT as seek (the TV
// media convention); UP opens the same action bar (where the video can be
// selected and the previous item reached) and DOWN still steps to the next item.
//
// `selectable: false` is the ASSIGNED party wall: a display nobody opened from a
// grid, with no selection behind it. It keeps the slideshow semantics it always
// had, so a party running unattended answers OK exactly as before.
export interface ViewerRemoteOptions {
  selectable?: boolean;
}

export function mapViewerRemoteEvent(
  eventType: string,
  isVideo: boolean,
  options: ViewerRemoteOptions = {},
): ViewerRemoteAction {
  if (eventType === 'menu') return 'toggle-overlay';
  if (options.selectable) return mapSelectableEvent(eventType, isVideo);

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

function mapSelectableEvent(eventType: string, isVideo: boolean): ViewerRemoteAction {
  switch (eventType) {
    case 'up':
    case 'longUp':
      return 'open-actions';
    case 'playPause':
      return 'toggle-play';
    default:
      break;
  }
  if (!isVideo) {
    switch (eventType) {
      case 'select':
        return 'toggle-select';
      case 'left':
      case 'longLeft':
      case 'rewind':
        return 'prev';
      case 'right':
      case 'longRight':
      case 'fastForward':
        return 'next';
      default:
        return 'none';
    }
  }
  switch (eventType) {
    case 'select':
      return 'toggle-play';
    case 'rewind':
    case 'left':
    case 'longLeft':
      return 'seek-back';
    case 'fastForward':
    case 'right':
    case 'longRight':
      return 'seek-forward';
    case 'down':
    case 'longDown':
      return 'next';
    default:
      return 'none';
  }
}

// What the viewer's on-screen action bar offers, in order. Pure, so the TV app
// and the browser display show the same bar and the tests can hold it.
//
//   play-pause  — always first: the five-way route to the slideshow (or to
//                 video playback) for remotes without a transport key.
//   select      — select / deselect the item on screen (for a video this is the
//                 ONLY route: its OK is play/pause).
//   prev / next — present for a video, whose LEFT/RIGHT seek.
//   clear       — present only when something is selected.
export type ViewerBarAction = 'play-pause' | 'select' | 'prev' | 'next' | 'clear-selection';

export function viewerBarActions(input: { isVideo: boolean; selectedCount: number }): ViewerBarAction[] {
  const actions: ViewerBarAction[] = ['play-pause', 'select'];
  if (input.isVideo) actions.push('prev', 'next');
  if (input.selectedCount > 0) actions.push('clear-selection');
  return actions;
}
