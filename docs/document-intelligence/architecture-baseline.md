# Document Intelligence v1 — Architecture Baseline Inventory

Recorded at `682a01c` (NubArca 0.4.0) by reading the code; nothing in this inventory was run in production. Implemented ≠ enabled ≠ operative: section 2 separates them. Line numbers refer to that commit.
---

## 0. One-paragraph summary

A complete, carefully-bounded **owner-private document RAG** (`user-documents` domain) exists:
byte-sniffed format detection, native extraction for text/PDF/DOCX/XLSX/PPTX, Tesseract OCR as a
child process for unusable PDF pages, a deterministic char-budget chunker, local ONNX
`multilingual-e5-small` (384-d) passage/query embeddings, per-request BM25F + exact in-process
cosine + RRF(k=60), a lexical-anchored evidence gate, and a LocalTrusted-only chat endpoint
(`POST /api/assistant/documents/chat`). A visual page-retrieval layer (SigLIP2 1152-d, PDFium,
LibreOffice in a network-less worker) narrows candidate files. **But**: (1) ingestion is
**CLI-only, per owner** (`documents index --owner … --embed`) — the two document job handlers are
no-op skeletons and nothing fires on upload; (2) there is **no search endpoint and no frontend UI**
for documents (not even for the chat); (3) citations carry **file name + heading only** — the
persisted page/slide/sheet locators are never read back, so PDF answers cite no page;
(4) the tracked production Compose files do **not forward** the `Rag__*`, `Assistant__PrivateKnowledgeModel`,
`Assistant__Models__<local>__*` or `Ai__DocumentExtraction__*` keys, and the office renderer is
not in the 4-file release stack. The embedding provider is shaped for encoder models (mean/CLS
pooling, `hidden == output dim`, single-sequence, input_ids/attention_mask[/token_type_ids] only);
Qwen3-Embedding needs last-token pooling, Matryoshka truncation, and possibly `position_ids`.

---

## 1. Components that exist

### 1.1 Format identification
| Item | Where | Notes |
|---|---|---|
| `DocumentFormatKind` {NativeText, Pdf, WordOpenXml, SpreadsheetOpenXml, PresentationOpenXml} | `src/Ai/Documents/DocumentFormatProbe.cs` (enum near top, ~l.6) | Magic-byte probe: `%PDF-`, ZIP/OPC main-part, CFB (legacy Office) refused. Filename contradicting the package is refused. |
| Candidate selection (SQL) | `src/Ai/Documents/OwnerDocumentEligibility.cs:72-87` | Declared MIME allowlist (text types l.93-105; rich l.108-114) or generic `octet-stream`/`zip` + `.pdf/.docx/.xlsx/.pptx` extension. **Images (JPG/PNG/TIFF scans) and `.doc/.xls/.ppt/.odt/.rtf/.html/.eml` are never candidates.** |
| Native text short-circuit | `src/Ai/Documents/OwnerDocumentIndexer.cs:291-293` | Allowlisted text MIME bypasses the probe. |

### 1.2 Text extraction per format (`IDocumentExtractionProvider`, `src/Ai/Documents/DocumentExtractionContracts.cs:135-146`)
| Format | Provider | Library | Profile key (`DocumentTextSources`, `OwnerDocumentIndexer.cs:1054-1105`) | Output blocks |
|---|---|---|---|---|
| Text/MD/CSV/JSON/XML/YAML | `NativeTextExtractionProvider.cs` (+ `NativeTextExtraction.cs` allowlist l.289-300) | BCL | `doc-native-text-v1` (model `native-text-extraction-v1`) | 1 block, no heading/locator (`NativeTextExtractionProvider.cs:43-48`) |
| PDF | `PdfExtractionProvider.cs` | **PdfPig 0.1.16** (`ContentOrderTextExtractor`, l.88); PDFtoImage 5.4.0 (PDFium+SkiaSharp) for OCR render | `doc-pdf-local-ocr-v1` | 1 block **per page**, `Kind=page`, `Page=n`, **no heading** (l.144-152) |
| DOCX | `WordDocumentExtractionProvider.cs` | DocumentFormat.OpenXml 3.5.1 | `doc-openxml-word-v1` | one block per paragraph / table, heading path from Heading styles (l.77-163, 379-407) |
| XLSX | `SpreadsheetExtractionProvider.cs` | OpenXml | `doc-openxml-spreadsheet-v1` | one block per sheet, heading = sheet name (l.196-201) |
| PPTX | `PresentationExtractionProvider.cs` | OpenXml | `doc-openxml-presentation-v1` | body + notes block per slide, heading = slide title (l.122-134) |
| OPC hardening | `OpenXmlPackageReader.cs` | ZIP directory bounds before parse | — | entries/part/total uncompressed bounds |

Extraction profiles are **auto-created on first use** (`OwnerDocumentIndexer.cs:544-590`, `964-1008`), `Provider=none`, capability `document-extraction`.

PDF specifics:
- Per-page quality heuristic `PdfTextQuality.IsUsable` (`PdfExtractionProvider.cs:204-253`: ≥16 chars, ≥60 % legible, dominant-char ≤50 %, has a letter). Unusable page → OCR.
- Encrypted → permanent `EncryptedDocument` (l.52-57). **Any other PdfPig exception maps to `OfficePackageInvalid`** (l.58-64) — misleading reason token for a corrupt PDF.
- Page ceiling `MaxPdfPages=500`, OCR-page ceiling `MaxOcrPages=200` (both refuse whole doc), char ceiling refuses rather than truncates (l.137-141).
- **No timeout around PdfPig parsing/text extraction** (managed, in-process). A pathological PDF can spin the indexing thread; only cancellation stops it.
- OCR rendering re-opens the **whole PDF bytes per OCR page** (`PdfPageRenderer.cs:62-69`, `Conversion.ToImage` with `page: index`), no render timeout on this path.

### 1.3 OCR
- `TesseractOcrProvider` (`src/Ai/Documents/TesseractOcrProvider.cs`): child process `tesseract - - -l <langs>`, page PNG on stdin, text on stdout, `ArgumentList` (no shell, l.103-116), per-page timeout + kill (l.135, 169-177, 248-252), stdout cap, stderr drained/never logged, languages validated via `--list-langs` (l.281-302).
- Contract `IDocumentOcrProvider` (`DocumentOcrContracts.cs:218-228`) — swap seam for a stronger OCR.
- Invoked **only** from `PdfExtractionProvider.RecognizeAsync` (l.169-189). Not used for images, not for PPTX embedded images.
- Off by default: `Ai:DocumentExtraction:OcrEnabled=false`, `OcrLanguages="eng"` (`NativeTextExtraction.cs:231,235`); `OcrRenderDpi=200`, `OcrPageTimeoutSeconds=30`, `MaxConcurrentOcrPages=1` (clamped 1-4).
- Image ships `tesseract-ocr`, `-eng`, `-ita`, `fonts-dejavu-core` (`src/Dockerfile:86-89`).
- OCR not ready → `Unavailable` (retryable, nothing recorded) for the whole PDF (`PdfExtractionProvider.cs:112-119`).

