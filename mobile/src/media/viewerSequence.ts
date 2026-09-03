// Pure viewer-sequence state. Extracted from ViewerProvider so the privacy
// rules are testable without React:
//
//   * open() replaces EVERYTHING — no merge, no residue of a previous user's
//     sequence can survive an open();
//   * close()/reset() drop the sequence AND every reference to its slides, so
//     after logout/reset no item or metadata of the previous session is even
//     reachable from this object;
//   * there is deliberately NO "last sequence kept during the animation"
//     anymore — that ref is what once let state survive an account switch.

export interface ViewerSlide {
  // Stable key across pages: fileItemId for owned, albumItemId for shared.
  key: string;
  kind: 'image' | 'video';
  displayName: string;
  // Image: authenticated path/URL fetched by the image loader as-is.
  imagePath: string;
  // Video: the exact authorized URI plus the cookie snapshot available when
  // the sequence opens; null when there is no playable source. VideoSlide
  // preserves the URI but refreshes that manual cookie at focus boundaries so
  // a long-lived viewer survives session renewal. The container (HLS vs
  // progressive) is decided by the bounded Range probe, not here.
  videoSource: { uri: string; headers: { cookie: string } } | null;
  posterUrl: string | null;
}

/**
 * What a gallery gets back when its viewer closes.
 *
 * SCOPED, because a viewer opened from Files must not leave an anchor that
 * Photos consumes later — they are different libraries and the ids mean
 * different things in each.
 *
 * `openedKey` is kept alongside `focusedKey` so a gallery can tell the two
 * cases apart. Opening an item and closing it again should move nothing: the
 * gallery is already where the user left it, and scrolling it "back" to the
 * item they never left is a jump they did not ask for.
 */
export interface ViewerReturnPosition {
  scopeKey: string;
  openedKey: string;
  focusedKey: string;
}

export interface ViewerSequenceSnapshot {
  slides: ViewerSlide[];
  focusedKey: string;
  index: number;
  /** Which gallery opened this sequence, and with which item. */
  scopeKey: string;
  openedKey: string;
}

export class ViewerSequenceModel {
  private snapshotValue: ViewerSequenceSnapshot | null = null;

  // What the gallery should look at when it comes back. Set on close, taken
  // once by the gallery it belongs to, and never accompanied by the slides
  // themselves — the sequence is dropped, only the identities survive.
  private returnValue: ViewerReturnPosition | null = null;

  snapshot(): ViewerSequenceSnapshot | null {
    return this.snapshotValue;
  }

  open(slides: ViewerSlide[], focusedKey: string, scopeKey: string): void {
    const index = Math.max(
      0,
      slides.findIndex((s) => s.key === focusedKey),
    );
    if (slides.length === 0) {
      this.reset();
      return;
    }
    this.snapshotValue = {
      slides,
      focusedKey: slides[index].key,
      index,
      scopeKey,
      openedKey: slides[index].key,
    };
  }

  /**
   * Move to another slide.
   *
   * `focusedKey` moves WITH the index. It used to stay pointing at whatever was
   * opened, so after swiping from item 24 to item 29 the viewer's own idea of
   * its current item was still 24 — and the gallery, on the way back, returned
   * to the wrong photo. The invariant is now simply
   * `focusedKey === slides[index].key` for every non-empty snapshot.
   */
  setIndex(index: number): void {
    if (this.snapshotValue === null) return;
    if (index < 0 || index >= this.snapshotValue.slides.length) return;
    this.snapshotValue = {
      ...this.snapshotValue,
      index,
      focusedKey: this.snapshotValue.slides[index].key,
    };
  }

  // Close the viewer. The slide sequence is dropped; the identity of the item
  // that was on screen is kept, once, for the gallery that is about to reappear.
  close(): void {
    const snapshot = this.snapshotValue;
    this.returnValue =
      snapshot === null
        ? null
        : {
            scopeKey: snapshot.scopeKey,
            openedKey: snapshot.openedKey,
            focusedKey: snapshot.focusedKey,
          };
    this.snapshotValue = null;
  }

  /**
   * The return position, consumed — but only by the gallery that opened it.
   *
   * One-shot by construction: a matching read clears it, so a later unrelated
   * visit to the same gallery is not yanked back to an item somebody looked at
   * once. A NON-matching scope takes nothing and leaves it in place, so the
   * gallery it belongs to can still claim it.
   */
  takeReturnPosition(scopeKey: string): ViewerReturnPosition | null {
    const position = this.returnValue;
    if (position === null || position.scopeKey !== scopeKey) return null;
    this.returnValue = null;
    return position;
  }

  // Account switch / logout. Unlike close this keeps NOTHING: a pending return
  // anchor is one account's browsing position, and the next account must not
  // inherit it.
  reset(): void {
    this.snapshotValue = null;
    this.returnValue = null;
  }
}
