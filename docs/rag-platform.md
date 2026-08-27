# The RAG platform

NubArca has a local, profile-driven, privacy-aware retrieval substrate. It has
explicit **domains**, local **ONNX text embeddings**, **hybrid** lexical/vector
retrieval, source **provenance**, and policy-controlled **evidence flow**.

Product Help is the first consumer and one controlled test domain. It is not the
architecture: no foundational type here is named after it.

## What the platform owns

```text
                         NubArca RAG platform
                                  │
        ┌─────────────────────────┼─────────────────────────┐
        │                         │                         │
   Domain policy             Ingestion/index            Retrieval
        │                         │                         │
        │                  IRagSourceProvider               │
        │                         │                         │
        │                      sources                      │
        │                         │                         │
        │                       chunks                      │
        │                    ┌────┴────┐                    │
        │                    │         │                    │
        │                 lexical   local ONNX              │
        │                    │      embeddings              │
        │                    │         │                    │
        │                    │      pgvector                │
        │                    │         │                    │
        └────────────────────┴────┬────┴────────────────────┘
                                  │
                             hybrid fusion
                                  │
                              evidence
                                  │
                       Assistant / other callers
```

The generative model does not retrieve. It has no database access, no
filesystem, no Git, no vector store and no tools — it is handed a bounded set of
evidence that the platform selected, and it has no way to ask for more.

## Domains

A domain is a body of knowledge with ONE privacy story.

| Domain | Scope | Privacy | Owner required | External generation |
|---|---|---|---|---|
| `product-help` | System | Public | no | **allowed** |
| `nubarca-repository` | System | SystemInternal | no | **denied** |
| `user-documents` | Owner | OwnerPrivate | **yes** | **denied** |

Policy lives in `RagDomainRegistry` — in **code**, not in a database row. The
database records which sources exist and which revision was indexed; it does not
record whether evidence may leave the trust boundary. If it did, one `UPDATE`,
one careless admin endpoint or one backup restored from a fork could turn
`SystemInternal` into `Public`.

`user-documents` is registered as POLICY and is not yet activated: there is no
owner-scoped source provider and no private Assistant operation, and the
Assistant gate refuses owner-private evidence at every trust level until the
operation that derives the owner server-side exists. Registering it first is
deliberate — the definition is the restrictive statement, and every later piece
has to be built against it rather than alongside it.

`nubarca-repository` is not External-safe even though NubArca is public on
GitHub today. Public hosting is a fact about this month, not a property of the
domain: the same code has to stay correct for an installation carrying local
patches, for a private fork, and for whichever system-internal domain is added
next.

The effective ability to use evidence is an intersection:

```text
model trust  ∩  domain policy  ∩  operation policy  ∩  caller permissions
```

`AssistantRagPolicy` owns the second term and is checked over the **evidence
itself**, before a prompt exists — so a retrieval bug that returned repository
chunks to a Product Help caller fails the request instead of leaking them.

## Sources, membership, chunks, embeddings

```text
rag_sources           one CONTENT interpretation of a document
                      identity: (SourceKey, ContentHash, IndexFormatVersion)
rag_domain_sources    membership: this domain uses that content, at this revision
rag_chunks            one retrievable passage
rag_chunk_embeddings  canonical float32 vector per (chunk, profile)

rag_chunk_embedding_vectors_384    pgvector accelerator, derived
```

A source exists **once per interpretation of its bytes**. `docs/help/faces.md` is
one row whether it is only repository knowledge or also approved Product Help,
with one set of chunks and one embedding per profile, and two membership rows.
Adding a domain costs a membership row rather than a second copy of the text and
every vector.

What a source row deliberately does NOT carry is the revision. Content identity
is what the document is, what its bytes are and how NubArca read them; which
snapshot a domain is using those bytes at is that domain's claim, and it lives on
the membership. See "A shared source may hold two snapshots" below for the
release lifecycle that separation exists for.

Domain-specific classification — Product Help's feature name, aliases, audience,
intent and editorial priority — lives on the MEMBERSHIP. It is that domain's
opinion about the document. A C# file does not acquire an `intent=how-to`
because the schema can hold one.

These tables are deliberately separate from `document_texts` /
`document_chunks` / `document_chunk_embeddings`, which are owner/file-scoped
artifacts of a user's own library, and from the photo and face vector tables.
The concept is shared; the ownership semantics and the vector spaces are not.

## Provenance and revision

Every source carries a SHA-256 **content hash**; every domain membership carries
the **revision** that domain is describing.

