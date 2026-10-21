# MailArchive — shared contract (MAILARCHIVE-01)

Status: **frozen** for MAILARCHIVE-01. Changing anything below requires the
coordinator to update this file first; components never diverge silently.

MailArchive imports Microsoft Outlook PST archives into NubArca for offline,
owner-private consultation. Three components meet at this contract:

| Component | Location | Talks to |
|---|---|---|
| PST engine (Rust) | `tools/nubarca-pst-importer/` | stdin/stdout + a work directory (§2) |
| Backend (.NET) | `src/NubArca.Api/MailArchive/`, `Domain/Mail*.cs` | the engine as a child process; PostgreSQL; existing storage/FileItem services |
| Frontend (React) | `frontend/src/mail/` | the owner-private HTTP API (§5) |

## 1. Identities

| Concept | Identity | Notes |
|---|---|---|
| PST source | an owner's active `FileItem` whose first 4 bytes are `!BDN` | the only supported way in (upload, resumable upload, admin import or staging all end as a FileItem) |
| Source fingerprint | `ContentFingerprint` (HMAC-SHA256 of the blob SHA-256, scheme `hmac-sha256-v1`) | internal only; never in an API, log or DTO. Same bytes ⇒ same archive |
| Archive | `mail_archives` unique `(OwnerUserId, SourceFingerprint)` | a modified PST (different bytes) is a different archive (a different PST *version*) |
| Folder | engine `folderId` = `f:` + 8 lowercase hex of the PST NID | unique per archive |
| Message | engine `messageId` = `m:` + 8 lowercase hex of the PST NID | unique per archive. **Never** the Internet `Message-ID` |
| Attachment | `key` = dotted path of 0-based attachment indexes: `"0"`, `"2"`, `"2.0"` (attachment 0 of the message embedded as attachment 2) | unique per message |
| Tombstone | `(OwnerUserId, SourceFingerprint, SourceMessageKey)` | written when the owner deletes a message |

The engine identifiers (`f:…`, `m:…`, attachment keys) are internal source
coordinates. They are stored, never shown to users and never accepted from the
HTTP API.

## 2. Engine protocol `nubarca-pst/1`

Binary: `nubarca-pst-importer`. Read-only: the PST is opened read-only and is
never written, locked for writing, truncated or touched. Output is UTF-8
NDJSON (one JSON object per line) on **stdout**; human diagnostics go to
stderr and never contain message content. Attachment bytes are never inside
JSON (no base64).

Exit codes: `0` success; `2` fatal input error (a `fatal` event was printed);
`3` usage error; `1` internal error.

Every invocation accepts limits (defaults in brackets):
`--max-attachment-bytes` [536870912], `--max-body-bytes` [16777216] per body
part, `--max-document-bytes` [33554432], `--max-embedded-depth` [3],
`--max-folder-depth` [64], `--max-attachments-per-message` [500].
Exceeding a limit is a classified per-item outcome, never a crash and never
unbounded memory.

### 2.1 `probe --pst FILE`

One line:

```json
{"type":"probe","protocol":"nubarca-pst/1","format":"unicode","encryption":"none","storeDisplayName":"Personal Folders","fileSize":271360}
```

`format`: `unicode` | `ansi`. `encryption`: `none` | `permute` | `cyclic`.

### 2.2 `list --pst FILE`

Streams the folder hierarchy below the IPM subtree, depth-first, parents
before children, then each folder's messages in contents-table order:

```json
{"type":"folder","folderId":"f:00008022","parentFolderId":null,"name":"Top of Outlook data file","path":["Top of Outlook data file"],"depth":0,"containerClass":"IPF.Note","contentCount":0}
{"type":"message","messageId":"m:00200024","folderId":"f:00008062"}
{"type":"error","scope":"folder","id":"f:000080a2","code":"corrupt","message":"folder table unreadable"}
{"type":"end","folders":7,"messages":42,"errors":1}
```

`parentFolderId` is `null` only for the IPM subtree root. `error.scope`:
`folder` | `message`. A `list` that cannot finish prints `fatal`.

### 2.3 `serve --pst FILE --work-dir DIR`

A long-lived extraction server. Requests on stdin, one JSON per line; one
response line per request, in order. The engine writes files only below
`DIR/<requestId>/` (`requestId` matches `^[A-Za-z0-9_-]{1,64}$`, anything else
is a `bad_request`). The caller deletes that directory after consuming it.

```json
{"op":"extract","requestId":"r42","messageId":"m:00200024"}
{"op":"shutdown"}
```

Success:

