# Document Intelligence v1 — benchmark harness and golden set

This page covers what `scripts/document-bench/` measures, how to run it on the
target server, and which product decision each result feeds. The harness is a
throwaway tool. It is not a NubArca runtime component: it ships in no image, no
production code imports it, and it changes no production defaults.

> **The golden set is SYNTHETIC.** Every person, company, code and amount in it
> is fictional. A score on it ranks candidates under controlled conditions. It
> is **not** a validation on a real document corpus. A decision that the
> synthetic set favours still has to be spot-checked on real owner documents,
> privately, before it becomes a default.

## 1. Golden set v1

`python -m docbench generate` builds the set deterministically. Its source is
the committed Python under `docbench/corpus/` (`docs_it.py`, `docs_en.py`,
`queries.py`). Rendering uses reportlab in invariant mode with bundled DejaVu
fonts, rasterisation uses pypdfium2, and every degradation is seeded per
document, page and variant. Office files get fixed core properties and
normalised zip timestamps. Two runs therefore produce byte-identical output,
and `manifest.json` records two hashes: `truth_sha256` (dataset version
`golden-v1+<truth sha[:12]>`) and `content_sha256` over every file.

The set is never committed. It is regenerated where it is used, in about two
minutes and roughly 45 MB. `golden/` and `results/` are gitignored.

| | v1 |
|---|---|
| Documents | 22 (16 Italian, 6 English) |
| Pages | 59 |
| Queries | 172: 164 answerable, 8 unanswerable, 8 answerable only from a chart or diagram |
| Stored formats | digital PDF, scanned PDF, phone-photo JPG, DOCX, PPTX, XLSX |
| Page image variants | `clean` (200 dpi), `scan` (grey, skew, blur, speckle), `photo` (perspective, lighting, rotation, ≤ 1800 px), `lowdpi` (96 dpi binarised) |

**Document types.** Residential and commercial leases, invoices, a bank
statement, a utility bill, a boiler manual, a lab report, condominium and board
minutes, a project kickoff deck with speaker notes, a three-sheet budget
workbook, a financial report with charts and a two-row header, a two-column
newsletter, physics notes with formulas, a home-insurance policy, a SaaS
agreement, a router manual, a discharge summary, a quarterly review deck, an
inventory workbook, and an academic paper with formulas, a merged-cell table
and a chart. All medical content is fictional.

**Phenomena** are placed on purpose:

- tables with merged cells (colspan and rowspan) and tables continued across pages
- multi-row headers and repeated running headers and footers
- footnotes
- formulas, drawn as images with an invisible text layer
- bar, line and pie charts and a diagram, each with labels that are only in the image
- two-column reading order
- near-duplicate distractor documents (`distractor_of`)

**Ground truth** (`truth/*.jsonl`):

- **Per page:** the reference text in reading order, with furniture marked; the
  block list; table structures as trees and HTML; formulas (LaTeX and plain);
  chart labels; and the oracle layout regions (bbox fractions) that the
  renderer recorded.
- **Per query:** the query types, the language pair, and graded judgements at
  document, page and span level. Grade 2 answers the query and grade 1 is
  related. Every span is asserted verbatim on its page by the tests.

**Query types:**

- Language pairs: IT→IT, IT→EN, EN→IT, EN→EN.
- Phrasing: technical, paraphrase, indirect, ambiguous.
- Content: names, codes, numbers, specialist terms, table, formula.
- Length: short, long, and long-passage.
- Scope: cross-page, page-exact, distractor-sensitive.
- Unanswerable, and visual-only.

The questions are deliberately kept in `.py` only. The repository's own RAG
index ingests Markdown, JSON and text, so a golden question quoted in a doc
would leak into the knowledge it is meant to test. **Do not quote golden
queries in documentation, issues or results committed elsewhere.**

## 2. Harness

