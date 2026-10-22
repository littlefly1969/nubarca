# outlook-pst 1.2.0, patched for NubArca

This is `outlook-pst` 1.2.0 (MIT, Microsoft,
https://github.com/microsoft/outlook-pst-rs) as published on crates.io,
`src/` and `README.md` unchanged except for the one change below. The
examples and dev-dependencies are left out because they are not built here.
`Cargo.toml` above `[patch.crates-io]` wires it in place of the registry
crate; the engine's exact `=1.2.0` pin still applies.

## The change

`src/ltp/heap.rs`, `HeapNodePageMap::new`: the published crate rejects a
heap page whose `HNPAGEMAP.cFree` differs from the number of zero-length
allocations it counts. Real PSTs do not keep that invariant: in the EDRM
Enron PSTs every message's recipient table has `cFree = 0` with zero-length
allocations, so the crate reported `Invalid HNPAGEMAP cFree: 0x0000` and every
message lost its To/Cc/Bcc. libpff and the Java/JS ports do not check the
field. The patch ignores `cFree`; the allocation offsets (count, order,
bounds) are still validated exactly as before.

## Upgrading

On a new upstream release, check whether the check is still there. If it is
fixed upstream, drop this directory and the `[patch.crates-io]` entry; if not,
re-apply the change to the new release and update this file.