```json
{"type":"extracted","requestId":"r42","messageId":"m:00200024","document":"r42/message.json","documentBytes":5123,
 "attachments":[{"key":"0","file":"r42/att-0.bin","sizeBytes":20480,"sha256":"<64 hex>"}],
 "warnings":[{"code":"body_truncated","message":"html body truncated at limit"}]}
```

Failure (the server stays up):

```json
{"type":"error","requestId":"r42","messageId":"m:00200024","code":"not_found","message":"no such message"}
```

Per-item error codes: `not_found` | `corrupt` | `too_large` | `unsupported` |
`io_error` | `bad_request` | `limit_exceeded`.

`extract --pst FILE --message m:… --out-dir DIR` is the same extraction as a
one-shot command, for operators and tests.

### 2.4 Normalized message document `nubarca-mail-message` v1

`message.json` — the content NubArca stores as the message's immutable blob.
It never contains attachment bytes.

```json
{
  "format": "nubarca-mail-message", "version": 1,
  "source": {"messageId": "m:00200024", "folderId": "f:00008062"},
  "messageClass": "IPM.Note",
  "subject": "Quarterly report", "conversationTopic": "Quarterly report",
  "from":   {"name": "Ada Example", "address": "ada@example.test", "addressType": "SMTP"},
  "sender": null,
  "to":  [{"name": "Bob", "address": "bob@example.test", "addressType": "SMTP"}],
  "cc":  [], "bcc": [],
  "sentAt": "2024-03-01T10:00:00Z", "receivedAt": "2024-03-01T10:00:05Z",
  "createdAt": null, "modifiedAt": null,
  "internetMessageId": "<abc@example.test>", "inReplyTo": null, "references": null,
  "importance": 1, "sensitivity": 0, "flags": 1, "hasAttachments": true,
  "transportHeaders": null,
  "body": {"text": "…", "html": "…", "rtf": null, "truncated": {"text": false, "html": false, "rtf": false}},
  "attachments": [
    {"key": "0", "method": "by_value", "fileName": "report.pdf", "displayName": "report.pdf",
     "mimeType": "application/pdf", "contentId": null, "inline": false, "hidden": false,
     "sizeBytes": 20480, "extracted": true, "skipReason": null, "embeddedMessage": null},
    {"key": "1", "method": "embedded_message", "fileName": null, "displayName": "Fwd: hello",
     "mimeType": null, "contentId": null, "inline": false, "hidden": false, "sizeBytes": 4096,
     "extracted": false, "skipReason": null,
     "embeddedMessage": {"…same fields as a message, without format/version/source…": true}}
  ],
  "warnings": []
}
```

- Timestamps: ISO-8601 UTC or `null`. Strings are UTF-8 (ANSI PSTs decoded via
  their code page). `rtf` is the **decompressed** RTF, only when the message
  has neither text nor HTML body, or when it is the only faithful body.
- `method`: `by_value` | `embedded_message` | `by_reference` |
  `by_reference_only` | `ole` | `unsupported`.
- `extracted: true` ⇔ an attachment file is listed in the `extracted` response
  with the same `key`. `skipReason` when not extracted: `unsupported_method` |
  `too_large` | `corrupt` | `limit_exceeded` | `depth_exceeded`. An embedded
  message is described inline (bounded by `--max-embedded-depth`); its own
  binary attachments are extracted with keys `"<parent>.<i>"`.
- Nothing is invented: a missing property is `null`, never a guess.

## 3. Persistence model (backend)

Tables (snake_case, all owner-scoped, `OwnerUserId` on every row):

- `mail_archives` — source, owner, fingerprint, display name, format, counts.
  Unique `(OwnerUserId, SourceFingerprint)`. `SourceFileItemId` nullable,
  `ON DELETE SET NULL` (deleting the PST file never deletes the archive).
- `mail_import_runs` — one import execution: job id, status, phase, counters,
  sanitized error. At most one active (`queued`/`running`) run per archive
  (filtered unique index).
- `mail_import_items` — the persisted manifest, **per archive** (it survives
  runs): unique `(ArchiveId, SourceMessageKey)`, `Ordinal` (scan order, keyset
  key), status, attempts, sanitized failure, created message id.
- `mail_folders` — original folder tree: unique `(ArchiveId, SourceFolderKey)`.
- `mail_messages` — one message occurrence in one archive: unique
  `(ArchiveId, SourceMessageKey)`; queryable metadata; `BlobObjectId` → the
  normalized document; `Status` `importing` | `complete`. Only `complete` rows
  are ever visible.
