import { PRINT_SELECTION_MAX } from '@nubarca/contracts';

// Where a selection's Print goes: the studio, composing from the selected
// photographs only, in the order they were selected. Videos are left out and
// counted, so the studio can say so. Past PRINT_SELECTION_MAX photographs it
// goes nowhere: their ids travel in the URL, which has to stay loadable.

export function printStudioPath(
  selected: readonly string[],
  items: readonly { id: string; kind: string }[],
  albumId: string | null,
): string | null {
  const kindOf = new Map(items.map((it) => [it.id, it.kind] as const));
  const files = selected.filter((id) => kindOf.get(id) === 'image');
  if (files.length > PRINT_SELECTION_MAX) return null;
  const videos = selected.filter((id) => kindOf.get(id) === 'video').length;
  const query = new URLSearchParams();
  if (albumId) query.set('album', albumId);
  query.set('files', files.join(','));
  if (videos > 0) query.set('videos', String(videos));
  return `/print?${query.toString()}`;
}