- `product-help` is REVISION-GATED: an index built from a different revision
  than the running build is refused, because Help that describes a feature this
  installation does not have is worse than no Help.
- `nubarca-repository` is revision-EXPLICIT but not gated: its purpose is asking
  about a checkout, including one that is not what is running. `rag query`
  reports which revision answered.

Content hashes are the idempotence key. A source whose hash has not changed
keeps its chunks; a chunk whose text hash has not changed keeps its embedding.
That is the difference between reindexing after `git pull` costing seconds and
costing an hour of inference.

## Ingestion

`RepositorySnapshotSourceProvider` enumerates **approved tracked files** of a
local checkout:

```text
git ls-files  +  path policy  +  content checks  =  indexable source
```

`git ls-files` is the FIRST gate, not the last. `.git` internals, untracked
files, build output, dependencies, lockfiles, generated bundles, secret material
and binaries are all excluded, and so is the retrieval evaluation set — see
below. `.env.example` is allowed by NAME, as an explicitly classified example.
The suspect words `secret`, `credential`, `password` and `token` disqualify a
CONFIGURATION file and not a source file: applied to everything they excluded
`AddTvPersonalSecretScheme.cs` and `PasswordResetToken.cs`, which are precisely
the answer to "how does NubArca handle credentials". Git runs at index time only — answering a
question never touches a checkout.

`ProductHelpSourceProvider` is a **projection** of the same checkout through
Slice 1's manifest. Unclassified still means *not a member*, not *a low-priority
member*: that rule is why an operations runbook stopped outranking the guidance
somebody asking "how do I use faces?" actually needs.

### Exactly one revision, actually read

The repository provider reads Git OBJECTS at a resolved commit — `ls-tree` for
the tree, `cat-file --batch` for the blobs — not files from the working tree. An
index that stamps a source with a revision has to have read that revision;
otherwise "this is how NubArca works at `943e37b`" describes whatever somebody
had half-edited on disk when the command ran. A commit-ish resolves to a full
40-character SHA before anything is written, so `--revision main` cannot mean
something different next week, and an unresolvable one fails before any row
changes.

Tracked SYMLINKS are refused by mode, and their targets are never resolved,
normalized or read. A link's blob is its target string, so following one imports
whatever that path names — possibly outside the checkout entirely. Submodule
entries are skipped for the same reason: there is no blob to read.

Git runs at index time only.

### The object store is read under a bound, or not at all

`ls-tree` is asked for `-l`, so every entry carries its blob SIZE before anything
is opened:

```text
path eligible  →  mode eligible  →  size known and under the limit  →  read blob
```

That ordering is the point. Size used to be learned by reading the object, so
`too-large` was a verdict delivered after allocating the thing it refused, and a
tracked multi-gigabyte blob was an `OutOfMemoryException` in a service — from a
file nothing was ever going to index. Underneath it, `GitCatFileSession` enforces
its own hard ceiling from the `cat-file` header before `new byte[size]`, because
that number comes from a subprocess and a caller that forgot to check must not be
able to turn it into an allocation.

Every blob read is also TIME-bounded. A `--batch` that stops answering would
otherwise hang an index run forever with no reason code and no way out.

A session that stops mid-response is **dead**. The stream is a single
conversation — one object id in, a header and exactly that many bytes out — so
anything that abandons a response leaves those bytes queued, and the next read
would parse blob content as a header and return every later object as somebody
else's. Resynchronising means consuming what is left, which is the work being
refused. So the process is killed, the session is faulted, and every later call
fails immediately: `git-object-read-timeout`, `git-object-too-large`, sanitized,
carrying no git stderr and no filesystem path.

Cancellation is **not** a timeout. `Task.Delay` linked to the caller's token
completes the instant a run is cancelled, and reading that as "git was too slow"
reported every cancelled index as a repository failure — a permanent-looking
error for something the operator did on purpose. The damage to the stream is the
same and the session dies either way; the reason is not, and an
`OperationCanceledException` reaches the caller as itself.

### Partial runs conclude nothing

`rag index --limit N` is a PARTIAL run, and a partial run may not reconcile.
"I did not see this source" means "it left the snapshot" only if the run could
have seen it — so a capped pass over a complete index would otherwise interpret
everything past the cap as deleted and remove its memberships, a command meant
to do less work destroying most of the index. Completeness is derived from the
REQUEST, never from how many sources were enumerated: inferring it from a count
would make an empty repository look like a complete run that found nothing.
`rag index` reports `partial` and `reconciliation_performed` on every run.

### A shared source may hold two snapshots

Domains sharing a document upgrade **one at a time, in either order**.