- `mail_attachments` — `(MessageId, AttachmentKey)` unique; original name, MIME,
  Content-ID, inline flag, size, `FileItemId` nullable `ON DELETE SET NULL`.
  It holds **no** blob reference.
- `mail_deletion_tombstones` — unique
  `(OwnerUserId, SourceFingerprint, SourceMessageKey)` plus the deleted
  message id and time. Nothing else (no subject, no address).

No `MailOccurrence` table: a `mail_messages` row already *is* one occurrence
(one archive, one folder). The same e-mail in two PSTs is two rows whose
document blobs deduplicate physically through the normal SHA-256 path.

### Import item statuses

`pending` → `importing` → `imported` | `failed` | `skipped_deleted` |
`deleted`. `failed` is retried by a later run (attempts counted). `deleted`
and `skipped_deleted` are final for that fingerprint.

### Run statuses / phases

Status: `queued` | `running` | `succeeded` | `partial` | `failed` |
`cancelled`. Phase while running: `preparing` | `scanning` | `importing` |
`finalizing`. `partial` = finished with at least one failed message.

## 4. Immutability strategy (gate)

The message content is **not** a `FileItem`. It follows the established
owner-private pattern of Plates and the Aesthetics Lab: an owner table row
(`mail_messages.BlobObjectId`) owns one reference to a content-addressed blob,
is counted by `BlobReferenceAuditService` (so `audit-references` /
`repair-references` see it), is protected by a `RESTRICT` foreign key, and is
released in the same transaction as the row's hard delete.

Consequences:

1. No generic endpoint can reach a message: every `/api/files/*`, folder,
   album, share, vault, organizer, export and duplicate-cleanup path operates
   on `FileItem`/`Folder` ids, and no `FileItem` points at a message document.
2. MailArchive exposes no update route (no PATCH/PUT/rename/move). The API
   only lists, reads, searches and deletes.
3. The core is unchanged: the only edit outside MailArchive is one more owner
   table in `BlobReferenceAuditService` (the extension point every owner table
   uses).
4. The Private Vault is not used.

## 5. HTTP API (owner-private)

All routes `RequireAuthorization()`, owner from the cookie, foreign or missing
ids → `404`. JSON camelCase. Errors: `{ "error": "<code>" }`. Dates ISO-8601
UTC. No storage key, hash, fingerprint, path, NID or raw document is ever
returned.

```ts
type MailStatus = { available: boolean; reason: 'ok' | 'disabled' | 'importer_missing' };

type MailSourceCandidate = { fileItemId: string; name: string; sizeBytes: number; createdAt: string; alreadyImportedArchiveId: string | null };

type MailRun = {
  id: string; archiveId: string;
  status: 'queued' | 'running' | 'succeeded' | 'partial' | 'failed' | 'cancelled';
  phase: 'preparing' | 'scanning' | 'importing' | 'finalizing' | null;
  totalMessages: number; processedMessages: number;
  importedMessages: number; alreadyPresentMessages: number;
  skippedDeletedMessages: number; failedMessages: number;
  attachmentsCreated: number; attachmentsReused: number; attachmentsSkipped: number;
  cancellationRequested: boolean;
  errorCode: string | null; errorMessage: string | null;
  createdAt: string; startedAt: string | null; completedAt: string | null;
};

type MailArchive = {
  id: string; displayName: string; format: 'unicode' | 'ansi' | null;
  sourceAvailable: boolean; sourceFileName: string | null;
  messageCount: number; folderCount: number;
  createdAt: string; lastImportedAt: string | null;
  latestRun: MailRun | null;
};

type MailFolder = { id: string; parentId: string | null; name: string; depth: number; messageCount: number };

type MailAddress = { name: string | null; address: string | null };

type MailMessageSummary = {
  id: string; archiveId: string; folderId: string;
  subject: string | null; from: MailAddress | null; toSummary: string | null;
  date: string | null; hasAttachments: boolean; attachmentCount: number; preview: string | null;
};

type MailMessagePage = { items: MailMessageSummary[]; nextCursor: string | null };

type MailAttachmentView = {
  id: string; name: string; mimeType: string | null; sizeBytes: number | null;
  inline: boolean; contentId: string | null; embeddedPath: string | null;
  state: 'available' | 'file_deleted' | 'not_extracted';
  skipReason: string | null;
  fileItemId: string | null;   // when available: open via /api/files/{id}/content
  currentFileName: string | null;
};

type MailEmbeddedMessage = { path: string; subject: string | null; from: MailAddress | null; date: string | null; bodyText: string | null };

type MailMessageDetail = MailMessageSummary & {
  archiveName: string; folderPath: string[];
  to: MailAddress[]; cc: MailAddress[]; bcc: MailAddress[];
  sentAt: string | null; receivedAt: string | null;
  internetMessageId: string | null; messageClass: string | null;
  body: { kind: 'html' | 'text' | 'rtf' | 'none'; html: string | null; text: string | null; truncated: boolean };
  attachments: MailAttachmentView[];
  embeddedMessages: MailEmbeddedMessage[];
};

type MailFileOrigin = { messageId: string; archiveId: string; archiveName: string; subject: string | null; from: MailAddress | null; date: string | null; attachmentName: string };
```

