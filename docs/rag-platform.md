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

## Visual document retrieval

A document can now be found by how its PAGES LOOK — layout, tables, forms,
slides, heading hierarchy, spatial structure — and not only by the words it
contains. Everything about the boundary is unchanged.

> **A visual embedding is private derived data, not authority.**
>
> **A visual hit finds where to look; an eligible current text chunk is still
> what NubArca is allowed to say.**

The pipeline, in order:

```text
global owner-private text retrieval
+ dense visual retrieval  ->  candidate FileItems
                          ->  the SAME text retrieval, scoped to those files
= two ranked lists, fused by RRF
-> the existing evidence gate
-> bounded TEXT RagEvidence
-> LocalTrusted generation
```

`DocumentVisualUnit` never becomes `RagEvidence`, no page image reaches a
generative model, and a visually perfect page whose text says nothing relevant
ends in **no model call at all**. That last one is the case the architecture
exists for: visual similarity is not permission to improvise.

### The signal is additive

Text remains better at exact identifiers, filenames, configuration keys and
ordinary prose. Visual retrieval adds where a text embedding flattens the thing
that mattered: a table, a form, an invoice, a dashboard, a slide, a multi-column
layout, a heading hierarchy. The claim is `text + visual > text alone` on a
deliberately mixed set, and it is measured rather than asserted — see below.

### Same weights, separate identity

The dense baseline reuses the SigLIP2 So400m 384 checkpoint the photo library
already runs. It is a 1.6 GB asset already on disk, already compiled for the
configured device, and already the multimodal space this installation reasons
in; a second copy of the same model to embed pages instead of photos would cost
gigabytes to change nothing.

What is NOT shared is everything that decides meaning. `document-visual-siglip2-so400m-patch14-384-v1`
is its own `AiProfile` under its own capability (`document-visual-embedding`),
so document vectors live in their own table keyed by their own profile and
cannot be counted, compared or reindexed as photo vectors. A document-visual
model swap reindexes documents and leaves the photo library alone, and the
reverse. Never the capability default: which profile embeds pages is stated by
`Ai__DocumentVisual__DenseProfileKey`, so a newer profile cannot become active
by existing.

### Rendering, and where it happens

| Family | Renderer | Render identity |
|---|---|---|
| PDF | PDFium, in process | `pdfium-page-render-v1` |
| text / Markdown | NubArca's own deterministic canvas | `nubarca-text-canvas-v1` |
| DOCX / XLSX / PPTX | LibreOffice headless → PDF → PDFium, in an **isolated worker** | `libreoffice-office-pdf-v1` |

The render identity changes when the engine, the pagination or the bundled fonts
change — never on a timestamp or a build SHA, either of which would re-render
every library for a release that touched none of this. A stored index whose
render identity is not the active one is unreachable, which is what a renderer
upgrade costs.

**The rendered page is never stored.** Render, embed, discard. Persisting it
would be a second copy of everybody's paperwork with its own backup, deletion
and share-boundary problems, in exchange for nothing retrieval needs; a SHA-256
`PixelHash` is kept so a rebuild can prove determinism without the bytes it
hashes.

**Office rendering does not run in the API.** Laying out a DOCX means running a
real office suite over a file somebody uploaded: dozens of legacy parsers,
document-declared relationships, a macro engine. The api container holds
database credentials, storage credentials and every owner's identity. So the API
sends BYTES and a FORMAT ORDINAL over a Unix socket to a container with
`network_mode: none`, no credentials, no owner identity, a read-only root
filesystem and `cap_drop: ALL`, and receives a PDF. There is no operation in
that protocol for a path, a filename, a command, an import filter, a binary or a
URL — not "those are validated", but *cannot be expressed*. The network sandbox
is the egress guarantee; LibreOffice's own macro setting is defence in depth.

Without the worker deployed, PDFs and text still get visual search and Office
documents stay text-only. Nothing is marked permanently unrenderable, so
deploying it later simply starts rendering them.

### A rendered office page is not a citation

`DocumentChunk.Page` still means a real PDF page and nothing else, and the same
rule now governs visual units. A PDF page is page N of that PDF under any
renderer, so the PDF renderer fills in a source page. An office page ordinal is
LibreOffice's pagination — a different build breaks the same DOCX elsewhere — so
those are deliberately null, and a text canvas sheet has no counterpart in a
Markdown file at all.

