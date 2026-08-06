# syntax=docker/dockerfile:1
# Gate 3A — reproducible OpenVINO-direct native layer, self-verified fail-closed.
#
# Build:  docker build -t nubarca-openvino-native:local scripts/openvino-direct
# GPU verify (real device):
#   docker run --rm --device /dev/dri --group-add "$(getent group render | cut -d: -f3)" \
#     nubarca-openvino-native:local GPU
#
# The build performs OpenVINO-CPU verification (docker build has no GPU access);
# the default container entrypoint performs OpenVINO-GPU-FP32 verification.

# ---- stage: fetch + verify + build ------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS native
RUN apt-get update && apt-get install -y --no-install-recommends \
      curl unzip ca-certificates python3 python3-pip python3-numpy \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /build
COPY onnxruntime-openvino.lock fetch-native-libs.sh generate-sbom.sh ./
COPY verify/ verify/
RUN chmod +x fetch-native-libs.sh generate-sbom.sh \
    && ./fetch-native-libs.sh /opt/nubarca/ort-openvino \
    && ./generate-sbom.sh /opt/nubarca/ort-openvino /opt/nubarca/ort-openvino/sbom.cdx.json
RUN pip3 install --break-system-packages --no-cache-dir onnx \
    && python3 verify/make-tiny-model.py /opt/nubarca/model.onnx \
    && dotnet publish verify/Verify.csproj -c Release -o /opt/nubarca/ort-verify
# Fail-closed build-time verification on OpenVINO CPU (no /dev/dri in docker build).
RUN NUBARCA_ORT_NATIVE_DIR=/opt/nubarca/ort-openvino \
    LD_LIBRARY_PATH=/opt/nubarca/ort-openvino \
    dotnet /opt/nubarca/ort-verify/ov-native-verify.dll /opt/nubarca/model.onnx CPU

# ---- stage: runtime image carrying the verified native stack ----------------------
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends \
      intel-opencl-icd ocl-icd-libopencl1 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=native /opt/nubarca/ort-openvino /opt/nubarca/ort-openvino
COPY --from=native /opt/nubarca/ort-verify  /opt/nubarca/ort-verify
COPY --from=native /opt/nubarca/model.onnx  /opt/nubarca/model.onnx
ENV LD_LIBRARY_PATH=/opt/nubarca/ort-openvino \
    NUBARCA_ORT_NATIVE_DIR=/opt/nubarca/ort-openvino \
    NUBARCA_ORT_ABI=1.24.1
ENTRYPOINT ["dotnet", "/opt/nubarca/ort-verify/ov-native-verify.dll", "/opt/nubarca/model.onnx"]
CMD ["GPU"]