The predecessor could not. One row per source key owned the revision AND the
bytes AND the chunks, so indexing `nubarca-repository` at commit B rewrote what
`product-help` was serving at commit A. That was refused — correctly, because
detaching the other domain, preferring the newer revision or duplicating under an
ad-hoc key each pick a winner nobody asked for. But Help could not go first for
exactly the same reason, so two domains sharing a file could only ever move in
one atomic multi-domain reindex, and a release lifecycle with no legal first step
is not a lifecycle.

Splitting content identity from snapshot membership dissolves it:

```text
rev A   repository ─┐
                    ├─ source(faces.md, hash₁)     one row, one set of chunks
        help       ─┘

rev B   repository ─── source(faces.md, hash₁)     bytes unchanged: NOTHING
        help       ─── source(faces.md, hash₁)     is re-derived, revisions
                                                   move independently
```

The common case does not write at all. A file unchanged between A and B is the
same content row, so the second domain's upgrade is one membership revision
moving forward — zero chunks and zero embeddings re-derived.

A file that DID change is the interesting one, and there are two shapes:

- **only this domain uses the row** — it is rewritten IN PLACE, so the
  ordinal-by-ordinal chunk comparison still applies and an edit to one paragraph
  still costs one embedding. This is the ordinary `git pull` case, and forking
  unconditionally would have thrown away every vector of every edited file;
- **another domain is serving the row** — it is never rewritten. A second content
  row is created, the two coexist for exactly as long as the two domains
  disagree, and the old one is deleted when its LAST membership leaves it.

### Mixed revisions fail closed

Indexing commits incrementally, so an interrupted reindex can leave one domain
holding memberships from two commits. There is no honest single revision for that
corpus — not the newest, not the most common, not the first — so retrieval
refuses it with `rag_mixed_revision_index` until a complete reindex converges.
That is a different condition from `rag_revision_mismatch`, which is a coherent
index belonging to a different build: an operator fixes the first by finishing
the reindex and the second by rebuilding the image.

Note what this is measured over. Two **domains** at two revisions is an ordinary
sequential upgrade and is nobody's incoherence; one **domain** at two revisions
is still the thing that fails closed.

### Chunking has a version

A source is reused only when its BYTES and NubArca's reading of them are both
unchanged. `RagIndexFormat.Current` is the second half: change a chunker's
heading rules, teach it a new declaration form, or change which symbols are
extracted, and every already-indexed source would otherwise keep its old chunks
forever — the improvement reaching new files only, and the corpus quietly
becoming a mix of two interpretations. Bumping it is a deliberate act with a
visible cost, and it is never derived from an application version.

## Chunking

Markdown follows sections and carries a heading trail (`Volti › Gruppi
suggeriti`), keeping fenced code blocks whole. Source code uses a deterministic
declaration-aware splitter: a declaration opens a chunk, and the comment written
above it travels with it. Whole-file chunks are never produced — a 700-line
service as one vector is one vector's worth of "this file is about several
things".

## Retrieval

**Lexical** (BM25F) stays first-class. Exact identifiers, configuration keys and
file names are a permanent use case that vectors are worse at, so the lexical
path is never deleted once embeddings work. Each domain has its own ranking
profile: Product Help prefers a user guide over a technical reference for a
how-to question, which would be actively wrong for a domain made of source code.

**Semantic** embeds the question locally and asks pgvector for the nearest
chunks OF THIS DOMAIN under THIS profile. Both filters are pushed into the
query rather than applied to the results.

**Fusion** is Reciprocal Rank Fusion, `k = 60`, over RANKS rather than scores:
BM25F and cosine are not calibrated to the same scale, and min-max normalizing
each result set makes the top score 1.0 whether the best hit was excellent or
merely the least bad.

**The evidence gate** can return *no strong evidence*. It is anchored on the
lexical gate, which has a golden set behind it; a purely semantic candidate
qualifies only at a deliberately high cosine. Below the gate there is no model
call at all, at either trust level — sending a third party a question plus three
irrelevant paragraphs buys a boundary crossing and the answer most likely to be
wrong.

Retrieval modes are observable: `lexical`, `hybrid`, or
`lexical-fallback-<reason>`. A degraded run says it is degraded.

### Semantic is configured PER DOMAIN

One switch for the whole substrate stopped being defensible the moment it was
measured. Against `multilingual-e5-small`, Product Help's MRR goes from 0.938 to
0.969 and the repository's Recall@5 goes from 0.800 **down** to 0.700. Those are
not two opinions about one setting.

