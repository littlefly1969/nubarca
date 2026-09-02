// The item a gallery should be looking at when it comes back from the viewer.
//
// The user opens item 24, swipes to 29, and closes. The gallery they land on
// should be showing 29 — that is where they were, and returning to 24 asks them
// to find their place again.
//
// The anchor is consumed on the way in and cleared as soon as the grid has
// honoured it, so a later, unrelated visit to the same gallery is never yanked
// back to something somebody looked at once. It is scoped to the signed-in
// identity by construction: the viewer provider is remounted per identity, so
// there is no path by which one account's browsing position reaches another.

import { useCallback, useState } from 'react';
import { useFocusEffect } from 'expo-router';
import { useViewer } from './viewerContext';

export interface ReturnAnchor {
  /** Pass to MediaGrid's `anchorItemId`. */
  itemId: string | null;
  /** Pass to MediaGrid's `onAnchorConsumed`. */
  consume: () => void;
}

export function useReturnAnchor(): ReturnAnchor {
  const viewer = useViewer();
  const [itemId, setItemId] = useState<string | null>(null);

  useFocusEffect(
    useCallback(() => {
      // Taking it here is what makes it one-shot: the viewer no longer holds
      // it, so a second focus without a second viewer visit finds nothing.
      const anchor = viewer.takeReturnAnchor();
      if (anchor !== null) setItemId(anchor);
    }, [viewer]),
  );

  const consume = useCallback(() => setItemId(null), []);
  return { itemId, consume };
}
