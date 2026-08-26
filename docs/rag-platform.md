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
rag_sources           one row per document, identified by SourceKey
rag_domain_sources    membership: this source belongs to this domain
rag_chunks            one retrievable passage
rag_chunk_embeddings  canonical float32 vector per (chunk, profile)

rag_chunk_embedding_vectors_384    pgvector accelerator, derived
```

A source exists **once**. `docs/help/faces.md` is one row whether it is only
repository knowledge or also approved Product Help, with one set of chunks and
one embedding per profile, and two membership rows. Adding a domain costs a
membership row rather than a second copy of the text and every vector.

Domain-specific classification — Product Help's feature name, aliases, audience,
intent and editorial priority — lives on the MEMBERSHIP. It is that domain's
opinion about the document. A C# file does not acquire an `intent=how-to`
because the schema can hold one.

These tables are deliberately separate from `document_texts` /
`document_chunks` / `document_chunk_embeddings`, which are owner/file-scoped
artifacts of a user's own library, and from the photo and face vector tables.
The concept is shared; the ownership semantics and the vector spaces are not.

## Provenance and revision

Every source carries its **revision** and a SHA-256 **content hash**.

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
source leading. The lexical baseline currently clears those comfortably —
Recall@5 1.000, MRR 0.938, 16/16 — from the database index and from the bundled
corpus alike, which is what makes the two paths interchangeable.

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
the repository indexed itself, the single best lexical match for
*"which code prevents an External model from using repository knowledge?"* became
the file containing that exact sentence. It led three of four failures and took
MRR from 0.583 to 0.395.

`src/NubArca.Api/Rag/Evaluation/` is therefore excluded from the repository
corpus, as a rule rather than as one file's exemption: a corpus that contains
the questions cannot answer them, it can only find them. Everything else in
`Rag/` stays indexed — it is exactly the knowledge this domain exists to hold.

`nubarca-repository` is measured with `rag evaluate` against an indexed
checkout, not in the fast suite: building a 22,000-chunk index is not something
a unit test should do. What the fast suite does assert is that every expectation
in the repository golden set still names a file that exists, so a rename cannot
silently invalidate half the set.

The lexical-only baseline over 1,891 sources and 22,717 chunks at revision
`943e37b` is Recall@5 **0.800**, MRR **0.575**, 7/10 top-3. The split is the
interesting part, and it is the argument for hybrid retrieval in one table:

- **exact-identifier questions** — `PhotoVectorIndexService`, a test name, a
  configuration key, `face_previews table` — are answered first-hit by the
  lexical path, and no embedding model reliably does that;
- **conceptual questions** — *"where is the external Help privacy boundary
  enforced?"* — return plausible-but-not-expected sources: the documentation
  about the boundary and the tests that assert it, rather than the service that
  implements it. All three remaining failures are of this kind.

That is the gap semantic retrieval exists to close, and it is the number to
watch when a model is configured. It is recorded here rather than tuned away:
raising it by adjusting weights until these ten questions pass would improve the
score and not the product.

## Deliberately not in this slice

No private/owner RAG. No user documents, OCR, media metadata, People or Faces as
retrievable knowledge. No Assistant read tools, no actions, no writes. No
cross-domain retrieval and no model-directed domain hopping. No LLM query
rewriting or reranking. No hosted embeddings. No GitHub at query time. No Git
writes and no code execution. The production image does not ship the repository:
repository dogfooding is a development, test and operator indexing source, and
Product Help remains the production-facing domain.
