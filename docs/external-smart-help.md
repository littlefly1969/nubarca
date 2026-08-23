# External Smart Help

An optional Help assistant that explains **NubArca as a product** through an
external LLM. It is disabled by default and does no useful work until an operator
configures a provider.

## The boundary

    External LLM  = intelligence about NubArca
    Local models  = intelligence about your data

The external model is never given access to the library. That is enforced by
structure, not by asking it nicely:

- **`ExternalHelpService` cannot reach private data.** Read its constructor: a
  provider client, a public-corpus retriever, options, a logger. No DbContext, no
  file/folder/storage service, no people or face service, no album service, no
  media or semantic search, no OCR, no metadata, no AI artifacts. A reviewer
  checks the boundary by reading four parameters.
- **The model has no tools.** The outbound request DTO has no `tools`,
  `functions` or `tool_choice` — not empty ones, absent ones. A capability that
  is never offered cannot be talked into existing by a later prompt edit.
- **The request contract cannot carry a private reference.** The chat DTO has a
  message and a bounded history and no `fileId`, `albumId`, `personId`,
  `faceId`, `searchId`, `currentMedia`, `context` or `url` field. A client cannot
  attach library context because the shape has nowhere to put it.
- **The model does not retrieve.** A local BM25 over an approved corpus selects
  bounded excerpts; there is no callback, no second round trip and no way for the
  model to ask for more.

### What the UI says, and why

> The question you type is sent to the configured external AI provider. NubArca
> does not attach or retrieve files, photos, people, metadata, search results, or
> other private library data.

It deliberately does **not** say "no user data leaves NubArca". The user's own
words leave by definition, and a privacy promise that is false in the easy case
is worth nothing in the hard one.

## The knowledge corpus

Help answers from an allowlist of **public, tracked** product documentation —
`README.md`, `ARCHITECTURE.md` and `docs/**.md` — built at image-build time from
the same source revision as the running release.

It is an ALLOWLIST rather than a denylist of secrets, because a denylist is a
claim to have thought of everything. Anything nobody added on purpose is out:
`.env` files, operator Compose overrides, `deploy/`, source, tests, build output,
dependencies, hidden directories, and `docs/current-work.md` (internal working
notes rather than user-facing product documentation).

Building it in the image rather than fetching at runtime buys: no GitHub token,
no GitHub write permission, no network dependency, no rate limit, deterministic
knowledge, and documentation matching the exact installed revision.

    source checkout at release SHA
        ↓  dotnet NubArca.Api.dll help-knowledge build --source . --out help-corpus.json --revision <sha>
    help-corpus.json, shipped in the image
        ↓  local BM25 retrieval
    bounded public excerpts
        ↓
    external LLM

**Revision gate.** The corpus records the revision it was built from, and the
application refuses one that disagrees with its own `NUBARCA_GIT_SHA`. Help that
answered from a newer `main` would tell an operator to click something their
installation does not have, which is worse than no Help.

## Configuration

Server-side only. The API key never reaches the browser, never enters the
database, never appears in a log or in an error returned to a user.

```bash
ExternalHelp__Enabled=false            # default; nothing happens until this is true
ExternalHelp__BaseUrl=                 # provider root, https:// required
ExternalHelp__ApiKey=
ExternalHelp__Model=
ExternalHelp__ProviderLabel=External AI
ExternalHelp__TimeoutSeconds=30
ExternalHelp__MaxOutputTokens=800
```

`https://` is required because the key travels in an `Authorization` header on
every request. `ExternalHelp__AllowInsecureBaseUrl=true` exists for a local test
double and for nothing else.

Bounds — question length, history turns, history size, excerpt count, context
size — are configurable and clamped, so a hand-edited value can tighten a
boundary but never remove one.

## Protocol

The first adapter speaks the **OpenAI-compatible Chat Completions** format:

    POST {BaseUrl}/v1/chat/completions
    Authorization: Bearer <key>
    { "model": …, "messages": [ {role, content} … ], "max_tokens": … }

The format is the interoperability target, not the vendor — several providers
speak it. NubArca carries no provider SDK and no provider types: everything talks
to `IExternalHelpChatClient` and NubArca-owned DTOs, so a second provider is a
new class rather than a change to everything around it.

## Failure

An unavailable provider is **not** NubArca being unavailable. Timeouts,
cancellation, 401/403, 429, 5xx, malformed JSON, empty answers and network
failures all become a sanitized reason code; nothing carries a provider body, a
header or an exception message. External Help does not participate in
`/health/ready`, and the rest of the product is unaffected when it is off.

## Conversations are not stored

There is no Help conversation table and no migration. The conversation lives in
the browser and a bounded slice of it rides with each request. A help feature
should not create a new permanent category of user data on its own.

## Tests

`tests/NubArca.Api.Tests/Help/` — the provider contract against a fake HTTP
provider, tool absence asserted on the raw body, the API-key leak paths, the
corpus allowlist and the revision gate, and the privacy sentinel test: private
data seeded with unmistakable markers, an ordinary question asked through the
real endpoint, and the COMPLETE outbound body asserted to contain none of them —
while containing approved public text, so the absence means something.
