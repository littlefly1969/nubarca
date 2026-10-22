# MailArchive — operations

MailArchive imports Outlook PST files offline and lets their owner read,
search and delete the messages. It is owner-private: nothing in it reaches
another user, a public share, a log line or a diagnostic. The shared contract
(identities, protocol, tables, API) is [contract.md](contract.md); this page is
what an operator needs to run it.

## Components

| Piece | Where | Role |
|---|---|---|
| `nubarca-pst-importer` | `tools/nubarca-pst-importer`, installed in the api image at `/opt/nubarca/bin/nubarca-pst-importer` | Read-only Rust engine (Microsoft's `outlook-pst` reader). Speaks `nubarca-pst/1` over stdin/stdout. Never writes a PST. |
| `mail.import` job | worker, Normal priority | Copies the source PST to a private working copy, scans folders and messages, extracts each message through one long-lived engine session, stores the normalized document and creates attachment files. Sliced, checkpointed, cancellable and resumable. |
| `/api/mail/*` | api | Status, sources, archives, runs, failures, folders, message search and detail, delete, file origins. Every route is owner-scoped and `no-store`. |
| `/mail` | web | Imports tab, mail view, documents view and structured search. |

## How data is kept

- A **message** is a `mail_messages` row that owns one content-addressed
  document blob (the normalized JSON, `nubarca-mail-message` v1). It is never a
  `FileItem`, so no generic file route can rename, move or rewrite it. There is
  no update route at all: messages are read, searched and deleted.
- An **attachment** becomes an ordinary `FileItem` under
  `Posta/<archive>/<folder path>` in the owner's files, with normal SHA-256
  dedup (the same bytes in two messages are one blob, two files). The
  `mail_attachments` link holds the `FileItemId` only, never a blob reference,
  and is `ON DELETE SET NULL`.
- **Deleting a message** removes its row, its links and its document blob
  reference in one transaction and writes a tombstone keyed by the archive's
  fingerprint and the message's stable PST key. Its attachments stay in Files.
  Re-importing the same PST never brings the message back.
- **Deleting an attachment file** goes through the normal file lifecycle. The
  message then shows the attachment as "deleted from NubArca"; it is never
  recreated by a later import of the same archive (the upload-operation claim
  that created it is already completed).
- The same PST uploaded twice is one archive (matched by a peppered content
  fingerprint, not by name). Message-ID is stored for display and search only;
  it is never used for dedup.

## Configuration

Section `MailArchive` (environment form `MailArchive__Key`):

| Key | Default | Notes |
|---|---|---|
| `Enabled` | `true` | `false` hides nothing but refuses new imports (`disabled`). |
| `ImporterPath` | `/opt/nubarca/bin/nubarca-pst-importer` | Missing binary: status `importer_missing`, imports refused, nothing else affected. |
| `WorkRootPath` | `<Storage:RootPath>/mail-work` | Holds one read-only working copy of the PST per active run plus extracted parts. Deleted when the run ends. Must be on a volume with room for the largest PST. |
| `MaxAttachmentBytes` | 512 MiB | Larger attachments are described, not extracted (`too_large`). |
| `MaxBodyBytes` | 16 MiB | Per body part; truncation is recorded and shown. |
| `MaxDocumentBytes` | 32 MiB | Normalized document cap. |
| `MaxEmbeddedDepth` | 3 | Nested attached messages. |
| `MaxAttachmentsPerMessage` | 500 | |
| `ExtractTimeoutSeconds` | 300 | Per message. |
| `ListTimeoutMinutes` | 120 | Whole-archive scan. |
| `ScanBatchSize` | 500 | Rows per scan commit. |
| `AttachmentRootFolderName` | `Posta` | Top folder for attachment files. |
| `MaxEngineMemoryBytes` | 2 GiB | Engine address-space ceiling (`RLIMIT_AS`); 0 = none. |
| `MaxEngineCpuSeconds` | 3600 | Engine CPU time per process (`RLIMIT_CPU`; one `serve` session covers a whole slice); 0 = none. |
| `RequireFilesystemSandbox` | `true` | Refuse to parse without Landlock: the run fails `sandbox_unavailable`. `false` only on a kernel without Landlock, after reading "Parser isolation". |
| `EngineRunAs` | `65534:65534` | `uid:gid` the engine switches to when the worker runs as root. Empty = no switch. |

### Large PST files

The PST must first be an owner file. Two existing limits apply to that upload:

- `Storage__MaxUploadBytes` (default 2 GiB) — per-file storage cap.
- `Uploads__MaxFileSizeBytes` (default 10 GiB) — resumable upload cap.

Raise both for archives larger than 2 GiB. During an import the work volume
needs the PST's size again (the working copy) plus the largest message's
parts; the job checks free space before copying and fails the run with
`insufficient_work_space` rather than filling the disk.

Attachment files count against the owner's quota like any upload; a quota
failure fails that message (retryable), not the run. Message documents are
small and are not counted against the quota.

## Running an import

1. Upload the `.pst` to Files (any route: web upload, resumable upload, admin
   import or staging — the result is always an owner file).
2. Open **Posta → Importazioni → Importa PST** and pick the file, or
   `POST /api/mail/archives {"fileItemId": …}`.
3. Follow the run: phase, counters, failures. **Interrompi** cancels
   cooperatively; **Riprendi / Reimporta** resumes from where it stopped. A
   message already imported is never imported twice; a tombstoned message is
   skipped.

A run that ends `partial` lists its failed messages (folder path, position,
code). Re-import retries only those.

A run can also end `partial` because the **scan** was incomplete (`errorCode`
`scan_errors`): some folder or message could not be enumerated. The run panel
shows how many and which folders. Such a scan is never trusted as final: the
next re-import scans the PST again and imports whatever has become readable
(for example after an engine upgrade); messages already imported are not
touched. Only a scan without errors is final, and later runs then skip it.

## Health checks

```bash
$DC exec api dotnet NubArca.Api.dll storage blobs audit-references   # message documents are counted
$DC exec api /opt/nubarca/bin/nubarca-pst-importer probe --pst <a PST inside the container> --run-as 65534:65534
```

The probe's `sandbox` field reports the confinement in effect; every run also
logs it (worker log, `MailArchive engine sandbox: …`).

`GET /api/mail/status` answers `available`, `disabled` or `importer_missing`.

## Rollback

Migration `AddMailArchive` is additive (see `deploy/migration-policy.json`).
While the previous application runs against the upgraded schema, do **not** run
its `storage blobs repair-references`: it does not know `mail_messages` and
would release the message documents.

## Parser isolation

The PST is untrusted input parsed by native code, so the engine is confined
before it reads the first byte. Layers, all applied by the engine itself
(`tools/nubarca-pst-importer/src/sandbox.rs`) on every invocation:

| Layer | What it enforces | If unavailable |
|---|---|---|
| Empty environment | The worker starts it with **no** environment variable and `/` as working directory: no connection string, secret, token or proxy setting is inherited. | — (always) |
| Unprivileged user | Started as root (the container default), it hands its work directory to `EngineRunAs` (65534 = `nobody`) and switches real, effective and saved uid/gid, dropping supplementary groups. It then checks it can still read the PST copy and write its directory, or fails up front. | fatal `sandbox_unavailable` |
| No privileges | Capability bounding and ambient sets emptied, every capability cleared, `PR_SET_NO_NEW_PRIVS`. | fatal |
| Filesystem (Landlock ABI 3) | Read: only the run's PST working copy. Read/write: only its own work directory (`<WorkRoot>/runs/<run>/serve`, or the extract out dir). Everything else, including other runs and the storage objects, is denied. | fatal by default (`RequireFilesystemSandbox`) |
| Network and processes (seccomp) | `socket`, `connect`, `bind`, `listen`, `accept`, `execve`, `fork`/`clone`, `ptrace`, `mount`, `unshare`, `bpf`, `io_uring`, module and key syscalls answer `EPERM`. | fatal |
| Resources (rlimits) | Address space `MaxEngineMemoryBytes`, CPU `MaxEngineCpuSeconds`, file size (largest attachment/document + 1 MiB), 64 descriptors, no core dumps. Each message also has the `ExtractTimeoutSeconds` watchdog (the process is killed). | fatal |

Requirements: Linux ≥ 5.13 with Landlock enabled (`lsm=…,landlock`; ABI 3
needs 6.2, older kernels report `partial`) and Docker's default seccomp profile,
which allows the `landlock_*` and `seccomp` syscalls. The work root must be
traversable by `EngineRunAs` (directories `0755` or `0711` up to it); the
default `<Storage:RootPath>/mail-work` is, unless the storage root itself is
`0700`. In that case either open the traversal bit on the storage root or
point `MailArchive__WorkRootPath` to a dedicated volume.

Verify on the host, inside the api container:

```bash
$DC exec api sh -c 'd=$(mktemp -d) && mkdir $d/work $d/outside && chmod 755 $d && echo x > $d/outside/secret && \
  cp /dev/null $d/x.pst && /opt/nubarca/bin/nubarca-pst-importer sandbox-check --pst $d/x.pst \
  --work-dir $d/work --outside-read $d/outside/secret --outside-dir $d/outside --landlock required --run-as 65534:65534; rm -rf $d'
```

Expected: `"user":"switched"`, `"landlock":"full"`, every check `denied` except
`writeInside`, and `"environment":[]`. Measured on Docker 29 with the
`mcr.microsoft.com/dotnet/aspnet:10.0` image, both as root (`switched`) and as
uid 1654 (`unprivileged`). Tests: `tools/nubarca-pst-importer/tests/sandbox.rs`
(each layer from inside the real binary, including the uid switch when run as
root) and `MailArchiveRealEngineTests` (the process exactly as the worker
starts it: empty environment, flags, confined).

The boundary back into the worker is part of the isolation: what the engine
writes is read without following links (see Security notes), so a compromised
parser cannot make the unconfined worker import a file it could not read
itself (`EngineFilesTests`,
`A_compromised_engine_cannot_make_the_worker_import_a_file_outside_its_staging`).

Not covered: a kernel exploit reachable through the allowed syscalls (reading
and writing files, memory management). That boundary is the kernel's.

## Search

The field filters (`from`, `to`, `subject`) look in the sender, the
recipients (To/Cc/Bcc, name and address) and the subject as a
case-insensitive substring. On PostgreSQL each of the three columns has a
trigram GIN index (`pg_trgm`, created by the `AddMailArchive` migration; a
trusted extension, no superuser needed), and the owner filter is applied in
the same query. Terms shorter than three characters cannot use a trigram
index and scan the owner's messages.

The quick search (`q`) matches those three fields as above **or** the message
text. The text (`mail_message_texts`, one row per message, written in the
same transaction that completes it and deleted with it) is the plain-text
body, or the visible text of the HTML body when there is none, followed by
the subject and text of every embedded (forwarded) message; lower-cased,
whitespace-collapsed, capped at 100,000 characters. RTF-only bodies are not
converted. It is derived from the message's document and only ever searched,
never returned. The text matches when it holds every word of the term (letters
and digits, two characters or more, at most eight words) as a word prefix:
PostgreSQL full-text search with the `simple` configuration (no stemming, no
stop words, accents significant: «perche» does not find «perché»), through the
GIN expression index `ix_mail_message_texts_fts` on
`to_tsvector('simple', "Text")` created by `AddMailMessageTexts`. The header
and body matches are two separate top-N queries merged by date, and the body
side is ordered by the text row's own copy of the date, so PostgreSQL always
takes the matches from the full-text index instead of walking the owner's
mail by date and testing each text.

Measured on PostgreSQL 16 with 100,000 messages (and their texts) over two
owners: the field filters are a Bitmap Index Scan on their trigram index,
1–8 ms execution; `q` is a BitmapOr of the three trigram indexes plus a
Bitmap Index Scan on the full-text index, 6–16 ms execution, 20–65 ms end to
end through EF; no sequential scan of either table
(`MailArchivePostgresTests.Search_on_postgres_uses_the_trigram_and_full_text_indexes_and_stays_owner_scoped`).
A very common word prefix (one that most messages contain) makes the body side
sort every match; that is the cost of correct date order and stays linear in
the number of matches.

Messages imported by a build without `mail_message_texts` have no text row
and are found by their header fields only; importing the same PST again does
not add it (the messages are already present). A text backfill is not part of
this release.

## Measured performance (engine, release build, 4-core Xeon 2.8 GHz)

| Case | Time | Peak RSS |
|---|---|---|
| `list`, 20,000 messages in 53 folders (60 MiB PST) | 75 ms | 3.4 MiB |
| `serve`, all 20,000 messages + 2,000 attachments | 6.3 s (~3,190 msg/s) | 11.2 MiB |
| One 256 MiB attachment, streamed and hashed | 1.8 s (140 MiB/s) | 4.1 MiB |

End to end through the API, job and storage (real binary, SQLite test host,
same machine): 2,000 messages in 53 folders with 200 attachments imported in
27 s, about 74 messages/s.

On PostgreSQL 16 (Testcontainers, same machine; real binary; generated PSTs
with 10% of messages carrying an attachment; opt-in test
`MailArchivePostgresTests.Volume_import_end_to_end_on_postgres` with
`NUBARCA_MAIL_VOLUME_PST`): 1,000 messages at 68 msg/s and 5,000 at 75–94
msg/s, against 39–40 msg/s before batching; roughly 18–25 minutes per 100,000
messages. Items are processed in groups of up to 25 (never past the job
slice's item budget): one round trip marks the group importing, and its
messages without attachments complete together in one transaction (their
document blobs, rows, texts and manifest items); the checkpoint only moves
past committed groups, so a crash redoes the group with no duplicate. A
message with attachments still commits on its own after its files. What
remains is the per-blob cost of the content-addressed store (a shared storage
lock, the reference-count update and the row, about 7 ms per document) and
the attachment files' own ingestion; going further needs a batched blob API in
the core storage, which this feature does not change.

Attachment bytes are streamed block by block, so engine memory does not grow
with attachment size.

Attachment names: a clash in the target folder costs at most two indexed
lookups (the original name, then the source-derived name), independent of how
many same-named files the folder holds. 300 messages each with an
`image001.png` in one folder: all imported with unique names, no fallback scan,
about 94 ms per message end to end on the SQLite test host (including file
creation and the post-ingestion pipeline).

## Compatibility

Supported and verified on real files: Unicode PST (no encryption, permute,
cyclic), folders, messages, To/Cc/Bcc, text and HTML bodies, by-value
attachments (inline CID, duplicate names, zero-byte, multi-block, Unicode
names).

Verified end to end on a third-party PST: an EDRM Enron mailbox (Unicode,
permute encoding, 13 MiB, 8 folders, 71 messages with text bodies and 70
by-value attachments, as shipped in the `pst-extractor` npm package's test
data; not committed). Through the real engine, the worker and PostgreSQL:
71/71 messages and 70 attachments imported, every message with its
recipients (127) and a text row, reference audit clean. It exposed a reader
bug: `outlook-pst` 1.2.0 rejected every recipient table of that file
(`HNPAGEMAP cFree` not matching its zero-length allocations, which real
writers do not keep), so all To/Cc/Bcc were lost with a
`recipients_unreadable` warning. The engine now builds a vendored copy of the
reader with that one check relaxed (`tools/nubarca-pst-importer/vendor/outlook-pst/NUBARCA-PATCH.md`).
OST files from the same package are refused as `unsupported`, as designed.

Implemented, covered by unit tests only: embedded messages, compressed RTF
(used when there is no text or HTML), transport headers, 8-bit strings with
code pages, ANSI PSTs.

Not supported: 4K-page Unicode PSTs (reported `unsupported`), OST files,
repair of damaged files, HTML recovered from RTF, content of by-reference and
OLE attachments (described with `unsupported_method`).

## Security notes

- The engine opens the working copy read-only; the source FileItem is never
  touched.
- The worker reads the engine's output (`message.json`, attachment parts)
  through `EngineFiles` only: the reference must name a plain path below the
  request directory, it is opened relative to the staging directory with
  `openat2(RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS | RESOLVE_NO_MAGICLINKS |
  RESOLVE_NO_XDEV)` (a per-component `O_NOFOLLOW` walk on kernels without
  openat2), and the opened descriptor must be a regular file with one link.
  Everything is then read from that descriptor, so there is no check-then-open
  window. A link, FIFO, directory or escape fails the message with
  `unsafe_engine_output` and restarts the engine session; nothing it pointed
  at is read. Cleaning the staging never follows a link the engine left.
- No endpoint takes a server path: imports start from a `fileItemId` the
  caller owns.
- HTML bodies are sanitized on the server with an allow-list (no script,
  style, frames, forms, event handlers, remote images or `javascript:` links;
  `cid:` images resolve to the owner's attachment file) and rendered in a
  sandboxed iframe without scripts and with a restrictive CSP.
- Engine stderr goes to debug logs only, truncated; error messages stored on
  runs and items are sanitized and capped. Subjects, bodies, addresses and
  file names are never logged.
