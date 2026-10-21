# Owner-private document sources

The `user-documents` pipeline (extraction → chunking → embeddings → hybrid
retrieval) was built around `FileItem`. A second kind of owner-private source,
archived email, needs the same engine without becoming a `FileItem`. This
contract is the seam between the two: the engine owns chunking, embeddings,
indexes, ranking and the job; a source kind owns identity, liveness and text.

Status: contract agreed for Document Intelligence v1. Only the `file` kind is
implemented in this release; `email` plugs in later through the same seam.

## Identity

Every derived row (`document_texts`, the per-source processing state, visual
indexes) is keyed by `(SourceKind, SourceId)` with the owner denormalized:

| SourceKind | SourceId | Owner of authority |
|---|---|---|
| `file` | `FileItem.Id` | the live `FileItem` |
| `email` | `MailMessage.Id` | the live `MailMessage` (future) |

`FileItemId` stays on `document_texts` for the `file` kind (nullable for other
kinds), so existing queries and the cascade keep working. New code reads
`SourceKind`/`SourceId`.

## Eligibility is per kind, and is the only boundary

Each kind supplies one owner-prefiltered predicate:

```csharp
IQueryable<Guid> EligibleSourceIds(NubArcaDbContext db, Guid ownerUserId);
```

- `file`: owner matches, not deleted, not in the Private Vault, in the media
  library, active (today's `OwnerDocumentEligibility`).
- `email` (future): owner matches, message status complete, archive exists and
  is not deleted.

`OwnerDocumentEligibility.EligibleChunks` is the union of the per-kind branches.
Derived rows are never authority: deleting or hiding a source removes its
answers on the next query because its branch stops matching, whether or not
cleanup has run.

## Indexing entry

A kind registers an `IOwnerDocumentSource`:

- `Kind` — the discriminator.
- `ListCandidatesAsync(owner, afterSourceId, take)` — keyset page of
  `(SourceId, RevisionHash, DisplayName)`; never the whole set.
- `LoadAsync(owner, sourceId)` — either raw bytes plus a format hint (the
  `file` kind: the extractors decide the format from the bytes) or already
  normalized SECTIONS, each with text, a heading and a typed locator (email:
  subject, from/to, date, body).
- `RevisionHash` is the idempotence key: unchanged hash, nothing is re-derived.

The engine chunks (never across a locator unit), embeds with the domain's
profile and records per-source, per-phase state. Missing state = implicit
pending.

## Change signal

`IDocumentSourceChangeSink.SourceChanged(kind, ownerUserId, sourceId)` records
"new, changed or removed" and returns immediately; the background job does the
work. Callers (upload, import) never wait for extraction or embeddings. Removal
does not need a call to be safe, because eligibility already hides the source.

## Query

The owner-private search service accepts, server-side only:

- the query text and a limit;
- optional `SourceKinds` (default: all kinds the caller's surface allows);
- optional `AllowedSourceIds`, which can only REMOVE candidates from the
  owner's eligible set (a forged id of another owner is simply not there).

Each hit returns `SourceKind`, `SourceId` (the caller's own), display name,
locator (page/slide/sheet/section/email part, chunk ordinal), heading, a
bounded snippet, rank and the retrieval mode. Never vectors, chunk or text row
ids, blob ids, hashes or storage keys.

Email stays out of every system/global corpus; it is owner-private
`user-documents` content with `SourceKind = email`.

## Availability

`GET` status reports, per domain and per kind: whether lexical and semantic
retrieval are available and, when not, a reason token (`disabled`,
`no_profile`, `model_missing`, `not_indexed`, …), so a client can hide
semantic search and fall back to lexical without guessing.
