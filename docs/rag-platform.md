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

Policy lives in `RagDomainRegistry` — in **code**, not in a database row. The
database records which sources exist and which revision was indexed; it does not
record whether evidence may leave the trust boundary. If it did, one `UPDATE`,
one careless admin endpoint or one backup restored from a fork could turn
`SystemInternal` into `Public`.

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
dotnet NubArca.Api.dll rag validate-model
```

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
Lexical remains the better default for that domain today, and
`Rag__SemanticEnabled` is per installation.

## Deliberately not in this slice

No private/owner RAG. No user documents, OCR, media metadata, People or Faces as
retrievable knowledge. No Assistant read tools, no actions, no writes. No
cross-domain retrieval and no model-directed domain hopping. No LLM query
rewriting or reranking. No hosted embeddings. No GitHub at query time. No Git
writes and no code execution. The production image does not ship the repository:
repository dogfooding is a development, test and operator indexing source, and
Product Help remains the production-facing domain.
