// The TRANSIENT context a shared album's "Add from library" action hands to the
// Media Library.
//
// Deliberately React Router navigation state and not a URL contract. "I came
// from this album and I am picking media for it" is a moment in one session,
// not an addressable location: it must not survive being bookmarked, shared or
// pasted, and /media must stay the ordinary Media Library at the ordinary URL.
//
// The consumer reads it ONCE, on mount. Router state belongs to a history entry,
// so an unrelated later navigation to /media carries none and the target cannot
// go stale — which is exactly why the reader below narrows an `unknown` instead
// of trusting whatever happens to be in `location.state`.
export interface SharedAlbumAddContext {
  albumId: string;
  albumName: string;
  // Where "back to the album" goes. Held rather than rebuilt so the album's own
  // route stays the one thing that knows its shape.
  returnPath: string;
}

export function sharedAlbumAddContext(
  albumId: string, albumName: string,
): SharedAlbumAddContext {
  return { albumId, albumName, returnPath: `/shared-albums/${albumId}` };
}

// Narrows arbitrary router state. Anything not matching the full shape is
// treated as "no context", so a partially-written or foreign state object can
// never become a half-configured add target.
export function readSharedAlbumAddContext(state: unknown): SharedAlbumAddContext | null {
  if (typeof state !== 'object' || state === null) return null;
  const candidate = (state as { sharedAlbumAdd?: unknown }).sharedAlbumAdd;
  if (typeof candidate !== 'object' || candidate === null) return null;
  const { albumId, albumName, returnPath } = candidate as Record<string, unknown>;
  if (typeof albumId !== 'string' || albumId.length === 0) return null;
  if (typeof albumName !== 'string') return null;
  if (typeof returnPath !== 'string' || !returnPath.startsWith('/')) return null;
  return { albumId, albumName, returnPath };
}