Final citations therefore remain Slice-4 typed text provenance: heading/section
for DOCX, sheet/range for XLSX, slide for PPTX, page for PDF.

### Publication is all or nothing

Pages are rendered and embedded one at a time, so memory holds one image; but no
row reaches the database until every required unit has succeeded, and then all
of them arrive in one write together with the `Completed` index. A twenty-page
contract whose page 13 failed contributes **zero** hits from pages 1–12.

There is no code path that writes some units and marks the index done, and the
database says so too: a `Completed` index with no units is a write that cannot
commit. The failure it prevents is invisible to the person it misleads — a
document that reads as whole and is not.

### Derived rows are still not authority

Six conditions, recomputed live on every query, with the derived rows
deliberately left in place in every test:

- the file is this owner's, not deleted, not vaulted, in the library;
- the visual index is `Completed`;
- the index's blob is the file's **current** blob — which is what makes
  replacing a document's content invalidate its pages instantly, with no
  sweeper;
- the render profile is the active one;
- the embedding profile is the active one;
- and the owner comes from the live `FileItem`, never the denormalized copy.

### Owner-prefiltered, and the absence that guarantees it

`ORDER BY embedding <=> q LIMIT 10` against a global HNSW with
`WHERE OwnerUserId = …` is **not** an owner-prefiltered nearest-neighbour
search: the graph is traversed over everybody's vectors and the predicate
filters whatever the traversal surfaced, so a person with few documents in a
large installation silently gets fewer and worse results.

So the pgvector accelerator for document visual embeddings has **no ANN index**,
and that absence is asserted by a test against a real PostgreSQL 17. With no
index the planner has one plan available: restrict through the eligibility
joins, then rank the survivors exactly. What the table still buys is real — the
cosine is computed in the database instead of shipping every candidate's 4.6 KiB
of float32 to the application on every question.

Without pgvector the same ranking happens in process over the owner's bounded
corpus. Past `MaxVisualUnitsPerOwnerExactFallback` visual retrieval reports
itself **unavailable** and text answers the question; it never ranks an
arbitrary prefix of a library and presents it as somebody's documents.

### Narrowing a text pass, correctly

The scoped second pass narrows CANDIDATES, not the index — and getting that
wrong is subtle enough to be worth stating. BM25 weights a term by how rare it
is across the corpus, so building an index from three documents makes every term
unremarkable, the scores collapse under the minimum-score floor, and the
evidence gate rejects the very chunk the visual pass went looking for. The
allowlist is therefore applied when candidates are selected from the owner's
full, already-eligible index. It can only ever REMOVE candidates: a forged id —
another owner's file, a deleted one, a vaulted one — is not in that index at
all.

There is no `fileIds` on any DTO, no query-string parameter and no configuration
key that reaches it. The only value it ever holds is a list the server derived
moments earlier from this same owner's eligible visual index.

### A sorted corpus is not a set of matches

A bare top-K over cosine returns K rows whatever the library contains, so in a
small one every document comes back and "scope the text pass to what looks
relevant" becomes "scope it to everything". Hits need a positive cosine and a
RELATIVE floor under the best match. Relative because cross-modal cosine is not
calibrated across checkpoints: "within this much of the best thing found"
survives a model swap, and "0.2 is a match" is a claim about one set of weights.

A file is as relevant as its most relevant page. Summing a document's page
scores would let a hundred-page report outrank a one-page invoice purely by
being long — and length is the one property a visual embedding cannot see.

### Phase 0, measured

Both models were run for real, on this corpus, and the numbers decide.

**Dense — SigLIP2 So400m 384**, exported from the pinned revision
`c65677ac77ca25276518923f7c58cbf5d81ea602` by
`scripts/export-siglip2-so400m-onnx.py`:

| | |
|---|---|
| dimension | 1152 |
| page embed | 4.5–5.6 s warm, 9.1 s cold (CPU) |
| query embed | 0.57 s warm, 79 s cold — cold is session construction over 2.8 GB of external data, not inference |
| storage | 4.6 KB per page |
| table page vs prose page, for "a table of quarterly revenue and costs" | **0.1582 vs 0.0145** |

