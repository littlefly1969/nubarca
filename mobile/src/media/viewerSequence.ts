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
  // Video: FULLY-BUILT source (uri + cookie header snapshot), never rebuilt
  // by the viewer; null when there is no playable source. Shared HLS sources
  // carry contentType:'hls' because their route has no .m3u8 extension.
  // Shared HLS sources carry the explicit container type expo-video needs
  // when the route has no .m3u8 extension.
  videoSource: {
    uri: string;
    headers: { cookie: string };
    contentType?: 'hls';
  } | null;
  posterUrl: string | null;
}

export interface ViewerSequenceSnapshot {
  slides: ViewerSlide[];
  focusedKey: string;
  index: number;
}

export class ViewerSequenceModel {
  private snapshotValue: ViewerSequenceSnapshot | null = null;

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

  setIndex(index: number): void {
    if (this.snapshotValue === null) return;
    if (index < 0 || index >= this.snapshotValue.slides.length) return;
    this.snapshotValue = { ...this.snapshotValue, index };
  }

  // Close the viewer. After this call nothing about any previous sequence is
  // reachable from this object.
  close(): void {
    this.snapshotValue = null;
  }

  // Account switch / logout: identical to close, named for intent. Both names
  // exist so call sites read correctly at a glance.
  reset(): void {
    this.close();
  }
}