The CLI is `python -m docbench <command>`, run inside the benchmark image.
Every run writes `results/<UTC stamp>-<lane>-<name>/` containing the following
files:

- `manifest.json`:
  - Model: model key, repository, pinned revision, licence, runtime and runtime
    version, precision, preprocessing, tokenizer, prefixes and pooling, dims.
  - Run setup: threads, batch, concurrency.
  - Hardware: CPU model and flags (AVX2, AVX-VNNI, AVX-512, AMX), RAM, memory
    speed (dmidecode, when permitted), cgroup limits.
  - Provenance: library versions, dataset version and hashes, git commit, the
    `BENCH_ENVIRONMENT` label.
  - Errors and skips.
- `metrics.json` and per-query or per-page JSONL.

One model runs per process, so `peak_rss_mib` is that model's peak (child
processes such as `llama-server` are sampled too). A runtime or weights that is
missing becomes a **recorded skip** with a reason. It never fails the suite and
never triggers a download.

### Metrics

- **Retrieval:**
  - Measures: Recall@5/10/20, nDCG@5/10 (gain 2^g − 1), MRR and hit@k, at chunk,
    page and document level. Page and document rankings are derived from the
    chunk ranking by first appearance.
  - Recall, MRR and hit count only grade-2 judgements.
  - Breakdowns: overall and by query type.
  - Statistics: a 95 % percentile bootstrap CI (2000 resamples, fixed seed) and
    a paired sign-flip randomisation test (10 000 permutations) against the
    lane's baseline mode. `docbench compare` runs the same test across two runs.
  - Unanswerable queries are excluded from these means. They are scored by
    **abstention**: the AUC of the top-1 score separating answerable from
    unanswerable queries, the score threshold that keeps 95 % of answerable
    queries, and the share of unanswerable queries that still pass it.
- **Chunking diagnostics:**
  - Chunk counts and token sizes.
  - Answer-span coverage: a chunk is relevant at ≥ 60 % span coverage.
  - Fragmentation: answerable queries whose answer span no single chunk covers.
- **OCR:**
  - Text accuracy: CER (and a loose CER that ignores case and punctuation), WER,
    normalised edit distance, word recall.
  - Reading-order error: Kendall-tau over aligned blocks.
  - Omission and hallucination rate, line level, with chart labels allowed.
  - Formula match (LaTeX/unicode-normalised similarity ≥ 0.8).
  - Tables: TEDS and TEDS-S, with tables parsed from HTML, Markdown or OTSL output.
  - Cost: pages/s, latency p50/p95, failure %, load time, peak RSS.
  - Running headers and footers are removed from both sides before scoring.
  - An engine that emits plain text produces no table structure, so its TEDS is
    0 by construction. Read TEDS only for engines that emit tables.
- **Visual:**
  - Page-level retrieval metrics.
  - Cost: indexing s/page, query latency, peak RSS, load time, and index size
    (bytes per page at fp32 and fp16, projected GiB per 100 k pages). For
    multi-vector models, mean vectors per page.

### Lanes

