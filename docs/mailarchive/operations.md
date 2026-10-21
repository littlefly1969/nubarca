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

## Health checks

```bash
$DC exec api dotnet NubArca.Api.dll storage blobs audit-references   # message documents are counted
$DC exec api /opt/nubarca/bin/nubarca-pst-importer probe --pst <a PST inside the container>
```

`GET /api/mail/status` answers `available`, `disabled` or `importer_missing`.

## Rollback

Migration `AddMailArchive` is additive (see `deploy/migration-policy.json`).
While the previous application runs against the upgraded schema, do **not** run
its `storage blobs repair-references`: it does not know `mail_messages` and
would release the message documents.

## Measured performance (engine, release build, 4-core Xeon 2.8 GHz)

| Case | Time | Peak RSS |
|---|---|---|
| `list`, 20,000 messages in 53 folders (60 MiB PST) | 75 ms | 3.4 MiB |
| `serve`, all 20,000 messages + 2,000 attachments | 6.3 s (~3,190 msg/s) | 11.2 MiB |
| One 256 MiB attachment, streamed and hashed | 1.8 s (140 MiB/s) | 4.1 MiB |

Attachment bytes are streamed block by block, so engine memory does not grow
with attachment size. The .NET side processes one message at a time.

## Compatibility

Supported and verified on real files: Unicode PST (no encryption, permute,
cyclic), folders, messages, To/Cc/Bcc, text and HTML bodies, by-value
attachments (inline CID, duplicate names, zero-byte, multi-block, Unicode
names).

Implemented, covered by unit tests only: embedded messages, compressed RTF
(used when there is no text or HTML), transport headers, 8-bit strings with
code pages, ANSI PSTs.

Not supported: 4K-page Unicode PSTs (reported `unsupported`), OST files,
repair of damaged files, HTML recovered from RTF, content of by-reference and
OLE attachments (described with `unsupported_method`).

## Security notes

- The engine opens the working copy read-only; the source FileItem is never
  touched. Paths returned by the engine are resolved inside the request
  directory or rejected.
- No endpoint takes a server path: imports start from a `fileItemId` the
  caller owns.
- HTML bodies are sanitized on the server with an allow-list (no script,
  style, frames, forms, event handlers, remote images or `javascript:` links;
  `cid:` images resolve to the owner's attachment file) and rendered in a
  sandboxed iframe without scripts and with a restrictive CSP.
- Engine stderr goes to debug logs only, truncated; error messages stored on
  runs and items are sanitized and capped. Subjects, bodies, addresses and
  file names are never logged.
