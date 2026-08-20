// THE FIVE-WAY CAPABILITY MATRIX.
//
// The rule this encodes: every product FUNCTION must have a route using only
// UP / DOWN / LEFT / RIGHT / SELECT / BACK. MENU and the dedicated transport
// keys are accelerators — a remote that lacks them must lose nothing.
//
// It exists because the defect it describes was invisible in review. Several
// screens put real functions — the media-kind tabs, Filters, Slideshow, "show
// all photos" after a face search, Beauty Lab's Add images — exclusively inside
// a MENU overlay. Every one of them worked perfectly on the Fire TV remote used
// to test them, and none of them existed at all on a remote with no MENU key.
// Beauty Lab's empty state even instructed the user to press it.
//
// A comment cannot catch that coming back. A matrix can: the entries below are
// data, the tests assert over them, and a function added with `menuOnly` has to
// say so out loud.
//
// This module deliberately does NOT simulate focus. Whether a specific D-pad
// press lands on a specific control is a property of the native focus engine
// and belongs to device QA; what is checked here is the weaker, decidable claim
// that a five-way ROUTE has been designed and wired at all.

export type FiveWayKey = 'up' | 'down' | 'left' | 'right' | 'select' | 'back';

export const FIVE_WAY_KEYS: readonly FiveWayKey[] = [
  'up', 'down', 'left', 'right', 'select', 'back',
];

export interface TvCapability {
  /** Screen the function belongs to. */
  readonly screen: string;
  /** The product function, in the product's own words. */
  readonly action: string;
  /**
   * How it is reached with the five-way keys alone. Never empty — an empty
   * route IS the defect, and the type does not let one be omitted silently.
   */
  readonly fiveWayRoute: string;
  /** Optional faster routes. Never the ONLY route. */
  readonly accelerators: readonly string[];
}

