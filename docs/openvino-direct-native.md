# OpenVINO-direct native runtime (Gate 3A)

This documents the **reproducible native packaging** for running OpenVINO inference
**in-process** inside the .NET API/worker on Linux — replacing the Python
`openvino-query` / `openvino-worker` HTTP sidecars. Gate 3A only produces and
verifies the native stack; it does **not** change product code, the sidecars, or
the production images. Those follow in later gates.

## What is pinned

Single source of truth: [`onnxruntime-openvino.lock`](../scripts/openvino-direct/onnxruntime-openvino.lock).

| Item | Value |
|---|---|
| ONNX Runtime (managed + native) | **1.24.1** (`Microsoft.ML.OnnxRuntime` NuGet == native ABI) |
| Bundled OpenVINO | **2025.4.1** (soname `.2541`) |
| Native source | official Intel `onnxruntime-openvino` **1.24.1** PyPI wheel (`manylinux_2_28_x86_64`, cp312) |
| Wheel SHA-256 (full) | `d617fac2f59a6ab5ea59a788c3e1592240a129642519aaeaa774761dfe35150e` (matches PyPI's published digest) |
| Base image | `mcr.microsoft.com/dotnet/{sdk,runtime}:10.0` (Debian 12, glibc 2.36 ≥ 2.28) |

### Why not a NuGet OpenVINO EP?

- `Microsoft.ML.OnnxRuntime.OpenVino` **does not exist**.
- `Intel.ML.OnnxRuntime.OpenVino` (1.20.0–1.24.1) ships **win-x64 only** — no `linux-x64`.
- ORT's default GitHub Linux releases have **no** OpenVINO variant.

Intel *does* build the EP for Linux, but only ships it inside the **Python wheel**.
That wheel is self-contained (a standalone `libonnxruntime.so.1.24.1` + the OpenVINO
runtime + plugins), so we repackage its native libraries and drive them from the
standard `Microsoft.ML.OnnxRuntime` managed API — **no third-party wrapper, no
manual inference P/Invoke**.

## Required libraries (and only these)

Extracted from `onnxruntime/capi/` in the wheel — see the `required_globs` in
[`fetch-native-libs.sh`](../scripts/openvino-direct/fetch-native-libs.sh):

- `libonnxruntime.so.1.24.1` — OpenVINO-enabled ORT core
- `libonnxruntime_providers_{shared,openvino}.so`
- `libopenvino.so*`, `libopenvino_onnx_frontend.so*` (direct `ldd` dep of the EP)
- `libopenvino_intel_cpu_plugin.so`, `libopenvino_intel_gpu_plugin.so` (dlopen'd per device)
- `libtbb.so*`, `libtbbmalloc.so*`

Unrelated frontends (paddle/pytorch/tensorflow), AUTO/HETERO/MULTI, NPU and the C
API are deliberately excluded. The build-time verifier exercises the EP, so an
over-trimmed set fails the build rather than shipping broken.

## The `onnxruntime.dll`-on-Linux problem, and the supported fix

**Symptom.** With a `RuntimeIdentifier=linux-x64` publish of `Microsoft.ML.OnnxRuntime`
1.24.1, loading ORT fails while probing `onnxruntime.dll`, `libonnxruntime.dll`,
`onnxruntime.dll.so`, `libonnxruntime.dll.so` — never the conventional
`libonnxruntime.so`.

**Cause (verified).**
1. The managed P/Invoke literal is **`onnxruntime.dll`** (the `.dll` suffix is used
   on all platforms), so .NET's default search mangles *that* name — it never tries
   `libonnxruntime.so`.
2. The package's `build/native/Microsoft.ML.OnnxRuntime.props` copies the **Windows**
   `runtimes/win-x64/native/onnxruntime.dll` (a PE) into the output dir, guarded only
   by `Exists()`. On Linux the loader then finds a Windows PE at `onnxruntime.dll`
   and fails with *invalid ELF header*.

**Rejected fixes.**
- *Overwrite `onnxruntime.dll` with the ELF core* — works, but silently replaces a
  Windows binary with a Linux one; brittle and surprising.
- *Replace `libonnxruntime.so` contents in place* — **does not load**; the import is
  wired to the `onnxruntime.dll` name, not `libonnxruntime.so` (verified).

**Adopted fix — supported `NativeLibrary` resolver.** Register, before the first
native call, a `NativeLibrary.SetDllImportResolver` on the `Microsoft.ML.OnnxRuntime`
assembly that maps the `onnxruntime` import to our real
`…/libonnxruntime.so.1.24.1`. No PE is overwritten; the OV natives live in their own
directory (on `LD_LIBRARY_PATH` so the core can dlopen the providers + OpenVINO libs).
Reference implementation: [`verify/Program.cs`](../scripts/openvino-direct/verify/Program.cs).
This resolver moves into the API/worker bootstrap in Gate 3B.

## Fail-closed verification

[`verify/`](../scripts/openvino-direct/verify/) is a tiny .NET tool that:

1. asserts the OV-enabled ORT core file exists (exit 10 otherwise);
2. registers the resolver and asserts `OpenVINOExecutionProvider` is present
   (exit 11 — i.e. this really is an OpenVINO-enabled build, not stock CPU ORT);
3. runs a deterministic model on the OpenVINO EP and checks the numeric output
   (exit 12/13 otherwise).

The Docker build runs it on **OpenVINO-CPU** (docker build has no GPU) and fails the
build on any non-zero exit. GPU is verified at run time against a real device.

## Build & verify

```bash
# Reproducible native layer + build-time CPU fail-closed verification:
docker build -t nubarca-openvino-native:local scripts/openvino-direct

# Real-GPU verification (map only /dev/dri into GPU-using containers):
docker run --rm --device /dev/dri \
  --group-add "$(getent group render | cut -d: -f3)" \
  nubarca-openvino-native:local GPU
# expected: "[verify] OK — OpenVINO EP present and OpenVINO-GPU inference verified."

# Local (non-Docker) fetch of just the natives:
scripts/openvino-direct/fetch-native-libs.sh /tmp/ort-openvino
```

The native libraries are **fetched + verified**, never committed (see `.gitignore`).

## Licensing / redistribution

The repackaged native libraries are redistributed under their upstream licenses;
`fetch-native-libs.sh` also copies the wheel's `LICENSE`/`COPYING` files next to them.

| Component | License |
|---|---|
| ONNX Runtime | MIT |
| OpenVINO runtime + plugins | Apache-2.0 |
| oneTBB | Apache-2.0 |

`generate-sbom.sh` emits a CycloneDX `sbom.cdx.json` (components + per-file SHA-256)
into the native dir. When building with buildx, add `--sbom=true` for an image-level
SBOM attestation.

## GPU device permissions

The OpenVINO GPU plugin enumerates `/dev/dri/renderD*` via the Intel NEO OpenCL
runtime (`intel-opencl-icd`). Containers that use the GPU need the device mapped and
the host's **render** group id added (`--group-add "$(getent group render | cut -d: -f3)"`);
the numeric id differs per host, so resolve it at runtime — never hard-code it.
