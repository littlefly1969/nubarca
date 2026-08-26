# The Help assistant

An optional assistant that explains **NubArca as a product**. It is disabled by
default and does no useful work until an operator configures a model.

It is also the first piece of a wider Assistant substrate: a model configuration
with an explicit trust classification, a central capability policy, a text-only
model runtime, and a retrieval (RAG) domain. Help is currently the only consumer
of any of it.

## Two axes, not one

    protocol   what the endpoint SPEAKS   →  OpenAI-compatible chat completions
    trust      who HOLDS the bytes        →  External | LocalTrusted

A hosted provider, an operator's own Ollama/vLLM/llama.cpp/LM Studio server and
a future NubArca-managed runtime can all speak the same wire format, so the
format says nothing about the boundary. Trust is a separate, explicit operator
decision.

**It is never inferred from the URL.** `localhost`, `127.0.0.1`, an RFC1918
address, a Docker service name and plaintext HTTP are all things a reverse proxy
in front of a cloud API also looks like, and a trusted GPU box on another site is
none of them. Guessing would be wrong in both directions, and wrong in the
direction that leaks. The browser cannot override it either: the chat request has
no field for a model, a profile or a boundary.

| Trust | Meaning |
|---|---|
| `External` | Data may leave the NubArca trust boundary. |
| `LocalTrusted` | The operator asserts the endpoint is theirs and may receive private context in features designed for it. |
| `ManagedLocal` | Reserved for a runtime whose isolation NubArca itself owns. **Not implemented**, and refused by configuration validation, so no installation can claim a guarantee nothing provides. |

## Capability, and why local does not mean powerful

Trust decides what a model is **eligible** for. A feature decides what it
**uses**. The effective capability is the intersection:

    model trust  ∩  feature policy  ∩  user permissions  =  effective capability

| Capability | External | LocalTrusted |
|---|:--:|:--:|
| Receive public product context | yes | yes |
| Receive private context *in principle* | no | yes |
| Private RAG *in principle* | no | yes |
| Read tools *in principle* | no | yes |
| Propose actions *in principle* | no | yes |
| Write tools | no | no |
| Execute without confirmation | no | no |

Writing is not a trust question: nothing in NubArca changes because a model
suggested it, at any trust level.

And the distinction that matters today: **Help's own operation policy is public
product knowledge only**, so a LocalTrusted Help receives no private library
data. Configuring a local model makes Help local. It does not make Help able to
see anything new. `LocalTrustedHelpBoundaryTests` asserts exactly that, on the
outbound bytes.

## The boundary

The model is never given access to the library. That is enforced by structure,
not by asking it nicely:

- **`HelpAssistantService` cannot reach private data.** Read its constructor: a
  text-only model runtime, a product-help retriever, the model resolver, a
  logger. No DbContext, no file/folder/storage service, no people or face
  service, no album service, no media or semantic search, no OCR, no metadata,
  no AI artifacts, and no `IServiceProvider` to go looking for one. A reviewer
  checks the boundary by reading four parameters.
- **The model has no tools.** The outbound request DTO has no `tools`,
  `functions` or `tool_choice` — not empty ones, absent ones. The runtime
  interface has no optional `tools` parameter either: absence is the contract,
  and an optional parameter is presence with a default. When tool calling
  arrives it belongs behind its own interface, so text-only Help cannot acquire
  tools because a shared type grew a property.
- **The request contract cannot carry a private reference.** The chat DTO has a
  message and a bounded history, and no `fileId`, `albumId`, `personId`,
  `faceId`, `searchId`, `currentMedia`, `context`, `url` — or `domain`, which
  would point Help at a retrieval domain it was not meant to read.
- **The model does not retrieve.** Local retrieval over an approved corpus
  selects bounded evidence; there is no callback, no second round trip and no
  way for the model to ask for more.

### What the UI says, and why

External:

> The question you type is sent to the configured external AI provider. NubArca
> does not attach or retrieve files, photos, people, metadata, search results, or
> other private library data.

It deliberately does **not** say "no user data leaves NubArca". The user's own
words leave by definition, and a privacy promise that is false in the easy case
is worth nothing in the hard one.

LocalTrusted:

> The question you type is processed by the local model endpoint configured by
> whoever administers this installation. In this version the assistant uses
> public product documentation only: it does not access your library and does not
> perform actions.

It deliberately does **not** claim the endpoint has no internet egress. NubArca
does not run that process and cannot prove it; that guarantee would belong to a
`ManagedLocal` runtime, which does not exist. The two states get distinct badges
and distinct copy rather than one badge with different words.

## The `product-help` RAG domain

    scope       system / public
    revision    NUBARCA_GIT_SHA
    private     none
    runtime     no repository access, no network

