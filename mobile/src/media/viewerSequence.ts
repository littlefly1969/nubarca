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

export interface ViewerSequenceSnapshot {
  slides: ViewerSlide[];
  focusedKey: string;
  index: number;
}

export class ViewerSequenceModel {
  private snapshotValue: ViewerSequenceSnapshot | null = null;

  // What the gallery should look at when it comes back. Set on close, taken
  // once, and never accompanied by the slides themselves — the sequence is
  // dropped, only the identity of the last item survives.
  private returnAnchorValue: string | null = null;

  snapshot(): ViewerSequenceSnapshot | null {
    return this.snapshotValue;
  }

  open(slides: ViewerSlide[], focusedKey: string): void {
    const index = Math.max(
      0,
      slides.findIndex((s) => s.key === focusedKey),
    );
    if (slides.length === 0) {
      this.reset();
      return;
    }
    this.snapshotValue = { slides, focusedKey, index };
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
    this.returnAnchorValue = this.snapshotValue?.focusedKey ?? null;
    this.snapshotValue = null;
  }

  /**
   * The item the gallery should return to, consumed.
   *
   * One-shot by construction: reading it clears it, so a later unrelated visit
   * to the same gallery is not yanked back to an item somebody looked at once.
   */
  takeReturnAnchor(): string | null {
    const anchor = this.returnAnchorValue;
    this.returnAnchorValue = null;
    return anchor;
  }

  // Account switch / logout. Unlike close this keeps NOTHING: a pending return
  // anchor is one account's browsing position, and the next account must not
  // inherit it.
  reset(): void {
    this.snapshotValue = null;
    this.returnAnchorValue = null;
  }
}
