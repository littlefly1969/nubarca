// The selection on a television. Pure and node-testable; the React binding is
// useTvSelection.ts.
//
// One store per scope, exactly like the web:
//   Personal Area — the owner's selection, shared by the library and every
//                   album opened in the same Personal session, and dropped when
//                   the Personal Area locks (nothing picked survives a lock).
//   party album   — the TV session's own picks inside that album, never mixed
//                   with anybody's library.
//
// The grid tiles, the viewer's OK and the action bar all read and write the
// same store, so a tick placed in the viewer is on the tile when BACK returns
// there.
export type TvSelectionChange =
  | { kind: 'add'; ids: string[] }
  | { kind: 'remove'; ids: string[] }
  | { kind: 'clear' };

export class TvSelectionStore {
  private ids: readonly string[] = [];
  private set: ReadonlySet<string> = new Set();
  private version = 0;
  private readonly listeners = new Set<() => void>();
  private readonly changeListeners = new Set<(change: TvSelectionChange) => void>();

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  };

  onChange(listener: (change: TvSelectionChange) => void): () => void {
    this.changeListeners.add(listener);
    return () => { this.changeListeners.delete(listener); };
  }

  /** Changes on every mutation; a cheap snapshot key for React. */
  getVersion = (): number => this.version;

  get count(): number { return this.ids.length; }

  list(): readonly string[] { return this.ids; }

  has(id: string): boolean { return this.set.has(id); }

  /** Returns true when the id is selected afterwards. */
  toggle(id: string): boolean {
    if (this.set.has(id)) {
      this.commit(this.ids.filter((x) => x !== id));
      this.emit({ kind: 'remove', ids: [id] });
      return false;
    }
    this.commit([...this.ids, id]);
    this.emit({ kind: 'add', ids: [id] });
    return true;
  }

  clear(): void {
    if (this.ids.length === 0) return;
    this.commit([]);
    this.emit({ kind: 'clear' });
  }

  /** Adopt the authoritative membership from a mirror (the server). Not echoed. */
  adopt(ids: readonly string[]): void {
    const unique = ids.filter((id, i) => ids.indexOf(id) === i);
    if (unique.length === this.ids.length && unique.every((id, i) => this.ids[i] === id)) return;
    this.commit(unique);
  }

  private commit(ids: readonly string[]): void {
    this.ids = ids;
    this.set = new Set(ids);
    this.version += 1;
    for (const listener of this.listeners) listener();
  }

  private emit(change: TvSelectionChange): void {
    for (const listener of this.changeListeners) listener(change);
  }
}