Help asks `IRagRetriever` for the domain `product-help`, as a constant. A domain
is a body of knowledge with ONE privacy story, so a future private domain is a
separate domain rather than a filter over this one — and a retriever answers
`Unavailable` for a domain that is not its own rather than quietly serving
public evidence to something that asked for something else.

### The source manifest

Product Help answers from an explicit **manifest** —
`Rag/ProductHelp/ProductHelpSources.cs` — naming each document, who it is for,
and how much it should be trusted:

    Audience    user | admin | technical
    Intent      how-to | explanation | troubleshooting | reference
    SourceKind  user-guide | ui-contract | feature-catalog | admin-guide | technical-reference
    Priority    1–100
    Aliases     the words people use for the feature, in both languages

This replaces "every `docs/**.md`, automatically". That rule was convenient and
wrong in one specific way: it let an operations runbook and a model benchmark
compete, on equal footing, with the guidance somebody asking "how do I use
faces?" actually needs — and the runbooks are longer, so they often won.

It is still an ALLOWLIST rather than a denylist of secrets, because a denylist is
a claim to have thought of everything. Anything nobody added on purpose is out:
`.env` files, operator Compose overrides, `deploy/`, source, tests, build output,
dependencies, hidden directories, `docs/current-work.md`, and internal
implementation plans. So is any new public document nobody classified. The cost
is that a new product document must be named here to become Help knowledge; that
is the intended trade, and it is the moment to say who the document is for.

`docs/help/` holds material written for Product Help itself — currently the
Faces/People guidance, in Italian and English.

### Building it

Built at image-build time from the same source revision as the release:

    source checkout at release SHA
        ↓  dotnet NubArca.Api.dll help-knowledge build --source . --out help-corpus.json --revision <sha>
    help-corpus.json, shipped in the image
        ↓  local retrieval over the approved manifest
    bounded public evidence
        ↓
    the configured model

Building it in the image rather than fetching at runtime buys: no GitHub token,
no GitHub write permission, no network dependency, no rate limit, deterministic
knowledge, and documentation matching the exact installed revision.

**Revision gate.** The corpus records the revision it was built from, and the
application refuses one that disagrees with its own `NUBARCA_GIT_SHA`. Help that
answered from a newer `main` would tell an operator to click something their
installation does not have, which is worse than no Help.

### Retrieval

Lexical, local, deterministic. No vector database and no cloud embeddings in this
version: sending text to an embedding service to decide what to send to a chat
service would widen exactly the boundary the feature exists to keep narrow.
Semantic retrieval is the next step, and it lands *behind* `IRagRetriever`.

What it does that the predecessor did not:

- **Section-aware chunks.** Roughly 800–1,800 characters, aligned to headings and
  paragraphs, each carrying its heading trail. The predecessor accumulated to
  4,000 characters, producing excerpts spanning three unrelated topics — bad to
  retrieve and expensive to send.
- **One IT/EN stopword set.** Italian `come` ("how") is also an English verb, so
  a language-switched list would keep it for an Italian question and score every
  English sentence containing it. A word meaningless in either language is
  dropped for both. Diacritics are folded in both directions.
- **A feature alias catalogue.** `volti`, `faccia`, `persone`, `face`, `people`,
  `riconoscimento facciale` name one concept. Expansion is a fixed table, local,
  bounded, and scored at a discount, so a document using the person's own words
  still wins.
- **Field-aware ranking.** A BM25F over feature/aliases, section, title and body,
  weighted in that order, times the manifest priority, times an intent shaping
  step: a how-to question prefers a user guide and penalises a technical
  reference.
- **An evidence gate.** `Score > 0` is not evidence. A document must clear a
  score floor, match enough of what was actually typed, and match in a metadata
  field or on more than one word. Below that, Help answers that the documentation
  does not cover the question — and makes **no outbound call at all**, so a
  question does not cross the boundary to buy an answer improvised from three
  irrelevant paragraphs. The same rule applies to a LocalTrusted model: the
  privacy cost is lower, and a confidently wrong answer is exactly as wrong.
- **Match-centered excerpts.** When a chunk must be trimmed, the window is placed
  around what matched. The predecessor cut from character zero.

## Configuration

Server-side only. An API key never reaches the browser, never enters the
database, never appears in a log or in an error returned to a user.

```bash
Assistant__Enabled=false                                   # nothing happens until this is true
Assistant__HelpModel=help-default                          # which profile Help uses
Assistant__Models__help-default__Protocol=OpenAiCompatible
Assistant__Models__help-default__Trust=External            # External | LocalTrusted — required
Assistant__Models__help-default__BaseUrl=https://your-provider.example
Assistant__Models__help-default__ApiKey=
Assistant__Models__help-default__Model=
Assistant__Models__help-default__Label=External AI
Assistant__Models__help-default__TimeoutSeconds=30
Assistant__Models__help-default__MaxOutputTokens=800
Assistant__Help__CorpusPath=help-corpus.json
```