```text
Rag__Domains__product-help__SemanticEnabled=true
Rag__Domains__product-help__TextEmbeddingProfileKey=rag-text-multilingual-e5-small-v1

Rag__Domains__nubarca-repository__SemanticEnabled=false

Rag__Domains__user-documents__SemanticEnabled=true
Rag__Domains__user-documents__TextEmbeddingProfileKey=rag-text-multilingual-e5-small-v1
```

`Rag__SemanticEnabled` and `Rag__TextEmbeddingProfileKey` remain as an
installation-wide default, so an installation configured before this existed
keeps working. A domain that says nothing inherits it; a domain that says
`false` is off; and the two are different states, which is why the per-domain
setting is nullable — if "unmentioned" and "false" were the same value, adding a
`Domains` entry for the repository would silently turn Product Help off.

**An OwnerPrivate domain never inherits.** `user-documents` requires both its
switch and its profile key stated explicitly, because "semantic was turned on
for Help eighteen months ago" is not a decision anybody made about a person's
own documents. The rule is derived from the domain's privacy class rather than
from its key, so the next owner-private domain gets it without anyone
remembering to add it to a list.

Indexing and retrieval resolve through the same resolver. They have to: a domain
searched in a coordinate system it was never written into produces cosine
distances between two unrelated spaces, and nothing would report it.

`rag domains` prints each domain's resolved `semantic_enabled` and
`embedding_profile`, so the answer is readable rather than inferred from a global
switch and a fallback rule.

## Embeddings are local

```text
text → local tokenizer → ONNX Runtime → float vector → canonical bytes → pgvector
```

There is no hosted embedding path and nothing downloads weights. Embedding is
how NubArca decides *what to send* to a chat model; routing that decision
through a third party would send the entire corpus — and, when owner-private
domains arrive, a person's own documents — to an external service in order to
work out what is allowed to leave.

The model is `multilingual-e5-small`, 384 dimensions, selected because the
interface is Italian and much of the documentation is English, and because it is
ASYMMETRIC: `query: ` and `passage: ` prefixes force the query/passage seam to
be real from the first commit. Those prefixes are MODEL syntax and are applied by
the provider — RAG states semantic intent (`Query` or `Passage`), never a
literal string.

A missing model file is an availability condition with a reason code, and
retrieval degrades to lexical.

A TIMEOUT is a reason code too, and a resumable one: the text is already
indexed, the embeddings that completed are kept, and re-running the index
continues from where it stopped. The concurrency slot is released when the
native inference actually stops, not when NubArca stops waiting for it —
`Run` is a blocking native call, so releasing on the timeout let the next
caller start a second one immediately and a configured concurrency of 1 could
become N under a slow model.

## Owner-private documents

`user-documents` is the first domain whose knowledge belongs to a PERSON rather
than to the installation. Everything below exists because that one change makes
every previous assumption need re-checking.

```text
FileItem the owner currently owns, is active, is not in the Vault,
is in their media library, and whose type NubArca reads as text
        │
        ▼   local extraction — no PDF, no OCR, no Office, no network
document_texts            (owner + file + extraction profile)
        │
        ▼   deterministic bounded chunking, own format version
document_chunks           (owner denormalized onto every row)
        │
        ▼   local ONNX passage embeddings, profile-scoped
document_chunk_embeddings
        │
        ▼   owner-prefiltered lexical + exact cosine, then RRF
bounded evidence  →  LocalTrusted model  →  grounded answer
```

Private content lives in `document_texts` / `document_chunks` /
`document_chunk_embeddings`, NOT in `rag_sources`. Forcing it through the system
tables for symmetry would put a person's text in the table every system domain
reads, one forgotten `WHERE` away from a cross-owner answer. What is shared is
the CONTRACTS — chunking, embedding, fusion, the evidence gate, domain policy —
not the storage.

### Derived rows are not authority

A `DocumentText`, its chunks and its vectors record an extraction that happened
at some point in the past. Between then and now the file may have been deleted
or moved into the Private Vault. So **every read joins the live `FileItem`**
through `OwnerDocumentEligibility`, and a chunk whose file no longer qualifies is
not in the corpus at all — not filtered out afterwards.

That is deliberately not left to cleanup. A sweeper that deletes orphaned rows
runs on a schedule; a boundary that only holds once it has run fails for as long
as it is behind. Deleting a file removes its answers on the very next question,
because the join stops matching. The tests leave the derived rows in place on
purpose.

Vault exclusion is structural rather than remembered: `FileItem` carries a global
query filter of `PrivateVaultId == null`, and nothing in this bounded context
says `IgnoreQueryFilters()`.

