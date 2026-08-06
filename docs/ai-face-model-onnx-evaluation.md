# AI faces — local ONNX face-recognition model evaluation harness

This is an **evaluation-only** harness used to choose a local ONNX face-
recognition model (detector + recognition) **before** any People/Face feature is
built. It mirrors the photo-similarity ONNX eval harness
([docs/ai-image-onnx-evaluation.md](ai-image-onnx-evaluation.md)).

It performs **no** production processing, writes **no** `FaceDetection` /
`FaceEmbedding` / status rows, creates **no** clusters or identities, assigns
**no** names, and touches **no** Private Vault content. Face processing stays
**disabled by default** and the ONNX face backend is *unavailable* until both
model files for a package are configured and present on disk.

Explicitly out of scope on this branch: People UI, person names, clustering
persistence, cross-owner search, raw-vector exposure, Private Vault processing,
and enabling face processing by default in production.

## Candidate model packages

A face "package" = a SCRFD/RetinaFace **detector** (bounding box + 5-point
landmarks) followed by an ArcFace **recognition** embedder (512-d, cosine).

| Catalog key (`AiModel.Key` / dir) | Eval profile key | Detector file | Recognition file | Rec. input | Dim | Notes |
|---|---|---|---|---|---|---|
| `antelopev2` | `face-insightface-antelopev2-v1` | `scrfd_10g_bnkps.onnx` | `glintr100.onnx` | 112² | 512 | **Primary quality candidate** — RetinaFace/SCRFD-10GF + ResNet100 Glint360K ArcFace. |
| `buffalo_l`  | `face-insightface-buffalo-l-v1`  | `det_10g.onnx`         | `w600k_r50.onnx` | 112² | 512 | **Stable fallback** — SCRFD-10GF + ResNet50 WebFace600K ArcFace. |

Optional future candidates (not seeded here): AdaFace/CVLFace high-quality
recognition; OpenCV **YuNet/SFace** as a licensing-friendly detector/recognition
fallback.

Detector input is letterboxed to **640²**; landmarks required for alignment = 5;
distance metric = cosine. The config lives in code
(`OnnxFaceModels.Catalog`); an `AiProfile` links to it via its short `ConfigHash`
(= the catalog key). These are the harness's **documented assumptions** — verify
them against your actual export with `ai face detect-test` / `embed-test` (the
reported `dim` must be 512 and `finite=True`) before trusting `compare` output.

## Licensing (read before any non-personal use)

**InsightFace code is MIT.** The **pretrained model packages** (`antelopev2`,
`buffalo_l`) are published by InsightFace for **non-commercial research /
personal use** unless separately licensed. NubArca does **not** assume any
commercial grant — the seeded profile metadata records this and `ai face models`
prints the caveat. Confirm the exact terms on the model card before any
commercial deployment. Weights are **never** committed to this repo and are
**never auto-downloaded** — you place them manually (below).

## Pipeline (exact assumptions)

Implemented in `OnnxFacePreprocessor` / `ScrfdDecoder` / `FaceAlignment` /
`OnnxFaceBackend` (ImageSharp + ONNX Runtime, CPU):

1. **Decode** bytes as RGB24 (alpha dropped); apply **EXIF orientation**
   (`AutoOrient`) so rotated/portrait images are handled as viewed.
