# Subject cutout ("copy subject") model deployment

The photo viewer's ✂ button lifts the subject of an owner's photo off its
background and hands back a transparent PNG to copy, download or save as a new
file next to the photo. This page is the operator side: which model, where it
goes, how to switch it on, and what it costs.

No weights are committed to NubArca, and the feature is **off by default**.

## The model

| | |
|---|---|
| Model | BiRefNet-lite (Swin-Tiny backbone), general-purpose salient-object segmentation |
| Licence | MIT (code and weights) — fine inside a private NubArca, no separate public service needed |
| File | `BiRefNet-general-bb_swin_v1_tiny-epoch_232.onnx` from the BiRefNet GitHub release `v1` |
| Size | 224,005,088 bytes |
| SHA-256 | `5600024376f572a557870a5eb0afb1e5961636bef4e1e22132025467d0f03333` |
| Contract | input `input_image` float `[1,3,1024,1024]`, RGB stretched to the square, ImageNet mean/std; output `output_image` float `[1,1,1024,1024]` logits (sigmoid → alpha) |

Why this one (October 2026, measured on the same photo, OpenVINO 2026.4 CPU,
4 vCPU):

| Model | Licence | Result | Warm time | Peak RAM |
|---|---|---|---|---|
| U²-Net | Apache-2.0 | lost the second person | 0.07 s | 0.5 GB |
| IS-Net general | Apache-2.0 | lost the second person | 0.3 s | 1.0 GB |
| **BiRefNet-lite** | **MIT** | both people whole, clean curly hair | **3.7 s** | **3.3 GB** |
| BiRefNet (Swin-L) | MIT | indistinguishable from lite | 4.8 s | 5.7 GB |

Rejected on licence: BRIA RMBG 1.4/2.0 (non-commercial), Ultralytics YOLO-seg
(AGPL-3.0).

The full BiRefNet export has the same contract: switching is a file change
(`Ai__SubjectCutout__ModelFile`), not a code change.

**Use the OpenVINO provider.** Under plain ONNX Runtime CPU the same model took
~16 s and 7–12 GB of RAM per cutout on the same machine. Production already
runs `openvino-direct` (`docker-compose.facedirect-api.yml`).

## Install

On the host, inside the directory mounted as `/models/ai` in the api
container (`Ai__Onnx__ModelDir`):

```bash
mkdir -p birefnet-lite
curl -fL -o birefnet-lite/model.onnx \
  https://github.com/ZhengPeng7/BiRefNet/releases/download/v1/BiRefNet-general-bb_swin_v1_tiny-epoch_232.onnx
echo "5600024376f572a557870a5eb0afb1e5961636bef4e1e22132025467d0f03333  birefnet-lite/model.onnx" | sha256sum -c
```

Then in the production `.env`:

```bash
Ai__SubjectCutout__Enabled=true
# Optional, defaults shown:
# Ai__SubjectCutout__ModelFile=birefnet-lite/model.onnx
# NUBARCA_SUBJECT_CUTOUT_DEVICE=CPU
```

`Ai__Enabled=true` is required as well (the AI master switch). Recreate the api
through the normal deploy path (`deploy/FAST_DEPLOY.md`). Only the api runs the
model; the worker does not need it.

## Check

- Signed in, `GET /api/subject-cutout` answers `{"available":true}`.
- The ✂ button appears in the photo viewer. The first cutout after a restart
  also compiles the model (~8 s extra); later ones take the warm time above.
- The api log line `Subject cutout {w}x{h} in {ms} ms` gives the real cost on
  this host.

Unavailable reasons are logged as sanitized tokens: `subject-cutout-disabled`,
`onnx-modeldir-not-configured`, `onnx-model-not-found`, and the session
factory's own `onnx-*` reasons.

## Behaviour and limits

- On request only: no job, no backfill, nothing stored. The source is the
  photo's medium preview (upright, any original format), never the original.
- One cutout at a time per api process. A second request waits up to
  `Ai__SubjectCutout__QueueTimeoutSeconds` (20) and then gets "busy" (503).
- "Save to NubArca" stores an ordinary PNG `<name>-cutout.png` in the photo's
  folder through the normal upload path (quota, dedup, audit). The original is
  never touched.
- Owner-only. No public share, shared album or party route reaches it.
- Opt-in end-to-end test: `NUBARCA_SUBJECT_CUTOUT_MODEL=/path/model.onnx
  dotnet test --filter SubjectCutoutModelTests`.