// One entry per product FUNCTION. Presentation that carries no command is
// deliberately absent: the Party QR corners, the face-filter indicator, the
// title pill, and the viewer's ambient chrome (item name, position counter,
// slideshow pill) are INFORMATION, not commands.
//
// The chrome was briefly listed here with a five-way "route", and that was a
// category error worth naming: it made the matrix report a pass for something
// that is not a function, and the only way to keep it honest would have been to
// invent a D-pad command for decoration. MENU may still redisplay it — an
// accelerator to information is not a function gate.
export const TV_CAPABILITIES: readonly TvCapability[] = [
  // --- Personal Library ----------------------------------------------------
  { screen: 'PersonalLibrary', action: 'browse media',
    fiveWayRoute: 'grid focus, UP/DOWN/LEFT/RIGHT', accelerators: [] },
  { screen: 'PersonalLibrary', action: 'open a media item',
    fiveWayRoute: 'grid focus → SELECT', accelerators: [] },
  { screen: 'PersonalLibrary', action: 'select kind: All',
    fiveWayRoute: 'UP to Actions → SELECT → rail → SELECT "Tutti"', accelerators: ['MENU'] },
  { screen: 'PersonalLibrary', action: 'select kind: Photos',
    fiveWayRoute: 'UP to Actions → SELECT → rail → SELECT "Foto"', accelerators: ['MENU'] },
  { screen: 'PersonalLibrary', action: 'select kind: Videos',
    fiveWayRoute: 'UP to Actions → SELECT → rail → SELECT "Video"', accelerators: ['MENU'] },
  { screen: 'PersonalLibrary', action: 'open filters',
    fiveWayRoute: 'UP to Actions → SELECT → rail → SELECT "Filtri"', accelerators: ['MENU'] },
  { screen: 'PersonalLibrary', action: 'return to Personal home',
    fiveWayRoute: 'BACK', accelerators: ['Actions rail → back command'] },
  { screen: 'PersonalLibrary', action: 'load more media',
    fiveWayRoute: 'DOWN past the last loaded row (onEndReached)', accelerators: [] },
  { screen: 'PersonalLibrary', action: 'retry a failed load',
    fiveWayRoute: 'focus the retry control → SELECT', accelerators: [] },
  { screen: 'PersonalLibrary', action: 'clear filters when nothing matches',
    fiveWayRoute: 'focus the clear control → SELECT', accelerators: [] },

  // --- Album items / Party -------------------------------------------------
  { screen: 'AlbumItems', action: 'browse album media',
    fiveWayRoute: 'grid focus, UP/DOWN/LEFT/RIGHT', accelerators: [] },
  { screen: 'AlbumItems', action: 'open a media item',
    fiveWayRoute: 'grid focus → SELECT', accelerators: [] },
  { screen: 'AlbumItems', action: 'start the slideshow',
    fiveWayRoute: 'UP to Actions → SELECT → rail → SELECT "Slideshow"', accelerators: ['MENU'] },
  { screen: 'AlbumItems', action: 'show all photos (exit face filter)',
    fiveWayRoute: 'UP to Actions → SELECT → rail → SELECT "Mostra tutte"', accelerators: ['MENU', 'BACK'] },
  { screen: 'AlbumItems', action: 'return to the album list',
    fiveWayRoute: 'BACK', accelerators: ['Actions rail → back command'] },

  // --- Beauty Lab ----------------------------------------------------------
  { screen: 'BeautyLab', action: 'add images',
    fiveWayRoute: 'UP to Actions → SELECT → rail → SELECT "Aggiungi immagini"', accelerators: ['MENU'] },
  { screen: 'BeautyLab', action: 'enter selection mode',
    fiveWayRoute: 'UP to Actions → SELECT → rail → SELECT "Seleziona"', accelerators: ['MENU'] },
  { screen: 'BeautyLab', action: 'start / cancel / retry an analysis',
    fiveWayRoute: 'UP to Actions → SELECT → rail → SELECT the command', accelerators: ['MENU'] },
  { screen: 'BeautyLab', action: 'compare scores',
    fiveWayRoute: 'UP to Actions → SELECT → rail → SELECT "Confronta"', accelerators: ['MENU'] },
  { screen: 'BeautyLab', action: 'load more images',
    fiveWayRoute: 'DOWN to the load-more control → SELECT', accelerators: [] },
  { screen: 'BeautyLab', action: 'leave and lock',
    fiveWayRoute: 'BACK', accelerators: [] },

  // --- Fullscreen viewer ---------------------------------------------------
  // Directions here are owned by the viewer's remote policy, not by focus:
  // see video/remoteMap.ts. That is the second input mode, and it is safe only
  // because the viewer has no focusable views to compete with.
  { screen: 'Viewer', action: 'previous item',
    fiveWayRoute: 'LEFT (photo) / UP (video)', accelerators: ['REWIND on a photo'] },
  { screen: 'Viewer', action: 'next item',
    fiveWayRoute: 'RIGHT (photo) / DOWN (video)', accelerators: ['FAST_FORWARD on a photo'] },
  { screen: 'Viewer', action: 'start / pause / resume the photo slideshow',
    fiveWayRoute: 'SELECT', accelerators: ['PLAY_PAUSE'] },
  { screen: 'Viewer', action: 'play / pause a video',
    fiveWayRoute: 'SELECT', accelerators: ['PLAY_PAUSE'] },
  { screen: 'Viewer', action: 'seek a video',
    fiveWayRoute: 'LEFT / RIGHT', accelerators: ['REWIND', 'FAST_FORWARD'] },
  { screen: 'Viewer', action: 'return to the grid',
    fiveWayRoute: 'BACK', accelerators: [] },

  // --- Other screens -------------------------------------------------------
  { screen: 'ModeSelect', action: 'choose Party / Personal / Beauty Lab / Updates',
    fiveWayRoute: 'UP/DOWN to the entry → SELECT', accelerators: [] },
  { screen: 'Updates', action: 'check, install, authorize, go back',
    fiveWayRoute: 'UP/DOWN to the control → SELECT; BACK returns', accelerators: [] },
  { screen: 'PinEntry', action: 'enter the code',
    fiveWayRoute: 'UP/DOWN/LEFT/RIGHT/SELECT are the code alphabet; BACK deletes',
    accelerators: [] },
  { screen: 'Pairing', action: 'retry pairing',
    fiveWayRoute: 'focus the retry control → SELECT', accelerators: [] },
  { screen: 'Filters', action: 'edit and apply any filter',
    fiveWayRoute: 'UP/DOWN to the row → SELECT → editor → SELECT apply', accelerators: [] },
  { screen: 'PeoplePicker', action: 'include / exclude a person, search, clear',
    fiveWayRoute: 'UP/DOWN to the row → SELECT', accelerators: [] },
];

/** Functions whose only route needs a key the five-way contract excludes. */
export function menuOnlyCapabilities(
  capabilities: readonly TvCapability[] = TV_CAPABILITIES,
): TvCapability[] {
  return capabilities.filter((capability) => {
    const route = capability.fiveWayRoute.toLowerCase();
    return route.length === 0
      || route.includes('menu')
      || route.includes('play_pause')
      || route.includes('rewind')
      || route.includes('fast_forward');
  });
}

export function capabilitiesForScreen(
  screen: string,
  capabilities: readonly TvCapability[] = TV_CAPABILITIES,
): TvCapability[] {
  return capabilities.filter((capability) => capability.screen === screen);
}