### 1.4 Canonicalization & chunking
| Item | Where | Facts |
|---|---|---|
| Format version | `OwnerDocumentChunkFormat.Current = 2` (`src/Ai/Documents/OwnerDocumentChunking.cs:32`) | Separate from system `RagIndexFormat.Current`. Stored on `DocumentText.ChunkFormatVersion`. |
| Native-text chunker | `OwnerDocumentChunker.Chunk` (`OwnerDocumentChunking.cs:54-114`) | Reuses `MarkdownRagChunker` (section + heading trail, `src/Rag/Chunking/MarkdownRagChunker.cs`), then splits to `MaxChunkCharacters` (default **1 600 chars**, clamp 200-8 000; `NativeTextExtraction.cs:136,145`) preferring newline > sentence > space. **No overlap.** 1-based ordinals. |
| Rich chunker | `RichDocumentChunker.Chunk` (`src/Ai/Documents/RichDocumentChunker.cs:50-130`) | **One chunk per extracted block** when ≤ budget — i.e. one chunk per DOCX paragraph, per PDF page, per sheet, per slide; longer blocks split on newline/hard cut. **No merging of small blocks, no overlap, never crosses page/slide/sheet.** Chunk count > `MaxChunks` (4 000) refuses the document. |
| Token awareness | none | Budget is characters; e5 truncates at 512 tokens in the provider. `DocumentChunk.TokenCount` column exists but is never written. |
| Language | `DocumentText.Language` written from `artifact.Language` — all providers pass `null` **(verified for PDF; guess for the others)**. Lexical uses `RagLanguages.Unknown` (`OwnerDocumentCorpusSource.cs:103`). |

### 1.5 Text embeddings (local ONNX)
| Item | Where |
|---|---|
| Contract `ITextEmbeddingProvider` + `TextEmbeddingInputKind {Query, Passage}` | `src/Ai/TextEmbeddings/TextEmbeddingContracts.cs:16-23, 47-59` |
| Model catalog (code, not DB) `TextEmbeddingModelConfig` | `src/Ai/TextEmbeddings/RagTextEmbeddingModels.cs` (record ~l.11-42; catalog l.65-82) |
| Only real model | `multilingual-e5-small`, profile `rag-text-multilingual-e5-small-v1` (l.61), 384-d, `QueryPrefix "query: "`, `PassagePrefix "passage: "` (l.77-78), `MaxTokens 512` (l.79), `Pooling mean`, `Normalize true` |
| Dev/test model | `DeterministicTextEmbeddingProvider` (`rag-text-deterministic-v1`, 384-d hashed bag-of-tokens; `DeterministicTextEmbeddingProvider.cs:24-60`) |
| Provider | `OnnxTextEmbeddingProvider` (`src/Ai/TextEmbeddings/OnnxTextEmbeddingProvider.cs`) |
| Tokenizer | `Tokenizers.HuggingFace` 3.23.1 (`NubArca.Api.csproj:52`), loads `tokenizer.json` (l.186-192) — HF tokenizer.json of any type (WordPiece/SentencePiece-Unigram/BPE) **(guess: the binding is HF `tokenizers`, so Qwen BPE should load)** |
| Prefix handling | applied in provider: `PrefixFor(config, kind) + text` (l.118) |
| Truncation | `Truncate` keeps first `max-1` ids + **the original last id** as "separator" (l.238-250) |
| Inputs | `input_ids`, `attention_mask`, `token_type_ids` only if the graph declares it (l.199-219). **No `position_ids`, no KV-cache inputs.** Batch size 1 always. |
| Pooling | `Pool` (l.252-289): accepts pre-pooled `[1,dim]`, else requires `[1,T,dim]` with **`Shape[2] == config.Dimension`** (l.262); `cls` = token 0; else masked mean. **No last-token pooling.** |
| Normalization | L2 if `Normalize`; NaN/Inf refused (l.291-314) |
| Dimension handling | `CheckReadiness` requires `profile.Dimension == config.Dimension` (l.60-63); indexer/retriever re-check vector length (`OwnerDocumentIndexer.cs:926-935`, `OwnerDocumentVectorRetriever.cs:116-119`). No truncation/Matryoshka support anywhere. |
| Concurrency/timeout | own `SemaphoreSlim(Ai:MaxConcurrency)` (l.42), slot held until native call returns (l.120-144); timeout `Ai:TimeoutSeconds` (default 30) → `EmbeddingTimeout` (resumable) |
| Runtime | ONNX Runtime 1.24.1 in-process; `onnxruntime` (CPU) or `openvino-direct` (OpenVINO EP, **FP32 forced**) via `OnnxInferenceSessionFactory` (`src/Ai/Onnx/OnnxInferenceSessionFactory.cs:229-245`); logical model `OnnxModel.RagText` (`OnnxExecutionProviders.cs:56`) with device `Ai:Onnx:OpenVino:RagTextDevice` (default CPU; `AiOptions.cs:201`). Sessions cached for process life, keyed by model path/provider/device. |
| Resolution | `TextEmbeddingResolver` (`src/Ai/TextEmbeddings/TextEmbeddingResolver.cs`): domain → `RagSemanticProfileResolver` → AiProfile by key (capability `text-embedding`, modality `text`, Dimension>0) → AiModel.Provider → provider readiness. **Does NOT require `Ai:Enabled`.** |
| Profile seeding | `AiProfileRegistry.SeedRagTextEmbeddingProfilesAsync` (`src/Ai/AiProfileRegistry.cs:160-231`) iterates the code catalog; invoked **only** by CLI `rag seed-profiles` (`src/Cli/RagCliCommands.cs:314-319`). `IsDefault=false`, `ConfigHash=<catalog key>`. |

### 1.6 Storage tables (owner-private text)
| Table | Entity / config | Keys & notes |
|---|---|---|
| `document_texts` | `src/Domain/Ai/DocumentText.cs`; `src/Data/Configurations/Ai/DocumentTextConfiguration.cs` | `FileItemId` (FK **cascade** to FileItem, l.83-86), `SourceBlobObjectId`, `OwnerUserId`, `ProfileId` (extraction profile), `IsCurrent` (filtered unique `ux_document_texts_current_per_file`, l.70-73), unique `(FileItemId, ProfileId)` (l.51-53), `Source` (native/pdf/docx/xlsx/pptx), `Status` (completed/skipped), `ErrorCode`, `TextHash`, full `Text`, `ChunkFormatVersion`. |
| `document_chunks` | `src/Domain/Ai/DocumentChunk.cs`; `DocumentChunkConfiguration.cs` | `DocumentTextId` (cascade), `OwnerUserId`, `ProfileId`, `Ordinal`, `Heading`, `Text`, `TextHash`, offsets, **`Page`** (real PDF page only, l.49), **`LocatorKind/Index/Label`** (l.65-67), `TokenCount` (unused). Unique `(DocumentTextId, ProfileId, Ordinal)`. |
| `document_chunk_embeddings` | `src/Domain/Ai/DocumentChunkEmbedding.cs`; `DocumentChunkEmbeddingConfiguration.cs` | `(DocumentChunkId, ProfileId)` unique; float32 LE `EmbeddingBytes`; `Dimension` (any >0). **No pgvector accelerator for private text vectors.** Multiple profiles can coexist per chunk. |
| Migrations | `20260516212052_InitialStorageCore` (tables created early), `20260827124758_AddOwnerDocumentExtractionLifecycle` (IsCurrent/locators/format), `20260829173856_AddDocumentVisualRetrieval` | |