**Late-interaction candidate — `vidore/colSmol-500M`** (adapter revision
`0aaa9726104ce485884c7b8faa8a58a72d5fdbe7`, MIT) over
`vidore/ColSmolVLM-Instruct-500M-base` (revision `650243e9…`, MIT):

| | |
|---|---|
| parameters | 477,663,552 |
| weights on disk | 1.00 GB (adapter + backbone) |
| peak RSS | 2.9 GB |
| vectors per page | 875 × 128 |
| storage | **448 KB per page — 97× the dense baseline** |
| page embed | 22.3 s (CPU) — 4× the dense baseline |
| query embed | 0.19 s |
| MaxSim rerank | 152 ms per question, on top |

Run through NubArca's own pipeline — real dense pass, real MaxSim, real
evidence gate — over the shared golden set:

| mode | Recall@5 | MRR | top-3 | visual nDCG@5 |
|---|---|---|---|---|
| dense (SigLIP2) | 0.833 | 0.778 | 10/12 | 0.722 |
| dense + colSmol late interaction | 0.833 | 0.778 | 10/12 | 0.722 |

**Relative nDCG@5 change: 0.0%. Nothing recovered, nothing regressed.**

> **`vidore/colSmol-500M`: evaluated, not promoted.**

It clears neither half of the gate — no 10% relative nDCG@5 improvement and no
two additional visual queries recovered — while costing 97× the storage, 4× the
indexing time, 2.9 GB of resident memory and 152 ms per question.
`Ai__DocumentVisual__LateInteractionEnabled` stays false, and no production
model worker ships.

**Why this is a result and not an absence.** A benchmark that cannot detect a
difference is not evidence that there is none, so the lane reports its own
discriminating power alongside the comparison: all 13 questions produce
DISTINCT visual candidate sets, and real SigLIP2 puts the expected document
first for 11 of them. The corpus separates these pages; the candidate simply had
nothing to add to that separation. The reranker's engagement is asserted too —
identical rankings and a silently-skipped second stage look the same in a report
otherwise.

A larger quality-ceiling candidate (ColQwen-class, ~4.5 B parameters) was NOT
measured. At 22 s per page for a 500 M model on this CPU-only host, a
nine-times-larger one is not something this hardware can evaluate in a
reasonable time, and guessing at its numbers would be worse than saying so.

### Late interaction is a seam, not a dependency

The stable concept, and all NubArca depends on:

```text
query -> sequence of normalized vectors
page  -> sequence of normalized vectors
score = Σ_i max_j dot(Q_i, D_j)
```

`IVisualLateInteractionProvider` states it, `MaxSim` implements it against a
hand-computable fixture, and a second stage reranks the dense top K by exact
MaxSim. Because it reorders a list the owner-prefiltered pass already produced,
it inherits that filtering — which is why a specialised multi-vector ANN engine
(PLAID, WARP, TACHIOM) stays a replaceable optimisation rather than a component
that would have to re-establish the boundary inside itself.

**No model is promoted in this release**, and that is a measured decision rather
than an omission — see Phase 0 above. With no promoted profile the dense order
stands, and that is a complete, working configuration rather than a degraded
one. Multi-vectors are stored as exact float32; float16 halves the bytes and
changes the scores, and by how much is precisely the unmeasured quantity.

### Operator commands

```bash
dotnet NubArca.Api.dll documents visual-seed-profiles
dotnet NubArca.Api.dll documents visual-index    --owner <user-id> [--limit N]
dotnet NubArca.Api.dll documents visual-status   --owner <user-id>
dotnet NubArca.Api.dll documents visual-evaluate --owner <user-id> --queries cases.tsv
```

Readiness is reported in four independent parts, because they degrade
independently and one "AI is working" flag would hide which:
`text_private_ready`, `dense_visual_ready`, `office_renderer_ready`,
`late_interaction_ready`.

### Cost, and backup

Visual metadata and vectors are rebuildable derivatives. After a restore with no
models or worker present, text RAG works and the visual path is unavailable
until they are; `documents visual-index` rebuilds it. Rebuilding visual
derivatives never touches text extraction, text chunks, text embeddings or
source files.

