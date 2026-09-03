// The item a gallery should be looking at when it comes back from the viewer.
//
// The user opens item 24, swipes to 29, and closes. The gallery they land on
// should be showing 29 — that is where they were, and returning to 24 asks them
// to find their place again.
//
// The anchor is consumed on the way in and cleared as soon as the grid has
// honoured it, so a later, unrelated visit to the same gallery is never yanked
// back to something somebody looked at once. It is scoped twice over: to the
// signed-in identity by construction — the viewer provider is remounted per
// identity — and to the ORIGIN GALLERY by key, so a viewer opened from Files
// cannot leave a position that Photos consumes afterwards.

import { useCallback, useState } from 'react';
import { useFocusEffect } from 'expo-router';
import { useViewer } from './viewerContext';

export interface ReturnAnchor {
  /** Pass to MediaGrid's `anchorItemId`. */
  itemId: string | null;
  /** Pass to MediaGrid's `onAnchorConsumed`. */
  consume: () => void;
}

export function useReturnAnchor(scopeKey: string): ReturnAnchor {
  const viewer = useViewer();
  const [itemId, setItemId] = useState<string | null>(null);

  useFocusEffect(
    useCallback(() => {
      // Taking it here is what makes it one-shot: the viewer no longer holds
      // it, so a second focus without a second viewer visit finds nothing. A
      // scope that does not match takes nothing and leaves it for the gallery
      // it belongs to.
      const position = viewer.takeReturnPosition(scopeKey);
      if (position === null) return;
      // OPENED AND CLOSED THE SAME ITEM: move nothing. The gallery is already
      // where the user left it, and scrolling it "back" to an item they never
      // left is a jump they did not ask for. If the column count changed while
      // the viewer was open, the list's own anchor has already handled it.
      if (position.focusedKey === position.openedKey) return;
      setItemId(position.focusedKey);
    }, [viewer, scopeKey]),
  );

  const consume = useCallback(() => setItemId(null), []);
  return { itemId, consume };
}
