# nubarca-pst-importer

A standalone, **read-only** Rust engine that reads Outlook PST files for the
NubArca mail archive (MAILARCHIVE-01). It implements §1 (CLI and protocol
`nubarca-pst/1`) and §2 (the normalized `nubarca-mail-message` v1 document) of
[`docs/mailarchive/contract.md`](../../docs/mailarchive/contract.md). The .NET
side runs it as a child process. It never links into the API, opens no network
connections, and never writes to the PST.

## Build

```bash
cd tools/nubarca-pst-importer
cargo build --release          # target/release/nubarca-pst-importer
cargo fmt --check
cargo clippy --all-targets -- -D warnings
cargo test --workspace
```

Build settings:

- Rust edition 2021, MSRV 1.82. The workspace pins every dependency exactly and commits `Cargo.lock`.
- The PST reader is `outlook-pst =1.2.0`, used as a thin adapter (no fork). RTF decompression uses `compressed-rtf =1.0.1`.
- The release profile keeps `panic = "unwind"` and `overflow-checks = true`, so arithmetic faults on corrupt input become classified per-item errors instead of wrong data.

The workspace member `fixture-gen/` (`outlook-pst-rw =1.2.14`) writes test
PSTs. It is a dev-dependency only and is never linked into the importer.

## Protocol summary

```
nubarca-pst-importer probe   --pst FILE
nubarca-pst-importer list    --pst FILE
nubarca-pst-importer serve   --pst FILE --work-dir DIR
nubarca-pst-importer extract --pst FILE --message m:XXXXXXXX --out-dir DIR
```

Stdout is NDJSON, one event per line. Stderr carries only short, content-free
diagnostics. Exit codes: `0` ok, `2` fatal input error (a `fatal` event was
printed), `3` usage error, `1` internal error.

### Commands

- **`probe`** prints one `probe` event: format (unicode or ansi), encryption, store display name.
- **`list`** walks the folder tree depth-first, parents first. It prints a `folder` event, then that folder's `message` events, then its children, and ends with `end` (folder, message and error counts).
  - Unreadable items produce `error` events with a scope and a code. The walk continues.
  - Ids are stable within one file: `f:<nid hex8>` and `m:<nid hex8>`.
- **`extract`** writes `message.json` plus `att-<key>.bin` files into `--out-dir`, then prints one `extracted` event. That event lists each file's relative path, byte size and SHA-256.
- **`serve`** reads requests from stdin, one per line: `{"op":"extract","requestId":"…","messageId":"m:…"}` or `{"op":"shutdown"}`.
  - Responses come back in request order and are flushed per line.
  - Each request writes into `<work-dir>/<requestId>/`. `requestId` must match `^[A-Za-z0-9_-]{1,64}$`, and that directory must not already exist.
  - A failed request removes its directory. A bad request gets an `error` event with code `bad_request` and the server keeps going. EOF is treated as shutdown.

### Error codes

The same codes are used per item and in `fatal`: `not_found`, `corrupt`,
`too_large`, `unsupported`, `io_error`, `bad_request`, `limit_exceeded`.

### Document (`nubarca-mail-message` v1)

- **Fields:** subject, from, sender, to/cc/bcc, replyTo, dates, messageId/inReplyTo/references, importance/sensitivity/flags, transport headers, bodies (`text`, `html`, and `rtf` when no other body exists), an attachment descriptor list, embedded messages recursively, and `warnings`.
- **No binary in the document:** attachment bytes go only to files. Attachment keys are positional, with `"<parent>.<i>"` for attachments of embedded messages.

## Examples

```bash
B=target/release/nubarca-pst-importer
$B probe --pst tests/fixtures/basic.pst
$B list  --pst tests/fixtures/basic.pst
$B extract --pst tests/fixtures/basic.pst --message m:00200004 --out-dir /tmp/out
printf '%s\n' '{"op":"extract","requestId":"r1","messageId":"m:00200004"}' '{"op":"shutdown"}' \
  | $B serve --pst tests/fixtures/basic.pst --work-dir /tmp/work
```

(`m:00200004` is the first message of `basic.pst`; take ids from `list` output.)

## Limits

