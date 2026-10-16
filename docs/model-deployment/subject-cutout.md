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

- Signed in, `GET /api/subject-cutout` answers `{"available":true,…}`.
- The ✂ button appears in the photo viewer. The first cutout after a restart
  also compiles the model (~8 s extra); later ones take the warm time above.
- The api log line `Subject cutout {w}x{h} (point: …) in {ms} ms` gives the
  real cost on this host.

Unavailable reasons are logged as sanitized tokens: `subject-cutout-disabled`,
`onnx-modeldir-not-configured`, `onnx-model-not-found`, and the session
factory's own `onnx-*` reasons.

## Tap a subject (optional)

With point selection on, the owner can tap one subject in the cutout preview
to keep only that one (one of two people, say). A promptable segmenter decides
**which** subject was touched; BiRefNet still draws every edge, so hair stays
as clean as in the whole-subject cutout.

| | |
|---|---|
| Model | SAM 2.1 tiny (Hiera-T), image mode |
| Licence | Apache-2.0 (code and weights) |
| Source | the official Meta checkpoint `sam2.1_hiera_tiny.pt`, exported to two ONNX files with `scripts/export-sam2-point-onnx.py` |
| Files | `sam2.1-tiny/encoder.onnx` (~109 MB) and `sam2.1-tiny/decoder.onnx` (~17 MB) under `Ai__Onnx__ModelDir` |

How a tap is resolved: the point model proposes three masks around the touch
(for example the shirt, the person, a larger region). The service keeps the
largest one that lies at least 90% inside BiRefNet's foreground, limits it to
the foreground region under the touch when that region stands apart, grows it
by 2% of the long edge and uses it to bound BiRefNet's alpha. A touch that
lands on no subject answers "no subject" (422).

Measured in October 2026 on the same photo (OpenVINO 2026.4 CPU, 4 vCPU), with
one tap on each person:

| Model | Licence | Result | Encoder | Per tap | Peak RAM |
|---|---|---|---|---|---|
| SAM ViT-B (quantized) | Apache-2.0 | half a shirt | 9.8 s | 0.1–0.2 s | 2.8 GB |
| MobileSAM | Apache-2.0 | took parts of the other person | 0.3 s | 0.1 s | 0.5 GB |
| **SAM 2.1 tiny** | **Apache-2.0** | **each person whole** | **0.75 s** | **0.03 s** | **0.8 GB** |
| SAM 2.1 small | Apache-2.0 | same as tiny | 0.76 s | 0.03 s | 0.9 GB |

A tapped cutout therefore costs the BiRefNet time plus about 1 s. The first
OpenVINO compile of the SAM 2.1 encoder takes about **2 minutes**; it happens
once, because the compiled model is kept in `Ai__Onnx__OpenVino__CacheDir`
(the facedirect override already sets a persistent one). Under plain ONNX
Runtime CPU the encoder takes 3–4 s instead.

### Export the model

On a machine with Python and room for PyTorch (not the production api
container), in an empty working directory:

```bash
python3 -m venv venv
./venv/bin/pip install torch hydra-core iopath onnx
git clone https://github.com/facebookresearch/sam2
git -C sam2 checkout 2b90b9f5ceec907a1c18123530e92e794ad901a4
curl -fLO https://dl.fbaipublicfiles.com/segment_anything_2/092824/sam2.1_hiera_tiny.pt
./venv/bin/python <nubarca checkout>/scripts/export-sam2-point-onnx.py \
  --sam2 sam2 --checkpoint sam2.1_hiera_tiny.pt --output out
```

Before importing anything, the script refuses a `sam2` checkout that is not at
the pinned commit (or has local changes to tracked files) and a checkpoint
whose SHA-256 is not Meta's:

| File | SHA-256 |
|---|---|
| `sam2.1_hiera_tiny.pt` (input) | `3c1e81ca9b037dd39d70a014ddb9a813d6c4c4e12555420db7eaff31689bd4e3` |
| `sam2.1-tiny/encoder.onnx` (validated, 109,384,644 bytes) | `a3905dc097ecbd53813427688f964489699c8a706b7491f72f751db19208da8f` |
| `sam2.1-tiny/decoder.onnx` (validated, 16,557,981 bytes) | `685ead2f9f1468354f5bee2a35bd4e0ed7e687b143bc3ccb87c95caa181ea8f6` |

It then prints both files' SHA-256 and whether they match the validated
export. A different torch version can serialise the same graph differently:
such a file is not refused, but it must pass the opt-in model test below
before it is deployed. Copy `out/sam2.1-tiny/` into the model directory, then
in the production `.env`:

```bash
Ai__SubjectCutout__PointSelection__Enabled=true
# Optional, defaults shown:
# Ai__SubjectCutout__PointSelection__EncoderFile=sam2.1-tiny/encoder.onnx
# Ai__SubjectCutout__PointSelection__DecoderFile=sam2.1-tiny/decoder.onnx
```

Both models run on `NUBARCA_SUBJECT_CUTOUT_DEVICE`. `GET /api/subject-cutout`
answers `"pointSelection":true` once both files load; the cutout preview then
says the owner can tap a subject. Unavailable reasons add
`subject-point-disabled` and `subject-point-output-shape` (a file that is not
this export).

Opt-in end-to-end test: add `NUBARCA_SUBJECT_POINT_ENCODER` and
`NUBARCA_SUBJECT_POINT_DECODER` to the `SubjectCutoutModelTests` run below.

## Behaviour and limits

- On request only: no job, no backfill, nothing stored. The source is the
  photo's medium preview (any original format), never the original. Medium
  previews keep the original's EXIF orientation without turning their pixels,
  so the service turns the preview upright before either model sees it: the
  cutout, its region, a tapped point and a print's mask are all in the
  upright photo's frame, the same as the AutoOriented original a print uses.
- A tap selects the subject under the finger: on the cutout model's solid
  foreground, on its soft fringe (hair, a semi-transparent edge, alpha ≥ 32)
  or within 1.5 % of the long edge of that fringe. A tap anywhere else is
  `no_subject`, even when SAM proposes the nearest person for it.
- `Ai__SubjectCutout__TimeoutSeconds` (240) bounds one inference. The first
  request after a restart also compiles the models for the device, BiRefNet
  and, on a tap, both SAM files; on a modest CPU that has taken past two
  minutes. A warm cutout takes seconds.
- One cutout at a time per api process (a tapped one included). A second request waits up to
  `Ai__SubjectCutout__QueueTimeoutSeconds` (20) and then gets "busy" (503).
- "Save to NubArca" stores an ordinary PNG `<name>-cutout.png` in the photo's
  folder through the normal upload path (quota, dedup, audit). The original is
  never touched.
- Owner-only. No public share, shared album or party route reaches it.
- The owner print's "Esce dalla foto" style (`popOut`) uses the same cutout
  model, on the same gate, once per print: the subject's mask is drawn over
  the original's own pixels and never leaves the server. Without the model
  the style is not offered and a request for it is refused with
  `subject_unavailable`; a photo with nothing to lift is refused with
  `no_subject`. A print through an album share cannot ask for it.
- With point selection on, the owner can tap the one person who breaks out.
  The request carries only that normalized point (`subject: {x, y}`); the
  server lifts the subject again from it, with the same rules as a tapped
  cutout, and never takes a mask from the browser. The point is part of the
  print's idempotency fingerprint and its render specification. A tap on no
  one is refused with `no_subject`.
- Opt-in end-to-end test: `NUBARCA_SUBJECT_CUTOUT_MODEL=/path/model.onnx
  dotnet test --filter SubjectCutoutModelTests`.