| Method & route | Result |
|---|---|
| `GET /api/mail/status` | `MailStatus` |
| `GET /api/mail/sources` | `{ items: MailSourceCandidate[] }` — the owner's active PST files (by `.pst` name), newest first, max 200 |
| `POST /api/mail/archives` body `{ fileItemId }` | `202 { archive: MailArchive, run: MailRun }`; `400 not_pst`; `404`; `409 { error: "import_running", runId }`; `503 unavailable` |
| `GET /api/mail/archives` | `{ items: MailArchive[] }` |
| `GET /api/mail/archives/{id}` | `MailArchive` |
| `POST /api/mail/archives/{id}/import` | `202 MailRun` (resume/re-import); `409 import_running`; `409 source_unavailable` (PST file gone or changed) |
| `GET /api/mail/archives/{id}/folders` | `{ items: MailFolder[] }` (whole tree, parents first) |
| `GET /api/mail/runs/{id}` | `MailRun` |
| `GET /api/mail/runs/{id}/failures?cursor=` | `{ items: [{ folderPath: string[]; position: number; code: string; message: string | null }], nextCursor }` |
| `POST /api/mail/runs/{id}/cancel` | `202 MailRun`; `409 not_active` |
| `GET /api/mail/messages` | `MailMessagePage`. Query: `archiveId`, `folderId`, `q` (subject/from/recipients), `from`, `to` (any recipient), `subject`, `after`, `before` (ISO dates, inclusive/exclusive), `hasAttachments`, `cursor`, `limit` (1–100, default 50). Order: date desc, then id. Keyset cursor (opaque). |
| `GET /api/mail/messages/{id}` | `MailMessageDetail` |
| `DELETE /api/mail/messages/{id}` | `204` (also when already deleted by this owner) |
| `GET /api/mail/file-origins/{fileItemId}` | `{ items: MailFileOrigin[] }` (empty when the file has no live origin) |

HTML bodies are sanitized **server-side** (allow-list: no script, style
elements, iframes, objects, forms, event handlers, `javascript:` or remote
URLs). `cid:` references resolve to `/api/files/{fileItemId}/content` of the
owner's live attachment file, and are dropped otherwise. The frontend renders
the HTML in a sandboxed `iframe` (`sandbox="allow-same-origin"`, never
`allow-scripts`) carrying a CSP that forbids remote loads.

## 6. Attachments and deletion policy

- Each extracted attachment becomes a normal `FileItem` through
  `IFileItemService.CreateAsync` under `Posta/<archive name>/<PST folder path>/`
  (name clash ⇒ `name (2).ext`), followed by the normal post-ingestion pipeline
  (previews, document extraction and indexing as for any upload).
- Crash safety: each attachment is ingested under an `UploadOperation` claim
  keyed `mailarchive:<messageRowId>:<attachmentKey>`; `CreateAsync` completes
  the claim in the same transaction that commits the `FileItem`, so a resumed
  import adopts the file instead of creating a second one.
- The normalized document is stored and the message flips to `complete` in
  ONE transaction (blob reference + row state). Until then it is invisible.
- Deleting a message, in ONE transaction: write the tombstone, delete its
  `mail_attachments` rows, mark the manifest item `deleted`, delete the
  message row, release its document blob reference (janitor grace applies).
  Attachment `FileItem`s and their blob references are untouched.
  `IBlobService.ReleaseAsync` is never called for an attachment link.
- A re-import of the same fingerprint skips tombstoned messages
  (`skipped_deleted`) and already complete ones (`alreadyPresent`).
- A renamed or moved attachment stays linked (by `FileItemId`). A trashed one
  shows `file_deleted`; a purged one has `FileItemId` set null by the database.

## 7. Out of scope (MAILARCHIVE-01)

Online mail protocols, writing PSTs, editing mail, full conversation
threading, a MailArchive RAG domain, semantic search over mail, archive-wide
deletion.