| Flag | Default | Effect when exceeded |
|---|---|---|
| `--max-attachment-bytes` | 536870912 | descriptor with `extracted:false`, `skipReason:"too_large"`, no file |
| `--max-body-bytes` | 16777216 (per body part) | body truncated at a UTF-8 boundary, recorded in `truncated` |
| `--max-document-bytes` | 33554432 | drop rtf, then html, then text, then headers (warning `document_truncated`); still too big gives per-item `too_large` |
| `--max-embedded-depth` | 3 (hard cap 16) | embedded attachment described with `skipReason:"depth_exceeded"` |
| `--max-folder-depth` | 64 | `error` event `limit_exceeded` for the deeper subtree |
| `--max-attachments-per-message` | 500 | further attachments described with `skipReason:"limit_exceeded"` (up to 4× the limit), then warning `attachments_limit_exceeded` |

Internal safety bounds, not configurable:

| Bound | Value | Notes |
|---|---|---|
| Recipients | 10,000 per message | warning beyond that |
| Transport headers | 256 KiB | warning `headers_truncated` |
| Short string properties | 64 KiB | |
| Tables | 256 MiB | |
| Heap | 16 MiB per node | |
| Sub-nodes | 1M per node | |
| Attachment descriptors | 20,000 per document | |
| `serve` request line | 64 KiB | |

## Big attachments: streaming strategy

`outlook-pst`'s attachment API loads the whole attachment into memory, and
uses several copies on the way. `examples/measure_library_attachment.rs`
measured **~772 MiB peak RSS for one 256 MiB attachment** (VmHWM 791060 kB).

The importer does not use that path for attachment data:

1. It resolves the attachment's data node.
2. It walks the data tree itself (XXBLOCK → XBLOCK → leaf blocks of at most 8 KiB, with a level check and a visited set).
3. It decodes each leaf through the library's block reader and streams it to the output file through a SHA-256 hashing writer.

The total size declared by the data tree is checked against
`--max-attachment-bytes` **before** any file is created. The limit is also
enforced while writing.

## Measurements

Machine: 4-core Intel Xeon @ 2.80 GHz, 15 GB RAM, Linux 6.18, cargo 1.97,
release build, temp dirs on the local disk. Peak RSS is the process's VmHWM, printed
when `NUBARCA_PST_REPORT_PEAK_RSS=1` is set.

**Large attachments.** Run with
`NUBARCA_PST_LARGE_BYTES=268435456 cargo test --release --test large -- --nocapture`.

| Case | Time | Peak RSS |
|---|---|---|
| 64 MiB attachment | 0.61 s (105 MiB/s) | 3232 KiB |
| 256 MiB attachment | 1.83 s (140 MiB/s), SHA matches the generator | 4156 KiB |
| `too_large` path | — | ~2.9 MiB |
| Library attachment API, same 256 MiB (for comparison) | — | ~772 MiB |

**Volume corpus.** Run with
`cargo test --release --test volume -- --ignored --nocapture`.

The corpus has 20,000 messages in 53 folders, with an attachment on every
10th message. Generating it (60.4 MiB PST) took 3.8 s.

| Operation | Time | Throughput | Peak RSS |
|---|---|---|---|
| `list` | 75 ms | ~266k msg/s | 3468 KiB |
| `serve` extracting all 20,000 | 6.27 s | ~3190 msg/s; 2000 attachment files, 34.6 MiB written | 11468 KiB |
| `probe` on the 63 MB file | ~0.03 s | — | — |

Files are not fsynced. Durability is the caller's responsibility after it
imports the files. An earlier per-file `fsync` made the volume run take
about 43 s.

## Read-only guarantee and robustness

- **Read-only open:** the PST is opened `O_RDONLY`. The library's `open()` also requests write access, so the engine is built from `PstFile::read_from` on a read-only handle.
- **Tests confirm it:** they compare SHA-256, size and mtime before and after every command, and check the open flags via `/proc/<pid>/fdinfo`.
- **B-tree validation at open:**
  - Levels must strictly decrease.
  - No page may be visited twice.
  - The page count must not exceed what the file size allows.

  This prevents infinite recursion in the library on cyclic trees.
