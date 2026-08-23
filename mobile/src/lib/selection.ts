// Multi-select id set for gallery selection mode (pure, node --test-able).
//
// Selection is ID-based and survives harmless rerenders. It must NOT survive
// logout or an authenticated-user change — the session provider calls
// `clear()` on both events, and this class carries no identity of its own.

export class IdSelection {
  private ids: ReadonlySet<string> = new Set();

  static of(...ids: string[]): IdSelection {
    const s = new IdSelection();
    s.ids = new Set(ids);
    return s;
  }

  get size(): number {
    return this.ids.size;
  }

  has(id: string): boolean {
    return this.ids.has(id);
  }

  values(): string[] {
    return [...this.ids];
  }

  toggle(id: string): IdSelection {
    const next = new Set(this.ids);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    this.ids = next;
    return this;
  }

  selectMany(ids: Iterable<string>): IdSelection {
    const next = new Set(this.ids);
    for (const id of ids) next.add(id);
    this.ids = next;
    return this;
  }

  clear(): IdSelection {
    this.ids = new Set();
    return this;
  }
}