### 1.7 pgvector tables (all raw SQL, guarded on extension availability)
| Table | Dim | Index | Used by |
|---|---|---|---|
| `rag_chunk_embedding_vectors_384` | 384 | HNSW cosine | **system** RAG domains only (`src/Rag/Storage/RagVectorIndexService.cs:36` `SupportedDimension = 384`; migration `20260826173229_AddRagVectorIndex384.cs:35-53`) |
| `document_visual_embedding_vectors_1152` | 1152 | **no ANN index by design** (exact, owner-filtered SQL) | document visual (`AddDocumentVisualRetrieval.cs:220-232`; `DocumentVisualVectorIndexService.cs:102-133`) |
| `blob_embedding_vectors_1152` / `_768` | 1152 / 768 | HNSW | photos (`PhotoVectorIndexService.cs:31`, `AddPhotoVectorIndex768`) |
| `video_semantic_sample_embedding_vectors_1152` | 1152 | HNSW | video (`VideoSemanticSampleVectorIndexService.cs:31`) |
| face vector tables | — | — | faces |

No 768/1024 text table exists. Private text retrieval does not need one (exact cosine in process); system domains would.

### 1.8 Lexical BM25F (private docs)
- `RagLexicalIndex` (`src/Rag/Retrieval/RagLexicalIndex.cs`, BM25 saturation l.205; `Search(shape, take, allowedFileItemIds)` l.97-133) with `RagText` tokenizer (accent fold, IT/EN stopwords, no stemming; `src/Rag/Text/RagText.cs`).
- Ranking profile `RagRankingProfiles.UserDocuments` (`src/Rag/Retrieval/RagRankingProfile.cs:144-160`): Section 2.5, Title(file name) 2.5, Body 1.0, alias expansion off, `MinimumScore 0.35`, `RelativeFloor 0.25`.
- Built **per question, never cached**, from the owner's whole eligible corpus (`RagRetriever.cs:293-326`), `OwnerDocumentCorpusSource.LoadAsync` (`src/Rag/Retrieval/OwnerDocumentCorpusSource.cs:64-127`) loads all chunk texts up to `Rag:MaxIndexedChunks+1` (default **200 000**) and refuses beyond.

### 1.9 Vector retrieval (private)
- `OwnerDocumentVectorRetriever` (`src/Rag/Retrieval/OwnerDocumentVectorRetriever.cs:64-191`): embed query (`Query` kind), load eligible `(chunk, embedding)` rows for the active profile, **`Take(MaxCandidateVectors=20 000)` ordered by random GUID (l.45, 139-141) — silent arbitrary truncation above 20k chunks**, exact cosine in C#.
- Inconsistency: lexical refuses at 200k chunks; vector silently samples at 20k.

### 1.10 Fusion & evidence gate
- `RrfFusion.Fuse` k=60 over ranks (`src/Rag/Retrieval/RrfFusion.cs:33-73`).
- Gate `HasStrongEvidence` (`src/Rag/Retrieval/RagRetriever.cs:382-385`): any lexical hit, or a vector hit with cosine ≥ `Rag:MinimumVectorScore` (default **0.80**, global, `RagOptions.cs:67`) — calibrated for e5's compressed cosine range **(guess re. calibration)**.
- Evidence budget from `Assistant:Help` bounds (`EffectiveEvidenceChunks/Characters`), `CenterOnMatch` truncation (`RagRetriever.cs:388-447`).
- Policy gate `AssistantRagPolicy.Refuse` over evidence owner/domain/trust (`src/Assistant/AssistantRagPolicy.cs:87-108`).

### 1.11 Visual retrieval pipeline
| Piece | Where |
|---|---|
| Options `Ai:DocumentVisual:*` | `src/Ai/DocumentVisual/DocumentVisualOptions.cs` (Enabled, DenseProfileKey, RenderOfficeEnabled, LateInteractionEnabled, LateProfileKey, OfficeRendererSocketPath, LateInteractionSocketPath, bounds) |
| Profile | `document-visual-siglip2-so400m-patch14-384-v1`, capability `document-visual-embedding`, 1152-d, `ConfigHash=siglip2-so400m-patch14-384` (`AiProfileRegistry.cs:330-391`); seeded by CLI `documents visual-seed-profiles` |
| Resolver | `DocumentVisualProfileResolver.cs:59-120` — **requires `Ai:Enabled`** (goes through `IAiBackendResolver`) + `Ai:DocumentVisual:Enabled` |
| Renderers | `PdfVisualRenderer` (PDFium, `pdfium-page-render-v1`), `TextCanvasVisualRenderer` (`nubarca-text-canvas-v1`), `OfficeVisualRenderer` (Unix-socket client, magic `NBDR` v1, format ordinal 1-3; `OfficeVisualRenderer.cs:30-31,194-198,218-245`) |
| Indexer | `OwnerDocumentVisualIndexer.cs` — eligibility requires a **current completed `DocumentText`** (l.117-131), renders, embeds pages with SigLIP2 image tower, publishes all-or-nothing |
| Entities | `DocumentVisualIndex` (FileItemId, SourceBlobObjectId, RenderProfileKey, EmbeddingProfileId, Status), `DocumentVisualUnit` (SourcePage/locators, PixelHash), `DocumentVisualEmbedding` (Layout, VectorCount for multi-vector) |
| Retriever | `OwnerDocumentVisualRetriever.cs:132-330` — SigLIP2 text tower query, pgvector exact SQL or in-process fallback (≤ `MaxVisualUnitsPerOwnerExactFallback` 50k), relative floor, max-per-file, ≤8 candidate files |
| Pipeline | `OwnerDocumentRetrievalPipeline.cs` — global text retrieval + visual → scoped text retrieval (`AllowedFileItemIds`) → `PrivateDocumentEvidenceFusion` |
| Evaluator | `DocumentVisualEvaluator.cs`, CLI `documents visual-evaluate` |

Memory note: `PdfVisualRenderer.RenderAsync` accumulates **all page PNGs in a List before any embedding** (`PdfVisualRenderer.cs:~100-130`; indexer consumes `render.Artifact.Units` at `OwnerDocumentVisualIndexer.cs:~296-330`) — bounded only by `MaxVisualUnitsPerDocument=400` and `MaxVisualTotalPixelsPerDocument=4e8`. The docs' "memory holds one image" claim is not what the code does.