### Owner-prefiltered, not owner-filtered

Lexical retrieval builds an index from ONE person's rows, so there is nothing of
anybody else's to rank. The private index is **never cached across requests**:
keying the cache by `(domain, owner)` would keep every questioner's private
index resident for the life of the process, and would make "which index does
this caller get" a question answered by a cache key — where being wrong once is
a privacy incident. Building it per question costs a scan of one person's text.

Semantic retrieval is **exact cosine over the owner's eligible vectors**, not an
approximate index with a predicate. `ORDER BY embedding <=> query LIMIT 10`
against a global HNSW with `WHERE OwnerUserId = …` is not an owner-prefiltered
nearest-neighbour search: the traversal covers everybody's vectors and the
predicate filters what it happens to surface, so a person with few documents in
a large installation silently gets fewer and worse results. An index per owner
and partitioning are both real designs that want a benchmark this slice does not
have; a few thousand dot products is microseconds, and the candidate set is
bounded regardless.

### The model boundary

`Assistant__PrivateKnowledgeModel` names the model, and it must resolve to
`LocalTrusted`. There is **no fallback** — not to Help's model, which is the one
place in the product allowed to be External, and not to anything else. An
installation that points this at a provider gets a feature that is OFF, with its
own reason code `private_model_not_local` rather than a generic
"not configured" that would send an operator hunting for a typo.

An External configuration produces **zero provider calls**. Not a clean body —
no body, because the resolver refuses to hand the service a usable non-local
profile at all. The question itself never leaves either: "what does my contract
say about termination" has already disclosed something, and evidence is not the
only private part of a request.

`POST /api/assistant/documents/chat` carries a message and a bounded history and
nothing else. No owner, no domain, no file id, no model, no trust. A client
cannot point it somewhere else because the shape it posts into has nowhere to
put the instruction — a stronger statement than a server that accepts such
fields and promises to ignore them.

Retrieved document text is UNTRUSTED evidence, delimited and named as reference
material. That framing is hardening, not a control: a determined injection can
say anything, and phrasing does not stop it. What stops it is that the model has
no tools, no functions, no `tool_choice`, no second retrieval round, no database
handle, no filesystem and no action — and the owner was fixed before the
evidence was read. The worst a hostile document achieves is a wrong answer to
one question.

### What a citation may say

The document's NAME and the SECTION heading — both things the owner wrote or
chose. Never a `FileItemId`, `DocumentTextId`, chunk id, `BlobObjectId`, blob
hash, `StorageKey`, filesystem path or owner id. A citation exists so a person
can open their document, not so anything can address it, and an identifier on
the wire is a durable handle to private content sitting in somebody else's logs.

## Rich document ingestion

A supported document in somebody's library is compiled into owner-private text
locally, and nothing about the boundary changes: rich extraction changes WHAT can
become eligible document text, never who owns it, who may retrieve it, which
model may see it, or how that model is authorized.

### Supported, and deliberately not

| Family | Read | Not read |
|---|---|---|
| Native text | the allowlisted text types | — |
| PDF | page text, with local OCR for pages whose text is unusable | JavaScript, launch/submit/URI actions, embedded files; form-field values are excluded in this slice |
| Word `.docx` | paragraphs, heading path, lists, tables as rows, footnotes/endnotes, inserted tracked changes, hyperlink display text | deleted tracked-change text, macros, embedded objects, custom XML, hyperlink targets |
| Excel `.xlsx` | visible sheets, used cells, shared/inline strings, numbers, booleans, errors, cached formula results, bounded formula expressions | hidden and very-hidden sheets, formula EVALUATION, macros, external workbook links, embedded files |
| PowerPoint `.pptx` | visible slides, titles, bullets, tables, speaker notes, shape/image alt text | hidden slides, embedded media, OCR of embedded images, actions, external links |

Out of scope entirely, and refused by name so an operator can read why: legacy
binary `.doc`/`.xls`/`.ppt`, macro-enabled `.docm`/`.xlsm`/`.pptm`, and
password-protected documents of either family.

### Format comes from the bytes

The declared content type and the filename answer exactly one question — is
opening this file worth doing — and never what it IS. Both are attacker
controlled and both are routinely wrong by accident; clients upload OOXML
packages as `application/octet-stream` constantly.

Acceptance requires the package to declare its own main part, and a filename that
contradicts that declaration is REFUSED rather than resolved in either direction.
A DOCX renamed `.xlsx` reaching the spreadsheet parser is not a cosmetic
mis-route: it is untrusted input arriving somewhere written for a different
structure.

