// Canonical query serialization (§42).
//
// Sharing a TypeScript interface is not enough: two clients can agree on the
// SHAPE of a query and still put different things on the wire. So the
// parameter builders live here too, and every client calls them.
//
// Parameters are produced as an ORDERED list of pairs rather than a
// URLSearchParams. Two reasons, both practical:
//   * React Native's URLSearchParams is a polyfill with its own gaps, and this
//     package must not assume any one runtime's implementation;
//   * a deterministic order makes cross-client parity assertable as exact
//     equality, instead of "the same set, somehow".

export type QueryParams = ReadonlyArray<readonly [string, string]>;

/** Accumulates parameters in insertion order, skipping absent values. */
export class QueryBuilder {
  private readonly pairs: Array<[string, string]> = [];

  set(key: string, value: string): this {
    this.pairs.push([key, value]);
    return this;
  }

  /** Set only when defined — `null`/`undefined` mean "the caller said nothing". */
  setOptional(key: string, value: string | null | undefined): this {
    if (value !== null && value !== undefined && value !== '') this.set(key, value);
    return this;
  }

  setBool(key: string, value: boolean | null | undefined): this {
    if (value !== null && value !== undefined) this.set(key, value ? 'true' : 'false');
    return this;
  }

  setNumber(key: string, value: number | null | undefined): this {
    if (value !== null && value !== undefined && Number.isFinite(value)) {
      this.set(key, String(value));
    }
    return this;
  }

  /** A comma-joined id list, omitted entirely when empty. */
  setIdList(key: string, values: readonly string[] | null | undefined): this {
    if (values !== null && values !== undefined && values.length > 0) {
      this.set(key, values.join(','));
    }
    return this;
  }

  build(): QueryParams {
    return this.pairs.slice();
  }
}

/** Encode pairs into a query string, without the leading '?'. */
export function toQueryString(params: QueryParams): string {
  return params
    .map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(v)}`)
    .join('&');
}

/** `path` with the parameters appended, or bare when there are none. */
export function withQuery(path: string, params: QueryParams): string {
  const qs = toQueryString(params);
  return qs === '' ? path : `${path}?${qs}`;
}