### 1.12 Late-interaction seam
- `IVisualLateInteractionProvider` + `MaxSim` + `VisualLateInteractionReranker` (`src/Ai/DocumentVisual/VisualLateInteractionContracts.cs`, `MaxSim.cs`, `VisualLateInteractionReranker.cs`). **No production implementation is registered** (`RagServiceRegistration.cs:150-156` resolves it optionally; only test stubs implement it). `LateInteractionSocketPath` option exists with no client. colSmol-500M evaluated and not promoted (`docs/rag-platform.md:786-848`).

### 1.13 Office renderer worker
- `scripts/document-render-worker/{Dockerfile,server.py}`, `docker-compose.document-renderer.yml` (network_mode none, read_only, cap_drop ALL, 2 CPU / 2 GiB, socket volume). **Not part of the mandated 4-file release `$DC`** (CLAUDE.md) — needs a 5th `-f`.
- Used only for **visual** rendering of DOCX/XLSX/PPTX; text extraction never depends on it.

### 1.14 Readiness / status
- CLI `documents status --owner` (`src/Cli/DocumentsCliCommands.cs:101-173`): counts per extraction profile, OCR readiness, embedded/missing.
- CLI `documents visual-status --owner` (l.237-310): `dense_visual_ready`, per-renderer readiness, `office_renderer_enabled`, `late_interaction_ready`, accelerator stats.
- CLI `rag validate-model`, `rag domains`.
- HTTP `GET /api/assistant/documents/status` (`src/Endpoints/PrivateDocumentAssistantEndpoints.cs:43-65`): enabled, modelBoundary, knowledgeAvailable, semanticEnabled, embeddingProfile, documents, chunks, reason. **Drops `VisualEnabled/VisualReason`** that the service computes (`PrivateDocumentAssistantService.cs:42-43,156-157`).
- `text_private_ready` named in docs is not a field anywhere (guess: docs vocabulary only).

### 1.15 Assistant private-documents chat
- `POST /api/assistant/documents/chat` (`PrivateDocumentAssistantEndpoints.cs:67-106`), auth + rate-limit policy `private-document-chat` (`Program.cs:519`). Body = message + history only; owner from cookie.
- `PrivateDocumentAssistantService.AskAsync` (`src/Ai/Documents/PrivateDocumentAssistantService.cs:160-297`): LocalTrusted only (`Assistant:PrivateKnowledgeModel`, `AssistantModelResolver.cs:117-140`), retrieval pipeline, no evidence → no model call, policy gate, OpenAI-compatible completion (`src/Assistant/OpenAiCompatibleTextModel.cs`).
- Citations `PrivateDocumentCitation(Document=file name, Section=heading)` (l.16, 290-294). **No page, slide, sheet, or file link.**
- Registered unconditionally (`Program.cs:1403-1413`, mapped `Program.cs:1689`).

### 1.16 Frontend
- **No UI** calls `/api/assistant/documents/*` (grep of `frontend/src` finds nothing). Only `HelpPage.tsx` uses the Help assistant. No document search UI.

---

## 2. Status matrix

| Component | Implemented | Enabled by default | Tests (examples) | Production enablement keys / steps | Wiring caveats |
|---|---|---|---|---|---|
| Format probe | yes | n/a (runs when indexer runs) | `Ai/Documents/DocumentFormatProbeTests.cs` (20) | — | — |
| Native text extraction | yes | indexer only | `NativeTextExtractionTests`, `NativeChunkSplitCompletenessTests` | — | — |
| PDF text (PdfPig) | yes | indexer only | `DocumentCompletenessBoundTests`, `PdfTextQualityTests`, `OwnerDocumentCompletenessIntegrationTests` | `Ai__DocumentExtraction__MaxPdf*` (optional) | corrupt-PDF reason token mislabeled; no parse timeout |
| DOCX/XLSX/PPTX text | yes | indexer only | `OfficeExtractionTests` (20) | `Ai__DocumentExtraction__MaxOffice*` (optional) | — |
| OCR (Tesseract) | yes | **no** (`OcrEnabled=false`) | `TesseractOcrProviderTests` (9; incl. real binary read-back, kill on timeout) | `Ai__DocumentExtraction__OcrEnabled=true`, `Ai__DocumentExtraction__OcrLanguages=eng+ita` | **not forwarded by `docker-compose.prod.yml`** (only `Ai__DocumentExtractionEnabled` is, which governs the skeleton job, not OCR) |
| Chunking v2 | yes | — | `OwnerDocumentChunkingTests`, `RagChunkingTests` | `Ai__DocumentExtraction__MaxChunkCharacters/MaxChunks` | per-paragraph DOCX chunks |
| e5-small embeddings | yes | **no** | `Rag/TextEmbeddingProviderTests` (13), `Rag/TextEmbeddingTimeoutTests` (5), `Integration/PrivateDocumentRealOnnxTests` (3, gated on `Ai__Onnx__ModelDir`) | `rag seed-profiles`; files `$Ai__Onnx__ModelDir/multilingual-e5-small/{model.onnx,tokenizer.json}`; `Rag__Domains__user-documents__SemanticEnabled=true`; `Rag__Domains__user-documents__TextEmbeddingProfileKey=rag-text-multilingual-e5-small-v1` (OwnerPrivate never inherits; `RagSemanticProfileResolver.cs:64`); optional `Ai__Onnx__OpenVino__RagTextDevice` | **No `Rag__*` key is forwarded by any tracked compose file**; must come from the operator's untracked `docker-compose.prod.local.yml` (not verifiable here) |
| Private BM25F + RRF + gate | yes | lexical works once chunks exist | `OwnerDocumentVectorRetrievalTests`, `OwnerDocumentIsolationTests`, `PrivateDocumentGoldenEvaluationTests`, `RrfFusionTests`, `OwnerProvenanceAndCeilingTests` | `Rag__MaxIndexedChunks`, `Rag__MinimumVectorScore` | 20k vector cap silent |
| Private chat endpoint | yes | **no** (`Assistant__Enabled=false`, no private model) | `PrivateDocumentBoundaryTests` (10) | `Assistant__Enabled=true`, `Assistant__PrivateKnowledgeModel=<name>`, `Assistant__Models__<name>__{Protocol=OpenAiCompatible,Trust=LocalTrusted,BaseUrl,Model}` | prod compose forwards only `Assistant__Models__help-default__*`; **`PrivateKnowledgeModel` not forwarded**; no UI |
| Visual dense retrieval | yes | **no** | `Ai/DocumentVisual/*` (≈140 tests), `Integration/DocumentVisualPgIntegrationTests`, `DocumentVisualRealOnnxTests` (gated), `DocumentVisualPhaseZeroTests` | `Ai__Enabled=true`, `Ai__DocumentVisual__Enabled=true`, SigLIP2 under `Ai__Onnx__ModelDir/siglip2-so400m-patch14-384/{model.onnx,text_model.onnx,tokenizer.json}`, `documents visual-seed-profiles`, `documents visual-index --owner` | keys forwarded (`docker-compose.prod.yml:278-284`) |
| Office renderer | yes | **no** | `OfficeVisualRendererTests` (10), `Integration/OfficeRendererWorkerTests` (5) | 5th compose file + `Ai__DocumentVisual__RenderOfficeEnabled=true` + `…OfficeRendererSocketPath=/run/nubarca-render/render.sock` | not in release `$DC` |
| Late interaction | seam only | no | `VisualLateInteractionTests` (14), `MaxSimTests` (12) | none usable | **no provider implementation** |
| Document backfill jobs | **skeleton no-op** | no | `AiJobSkeletonTests` | `Ai__DocumentExtractionEnabled`, `Ai__DocumentEmbeddingsEnabled` | `AiDocumentsExtractBackfillJobHandler.cs`/`AiDocumentsEmbeddingsBackfillJobHandler.cs` subclass `AiSkeletonBackfillJobHandler` whose terminal action is `NoOpAsync("skeleton-noop")` (`AiSkeletonBackfillJobHandler.cs`, end of `ExecuteAsync`). They also resolve `ITextExtractor`/`ITextEmbedder` (photo SigLIP tower!) via generic capabilities, i.e. unrelated to the real pipeline. |
| Product Help / repo RAG | yes | lexical yes | `Rag/*` | separate | not in scope but shares code |