### Packages are hostile structured input

Entry count, per-entry size and total uncompressed size are read from the archive
DIRECTORY and enforced before the Open XML SDK sees the package. That ordering is
the point — a compression bomb is refused on the strength of what it claims about
itself, before a byte is expanded. Reading first and measuring afterwards is how a
bomb wins.

Nothing is extracted to disk; traversal-shaped entries are refused anyway, because
the package is malformed by OPC's own rules and a later change that does
materialize a part must not be the first place anyone notices. External package
relationships are never dereferenced — a document containing
`https://example.invalid/SECRET`, `file:///etc/passwd` or a UNC path gets its
display text extracted and its target ignored.

**Parsing means reading visible document information. It never means executing
document behaviour.** An Excel formula is text and a cached number, never a
computation. A PowerPoint action is ignored. A macro makes the format refused.

### One reading is authority

`DocumentText.IsCurrent` says which extraction of a file answers questions, with a
filtered unique index behind it and the shared eligibility boundary requiring it.
Resolving that by "latest timestamp" or "first completed" would let a clock or an
index decide which interpretation of somebody's document they are answered from,
and every such answer looks correct.

- **Bytes changed** → the old reading stops being authority BEFORE the
  replacement is attempted, so a parse that never finishes cannot leave a
  replaced document answering questions. A permanent content verdict for the new
  bytes becomes current; a retryable failure leaves nothing current, which is
  visible and recovers.
- **Same bytes, newer parser** → the working reading keeps authority until the
  new one succeeds. A parser that cannot handle a format must not be able to
  withdraw a working document from its owner's corpus.

Historical rows are kept as provenance and are neither retrieved nor embedded.

### Location is typed

`LocatorKind` / `LocatorIndex` / `LocatorLabel` carry where a chunk sits in its
own document's units, and `DocumentChunk.Page` stays a real PDF page.

| Format | Kind | Index | Label | Page |
|---|---|---|---|---|
| native text | — | — | — | — |
| PDF | `page` | page number | — | same page number |
| DOCX | `section` | section ordinal | heading path | — |
| XLSX | `sheet` | sheet ordinal | sheet name | — |
| PPTX | `slide` | slide number | slide title | — |

A chunk never crosses a page, a slide or a sheet: a passage that does cites a
place that does not exist. Typed rather than a formatted citation string because
two readers need it — the citation builder, and a future visual derivative that
must point at the same page without parsing "Slide 7 — Launch plan" back into
structure.

### OCR

Local, off by default, and a child PROCESS rather than an in-process library. A
managed wrapper binds native code into the API, where a crash in an image decoder
handed an attacker-chosen page takes the process with it and a hang cannot be
interrupted; a child can be killed, which is what makes the page timeout a bound
rather than a report.

The page travels on stdin and the text comes back on stdout, so no private page is
ever written to a temp file. Arguments go through an argument vector, never a
shell string. Stdout is read under a hard cap because it is untrusted process
output; stderr is drained and never logged, since it carries paths. Cancellation
reaches the caller as itself — an operator's deliberate stop must not be recorded
as a timeout.

Nothing is ever downloaded. A configured language that is not installed makes OCR
report not-ready; the production image ships `eng` and `ita`. An installation
without the engine boots normally and reports PDFs needing recognition as
retryable, never permanently refused.

Tesseract is the BASELINE behind the seam, not a claim about NubArca's final OCR
quality. A stronger local document model enters through the same interface later
without touching owner authorization, `DocumentText`, RAG trust or Assistant
policy.

## Bounds and privacy

Every stage is bounded server-side: query characters, lexical candidates, vector
candidates, fused candidates, evidence chunks, evidence characters, embedding
tokens and total indexed chunks. Configuration may make a bound tighter and
cannot remove one.

Logs carry the domain, the retrieval mode, counts, the profile key, elapsed
time, the fallback reason and the revision. They do **not** carry the question,
the passage text, tokens or vectors — and neither the API nor the CLI ever
returns a raw vector.

## Operator commands

```bash
dotnet NubArca.Api.dll rag domains
dotnet NubArca.Api.dll rag status   --domain product-help
dotnet NubArca.Api.dll rag index    --domain nubarca-repository --source . --embed
dotnet NubArca.Api.dll rag coverage --domain nubarca-repository
dotnet NubArca.Api.dll rag query    --domain product-help "come faccio a utilizzare la funzione dei volti?"
dotnet NubArca.Api.dll rag evaluate --domain product-help
dotnet NubArca.Api.dll rag seed-profiles
dotnet NubArca.Api.dll rag validate-model --domain product-help
```

