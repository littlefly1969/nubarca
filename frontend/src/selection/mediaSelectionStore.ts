import type { MediaItem } from '@nubarca/api-client';

// THE selection, as a value that outlives the screen showing it.
//
// A selection used to be component state inside one media workspace, so
// changing tab, album, filter or page — anything that remounted or refetched the
// wall — silently dropped it. The rule now is the Google Photos one: what you
// picked stays picked until you act on it or clear it. That needs the selection
// to live ABOVE the screens, in one store per scope:
//
//   account   — the owner's library and albums: one selection for the whole
//               account (see AccountSelectionProvider)
//   album     — a shared album or a party: personal to the member, circumscribed
//               to that album and never mixed with the private library
//   print     — a print composition: belongs to the sheet being composed
//
// Every surface — grid, viewer, tray — reads and writes the same store, so a
// tick placed in the viewer is on the grid tile the moment the viewer closes,
// and the counter can never disagree with either.
//
// The store keeps ORDER (the order things were picked, which is the order the
// tray shows them in) and, per id, the last MediaItem seen for it. The snapshot
// is what lets the action model reason about media that is no longer loaded on
// the current wall (a photo picked in another album is still a photo). It is a
// display cache only: every operation is re-checked by the server.

export type SelectionChange =
  | { kind: 'add'; ids: string[] }
  | { kind: 'remove'; ids: string[] }
  | { kind: 'clear' };

export interface MediaSelectionSnapshot {
  readonly ids: readonly string[];
  readonly set: ReadonlySet<string>;
  readonly items: ReadonlyMap<string, MediaItem>;
}

const EMPTY: MediaSelectionSnapshot = { ids: [], set: new Set(), items: new Map() };

export class MediaSelectionStore {
  private snapshot: MediaSelectionSnapshot = EMPTY;
  private readonly listeners = new Set<() => void>();
  private readonly changeListeners = new Set<(change: SelectionChange) => void>();

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  };

  // Local mutations, for whoever mirrors the selection somewhere else (the
  // server-backed current selection). Changes ADOPTED from that mirror are not
  // reported back to it.
  onChange(listener: (change: SelectionChange) => void): () => void {
    this.changeListeners.add(listener);
    return () => { this.changeListeners.delete(listener); };
  }

  getSnapshot = (): MediaSelectionSnapshot => this.snapshot;

  get count(): number { return this.snapshot.ids.length; }

  has(id: string): boolean { return this.snapshot.set.has(id); }

  toggle(id: string, item?: MediaItem): boolean {
    if (this.has(id)) { this.remove([id]); return false; }
    this.add([id], item ? [item] : []);
    return true;
  }

  add(ids: readonly string[], items: readonly MediaItem[] = []): void {
    const fresh = ids.filter((id, i) => !this.snapshot.set.has(id) && ids.indexOf(id) === i);
    const known = this.mergeItems(items, new Set([...this.snapshot.ids, ...fresh]));
    if (fresh.length === 0 && known === this.snapshot.items) return;
    this.commit([...this.snapshot.ids, ...fresh], known);
    if (fresh.length > 0) this.emit({ kind: 'add', ids: fresh });
  }

  remove(ids: readonly string[]): void {
    const drop = new Set(ids.filter((id) => this.snapshot.set.has(id)));
    if (drop.size === 0) return;
    const items = new Map(this.snapshot.items);
    for (const id of drop) items.delete(id);
    this.commit(this.snapshot.ids.filter((id) => !drop.has(id)), items);
    this.emit({ kind: 'remove', ids: [...drop] });
  }

  clear(): void {
    if (this.snapshot.ids.length === 0) return;
    this.commit([], new Map());
    this.emit({ kind: 'clear' });
  }

  // Replace the selection outright (keeps snapshots of ids that survive). Used
  // to keep only what a bulk action could not process.
  replace(ids: readonly string[]): void {
    const keep = new Set(ids);
    const removed = this.snapshot.ids.filter((id) => !keep.has(id));
    const added = ids.filter((id, i) => !this.snapshot.set.has(id) && ids.indexOf(id) === i);
    if (removed.length === 0 && added.length === 0) return;
    const items = new Map([...this.snapshot.items].filter(([id]) => keep.has(id)));
    this.commit(ids.filter((id, i) => ids.indexOf(id) === i), items);
    if (removed.length > 0) this.emit({ kind: 'remove', ids: removed });
    if (added.length > 0) this.emit({ kind: 'add', ids: added });
  }

  // Refresh the display snapshot of every SELECTED id among these items. Never
  // selects anything: a wall calls it with each page it loads.
  remember(items: readonly MediaItem[]): void {
    const known = this.mergeItems(items, this.snapshot.set);
    if (known !== this.snapshot.items) this.commit(this.snapshot.ids, known);
  }

  // Adopt the authoritative membership from a mirror (the server). Not echoed.
  adopt(ids: readonly string[], items: readonly MediaItem[] = []): void {
    const unique = ids.filter((id, i) => ids.indexOf(id) === i);
    const set = new Set(unique);
    const kept = new Map([...this.snapshot.items].filter(([id]) => set.has(id)));
    for (const item of items) if (set.has(item.id)) kept.set(item.id, item);
    const same = unique.length === this.snapshot.ids.length
      && unique.every((id, i) => this.snapshot.ids[i] === id)
      && kept.size === this.snapshot.items.size
      && [...kept].every(([id, item]) => this.snapshot.items.get(id) === item);
    if (same) return;
    this.commit(unique, kept);
  }

  private mergeItems(items: readonly MediaItem[], selected: ReadonlySet<string>): ReadonlyMap<string, MediaItem> {
    let next: Map<string, MediaItem> | null = null;
    for (const item of items) {
      if (!selected.has(item.id) || this.snapshot.items.get(item.id) === item) continue;
      next ??= new Map(this.snapshot.items);
      next.set(item.id, item);
    }
    return next ?? this.snapshot.items;
  }

  private commit(ids: readonly string[], items: ReadonlyMap<string, MediaItem>): void {
    this.snapshot = ids.length === 0 && items.size === 0 ? EMPTY : { ids, set: new Set(ids), items };
    for (const listener of this.listeners) listener();
  }

  private emit(change: SelectionChange): void {
    for (const listener of this.changeListeners) listener(change);
  }
}
