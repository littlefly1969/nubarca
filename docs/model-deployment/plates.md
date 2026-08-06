# Plates model deployment

Deployment guidance for the owner-private **Plates (Targhe)** model pipelines:
ALPR (license-plate detection + OCR) and privacy-only face redaction. This is
documentation only — **no model weights are committed to NubArca**, and the
production default is **disabled**.

> Privacy boundaries (unchanged): Plates data is owner-private. Nothing here is
> exposed through Files, Gallery, People, Party, TV, or public shares. Face
> redaction is **not identity**: it never creates `FaceDetection`/`FaceEmbedding`/
> `FaceCluster`/`Person`/`PersonFaceAssignment` rows and never computes
> embeddings.

## Provider model

Each pipeline chooses a runner via a `Provider` config value. When `Provider` is
empty it falls back to the legacy `Enabled` bool (`Enabled=true` →
`DeterministicDev`), so existing dev/test config keeps working.

### ALPR — `Plates__Alpr__Provider`

| Provider          | Meaning |
|-------------------|---------|
| `Disabled`        | No ALPR. An analysis request records a safe `model_not_configured` outcome. |
| `DeterministicDev`| Deterministic, **non-semantic** dev/test pipeline. Never for production. |
| `Onnx`            | In-process ONNX detector + OCR. Requires model files (below). |

### Face redaction — `Plates__FaceRedaction__Provider`

| Provider                        | Meaning |
|---------------------------------|---------|
| `Disabled`                      | No redaction. `blurFaces=true` returns `face_redaction_not_configured` (409). Never serves the unredacted image. |
| `DeterministicDev`              | Deterministic, **non-semantic** dev/test detector (a fixed box). |
| `ExistingNubArcaFaceDetector` | **Reuses** the AI substrate's face-box detector (ONNX SCRFD) for **bounding boxes only**. No embeddings, clusters, people, or `FaceDetection` rows. |
| `OnnxDedicatedFaceDetector`     | Future/optional dedicated ONNX face detector. **Not implemented** in this build — selecting it reports unavailable. |

`Plates__FaceRedaction__Enabled` is the master switch; it must be `true` in
addition to selecting a provider.

## ALPR ONNX model contracts

No specific weight file is a product requirement. Any model that conforms to the
contracts below works; a non-conforming output surfaces a safe
`plate_detector_output_unsupported` / `plate_ocr_output_unsupported` error
(never a raw tensor dump).

### Detector (`Plates__Alpr__DetectorModelKind=Yolo`)

- One output of `F = 4 + numClasses` channels per candidate, layout `[1, F, N]`
  (channels-first, e.g. YOLOv8) or `[1, N, F]`. `numClasses = 1` (plate).
- Per candidate: `[cx, cy, w, h, class_scores...]` in **detector-input pixel
  space** (center form, no separate objectness channel).
- Input: RGB, letterboxed to `DetectorInputWidth x DetectorInputHeight`,
  normalized `/255`, NCHW.
- Confidence = max class score; NMS applied with `DetectorNmsThreshold`.

### OCR (`Plates__Alpr__OcrModelKind=FastPlateOcr`)

- One output of shape `[1, T, C]` or `[T, C]`, `C = OcrAlphabet.Length + 1`,
  **CTC blank at index 0** (symbol `k` at index `k+1`). Logits or probabilities
  (a softmax is applied for confidence).
- Input: the plate crop resized to `OcrInputWidth x OcrInputHeight`, RGB `/255`,
  NCHW.
- Greedy CTC decode (collapse repeats, drop blanks). Text is normalized by the
  existing conservative normalizer (no country-specific substitution).

### Directory layout (suggested)

```
models/
  plates/
    alpr/
      detector.onnx        # NOT committed
      ocr.onnx             # NOT committed
      manifest.json        # deployment documentation (see below)
```

```json
{
  "profileKey": "plate-alpr-v1",
  "provider": "Onnx",
  "models": {
    "plateDetector": { "kind": "Yolo", "path": "detector.onnx", "inputWidth": 640, "inputHeight": 640 },
    "plateOcr": { "kind": "FastPlateOcr", "path": "ocr.onnx", "inputWidth": 160, "inputHeight": 40, "alphabet": "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ" }
  },
  "notes": "No model weights are committed to NubArca."
}
```

The manifest is **deployment documentation**; the runtime is driven by the
`Plates__Alpr__*` config keys (point `DetectorModelPath` / `OcrModelPath` at the
files). Configure ONNX Runtime CPU threading via the existing app knobs; the
Plates pipeline gates concurrency with `WorkerConcurrency`.

## Face redaction — existing-detector option

`Provider=ExistingNubArcaFaceDetector` needs **no Plates-owned model file**: it
resolves the AI substrate's face detector by profile key and calls its
detection-only path (`IFaceDetector.DetectFacesAsync`), which returns normalized
boxes and **persists nothing**. Configure the underlying face model via the AI
substrate (`Ai__Onnx__ModelDir`, face profile) and validate it with
`ai diagnostics` / `ai face models`. Leave
`Plates__FaceRedaction__ExistingDetectorProfileKey` empty to use the default face
profile, or pin a specific one.

The dedicated-ONNX option (`OnnxDedicatedFaceDetector`) is reserved for a future
slice; `DetectorModelPath` / `NmsThreshold` exist for it but it is not wired.

## Diagnostics

```bash
# Sanitized config validation (no DB, no inference, no path leakage):
dotnet NubArca.Api.dll plates models validate
dotnet NubArca.Api.dll plates models validate alpr
dotnet NubArca.Api.dll plates models validate face-redaction

# Benchmark on a LOCAL image (no PlateImage/FileItem/DB record created):
dotnet NubArca.Api.dll plates benchmark alpr --image /path/to/car.jpg --runs 5
dotnet NubArca.Api.dll plates benchmark face-redaction --image /path/to/car.jpg --runs 5
```

Diagnostics print only sanitized facts: provider, profile key, model **kind**,
input size, model **basename**, presence booleans, timings, and counts. They
never print absolute paths, storage keys, blob ids, hashes, tensors, stack
traces, or secrets.

## Safe deployment steps

1. Install model files out of the repo; set `Plates__Alpr__DetectorModelPath` /
   `OcrModelPath` (ALPR) and/or configure the AI face model (existing-detector
   redaction).
2. Run `plates models validate` and confirm `status: ready`.
3. Only then set `Plates__Alpr__Provider=Onnx` (ALPR) and/or
   `Plates__FaceRedaction__Enabled=true` +
   `Plates__FaceRedaction__Provider=ExistingNubArcaFaceDetector`.
4. Ensure `Plates__Pepper` is a stable production secret (never rotated once rows exist;
   a changed pepper re-keys owner container keys).
5. Migrations are additive and do not auto-apply — back up and apply as usual.
   **This slice adds no new migration.**

> **Do not enable `Plates__Alpr__Provider=Onnx` in production until real model
> files exist and `plates models validate` passes.**

## Model provenance / license

Any ALPR model selected for deployment must be reviewed before use and recorded
with: a clear license, source URL, version/hash, the expected input/output
contract, and deployment approval. **Do not add unreviewed third-party weights to
the repository.** The reused NubArca face detector keeps its existing
provenance/license documentation in [ai-substrate.md](ai-substrate.md) — refer to
it rather than duplicating.
