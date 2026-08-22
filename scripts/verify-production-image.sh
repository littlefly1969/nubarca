#!/usr/bin/env bash
# Verify a built production API image WITHOUT deploying it.
#
# The point is provenance and completeness: that the image really came from the
# source SHA it claims, and that the pieces the runtime needs are inside it.
# It deliberately does NOT test the GPU. /dev/dri belongs to an installation,
# never to a build host or a CI runner, so a GPU probe here would either be a
# lie or a permanent failure. What CAN be checked is that the OpenVINO variant
# carries the native layer and the Intel userspace that a GPU device would need.
#
#   scripts/verify-production-image.sh <image-ref> <expected-git-sha> [variant]
#
# variant: "runtime" (default) or "openvino". Everything common is checked for
# both; the OpenVINO extras are only demanded of the OpenVINO image, because the
# lean runtime is CORRECT not to carry them.
set -euo pipefail

image="${1:?usage: verify-production-image.sh <image-ref> <expected-git-sha> [runtime|openvino]}"
expected_sha="${2:?expected git sha is required}"
variant="${3:-runtime}"

case "$variant" in
  runtime|openvino) ;;
  *) echo "unknown variant: $variant (expected 'runtime' or 'openvino')" >&2; exit 2 ;;
esac

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
lock="$here/openvino-direct/onnxruntime-openvino.lock"
# The expected ABI comes from the same lock the image was BUILT from, so this
# never drifts into asserting a version nobody ships any more.
# shellcheck source=/dev/null
[ -f "$lock" ] && . "$lock"
ORT_ABI_VERSION="${ORT_ABI_VERSION:-}"

failures=0
pass() { printf '  ok    %s\n' "$1"; }
fail() { printf '  FAIL  %s\n' "$1"; failures=$((failures + 1)); }

# One `docker run` per check would pay container startup many times over; this
# runs the whole probe in a single shell inside the image.
probe() {
  docker run --rm --entrypoint /bin/sh "$image" -c "$1" 2>/dev/null
}

echo "Verifying $image"
echo "  variant       : $variant"
echo "  expected SHA  : $expected_sha"
echo

# --- provenance -------------------------------------------------------------
got_sha="$(docker run --rm --entrypoint /bin/sh "$image" -c 'printf %s "${NUBARCA_GIT_SHA:-}"' 2>/dev/null || true)"
if [ -z "$got_sha" ]; then
  fail "NUBARCA_GIT_SHA is empty — the image cannot say what built it"
elif [ "$got_sha" = "$expected_sha" ]; then
  pass "NUBARCA_GIT_SHA == $expected_sha"
else
  fail "NUBARCA_GIT_SHA is $got_sha, expected $expected_sha"
fi

# --- the image actually starts ---------------------------------------------
# `dotnet --info` exercises the runtime the entrypoint depends on. A broken or
# absent .NET runtime fails here rather than at first boot in production.
if probe 'command -v dotnet >/dev/null && dotnet --list-runtimes' | grep -q 'Microsoft.AspNetCore.App'; then
  pass "ASP.NET Core runtime present and dotnet is startable"
else
  fail "no startable ASP.NET Core runtime"
fi

if probe 'test -f /app/NubArca.Api.dll && echo yes' | grep -q yes; then
  pass "/app/NubArca.Api.dll is present"
else
  fail "/app/NubArca.Api.dll is missing"
fi

# --- external media providers ----------------------------------------------
for tool in ffmpeg ffprobe; do
  if probe "command -v $tool" | grep -q "/$tool$"; then
    pass "$tool present"
  else
    fail "$tool missing"
  fi
done

# --- ONNX Runtime, which differs per variant BY DESIGN ----------------------
if [ "$variant" = "openvino" ]; then
  # The native layer is staged by fetch-native-libs.sh into a fixed directory
  # and pointed at by LD_LIBRARY_PATH. It ships under its SONAME: a bare
  # libonnxruntime.so does not exist, and demanding one fails a good image.
  if [ -n "$ORT_ABI_VERSION" ]; then
    want="libonnxruntime.so.${ORT_ABI_VERSION}"
  else
    want="libonnxruntime.so.*"
  fi
  if probe "ls /opt/nubarca/ort-openvino/$want" | grep -q libonnxruntime; then
    pass "OpenVINO ONNX Runtime present ($want)"
  else
    fail "missing /opt/nubarca/ort-openvino/$want"
  fi

  for lib in libonnxruntime_providers_shared.so libonnxruntime_providers_openvino.so; do
    if probe "test -f /opt/nubarca/ort-openvino/$lib && echo yes" | grep -q yes; then
      pass "$lib present"
    else
      fail "$lib missing"
    fi
  done

  # The OpenVINO core plus the two device plugins. The GPU plugin is what a
  # /dev/dri mount would actually drive; its ABSENCE here would mean the GPU
  # deployment can never work, whatever the installation maps in.
  for lib in 'libopenvino.so*' 'libopenvino_intel_cpu_plugin.so' 'libopenvino_intel_gpu_plugin.so'; do
    if probe "ls /opt/nubarca/ort-openvino/$lib" | grep -q libopenvino; then
      pass "${lib} present"
    else
      fail "${lib} missing"
    fi
  done

  if probe 'printf %s "${Ai__Onnx__OpenVino__NativeDir:-}"' | grep -q '/opt/nubarca/ort-openvino'; then
    pass "Ai__Onnx__OpenVino__NativeDir points at the staged libraries"
  else
    fail "Ai__Onnx__OpenVino__NativeDir is not set to /opt/nubarca/ort-openvino"
  fi

  # Intel GPU userspace. Present in the image; the DEVICE is the installation's.
  if probe 'ls /usr/lib/x86_64-linux-gnu/libOpenCL.so*' | grep -q libOpenCL; then
    pass "OpenCL ICD loader present"
  else
    fail "OpenCL ICD loader (ocl-icd-libopencl1) missing"
  fi
  if probe 'ls /etc/OpenCL/vendors/*.icd' | grep -q '\.icd'; then
    pass "Intel OpenCL ICD registered"
  else
    fail "no OpenCL ICD registered (intel-opencl-icd missing)"
  fi
else
  # The lean runtime carries the CPU execution provider from the NuGet package,
  # published beside the application rather than staged under /opt.
  if probe 'find /app -name "libonnxruntime.so*" -print -quit' | grep -q libonnxruntime; then
    pass "CPU ONNX Runtime present under /app"
  else
    fail "no ONNX Runtime native library under /app"
  fi
  # And it must NOT pretend to be the GPU image.
  if probe 'test -d /opt/nubarca/ort-openvino && echo yes' | grep -q yes; then
    fail "lean runtime unexpectedly carries the OpenVINO native directory"
  else
    pass "lean runtime correctly carries no OpenVINO layer"
  fi
fi

echo
if [ "$failures" -eq 0 ]; then
  echo "IMAGE VERIFIED"
  echo "Image: $image"
  echo "Variant: $variant"
  echo "NUBARCA_GIT_SHA: $got_sha"
  exit 0
fi
echo "IMAGE VERIFICATION FAILED ($failures check(s))"
exit 1
