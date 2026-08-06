// The media wall's shared geometry constants.
//
// Extracted from MediaGrid so the SHARED-ALBUM wall (SHARE-ALBUM-01) lays out
// with the same numbers rather than a second, subtly different set. A recipient
// looking at somebody else's album should see NubArca's wall, not a lookalike.
//
// Only the numbers live here — MediaGrid keeps its virtualization, selection and
// overlay behaviour, and the shared viewer deliberately has none of those.

export const MEDIA_WALL_GAP_PX = 6;

export interface MediaWallRowParams {
  targetRowHeight: number;
  minRowHeight: number;
  maxRowHeight: number;
}

// Row-height bands per breakpoint. Smaller screens get shorter rows so a
// comparable number of tiles stays visible.
export function mediaWallRowParams(width: number): MediaWallRowParams {
  if (width <= 640) return { targetRowHeight: 150, minRowHeight: 120, maxRowHeight: 185 };
  if (width <= 1024) return { targetRowHeight: 190, minRowHeight: 155, maxRowHeight: 235 };
  return { targetRowHeight: 230, minRowHeight: 180, maxRowHeight: 280 };
}
