# Owner-private document sources

The `user-documents` pipeline (extraction → chunking → embeddings → hybrid
retrieval) was built around `FileItem`. A second kind of owner-private source,
archived email, needs the same engine without becoming a `FileItem`. This
contract is the seam between the two: the engine owns chunking, embeddings,
indexes, ranking and the job; a source kind owns identity, liveness and text.

Status: implemented end to end in Document Intelligence v1. Only the `file`
kind ships in the engine; any other owner-private kind (`email`, later) gets
extraction from sections, chunking, embeddings, lexical + semantic retrieval,
search results and Assistant citations by **registering one
`IOwnerDocumentSource`** — no engine edit. `DocumentSourceKinds`
(`NubArca.Api.Domain.Ai`) is the one spelling of the discriminator. Proved by
`DocumentSourceKindEngineTests` with a fake sections kind.

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

Each kind supplies one owner-prefiltered, composable EF query
(`NubArca.Api.Ai.Documents.Sources`):

```csharp
public interface IOwnerDocumentSourceEligibility
{
    string Kind { get; }
    IQueryable<EligibleSourceRow> Eligible(AppDbContext db, Guid ownerUserId);
}

public sealed class EligibleSourceRow
{
    public string SourceKind { get; init; }   // stamped by the engine; a kind leaves it unset
    public Guid SourceId { get; init; }
    public Guid OwnerUserId { get; init; }    // the LIVE owner, off the kind's own row
    public string DisplayName { get; init; }  // what a citation and a search hit show
    public Guid? LiveBlobObjectId { get; init; } // `file` only: the blob the file names NOW
    public string? LiveRevision { get; init; }   // every other kind: its revision NOW
}
```

The query must be translatable for SQLite and PostgreSQL and end in one
`new EligibleSourceRow { SourceId, OwnerUserId, DisplayName, LiveBlobObjectId,
LiveRevision }` projection that sets all five members (the unused revision
member explicitly to null).

**Live content revision.** A reading answers only while it was made from the
revision the source has now, checked in the same eligibility query: for `file`
`document_texts.SourceBlobObjectId == FileItem.BlobObjectId`, for every other
kind `document_texts.SourceRevision == LiveRevision`, where `LiveRevision` is
the same string the kind's candidates carry as `RevisionHash`. The job demotes
a superseded reading only when it re-reads the source; between a replacement
(a new upload over a file, an edited or re-imported message) and that job, the
old reading is still `IsCurrent`, and this clause is what keeps it out of
search, the lexical corpus, vector retrieval, embedding and the Assistant. A
branch that supplies no revision matches nothing. `IOwnerDocumentSource` extends this interface, so a
registered source is always an eligibility branch.

- `file`: owner matches, not deleted, not in the Private Vault, in the media
  library, active (today's `OwnerDocumentEligibility`).
- `email` (future): owner matches, message status complete, archive exists and
  is not deleted.

`OwnerDocumentEligibility.EligibleChunks(db, kinds, owner, allowedSourceIds,
allowedKinds)` is the union of the registered kinds' branches (one branch, no
UNION, when only `file` is registered; otherwise a UNION ALL of small scalar
source rows, one SELECT per registered kind), joined to `document_texts` on
`(SourceKind, SourceId)` with the current, completed reading and the owner on
the chunk, the reading and the source row all required, and the reading's
revision equal to the source's live revision. It returns the chunk,
its reading and the source's kind, id, live owner and display name. Embedding,
the lexical corpus, vector retrieval, search and the Assistant all read through
it; the kinds come from `OwnerDocumentSources.Eligibility` (DI), and a caller
built outside DI gets `OwnerDocumentEligibility.FileKindOnly`. Derived rows are
never authority: deleting or hiding a source removes its answers on the next
query because its branch stops matching, whether or not cleanup has run.

Visual retrieval stays `file`-only (the renderer needs a file's bytes and the
visual index is keyed by `FileItem`); another kind is answered by the text
signals alone.

## Indexing entry

A kind registers an `IOwnerDocumentSource` (scoped, in DI, next to
`FileItemDocumentSource`):

- `Kind` — the discriminator.
- `Eligible(db, owner)` — the boundary above.
- `ListCandidatesAsync(owner, afterSourceId, take)` — keyset page of
  `(SourceId, RevisionHash, DisplayName)`; never the whole set.
- `GetCandidateAsync(owner, sourceId)` — one candidate, or null.
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
- optional `SourceKinds` (default: every registered kind; an unregistered kind
  matches nothing);
- optional `AllowedSourceIds`, which can only REMOVE candidates from the
  owner's eligible set (a forged id of another owner is simply not there).

Both travel as `RagQuery.AllowedSourceKinds` / `AllowedSourceIds` (ANDed) and
are applied to candidates of the owner's full eligible corpus, so BM25
statistics are unchanged by a narrowing.

Each hit returns its real `SourceKind`, `SourceId` (the caller's own), display name,
locator (page/slide/sheet/section/email part, chunk ordinal), heading, a
bounded snippet, rank and the retrieval mode. Never vectors, chunk or text row
ids, blob ids, hashes or storage keys.

Email stays out of every system/global corpus; it is owner-private
`user-documents` content with `SourceKind = email`.

## Availability

`GET` status reports, per domain and per kind: whether lexical and semantic
retrieval are available and, when not, a reason token (`disabled`,
`no_profile`, `model_missing`, `not_indexed`, …), so a client can hide
semantic search and fall back to lexical without guessing. One entry per
registered kind; lexical and semantic are reported from the owner's shared
corpus, and visual is `not_applicable` for every kind but `file`.

The Assistant's citations carry the source's `sourceKind` (a category, not an
id) next to the display name and locator; never a source id.