The owner-private corpus has its OWN verb, and every subcommand requires
`--owner`:

```bash
dotnet NubArca.Api.dll documents status --owner <user-id>
dotnet NubArca.Api.dll documents index  --owner <user-id> --embed
dotnet NubArca.Api.dll documents index  --owner <user-id> --limit 20
```

A separate verb rather than `rag index --domain user-documents --owner …`,
because which corpus a command touches should be visible in the command that was
typed rather than in its arguments. There is deliberately no "all owners" mode:
the one legitimate use — backfilling after enabling the feature — is served by
running it per owner, which is also the form that can be stopped halfway without
ambiguity. Nothing these commands print is a document name, a heading, an
excerpt or a storage key; an operator diagnosing an indexing problem needs counts
and reason tokens, and a terminal that echoed somebody's filenames would put them
in a scrollback buffer, a screenshot and a support ticket.

`rag query` is diagnostic and never calls a generative model. When Help gives a
bad answer the two candidate causes — retrieval found the wrong thing, or the
model wrote something wrong about the right thing — are fixed in completely
different places, and a CLI that also generated would not tell you which
happened.

`rag evaluate` runs a golden set and reports Recall@5, MRR and
top-3-expected-source. No LLM judges anything: that would measure the LLM, cost
money per run, and not be reproducible.

## Measuring it

`product-help` is measured in the fast test suite, against the sources the
release actually ships, and the floors are asserted there
(`RagGoldenEvaluationTests`): Recall@5 ≥ 0.90 and MRR ≥ 0.80, with every golden
question required to put its expected source in the top three and no forbidden
source leading. The lexical baseline clears those comfortably — Recall@5 1.000,
MRR 0.938, 16/16 — from the database index and from the bundled corpus alike,
which is what makes the two paths interchangeable.

With semantic retrieval enabled against `multilingual-e5-small`, the same
sixteen questions measure Recall@5 1.000, **MRR 0.969**, 16/16 in `hybrid` mode.
Recall was already perfect, so the gain is entirely in RANKING — the right
source moving up — which is the half that decides what a model actually reads
when six chunks of context are sent.

One query is kept forever:

    come faccio a utilizzare la funzione dei volti?

Before the retrieval rewrite it returned `docs/OPERATIONS.md` — a
backup-and-restore runbook that mentions faces, and is longer, so it won on word
count. It is a permanent regression canary, and a technical reference to
`face_previews` is not an acceptable answer to it either.

### The private set, and what it does not prove

`user-documents` is measured in the fast suite against a SYNTHETIC, non-secret
library of one person's documents — a boiler manual, travel notes, project
notes, configuration notes and a recipe — with six question shapes: an exact
phrase, a paraphrase sharing almost no vocabulary with its document, a question
by filename, a multi-sentence question, an exact configuration key, and one the
corpus cannot answer.

| | Recall@5 | MRR | top-3 |
|---|---|---|---|
| `user-documents` lexical | 1.000 | 1.000 | 5/5 |
| `user-documents` hybrid | 1.000 | 1.000 | 5/5 |

**Read that with the caveat it deserves.** Five documents on five unrelated
topics is an EASY set: every question has exactly one plausible target, so
first-hit is close to the floor rather than a result. It says the private path
works end to end — extraction, chunking, owner-prefiltered ranking, fusion, the
evidence gate — and it does not say private retrieval is good. The asserted
floors are a regression tripwire, deliberately loose, and tuning weights until
six questions score better would move the number and not the product.

Hybrid matches lexical exactly here, and that is expected rather than
encouraging: the fast suite's embedding provider hashes text into a vector
rather than modelling meaning, so what this measures is that RRF does not LOSE
what lexical found. Real semantic quality wants `multilingual-e5-small` against
a corpus with genuinely competing documents, which is a measurement with its own
slice — the same conclusion the repository domain reached.

The sixth question is the one that must fail. A corpus that answers everything is
guessing, and for "answer from MY documents" a confident answer with nothing
behind it is the worst outcome, so it is asserted to return no strong evidence
and make no model call.

### The corpus must not contain the question list

The first real evaluation of `nubarca-repository` measured worse after the slice
was committed than before it, and the reason was worth more than the score: the
golden set is a C# file holding the golden queries as string literals, so once
the repository indexed itself, the single best lexical match for a conceptual
golden question became the file containing that exact sentence. It led three of
four failures and took MRR from 0.583 to 0.395.