"Implemented but not wired" list:
1. Document backfill job types (`ai.documents.extract.backfill`, `ai.documents.embeddings.backfill`) — enqueueable via `jobs enqueue` (`CliEntryPoint.cs:1549-1550`) but do nothing.
2. Chunk `Page` / `Locator*` — written (`OwnerDocumentIndexer.cs:527-533`) but never read by corpus source, evidence, or citations (`OwnerDocumentCorpusSource.cs:70-74` selects no locator; `RagIndexedChunk` has no locator field).
3. `VisualEnabled/VisualReason` computed but not returned by `/status`.
4. `IVisualLateInteractionProvider`, `LateInteractionSocketPath` — no implementation.
5. `DocumentChunk.TokenCount` — never written.
6. Prod compose: no pass-through of `Rag__*`, `Assistant__PrivateKnowledgeModel`, `Assistant__Models__<local>__*`, `Ai__DocumentExtraction__*`.
7. Chat + status endpoints — no frontend consumer.

---

## 3. Ingestion triggering & lifecycle

- **Trigger: CLI only.** `documents index --owner <id> [--limit N] [--embed]` → `OwnerDocumentIndexer.IndexOwnerAsync` (`DocumentsCliCommands.cs:60-97`). Visual: `documents visual-index --owner`. No caller elsewhere (grep of `OwnerDocumentIndexer` usages). No hook in `Uploads/`, `Ingestion/` (`PostIngestionMediaPipelineService`) or any scheduler. No "all owners" mode by design (`docs/rag-platform.md:941-946`).
- **Scheduling class exists** for doc jobs (`Jobs/JobScheduling.cs:68-69` → `Compute`), so the job framework (priority, slicing, checkpoints, cancellation; `docs/job-scheduling.md`) is available but unused here.
- **Per-file state**: one `DocumentText` row per (file, extraction profile) with `Status ∈ {completed, skipped}` + `ErrorCode`; `IsCurrent`. **No `pending`/`running`/`failed` state, no per-phase (extract → chunk → embed → visual) status, no attempt counter, no timestamps per phase.** Embedding progress is derived (`missing_embeddings` in `documents status`). Retryable failures (OCR unavailable, storage IO, embedding timeout, renderer down) leave **no row at all** — only CLI counters.
- **Idempotency**: skip when current row has same blob + current chunk format + active extraction profile + completed (`OwnerDocumentIndexer.cs:207-237`); chunk-level reuse by `(ordinal, TextHash)` keeps embeddings (l.748-812); embeddings only for chunks lacking one for the active profile (l.879-898). Rename/move costs nothing.
- **Resumability**: keyset paging 64 files (l.96-125) and 32 chunks (l.856-949); each embedding saved individually (l.937-947) so an interrupted run resumes; extraction re-runs per file. No persisted checkpoint (re-scan from Guid.Empty each run; unchanged files short-circuit cheaply).
- **Retry**: implicit, by re-running the CLI. Permanent verdicts (`IsPermanent`) recorded against the blob (l.338-355) and not retried until the blob changes; environment failures not recorded.
- **Cancellation**: `ThrowIfCancellationRequested` in loops; OCR/Office/embedding propagate `OperationCanceledException`. But `DocumentsCliCommands.IndexAsync` calls `IndexOwnerAsync(owner, limit, embed)` **without a token** (l.68-69) — Ctrl-C does not reach the indexer cooperatively **(guess: process kill instead)**.
- **Memory guards**: size gate before read (`CandidateSourceBudget`, l.186-193) and bounded copy (l.257-259, 1012-1025); whole file buffered as `byte[]` (≤64 MiB PDF/Office default, 256 MiB hard). OCR concurrency 1 (process-wide). PDFium serialized by a static semaphore — **but `PdfPageRenderer.Native` (`PdfPageRenderer.cs:23`) and `PdfVisualRenderer.Native` (`PdfVisualRenderer.cs:30`) are two different semaphores**; the code comment itself says "two gates over one native library are one gate too few". Harmless while text and visual indexing run in separate CLI processes; a race once both run in one worker. Embedding concurrency `Ai:MaxConcurrency` **per embedder instance** (text RAG, photo text, photo image each have their own gate).
- **Encrypted PDFs**: permanent skip `EncryptedDocument`. Password-protected OOXML: refused by name (`docs/rag-platform.md:512-514`).
- **Corrupt PDFs**: permanent skip with reason `OfficePackageInvalid` (mislabeled). PdfPig per-page exceptions → empty page → OCR candidate (l.90-93).
- **Huge PDFs**: >64 MiB size skip; >500 pages refused; >200 OCR pages refused; >1 M chars refused; >4 000 chunks refused — all whole-document refusals (no partial indexing by design).
- **Timeouts**: OCR per page (30 s, kill); embedding per call (`Ai:TimeoutSeconds` 30 s); Office render (120 s) and PDF visual page render (30 s). **None** for PdfPig parse, OpenXml parse, OCR-path PDFium render, or the whole-document budget.
- **Deletion/vault**: no sweeper; eligibility joins live `FileItem` (deleted/vaulted/inactive files vanish from retrieval immediately); `document_texts` cascades on hard delete of FileItem.

---

## 4. Model profiles and adding a new text embedding model

### 4.1 Exact keys
| Kind | Profile key | Model key | Capability / modality | Dim | Seeded by |
|---|---|---|---|---|---|
| Extraction | `doc-native-text-v1` | `native-text-extraction-v1` | document-extraction / document | — | indexer (auto) |
| Extraction | `doc-pdf-local-ocr-v1` | `doc-pdf-local-ocr-v1-model` (key+"-model", `OwnerDocumentIndexer.cs:557`) | document-extraction / text | — | indexer (auto) |
| Extraction | `doc-openxml-word-v1`, `doc-openxml-spreadsheet-v1`, `doc-openxml-presentation-v1` | `<key>-model` | document-extraction / text | — | indexer (auto) |
| Text embedding | `rag-text-multilingual-e5-small-v1` | `multilingual-e5-small` | text-embedding / text | 384 | `rag seed-profiles` |
| Text embedding (dev) | `rag-text-deterministic-v1` | same | text-embedding / text | 384 | `rag seed-profiles` |
| Visual | `document-visual-siglip2-so400m-patch14-384-v1` | `siglip2-so400m-patch14-384` (**shared AiModel row with photos**) | document-visual-embedding / multimodal | 1152 | `documents visual-seed-profiles` |
| Photo (shared weights) | `photo-siglip2-so400m-patch14-384-v2` | same model row | image-embedding | 1152 | ai seed |

