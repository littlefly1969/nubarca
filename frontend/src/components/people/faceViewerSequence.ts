// What happens to an open face viewer when one of its faces stops being a
// candidate — the owner ignored it, moved it, or removed it.
//
// The viewer is CONTROLLED: its caller owns `faceIds` and `index`, so the caller
// is the only place that can drop a face from the sequence. Every People surface
// that opens the viewer has to make the same decision, and getting it slightly
// different in each one is how a viewer ends up sitting on a face that no longer
// exists — asking the server for it again on every refresh tick.
//
// The rule: drop the face; if anything is left, stay at the same POSITION (which
// is now the next face, or the last one if it was the last); otherwise there is
// nothing to look at and the viewer closes (null).

export interface FaceViewerSequence {
  faceIds: string[];
  index: number;
}

export function withoutFace(
  current: FaceViewerSequence | null,
  faceId: string,
): FaceViewerSequence | null {
  if (current === null) return null;
  const faceIds = current.faceIds.filter((id) => id !== faceId);
  if (faceIds.length === 0) return null;
  return { faceIds, index: Math.min(current.index, faceIds.length - 1) };
}
