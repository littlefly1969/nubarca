# NLU command-model deployment (TV natural-language gallery search)

The TV natural-language search interprets a typed command entirely **locally**.
There are two interpreter backends, selected by
`Ai:NaturalGallerySearch:Interpreter`:

- **`deterministic`** (default) — a built-in IT/EN grammar interpreter. No
  weights, no sidecar, warm, bounded, ~2 ms p50. Ships enabled everywhere and is
  the offline/dev/test default and the fallback. Measured accuracy on the
  synthetic corpus: **96.4 % exact structured match, 100 % valid output**
  (see `docs/model-deployment/nl-gallery-corpus.v1.json` + the benchmark test).
- **`onnx`** — an optional, isolated, **internal-only** decoder-LLM sidecar for
  harder/colloquial phrasings (e.g. lowercase names, freeform paraphrases). This
  document covers its install + operation. **It is off by default.**

Both backends produce the SAME strict structured draft, which is then validated
+ person/date-resolved deterministically on the API. No cloud service is ever
contacted; the sidecar runs with **outbound network disabled**.

## Chosen model

| | |
|---|---|
| Model | **microsoft/Phi-3.5-mini-instruct-onnx** |
| Params | ~3.8 B |
| License | **MIT** (unrestricted commercial use) |
| Revision (pin) | `7230dcd6c1dd28aab70f263ecc8734ec9d9bcb70` |
| Variant | `cpu_and_mobile/cpu-int4-awq-block-128-acc-level-4` (CPU int4) |
| On-disk size | ~2.2 GB |
| RAM (loaded) | ~3.0–3.5 GB |
| Runtime | ONNX Runtime GenAI (CPU) |
| Tokenizer | shipped in the model folder (`tokenizer.json`), same revision |

### Why this model (evaluation)

Decision weights (per the task): structured accuracy 60 %, warm latency 25 %,
memory/operability 15 %.

| Candidate | License | First-party CPU-int4 ONNX GenAI artifact | IT+EN instruct | Verdict |
|---|---|---|---|---|
| **Phi-3.5-mini-instruct** (3.8B) | **MIT** | **Yes** (official) | Strong | **Chosen** |
| Qwen2.5-3B-Instruct | Qwen research/community (restrictive) | No first-party prebuilt | Strong | Rejected — licence + no reproducible ONNX artifact |
| Llama-3.2-3B-Instruct | Llama community licence (use restrictions) | Partial | Strong | Backup only (licence constraints) |

Phi-3.5-mini wins on licence (MIT), reproducibility (official artifact, no
fragile self-export), and fit for short deterministic JSON output. It sits in the
requested 3–5 B band and is int4-quantised for the shared 32 GB / 6P+4E CPU host.

> **Accuracy + latency on the target host MUST be measured during setup** — do
> not trust device numbers copied from elsewhere. Run the harness against the
> sidecar (below) and record the numbers before enabling `onnx` in production.

## Install (operator step — never automatic)

Weights are **not** committed to git and are **not** downloaded by the API at
startup. Install them once into a host directory that is mounted read-only:

```bash
pip install 'huggingface_hub[cli]'
NLU_MODEL_DIR=/srv/nubarca-models/nlu ./scripts/install-nlu-model.sh
```

The installer downloads only the pinned revision + CPU-int4 variant, then either
verifies against `scripts/nlu-sidecar/SHA256SUMS` (enforced) or writes a
`.candidate` checksum file for you to review + promote on the first install.
Expected files: `genai_config.json`, `model.onnx`, `model.onnx.data`,
`tokenizer.json`, `tokenizer_config.json`, `special_tokens_map.json`,
`config.json`.

## Run the sidecar

Compose ALONGSIDE the prod files (never alone — see CLAUDE.md):

```bash
NLU_MODEL_DIR=/srv/nubarca-models/nlu \
docker compose -f docker-compose.prod.yml \
               -f docker-compose.prod.local.yml \
               -f docker-compose.nlu.yml \
               --env-file .env up -d nlu
```

The `nlu` service: internal-only (no published ports; reachable from api/worker
as `http://nlu:8090`), read-only weight mount, read-only rootfs, non-root user,
`no-new-privileges`, bounded CPU/RAM, health-checked at `/health`, warm model
loaded once at startup, inference concurrency 1, small bounded queue (429 when
full), greedy decoding, 200-token output cap, per-request timeout → 504.

## Enable in the API

Set on **api + worker** (in `.env`; never `source` it — see CLAUDE.md):

```
Ai__NaturalGallerySearch__Interpreter=onnx
Ai__NaturalGallerySearch__ModelServiceBaseUrl=http://nlu:8090
Ai__NaturalGallerySearch__InterpretTimeoutSeconds=12
Ai__NaturalGallerySearch__FallbackToDeterministic=true
```

With `FallbackToDeterministic=true`, if the sidecar is unreachable/unhealthy the
API silently falls back to the deterministic grammar (no user-facing failure).

### All natural-gallery settings (`Ai:NaturalGallerySearch:*`)

| Key | Default | Meaning |
|---|---|---|
| `Interpreter` | `deterministic` | `deterministic` \| `onnx` |
| `FallbackToDeterministic` | `true` | fall back when the sidecar is down |
| `ModelServiceBaseUrl` | (empty) | internal sidecar URL (e.g. `http://nlu:8090`) |
| `InterpretTimeoutSeconds` | `12` | hard per-interpret timeout |
| `MaxCommandLength` | `400` | reject longer commands |
| `DefaultTopK` | `300` | semantic reduction size |
| `MaximumTopK` | `500` | hard server ceiling |
| `MinimumTopK` | `1` | floor |
| `MaxSemanticCandidates` | `20000` | physical-candidate cap (truncation disclosed) |
| `UseEnglishSemanticTranslation` | `false` | EXPERIMENTAL; off — SigLIP2 is multilingual |
| `DebugLogCommands` | `false` | dev-only; MUST stay false in prod |

## Rollback

Point `NLU_MODEL_DIR` at a previous model directory (or `Interpreter=deterministic`
to disable the sidecar entirely), then `docker compose … up -d nlu` (or stop it).
The deterministic interpreter always remains available, so disabling `onnx` never
breaks natural-language search.

## Startup when files are missing

The sidecar logs a load failure and reports `/health` = 503 (not-ready); it does
**not** crash-loop. The API sees the sidecar as unavailable and (with fallback on)
uses the deterministic interpreter. Nothing downloads weights automatically.

## Privacy / no-cloud guarantees

- The sidecar has no outbound network at runtime; weights are local + read-only.
- User command text is never logged by the sidecar or the API, never audited,
  never placed in metrics labels, and never sent to any external service.
- `POST /api/tv/personal/gallery/interpret-command` is grant-gated, `no-store`,
  POST-body only, rate-limited, and audits only safe facts (outcome bucket +
  interpreter key).

## Benchmarking on the target host

Run the deterministic baseline benchmark (real numbers, no sidecar):

```bash
dotnet test tests/NubArca.Api.Tests --filter "FullyQualifiedName~Benchmark_Corpus"
```

For the `onnx` sidecar, extend the harness (`GalleryCommandBenchmark.RunAsync`)
with the `OnnxDecoderGalleryCommandInterpreter` pointed at the running sidecar and
record structured accuracy, warm p50/p95 latency, peak RSS, and model load time
under concurrent image-embedding load. Do not enable `onnx` in production until
those numbers justify it over the deterministic baseline.