(The `DocumentTextSources.*ModelKey` constants such as `openxml-word-extraction-v1` are declared but the auto-seeder actually uses `profileKey + "-model"` — a small inconsistency.)

### 4.2 Files expected
- `${Ai__Onnx__ModelDir}/multilingual-e5-small/model.onnx` + `tokenizer.json` (`RagTextEmbeddingModels.cs:69-72`, `OnnxTextEmbeddingProvider.cs:316-320`).
- `${Ai__Onnx__ModelDir}/siglip2-so400m-patch14-384/model.onnx`, `text_model.onnx`, `tokenizer.json` (`OnnxImageModels.cs:41-43,53-63`).
- Production model mount `/models/ai`, OpenVINO direct, cache `/var/cache/nubarca/openvino` (`docker-compose.facedirect-api.yml:25-29`). No `docs/model-deployment/*` page exists for e5 (only plates, subject-cutout, NLU, human-aesexpert, SigLIP2 benchmark).

### 4.3 What assumes e5 / 384 / encoder
| Assumption | Location | Impact for a new model |
|---|---|---|
| Catalog has one entry; mapping profile→catalog in code | `RagTextEmbeddingModels.cs:65-90` | add entry + mapping; `rag seed-profiles` then seeds it automatically |
| Pooling ∈ {mean, cls} | `TextEmbeddingPooling` (l.44-48), `Pool` (`OnnxTextEmbeddingProvider.cs:252-289`) | need `last` (last non-pad token, honoring left/right padding) |
| hidden size == stored dimension | `Pool` l.262; `CheckReadiness` l.60 | need `NativeDimension` vs `OutputDimension` + slice-then-renormalize for Matryoshka |
| Inputs only ids/mask/(token_type) | l.199-219 | decoder exports usually need `position_ids`; exports with past need `past_key_values.*` dummy inputs — must export without KV cache, or feed them |
| Static prefix strings | `QueryPrefix/PassagePrefix` | Qwen3 instruction can be expressed as a static QueryPrefix (`"Instruct: <task>\nQuery:"`) with empty PassagePrefix; per-task instructions would need a richer seam |
| Truncate keeps last id as "separator" | `Truncate` l.238-250 | correct for Qwen3 only if the tokenizer post-processor appends EOS (`<|endoftext|>`) **(guess: newer Qwen3-Embedding tokenizer.json does; must be verified on the exported asset)**; otherwise last-token pooling reads a content token |
| `MaxTokens 512` per input | catalog | Qwen3 supports 32k; chunk budget 1 600 chars ≈ 400-500 tokens so 512-1024 is fine |
| Batch size 1 | `Run` | correctness fine; throughput low for a 0.6 B model on CPU (guess: ~10-50× slower per chunk than e5-small) |
| FP32 forced on OpenVINO | `OnnxInferenceSessionFactory.cs:239-245` | 0.6 B FP32 ≈ 2.4 GB weights resident in **API** process for life (no eviction), next to SigLIP2 and face models |
| One device for all text models | `OnnxModel.RagText` / `RagTextDevice` | e5 and Qwen3 cannot be placed differently |
| 384 in system pgvector | `RagVectorIndexService.cs:36`; `RagVectorRetriever.cs:56-58` | **user-documents unaffected** (no pgvector); Product Help on a 1024 profile would degrade to lexical with `EmbeddingDimensionUnsupported` until a `rag_chunk_embedding_vectors_1024` migration + service generalization |
| Deterministic provider 384 | `DeterministicTextEmbeddingProvider.cs:30` (uses profile dimension at runtime) | tests that assume the 384 table only |
| Evidence gate cosine 0.80 global | `RagOptions.cs:67`, `RagRetriever.cs:382-385` | cosine distributions differ per model; needs per-profile threshold or recalibration **(guess: Qwen3 semantic-only hits would almost never pass 0.80, so the gate would be effectively lexical-only, safe but wasteful)** |
| Domain→profile resolution | `RagSemanticProfileResolver.cs`, `Rag__Domains__user-documents__TextEmbeddingProfileKey` | switching profile requires full re-embed (`documents index --embed`); old vectors stay (different ProfileId), so A/B side-by-side is possible |

### 4.4 Concretely, adding Qwen3-Embedding-0.6B (1024 and/or 768)
Minimum code changes **(design sketch, not a design)**:
1. `TextEmbeddingModelConfig`: add `Pooling = "last"`, `NativeDimension` (1024) distinct from `Dimension` (1024 or 768), optional `PositionIdsTensor`, and maybe `PaddingSide`. Two catalog entries / two profiles, e.g. `rag-text-qwen3-embedding-0.6b-1024-v1` and `…-768-v1` (Matryoshka = truncate pooled 1024 to first 768, then L2-normalize).
2. `OnnxTextEmbeddingProvider.Pool`: last-token pooling (index of last `mask==1`), dimension check against `NativeDimension`, then truncation; `Finalize` after truncation.
3. Inputs: supply `position_ids = 0..T-1` when the graph declares it; refuse graphs with `past_key_values` inputs (or feed empty past) — readiness should check input names.
4. Verify tokenizer.json appends EOS; otherwise add an `AppendEosTokenId` config and make `Truncate` reserve it.
5. Seeding: automatic via catalog (`AiProfileRegistry.cs:176-180`). Files at `${Ai__Onnx__ModelDir}/<subdir>/model.onnx` + `tokenizer.json` (an ONNX export >2 GB needs external-data file next to it; session loading handles that **(guess)**).
6. Config: `Rag__Domains__user-documents__TextEmbeddingProfileKey=<qwen profile>`; re-run `documents index --owner … --embed` per owner.
7. Evidence gate: make `MinimumVectorScore` per profile (or per domain) and calibrate.
8. No migration needed for user-documents; a 1024/768 pgvector table only if system domains adopt it.
9. Tests: catalog/readiness/pooling/truncation unit tests analogous to `TextEmbeddingProviderTests`; a gated real-model test analogous to `PrivateDocumentRealOnnxTests`.

---

## 5. FileItem coupling (private document pipeline)