| Lane | Command | Candidates | Notes |
|---|---|---|---|
| Lexical | `text --model none` | BM25F mirror of NubArca | Same tokenizer (accent fold, IT+EN stopwords, single characters dropped) and field weights (title/section 2.5, body 1.0, k1 1.2, b 0.75). `bm25f-gated` adds NubArca's evidence gate. |
| Text embedding | `text --model M --runtime R` | `e5-small` (current baseline), `granite-97m`, `granite-311m`, `qwen3-emb-0.6b` (1024 plus Matryoshka 768/512), optional `bge-m3` | Runtimes: `st` (sentence-transformers reference), `ort`/`ort-int8` (ONNX Runtime), `ov`/`ov-int8` (OpenVINO). Per-model prefixes and pooling: E5 uses `query: ` and `passage: ` with mean pooling; Qwen3 uses an instructed query with last-token pooling and left padding; granite and bge-m3 use CLS pooling with no prefix. Matryoshka vectors are truncated, then L2-normalised. Each run reports dense, BM25F, `hybrid-rrf60` and the gated hybrid. `--fusion-sweep` adds a k × dense-weight grid. |
| Parity | `parity --reference st-run --candidate ort/ov-run` | — | Cosine similarity of embeddings and top-10 overlap: a raw runtime must reproduce the reference before its speed counts. |
| Chunking | `text --chunking …` | `nubarca-char1600` (today's `RichDocumentChunker` rules: one block per PDF page or DOCX paragraph or table, slide plus a separate notes block, sheet rendered as `Header=value \| …`, split at 1600 characters, no overlap) versus `struct-tok512-ov64`, `struct-tok256-ov32`, `struct-tok512-ov0` | The structure-aware variants drop furniture, pack units within a section up to a token budget with sentence overlap, keep tables as their own chunks with header rows repeated, and prefix `Title › Section`. |
| Rerank | `text … --rerank R` | `bge-reranker-v2-m3`, `qwen3-reranker-0.6b` | Reranks the hybrid top-30. |
| OCR | `ocr --engine E` | `pdf-textlayer` (control), `tesseract` (eng+ita, NubArca's order), `rapidocr-ppocrv5` (ONNX); VLM adapters `paddleocr-vl-1.5-gguf` and `xiaomi-ocr-0-gguf` via llama.cpp `llama-server`, plus `glm-ocr` and `paddleocr-vl-1.5` via transformers | `--subset core` is a 24-page mix of tables, formulas, charts and columns that keeps slow VLMs tractable. `--mode region` crops the **oracle** layout regions, giving an upper bound for a layout-first pipeline (not available for `photo`). `predictions.jsonl` feeds `text --ocr-override` to measure retrieval on OCR'd text. |
| Visual | `visual --model V` | `siglip2-so400m` (current baseline), `neomme-260m` (dense and late interaction), optional `neomme-260m-st-late`, `vultron-flash-0.8b`, `qwen3-vl-emb-2b` | Indexes the stored-format page image. |
| Fusion | `fuse --text-run T --visual-run V` | Page-level RRF grid (k 20/60 × visual weight 0.25/0.5/1), and visual narrowing to top 50 followed by text | |
| Report | `report` | — | Writes `results/SUMMARY.md`. |

### Model pinning

`scripts/document-bench/models.lock.json` names every model by repository,
**exact commit SHA**, licence and file patterns. `fetch` refuses any entry whose
revision is not a 40-hex SHA. The SHAs were read from the Hugging Face API on
2026-10-21. Open pins:

- `qwen3-emb-0.6b-onnx` is `TODO`. No community ONNX export was pinned, so
  `qwen3-emb-0.6b:ort` stays a skip until one is reviewed and pinned.
- The NeoMME sentence-transformers *dense* variant is not in the lock. Its
  metadata could not be read. The dense score comes from `neomme-260m` through
  transformers.
- The official PaddleOCR-VL pipeline, with its separate layout model, is not
  wired. The GGUF (llama.cpp) and transformers paths are.
- `llama.cpp` is built from a ref the operator chooses
  (`--build-arg LLAMA_CPP_REF=<reviewed tag or commit>`). The Dockerfile
  refuses to build without one.

Python dependencies are pinned in `requirements-*.txt` and `constraints.txt`.
The CPU-only torch wheel comes from the PyTorch CPU index.

## 3. Running on the target server

Everything runs in a throwaway container. Nothing is installed on the host,
and the benchmark runs with `--network none`. Two operator-provided
directories hold all the state, and **neither is ever written into source**:

- `NUBARCA_BENCH_MODELS_DIR` holds the downloaded weights, about 25 GB with the
  optional models.
- `NUBARCA_BENCH_WORK_DIR` holds the golden set and results.

Use a data mount for both, not the root filesystem.

```bash
cd "$NUBARCA_PRODUCTION_CHECKOUT"     # or any checkout of this branch
: "${NUBARCA_BENCH_MODELS_DIR:?}" "${NUBARCA_BENCH_WORK_DIR:?}"

# 1. Build. The default target has no llama.cpp; vlm adds llama-server.
docker build -t nubarca-docbench:local scripts/document-bench
docker build -t nubarca-docbench:vlm --target vlm \
  --build-arg LLAMA_CPP_REF=<reviewed llama.cpp tag or commit> scripts/document-bench

# 2. Fetch weights. This is the ONLY step with network access, and it is explicit.
scripts/document-bench/fetch-models.sh --dry-run
scripts/document-bench/fetch-models.sh                    # non-optional models
scripts/document-bench/fetch-models.sh --include-optional # bge-m3, Qwen3-VL, Vultron, ...

# 3. Run, offline. Pin threads to the physical P-cores you want to measure.
docker run --rm --network none \
  -v "$NUBARCA_BENCH_MODELS_DIR":/models:ro \
  -v "$NUBARCA_BENCH_WORK_DIR":/work \
  -e BENCH_THREADS=8 -e BENCH_ENVIRONMENT="target" \
  --entrypoint /bench/run-suite.sh nubarca-docbench:vlm
# or a subset of lanes: ... nubarca-docbench:vlm golden lexical text report
```

Notes for the run:

- Run it when the server is otherwise quiet. Production containers compete for
  the same cores and memory bandwidth, which makes latency and throughput
  numbers pessimistic and noisy. Record the load in `BENCH_ENVIRONMENT`.
- Add `--cpuset-cpus` and `--memory` to `docker run` to measure inside the
  budget the worker will actually get.
- Memory speed comes from `dmidecode`, which needs privileges the container
  does not have. Record it by hand from the host if it matters.
- The text and visual lanes take tens of minutes each on CPU. The VLM OCR lane
  is the slow one, so start it with `--subset core`.

## 4. From results to decisions

| Decision | Primary evidence | Accept when |
|---|---|---|
| **Text encoder and runtime** | Text lane, `hybrid-rrf60`, page nDCG@10 and R@10 with CI, by type (especially `it-en`/`en-it`, `paraphrase`, `indirect`); passages/s, query p95, load s, peak RSS; parity versus `st` | It beats `e5-small` on a paired test (p < 0.05) within the worker's RAM and indexing budget. Parity is ≥ 0.99 mean cosine and ≥ 0.9 top-10 overlap. A Matryoshka dimension is chosen at the knee where nDCG stops falling. |
| **Chunking** | The same encoder across `--chunking` configs; fragmentation; chunk counts | A structure-aware config is preferred only if it is not worse on page nDCG and fixes fragmentation or long-passage queries. Keep in mind that it also changes what citations can point at. |
| **Fusion** | `hybrid-rrf60` versus a dense or BM25F-only run; `--fusion-sweep` grid; gated versus ungated | k and weights are chosen from the sweep only if they beat k=60 at equal weights on a paired test. Otherwise keep k=60. |
| **Lexical gate and score threshold** | Abstention: AUC, the threshold that keeps 95 % of answerable queries, and the unanswerable pass-through | This gives the per-profile score floor. A gate that costs more recall than it buys in abstention should be loosened. |
| **Reranker** | Rerank runs versus the same run without rerank; latency added per query | Kept only if the nDCG gain is significant and p95 stays inside the interactive budget. |
| **OCR engine** | OCR lane by variant (`scan` and `photo` weigh most): CER/WER, omission and hallucination, TEDS for table-emitting engines, formula match, pages/s, peak RSS, failure %; then `text --ocr-override` on the OCR'd pages | Tesseract stays the default unless a candidate is clearly better on scan and photo pages at an acceptable pages/s. A hallucination rate above Tesseract's disqualifies a VLM for unattended indexing. |
| **Visual page encoder** | Visual lane page metrics (especially `visual-only`, `table` and `formula`) plus index cost; fusion lane | Replaces SigLIP2 only if it improves page retrieval at an index size the storage budget can hold at 100 k pages. Late interaction must justify its multi-vector bytes. |

## 5. Results from the cloud container (NOT the target)

These were measured on a 4-vCPU Intel Xeon @ 2.10 GHz with 15.7 GB of RAM,
labelled `cloud container, not target`, on dataset
`golden-v1+1d8e66e64ff3`. Only the lanes that need no weights could run here,
because Hugging Face is blocked for downloads. They show that the harness
works. **They are not target numbers.** Latency and throughput must not be
quoted from them.

**Lexical lane** (page level, 164 answerable queries, 95 % bootstrap CI):

| Chunking | Mode | R@5 | R@10 | nDCG@10 [CI] | MRR | doc R@10 |
|---|---|---|---|---|---|---|
| nubarca-char1600 | bm25f | 0.869 | 0.902 | 0.823 [0.771, 0.872] | 0.801 | 0.933 |
| nubarca-char1600 | bm25f-gated | 0.741 | 0.744 | 0.712 [0.646, 0.777] | 0.705 | 0.750 |
| struct-tok512-ov64 | bm25f | 0.863 | 0.890 | 0.787 [0.737, 0.837] | 0.758 | 0.927 |
| struct-tok256-ov32 | bm25f | 0.866 | 0.896 | 0.801 [0.750, 0.849] | 0.772 | 0.927 |

What these show:

- NubArca's lexical gate removes about 15 points of page recall on this set.
  That is the price of its abstention. Without the gate, the top-1 BM25F score
  separates unanswerable queries only moderately (AUC 0.78 to 0.81).
- For lexical retrieval, the structure-aware chunkers are not better than the
  baseline. They lose on `long-passage` and `indirect` queries, whose terms are
  spread across sections. The cross-language queries (`it-en` and `en-it`,
  nDCG@10 around 0.3 to 0.5) are where a multilingual encoder has to earn its
  place.
- Every answer span sits inside some chunk in every config (fragmentation 0).
  `struct-tok512-ov0` equals `ov64` here because sections rarely overflow
  512 tokens.

**OCR lane** (59 pages):

- `pdf-textlayer`, clean: CER 0.000. This shows that the PDF text layer equals
  the reference text.
- `tesseract` eng+ita, all four variants: see the table below.

<!-- TESSERACT_RESULTS -->

`rapidocr-ppocrv5` and the VLM adapters were not run here because their
weights cannot be fetched. On the target they are recorded skips until
`fetch-models.sh` has run.

## 6. Known limitations

- **Synthetic set:**
  - Only 22 documents, so the CIs are wide. Treat a difference inside them as
    no difference.
  - The real corpus has handwriting, stamps, low-contrast faxes and far longer
    documents. None of these are modelled.
- **Region mode** uses the renderer's oracle layout, not a layout model. It is
  an upper bound.
- **Tesseract threading:** Tesseract runs with `OMP_THREAD_LIMIT=1` by
  default. With the limit at the container's full core count (4), the cloud
  container's Tesseract spun past a 120 s timeout on pages that one thread
  reads in under a second. The suite also runs the core subset with
  `--tesseract-omp 4`, so the target shows whether OpenMP helps there. NubArca
  itself does not set the variable today. A per-page timeout turns any hang
  into a recorded failure.
- **Visual-only queries:** answers drawn only into chart images are
  unreachable for any text lane. They are kept to measure the visual lane and
  fusion, and they cap text recall below 1.0.
- **Not yet verified:** the Docker image, the ONNX, OpenVINO and transformers
  adapters, and the VLM adapters were written against the documented model
  interfaces but have not been run, because there were no weights and no
  Docker daemon. Expect small fixes on the first target run, and treat their
  first manifests' errors and skips as part of the result.