Note what this paragraph does NOT do: quote the question. Describing a benchmark
by pasting its prompt puts the prompt back in the corpus, and the guard would
have to be widened until it excluded the documentation too. A test enforces
this — see below.

`src/NubArca.Api/Rag/Evaluation/` is therefore excluded from the repository
corpus, as a rule rather than as one file's exemption: a corpus that contains
the questions cannot answer them, it can only find them. Everything else in
`Rag/` stays indexed — it is exactly the knowledge this domain exists to hold.

`nubarca-repository` is measured with `rag evaluate` against an indexed
checkout, not in the fast suite: building a 22,000-chunk index is not something
a unit test should do. What the fast suite does assert is that every expectation
in the repository golden set still names a file that exists, so a rename cannot
silently invalidate half the set.

The lexical-only baseline over 1,891 sources and 22,717 chunks is Recall@5
**0.800**, MRR **0.575**, 7/10 top-3. The split is the interesting part:

- **exact-identifier questions** — `PhotoVectorIndexService`, a test name, a
  configuration key, `face_previews table` — are answered first-hit by the
  lexical path, and no embedding model reliably does that;
- **conceptual questions** — the prose ones asking where a behaviour is
  implemented — return plausible-but-not-expected sources: the documentation
  about a boundary and the tests that assert it, rather than the service that
  implements it. All three remaining failures are of this kind. The questions
  themselves live in `RagGoldenSet` and are deliberately not quoted here, for the
  reason above.

That was the gap semantic retrieval was expected to close. Measured against
`multilingual-e5-small` over the full 23,745-chunk index, it did not:

| | Recall@5 | MRR | top-3 |
|---|---|---|---|
| `product-help` lexical | 1.000 | 0.938 | 16/16 |
| `product-help` **hybrid** | 1.000 | **0.969** | 16/16 |
| `nubarca-repository` lexical | **0.800** | 0.575 | **7/10** |
| `nubarca-repository` hybrid | 0.700 | **0.625** | 6/10 |

Product Help improves exactly where there was room — a few hundred chunks of
curated PROSE, recall already perfect, so the gain is ranking, which is the half
that decides what a model actually reads.

The repository does not. MRR rises and Recall@5 falls, because a general-purpose
multilingual SENTENCE model asked to discriminate among 23,745 chunks of mostly
source code returns plausible neighbours that are wrong — a frontend test file
for a backend question — and those neighbours displace correct results that
lexical had found. Semantic similarity between two paragraphs of English prose
is a much stronger signal than semantic similarity between two blocks of C#.

That is recorded, not tuned. Adjusting RRF weights or the ranking profile until
these ten questions pass would move the score and not the product. What it
actually argues is that the repository domain wants either a code-aware
embedding model or a different fusion weighting, and choosing one means
measuring it — which is a decision with its own slice, not a knob to turn here.
Lexical remains the better default for that domain today, and semantic
enablement is now per domain (`Rag__Domains__<key>__SemanticEnabled`).

## Deliberately not in this platform yet

Owner-private RAG now EXISTS, for native text documents. What is still out:

**Ingestion.** No PDF, no OCR, no DOCX/XLSX/PPTX, no email, no web crawling. The
failure modes of a document parser are memory and code execution, and the way to
not have them is to not have a parser — so this slice ships a decoder and a set
of refusals. Richer ingestion is the next meaningful capability, on top of this
same boundary rather than beside it.

**Other private knowledge.** Media metadata, People and Faces are not
retrievable knowledge. Neither is Private Vault content — vaulted documents are
not extracted, chunked, embedded, indexed, retrieved or sent, and that is a
property of the schema rather than a rule to remember.

**Sharing.** No shared-document RAG: a file shared WITH somebody is not owned by
them, and being able to see something was never the test for knowledge
authority. No cross-owner admin search either — an administrator's authority
over an installation is not authority over a person's documents.

**The Assistant.** No read tools, no write tools, no actions, no ToolBroker. No
cross-domain retrieval and no model-directed domain hopping. No LLM query
rewriting or reranking. No server-side chat persistence.

**Everything hosted.** No hosted embeddings, no external private generation, no
automatic model downloads, no GitHub at query time, no Git writes, no code
execution.

**Retrieval sophistication.** No per-owner HNSW index and no partitioning —
exact cosine over an owner's eligible vectors is correct, and the alternatives
want a benchmark against a real corpus. No code-aware model for the repository
domain: the measurement above argues for one, and choosing it means measuring it.

The production image does not ship the repository: repository dogfooding is a
development, test and operator indexing source. Product Help and
`user-documents` are the production-facing domains.