Hard couplings:
| Layer | Location | Coupling |
|---|---|---|
| Entity | `DocumentText.FileItemId` (`src/Domain/Ai/DocumentText.cs:19`), FK cascade to `FileItem` (`DocumentTextConfiguration.cs:83-86`), unique `(FileItemId, ProfileId)` (l.51-53), filtered unique current-per-file (l.70-73) | identity of a "document" = FileItem |
| Entity | `DocumentText.SourceBlobObjectId` (l.32) | content identity = BlobObject |
| Entity | `DocumentVisualIndex.FileItemId` (`DocumentVisualIndex.cs:26`, config l.51,66) | same |
| Chunk/embedding | `DocumentChunk` → `DocumentTextId` only; `DocumentChunkEmbedding` → chunk only | **not directly coupled** |
| Eligibility | `OwnerDocumentEligibility.Eligible/IsEligibleFor/Extractable/EligibleChunks` (`OwnerDocumentEligibility.cs:43-87,158-181`): joins `FileItem` on owner, `DeletedAt`, `PrivateVaultId`, `MediaLibraryState`; returns `EligibleChunk.File` | **the** privacy boundary is defined over FileItem |
| Visual eligibility | `OwnerDocumentVisualEligibility.cs:71,96,143` | FileItem + DocumentText + blob currency |
| Indexer | `OwnerDocumentIndexer.cs:103-110` enumerates FileItems (keyset on `FileItem.Id`), `CandidateFile(Id, BlobObjectId, Name, MimeType, SizeBytes)`; bytes from `BlobObjects.StorageKey` + `IBlobStorage` (l.239-259); demotion by FileItem (l.605-660) | |
| Corpus | `OwnerDocumentCorpusSource.cs:70-74,175-194`: `Name` → `SourceKey/Path/Title`; `FileItemId` and live `OwnerUserId` from FileItem | |
| Retrieval contracts | `RagIndexedChunk.FileItemId` (`RagIndexedChunk.cs:60`), `RagQuery.AllowedFileItemIds` (`RagContracts.cs:76`), `RagLexicalIndex.Search(…, allowedFileItemIds)` (l.98-114), `OwnerDocumentVectorRetriever` (l.72,134), `RagRetriever` (l.135,151), pipeline (`OwnerDocumentRetrievalPipeline.cs:88`), visual retriever (`OwnerDocumentVisualRetriever.cs:195-322` groups by FileItemId) | the narrowing seam is typed as FileItem ids |
| Citations | `PrivateDocumentCitation(Document = FileItem.Name, Section)` | name only, no id |
| Policy | `AssistantRagPolicy` checks `OwnerUserId` + domain only | **not FileItem-coupled** |

Cheapest seam for emails (assessment only):
- The FileItem dependency is concentrated in **three** places: `DocumentText` identity columns, `OwnerDocumentEligibility` (+ visual eligibility), and `OwnerDocumentIndexer`'s enumeration/byte source. Chunking, embeddings, BM25F, RRF, gate, policy and the chat service only need `(owner, source id, display name, heading/locator, text)`.
- Lowest-cost option: add a **source-kind discriminator + generic source id** on `document_texts` (e.g. `SourceKind ∈ {file, email}`, `SourceId`), keep `FileItemId` nullable for back-compat, and give `OwnerDocumentEligibility.EligibleChunks` a per-kind eligibility branch (UNION of a FileItem join and an email-message join, each owner-prefiltered). Rename `RagIndexedChunk.FileItemId`/`AllowedFileItemIds` to a neutral source id. The indexer's extraction/chunk/embed core (`IndexFileAsync` after bytes are in hand, `ReplaceChunksAsync`, `EmbedAsync`) is reusable if fed an `(owner, sourceKind, sourceId, bytes|blocks, name, contentVersion)` tuple instead of `CandidateFile`.
- Things that would need care: the cascade FK and the two unique indexes (must become `(SourceKind, SourceId, …)`); `SourceBlobObjectId` as idempotence key (emails need a content hash/version); the "derived rows are not authority" rule requires an email-side liveness/eligibility predicate as strong as FileItem's; the "same owner" invariant (`OwnerUserId` denormalized from the live source).
- Alternative (more isolated, more code): a parallel `email_*` table family that emits `RagIndexedChunk`s into the same per-request corpus. Cheaper to secure, duplicates lifecycle code.

---

## 6. Search surface today