2. **Detection input:** letterbox-resize (keep aspect, Bicubic) into a black
   640² canvas at the top-left; normalize `(pixel − 127.5) / 128` in **RGB**
   (SCRFD is trained with `swapRB=True`, so the network input is RGB, matching
   ImageSharp's decode). NCHW `1×3×640×640`.
3. **SCRFD decode** (`ScrfdDecoder`, pure/testable): the detector's outputs are
   classified by channel count (1 = score, 4 = bbox-distance, 10 = 5-point kps)
   and grouped to the three FPN strides (8/16/32) by descending row count — so
   the decoder does **not** depend on the export's (numeric) ONNX output names or
   their order. `distance2bbox`/`distance2kps` decode to detector-input pixels;
   greedy IoU **NMS** (0.4) after a score threshold (0.5). Coordinates are mapped
   back through the letterbox scale to the oriented image and normalized to
   `[0,1]`.
4. **Alignment** (`FaceAlignment`, pure/testable): a **similarity transform**
   (scale + rotation + translation, 4 DOF) is estimated by least squares from the
   detected 5 landmarks onto the canonical InsightFace ArcFace 112² reference
   points, then applied as a forward ImageSharp affine warp → a 112² aligned
   crop **in memory** (never written to disk).
5. **Recognition:** normalize the aligned crop `(pixel − 127.5) / 127.5` (RGB,
   `swapRB=True`) → NCHW `1×3×112×112` → ArcFace ONNX → 512-d vector,
   **dimension-validated**, **NaN/Infinity-rejected**, and **L2-normalized**
   (`OnnxImageEmbeddings.Finalize`, shared with the image harness). Cosine == dot
   product on unit vectors.

Properties: one image at a time (bounded memory); **original blobs are never
modified**; **no** persistent face crop / thumbnail / derived artifact is
produced; corrupt/unsupported/huge images are caught and counted as failures
(never a crash); zero-face and multi-face images are handled explicitly.

### Robustness notes / risks (verify before trusting quality)

- **Output order/names:** handled by channel-count classification (above), not
  by name — but a detector export with unexpected output shapes surfaces a
  `detector-output-shape-unexpected` diagnostic and returns no faces (never a
  crash). A detector without a keypoint branch → `detector-has-no-landmarks`
  (recognition needs landmarks to align).
- **Alignment convention:** the estimated matrix is applied as a forward
  ImageSharp affine transform. If aligned crops look wrong, this is the first
  thing to verify (compare `ai face compare` on a known same-person pair).
- **Preprocessing drift:** wrong normalization/resize silently degrades quality;
  `ai face compare` is the sanity check.
- **CPU latency:** detection + recognition on CPU is hundreds of ms to a few
  seconds per image; a future production pipeline must stay cooperative/sliceable
  (this harness is bounded by `--limit` and `Ai__MaxConcurrency` /
  `Ai__TimeoutSeconds`).

## Model directory convention

```
Ai__Onnx__ModelDir=/models/ai
/models/ai/antelopev2/scrfd_10g_bnkps.onnx
/models/ai/antelopev2/glintr100.onnx
/models/ai/buffalo_l/det_10g.onnx
/models/ai/buffalo_l/w600k_r50.onnx
```

`Ai__Onnx__ModelDir` is the **same** value already used by the image harness.
`/models/` and `*.onnx` are git-ignored. On the prod/lab host, mount the model
dir read-only into the api+worker containers. The InsightFace packages ship
additional files (`1k3d68`, `2d106det`, `genderage`, …) — only the detector +
recognition files above are used here; the rest may be present but are ignored.

Obtain the packages yourself (e.g. via the `insightface` Python tooling's model
zoo, or your own export) — **no auto-download**. No Python runtime is added to
the production containers; inference is pure `Microsoft.ML.OnnxRuntime` (CPU).

## CLI

```
ai face models                                              # packages + file presence + license + dim
ai face seed-profiles                                       # seed eval profiles (NOT default; inert w/o weights)
ai face detect-test  --profile <key> --file <fileItemId>    # face count, rounded scores, normalized boxes
ai face embed-test   --profile <key> --file <id> [--face-index N=0]
                                                            # dim / L2-norm / finite / detect+embed ms
ai face compare      --profile <key> --file-a <id> --face-a N --file-b <id> --face-b N
                                                            # cosine similarity + distance of two faces
ai face benchmark    --profile <key> [--limit N=100]        # detect/embed timings (avg,p50,p95) + face stats
ai face sample-pairs --profile <key> [--limit N=25]         # safe file refs + face counts to pick pairs
```

`--profile` falls back to `Ai__FaceProfileKey` when set. All output is sanitized:
counts, timings, dimensions, rounded detection scores, normalized bounding boxes,
rounded cosine/distance, and owner-visible file **names** / logical ids only —
**never** raw vectors, `BlobObjectId`, SHA, `StorageKey`, physical paths, or model
internals. Commands require AI enabled (`Ai__Enabled=true`) + a seeded face eval
profile + both model files present; otherwise they report `unavailable` cleanly
(exit 0), write nothing, and leak nothing.

## Privacy / safety invariants

- **Private Vault excluded:** benchmark/sample candidate queries and the single-
  file lookups all go through the vault-filtered `FileItems` set, so vaulted
  content is never processed and its names/counts never appear (there is no
  vault-unlock path in this harness). Direct file commands on a vaulted file
  report "file not found".
- **Owner boundary:** no cross-owner clustering, no global identities, no public-
  share face metadata.
- **No persistence:** no `FaceDetection`/`FaceEmbedding`/status/cluster rows are
  written; aligned crops live only in memory; original blobs are immutable (no
  copy/move/rename/delete).
- **No raw vectors** leave the service layer through the API or CLI.

## Evaluation workflow (manual, on the host with weights)

1. Place the `antelopev2` and `buffalo_l` files under `Ai__Onnx__ModelDir` (above).
2. `ai face seed-profiles` (idempotent; profiles are non-default and inert until
   weights exist).
3. `ai face models` → confirm `detector_present=True recognition_present=True`.
4. Per profile:
   - `ai face detect-test --profile <key> --file <id>` on a few known people
     photos → face counts + landmark presence look right;
   - `ai face embed-test --profile <key> --file <id>` → `dim=512 finite=True`,
     `l2_norm≈1.0`, timings;
   - `ai face sample-pairs --profile <key>` → pick same-person / different-person
     ids and `ai face compare` them (same person should score high, different
     low);
   - `ai face benchmark --profile <key> --limit 100` → avg faces/image, zero-face
     rate, failure reasons, and detect/embed p50/p95.
5. Compare `antelopev2` vs `buffalo_l` on quality (compare scores), speed
   (benchmark), footprint, and license, then decide the production model in a
   later branch. **No automatic quality claims without real test output.**

## Not built here (future branches)

Detection/embedding **persistence** (`FaceDetection`/`FaceEmbedding` are Phase-0A
schema only), owner-scoped **clustering**, a **People** UI, person **names**,
face **search**, and any pgvector face index. The recognition dimension is 512;
a face pgvector table would be a separate additive migration when that phase
arrives.