Named profiles rather than one set of provider fields, because a later Assistant
will want a different model for Help than for routing or generation, and "which
model does this feature use" should be a configuration answer rather than a code
change in the feature.

Validation fails closed. An enabled profile needs a known protocol and an
explicitly stated trust; an unknown, empty, misspelled or **numeric** trust value
is invalid and never silently becomes Local. Case and surrounding whitespace are
tolerated, because those are accidents with no second meaning.

| | `External` | `LocalTrusted` |
|---|---|---|
| Transport | `https://` required | `http://` or `https://` |
| API key | required | optional |
| Sent an empty `Bearer` header | — | no, the header is omitted entirely |

`https://` is required for External because the key travels in an
`Authorization` header on every request and the bytes cross the boundary anyway.
HTTP is allowed for LocalTrusted because a trusted LAN or container-network
endpoint frequently terminates no TLS and wants no auth — and refusing that would
push operators towards declaring a real external provider "local" to make it
work, which is the opposite of the point.

There is deliberately **no** `AllowInsecureBaseUrl`. A switch that let an
External model use plaintext transport is exactly the ambiguity the trust
classification removes: a plaintext endpoint is now expressed as
`Trust=LocalTrusted`, which is a statement about who holds the bytes rather than
a hole in a statement about TLS.

Bounds — question length, history turns, history size, evidence count, evidence
size, timeout, output tokens — are configurable and clamped, so a hand-edited
value can tighten a boundary but never remove one.

### Legacy `ExternalHelp__*`

An installation configured before named profiles existed keeps working. When the
`Assistant` section is absent, a complete `ExternalHelp` configuration is adapted
into exactly one profile, **always classified External** — a configuration shape
that predates the trust axis cannot assert a trust classification, and an upgrade
must not quietly turn an external installation into a trusted-local one, however
the URL is written. The `Assistant` section wins whenever it is configured at
all, so a partial migration never produces a silent mix.

It is a deprecation path. New deployments configure `Assistant__*`.

## Protocol

    POST {BaseUrl}/v1/chat/completions
    Authorization: Bearer <key>          (omitted when there is no key)
    { "model": …, "messages": [ {role, content} … ], "max_tokens": … }

The format is the interoperability target, not the vendor. NubArca carries no
provider SDK and no provider types: everything talks to `IAssistantTextModel` and
NubArca-owned DTOs, so a second protocol is a new class rather than a change to
everything around it.

## Failure

An unavailable model is **not** NubArca being unavailable. Timeouts,
cancellation, 401/403, 429, 5xx, malformed JSON, empty answers and network
failures all become a sanitized reason code; nothing carries a body, a header or
an exception message. Help does not participate in `/health/ready`, and the rest
of the product is unaffected when it is off.

Two knowledge states are reported separately, because they have different
audiences: `help_knowledge_unavailable` (no corpus for this revision — an
administrator can fix it) and `help_no_supporting_knowledge` (the corpus is fine
and nothing in it answers this — nobody can fix it, so the copy asks for a
rephrasing).

## Logging

Never logged: the question, the conversation, the answer, evidence text, API
keys, auth headers, raw request or response bodies. Safe operational facts only —
profile key, trust classification, protocol, duration, HTTP status class,
sanitized failure reason, RAG domain, evidence count, retrieval outcome category.
The base URL is deliberately not logged either: it would put an operator's
internal hostname in a log for no operational gain.

## Conversations are not stored

There is no Help conversation table and no migration. The conversation lives in
the browser and a bounded slice of it rides with each request. A help feature
should not create a new permanent category of user data on its own.

## Tests

- `tests/NubArca.Api.Tests/Assistant/` — trust configuration and fail-closed
  validation, URL never deciding trust in either direction, the capability
  policy, and the protocol adapter against a fake endpoint with tool absence
  asserted on the raw body.
- `tests/NubArca.Api.Tests/Help/` — the privacy sentinel test (private data
  seeded with unmistakable markers, an ordinary question asked through the real
  endpoint, and the COMPLETE outbound body asserted to contain none of them while
  containing approved public text, so the absence means something); the
  API-key leak paths; the no-call gates; and the LocalTrusted boundary, over
  plaintext authless HTTP, proving trust eligibility and feature policy are
  separate.
- `tests/NubArca.Api.Tests/Rag/` — the manifest boundary, the revision gate, and
  golden retrieval against the REAL shipped sources, including the Italian
  question that motivated the rewrite:
  *"come faccio a utilizzare la funzione dei volti?"*