- **No document search endpoint.** The only document-facing HTTP endpoints are `GET /api/assistant/documents/status` and `POST /api/assistant/documents/chat` (`PrivateDocumentAssistantEndpoints.cs:43,67`). The chat returns generated text + `[{document, section}]`; no chunk text, no page, no file id, no score.
- `GET /api/search?q=` (`Program.cs:1748-1763`) is **filename/MIME substring search** over FileItems (`FileItemService.cs:640-660`), not content.
- `rag query` CLI is system-domain diagnostic; there is no `documents query` CLI. `documents visual-evaluate` is the only CLI that runs private retrieval and it prints metrics, not results.
- Frontend: nothing (§1.16).
- A plain "search my documents → documents/pages/chunks with provenance" endpoint does **not** exist. `OwnerDocumentRetrievalPipeline.RetrieveAsync` already produces the ranked owner-scoped `RagEvidence` (chunk id, file title, section, text, ranks) that such an endpoint would wrap, but `RagEvidence` lacks file id/page/locator and the no-id-on-the-wire citation rule (`docs/rag-platform.md:487-494`) would need an explicit, reviewed exception (e.g. returning the owner's own FileItemId so the UI can open the file, which every other owner endpoint already does).

---

## 7. Shared infrastructure with photo SigLIP2 / video

| Shared thing | Where | Risk from document work | Existing guard rail |
|---|---|---|---|
| SigLIP2 ONNX sessions (image + text towers) | `OnnxImageEmbedder` uses `OnnxModel.PhotoImage` (`OnnxImageEmbedder.cs:103,164`); `OnnxTextEmbedder.Supports` both `image-embedding` and `document-visual-embedding` (`OnnxTextEmbedder.cs:45`) | doc page embedding contends with photo backfill for the same session/DUAL pool and the same `_gate`; device change for photos changes docs | separate profile/capability/table; same weights = same preprocessing (`ConfigHash` shared) |
| `AiModel` row `siglip2-so400m-patch14-384` | `AiProfileRegistry.cs:339-358` | disabling/editing the model row disables both photo and document visual | profiles separate; doc profile `IsDefault=false` |
| `ai_profiles` / `ai_models` tables | — | new text profiles appear in `ai status` listings; a profile with wrong capability is refused by resolvers | resolvers filter by capability + modality + explicit key; never "newest" |
| `Ai:Enabled`, `Ai:MaxConcurrency`, `Ai:TimeoutSeconds` | `AiOptions.cs:50-51` | changing them for docs changes photo/face/video behavior | text RAG path does not depend on `Ai:Enabled` |
| `IOnnxInferenceSessionFactory` process cache | `OnnxInferenceSessionFactory.cs:33-36` | a large text model resident forever in API (and worker if jobs run there); OOM risk alongside SigLIP2 + faces | per-model device config; FP32 forced; no eviction |
| OpenVINO native core / cache dir | `EnsureNativeProviderInitialized` | provider is process-global; cannot run docs on CPU-ORT while photos on OpenVINO | validator rejects unknown providers |
| Jobs worker + `Compute` class | `JobScheduling.cs:68-69` | real document jobs would compete with photo/face/video compute jobs | priority classes, slicing, single worker (`.env.example` notes "run at most ONE worker") |
| PDFium (PDFtoImage) | two static semaphores | concurrency bug noted §3 | — |
| pgvector extension | migrations guarded | doc visual table has deliberately **no ANN index**; adding one would break owner-prefilter semantics (test asserts absence) | `DocumentVisualPgIntegrationTests` |
| `/models/ai` mount & ModelDir | `docker-compose.facedirect-api.yml:20-29` | new model directories coexist | catalog per subdir |

Guard rails to preserve: separate capability per vector space; explicit profile keys in config; vectors keyed by `ProfileId`; per-domain semantic resolution (OwnerPrivate never inherits); eligibility joins live FileItem; no ANN on owner-private tables; no IDs/text in logs; LocalTrusted-only generation.

---

## 8. Gaps vs. target pipeline (ranked)

Target: Upload → format id → native extraction/render → selective OCR → structure (layout, tables, formulas, reading order) → chunking → text+visual embeddings → indexing → hybrid search with page/section citations.

1. **Automatic, observable ingestion** (critical). No upload hook, no working job; only per-owner CLI. Needs a sliced, checkpointed `Compute` job (per-file work items, keyset), enqueue on upload/replace/restore and a backfill, plus a persisted per-file per-phase state (`pending/extracting/ocr/chunked/embedded/visual/failed-retryable/skipped-permanent`, attempt count, last error token, timestamps). Retryable failures are currently invisible.
2. **User-facing search surface** (critical). No "search my documents" endpoint and no UI (not even for chat). Needs an owner-scoped endpoint returning documents → pages/sections → snippets with scores/modes and a frontend.
3. **Page/section citations** (high). Locators are stored but dropped at `OwnerDocumentCorpusSource` → PDF answers have neither page nor section; DOCX/PPTX/XLSX only via heading. Plumb `Page/LocatorKind/Index/Label` through `RagIndexedChunk` → `RagEvidence` → citation DTO.
4. **Production wiring** (high). Tracked compose doesn't forward `Rag__*`, `Assistant__PrivateKnowledgeModel`, `Assistant__Models__<local>__*`, `Ai__DocumentExtraction__OcrEnabled/OcrLanguages`; office renderer not in release stack; no e5 model-deployment doc. **(Verify the operator's untracked `docker-compose.prod.local.yml` before concluding.)**
5. **Embedding model capability** (high, for Qwen3). §4.4: last-token pooling, Matryoshka, position_ids, per-profile gate threshold, batching; memory placement (API vs worker).
6. **Chunking quality** (high). Per-paragraph DOCX chunks, per-page PDF chunks regardless of structure, no overlap, no token budget, no small-block merging, no table-aware chunking.
7. **Structure/layout** (medium-high). No layout analysis (PdfPig content-order only), no table detection in PDFs, no formula handling, no reading-order model, no headings for PDFs, no language detection.
8. **OCR coverage** (medium). Only PDF pages; no OCR for image files (scans as JPG/PNG/HEIC/TIFF), no PPTX/DOCX embedded images; Tesseract baseline quality; whole-PDF re-open per OCR page.
9. **Scale limits** (medium). Per-question full-corpus load (≤200k chunks × ≤1 600 chars into memory) and silent 20k vector cap; exact cosine in C# over byte[] per question. Fine for hundreds-to-low-thousands of docs; not for large archives.
10. **Robustness timeouts** (medium). No timeout around PdfPig/OpenXml parsing; misleading `OfficePackageInvalid` for corrupt PDFs; PDFium dual-semaphore race once indexing runs in one process.
11. **Formats** (medium-low). No `.doc/.xls/.ppt/.odt/.ods/.odp/.rtf/.html/.epub/.eml/.msg`. (Legacy refused by design.)
12. **Visual memory** (low-medium). All page PNGs of a doc held before embedding.
13. **Late interaction** (low). Seam only; no promoted model — measured decision.

---

## 9. Risks and strictly necessary changes

Risks:
- **Privacy regression when adding automation/search**: the boundary is a set of joins (`OwnerDocumentEligibility`) and "never cached / owner-prefiltered" rules. A search endpoint, a job, or an email source that bypasses `EligibleChunks` (or adds an HNSW index over owner-private vectors) would break it silently. Keep every read through the eligibility predicate; keep the ANN-absence test.
- **ID exposure policy conflict**: useful search results need to address the file (open/download/page). Current doctrine forbids ids in citations; a reviewed exception (owner's own `FileItemId`, never chunk/blob ids) is needed and must be documented per CLAUDE.md "explicitly designed and reviewed as safe".
- **Resource contention**: a 0.6 B FP32 text model plus SigLIP2 plus face models in one API process; query embedding runs in the API on every chat/search. Placement (API vs worker), device, and `Ai:MaxConcurrency` per embedder need deciding. Job slicing must keep foreground imports unblocked (CLAUDE.md job rules).
- **Model swap semantics**: switching `TextEmbeddingProfileKey` makes semantic unavailable for unembedded chunks until a full re-embed (graceful: lexical fallback per question, but hybrid quality is mixed during migration — the retriever only uses vectors of the active profile, so partially embedded owners get partial semantic recall without any signal).
- **Evidence gate calibration**: `MinimumVectorScore=0.80` is global; a new model needs recalibration or semantic-only evidence never qualifies (safe) or qualifies too easily (unsafe for an absolute threshold below the model's noise floor).
- **Ops drift**: enabling anything private requires env keys that the tracked compose doesn't forward; deploying must follow `deploy/FAST_DEPLOY.md` and the operator's local override.
- **Docs vs code drift**: rag-platform.md claims PDF page citations and "one image in memory"; code does neither. Treat the doc as intent, the code as truth.

Strictly necessary changes for v1 (minimum set):
1. A real document ingestion job (replace the skeleton handlers or add new job types) with per-file per-phase state, cooperative slicing/checkpoints, cancellation that never records permanent failure, and enqueue on upload/content change + owner/installation backfill.
2. Plumb locators (page/slide/sheet/section) end-to-end into evidence and citations.
3. An owner-scoped document search endpoint (+ frontend) built on `OwnerDocumentRetrievalPipeline`, with a reviewed provenance DTO.
4. Compose pass-through (or documented operator override) for `Rag__Domains__user-documents__*`, `Ai__DocumentExtraction__OcrEnabled/OcrLanguages`, `Assistant__PrivateKnowledgeModel` and the local model profile; decide whether the document-renderer becomes part of the release stack.
5. If Qwen3-Embedding is adopted: provider extensions in §4.4 (last-token pooling, Matryoshka truncation, position_ids, EOS verification), per-profile gate threshold, and a model-deployment doc with file layout/hashes.
6. Small correctness fixes worth bundling: single shared PDFium gate; correct reason token for corrupt PDFs; a parse timeout/cancellation bound around PdfPig/OpenXml; consistent ceiling behavior between lexical (refuse at 200k) and vector (silent 20k sample); pass a CancellationToken from the CLI.
7. Keep scope: emails only need the seam noted in §5 (do not build it now).
