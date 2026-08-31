// @nubarca/contracts — the one client-side definition of NubArca's domain
// vocabulary. See README.md for what may and may not live here.
//
// There is deliberately NO transport in this package: no fetch, no cookies, no
// session. Each client keeps its own, because transports legitimately differ
// between a browser, a phone and a television. Meanings do not.

export * from './query.ts';
export * from './media.ts';
export * from './album.ts';