Per 1,000 visual units, canonical dense storage is
`1000 × 1152 × 4 B ≈ 4.6 MB`, plus the same again in the pgvector accelerator
when it is present, plus one small row per unit. Render and embed time depend
entirely on the model, the device and the page count; `documents visual-index`
reports the unit counts and `documents visual-evaluate` the query latencies, so
an operator measures their own rather than trusting a number from somebody
else's hardware.

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

dotnet NubArca.Api.dll documents visual-seed-profiles
dotnet NubArca.Api.dll documents visual-index    --owner <user-id> [--limit N]
dotnet NubArca.Api.dll documents visual-status   --owner <user-id>
dotnet NubArca.Api.dll documents visual-evaluate --owner <user-id> --queries cases.tsv
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

### The visual set, and what it does not prove

Visual retrieval is measured in the fast suite against a second SYNTHETIC
library — thirteen cases covering every shape the design calls out: ordinary
prose, a Markdown heading hierarchy, a PDF table, a PDF form, a scanned page, a
DOCX with numbered clauses, an XLSX grid, a PPTX slide, a visually similar
distractor, an exact identifier that TEXT must win, an unanswerable question,
and an Italian and an English paraphrase.

| mode | Recall@5 | MRR | top-3 | visual nDCG@5 |
|---|---|---|---|---|
| text-only | 0.833 | 0.778 | 10/12 | 0.722 |
| **dense-visual-expanded** | **0.917** | **0.917** | **11/12** | **0.889** |

One query recovered, none regressed. The recovered one is the shape the whole
capability exists for: a form whose text says "the applicant completes the form
and signs at the foot of the page", written in words that three other documents
in the library use more heavily — global text ranks it out of the evidence
budget, and its LAYOUT brings it back.

**What this does not prove.** The embedding providers in the fast suite are
DETERMINISTIC: they hash their input into a vector. The page vectors are seeded
by the fixture, so the harness controls exactly which document "looks like"
which question, and what is measured is the PLUMBING — that a visually-found
document reaches the top, that a text-only strength is not displaced by it, and
that the recovered/regressed accounting is honest. It says nothing about whether
SigLIP2 agrees, which is a different question with its own lane
(`DocumentVisualRealOnnxTests`, gated on `Ai__Onnx__ModelDir`) and its own
operator command (`documents visual-evaluate`, against a real library).

The English paraphrase fails in both modes, and it is left failing. The
deterministic text provider is not multilingual, so an English question against
an Italian document has nothing to match lexically; a cross-language answer
needs the real multilingual model, and papering over it here would hide a real
limitation of the fast lane behind a fixture.

Two aggregates are deliberately reported separately. `visual nDCG@5` covers only
the cases the visual signal is SUPPOSED to help with, because one number over a
mixed set cannot tell "visual retrieval works" from "the text path was already
good at most of these" — and RECOVERED and REGRESSED are both printed, because a
change that gains two table questions and loses two identifier lookups scores
flat and is a bad change.

## Deliberately not in this platform yet

Owner-private RAG now EXISTS, for native text documents. What is still out:

**Ingestion.** PDF, local OCR and DOCX/XLSX/PPTX are read, and their pages can
be rendered and searched visually. Still out: email, web crawling, and any
hosted document processing whatsoever.

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

**Everything hosted.** No hosted embeddings, no hosted OCR, no hosted document
rendering, no hosted visual embeddings, no external private generation, no
automatic model downloads, no GitHub at query time, no Git writes, no code
execution.

**Multimodal generation.** No page image reaches a generative model, and there
is no multimodal prompt, no LocalTrusted VLM and no generic image-understanding
API. Visual units keep enough identity for a future capability to re-render an
authorized current page; the image is not stored and not sent today.

**Retrieval sophistication.** No per-owner HNSW index and no partitioning —
exact cosine over an owner's eligible vectors is correct, and the alternatives
want a benchmark against a real corpus. No promoted late-interaction model and
no multi-vector ANN engine: the seam exists and the measurement decides. No code-aware model for the repository
domain: the measurement above argues for one, and choosing it means measuring it.

The production image does not ship the repository: repository dogfooding is a
development, test and operator indexing source. Product Help and
`user-documents` are the production-facing domains.