- **Size budgets:** nodes are size-checked before they are handed to the library's table reader.
- **Panic recovery:** every per-item operation runs under `catch_unwind`. After a recovered panic, the engine is reopened. The panic hook prints only a source location, never content.
- **Tests:**
  - Truncation at many lengths.
  - 144 corrupted byte ranges.
  - A crafted self-referencing node B-tree with a valid CRC.
  - Invalid and missing inputs.

  Every case yields valid NDJSON, exit 0 or 2, and one response per `serve` request.
- **Fuzz results:** manual fuzz runs (1000 iterations, random and CRC-fixed) found no crashes. Nine library panics (u16 overflow in `outlook-pst` `ndb/read_write.rs`) were recovered and classified `corrupt`.

## Test fixtures

- **`tests/fixtures/basic.pst`** (513 KiB) is generated by `fixture-gen` (`cargo run -p nubarca-pst-fixture-gen -- basic tests/fixtures/basic.pst`). It contains:
  - Nested folders.
  - Plain, HTML+text and international messages.
  - Inline CID images.
  - Duplicate attachment names.
  - A zero-byte attachment.
  - A multi-block attachment.
  - To/Cc/Bcc recipients.
- **`tests/fixtures/empty-outlook-permute.pst`** is `examples/Empty.pst` from the `outlook-pst` crate (MIT licence, © Microsoft Corporation). It is a real Outlook-created, permute-encoded store.
- **Generated at test time (not committed):**
  - The large-attachment and volume PSTs.
  - Permute- and cyclic-encoded copies of `basic.pst` (`fixture-gen` re-encodes blocks and recomputes the CRCs).

## Support matrix and residual limitations

**Verified end-to-end on real PST files:**

- Unicode format.
- Encryption: none, permute and cyclic.
- Folders, messages, recipients, text and HTML bodies.
- Attachments (with SHA-256), inline CID, duplicate names, zero-byte and multi-block data.
- Unicode text.
- Reopening a message by id, and the limits.

**Covered by unit tests only (no real fixture available):**

- Embedded messages.
- Compressed RTF bodies.
- Transport headers.
- Hidden attachments.
- 8-bit string properties with code pages.
- `by_reference`/`ole` attachment methods.
- **ANSI (97–2002) PSTs:** headers are recognised, and the ANSI engine shares the same code path (compiled for ANSI types), but no ANSI file was read end-to-end.

**Not supported:**

- **Unicode 4K-page PSTs** (version 36) are reported as `unsupported`.
- **RTF:** decompressed only when the message has neither a text nor an HTML body. The engine does not de-encapsulate HTML from RTF and does not detect "RTF is the only faithful body".
- **Attachments:** only `by_value` attachments and embedded messages are extracted. Other methods (`by_reference`, `by_reference_only`, `ole`) are described with `skipReason:"unsupported_method"`.
- **`inline`:** derived from a content id or the hidden-rendering flag. Rendering position is ignored.
- **Timestamps:** second precision, UTC.
- **`hasAttachments`:** comes from the attachment table. The message flag is used only if the table is unreadable.
- **Table memory:** the library's table reader materialises all rows of a table, bounded by the 256 MiB budget. Memory for huge folders is therefore bounded but not constant.
- **Loop detection:** sub-node and data-tree loop detection exist, but no test exercises them with a crafted file.
- **Not attempted:** password-protected stores (not tested), OST files, and repairing damaged files.

## Contract interpretations

These are places where the contract leaves room, and how the engine reads it:

- `serve` answers `shutdown` with `{"type":"shutdown"}` before it exits 0.
- One-shot `extract` failures print a `fatal` event carrying the per-item code, and exit 2.
- A pre-existing `<work-dir>/<requestId>` gives `bad_request`, so requests never merge into existing output.
- Fatal open errors reuse the per-item codes: not a PST → `unsupported`, missing file → `not_found`, damaged structure → `corrupt`.
- **`from`/`sender`:** `from` is the "represented" address. When it has no address and its name matches the sender (or is empty), the sender's address is used and `sender` is null. Otherwise `sender` is set only when it differs from `from`.
- Attachment `sizeBytes` is the actual data size for `by_value`, and `PR_ATTACH_SIZE` otherwise.
