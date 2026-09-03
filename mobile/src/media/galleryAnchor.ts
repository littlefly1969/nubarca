// Where a gallery is looking, expressed the only way that survives a relayout:
// as the ID of a media item.
//
// Both position commands in the gallery — coming back from the viewer, and
// keeping your place when the column count changes — are the same three steps:
// stable item ID, flat index, one scroll. This is the middle step, and it is
// the whole of it. There is deliberately no position service, no geometry and
// no pixels here: the list owns those, and the last engine that tried to own
// them alongside it is what this replaced.

export function indexOfItemId<T extends { id: string }>(
  items: readonly T[],
  id: string | null,
): number {
  if (id === null) return -1;
  return items.findIndex((item) => item.id === id);
}
