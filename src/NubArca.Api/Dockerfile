FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json ./
COPY src/NubArca.Api/NubArca.Api.csproj src/NubArca.Api/
RUN dotnet restore src/NubArca.Api/NubArca.Api.csproj

COPY src/NubArca.Api/ src/NubArca.Api/
RUN dotnet publish src/NubArca.Api/NubArca.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---- Gate 3A native layer: OpenVINO-enabled ORT core, fetched + verified, pinned.
# Only the runtime-openvino target consumes this; the default runtime stays lean.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS onnx-native
RUN apt-get update && apt-get install -y --no-install-recommends \
      curl unzip ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /native
COPY scripts/openvino-direct/onnxruntime-openvino.lock \
     scripts/openvino-direct/fetch-native-libs.sh \
     scripts/openvino-direct/generate-sbom.sh ./
RUN chmod +x fetch-native-libs.sh generate-sbom.sh \
    && ./fetch-native-libs.sh /opt/nubarca/ort-openvino \
    && ./generate-sbom.sh /opt/nubarca/ort-openvino /opt/nubarca/ort-openvino/sbom.cdx.json

# ---- shared runtime base (default deployment: onnxruntime CPU).
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime-base
WORKDIR /app

# Npgsql probes for the GSSAPI/Kerberos runtime (libgssapi_krb5.so.2) while
# negotiating a PostgreSQL connection; the slim aspnet base omits it, so the
# probe logs a "Dynamic loading not supported / libgssapi_krb5.so.2: cannot open
# shared object file" warning. Install ONLY the Kerberos runtime library (no
# recommends, apt lists removed) to satisfy the probe. This is a runtime-image
# fix only — no application, schema, or behaviour change.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 ffmpeg \
    && rm -rf /var/lib/apt/lists/*
# ffmpeg (above) also provides ffprobe. Both are used by the OPTIONAL external
# media providers: Media__VideoPosterProvider=ffmpeg extracts real video poster
# frames, and Media__VideoMetadataProvider=ffprobe reads container/stream
# metadata (duration, codecs, dimensions, fps, rotation). With the default
# providers ("synthetic"/"none") these binaries are present but never invoked.

COPY --from=build /app/publish ./

# Slice 100: the high-performance libvips derivative backend ships as a
# self-contained native library via the NetVips.Native.linux-x64 NuGet package
# (published to runtimes/linux-x64/native/libvips.so.42 with image codecs
# statically linked). It runs on this glibc-based aspnet image with NO extra
# apt packages. If you switch to an Alpine/musl base, swap the package for
# NetVips.Native.linux-musl-x64. If the native library cannot load for any
# reason, the pipeline automatically falls back to the pure-managed ImageSharp
# backend (set MediaDerivatives__ImageBackend=imagesharp to force it).

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "NubArca.Api.dll"]

# ---- direct-mode image: adds the OpenVINO native runtime + Intel GPU userspace.
# Built ONLY for containers that execute in-process OpenVINO inference
# (Ai__Onnx__ExecutionProvider=openvino-direct). No python, no wheel and no build
# tools are retained; the native licenses + SBOM ship alongside the libraries.
# Map /dev/dri + the render group into this container only when using a GPU device.
FROM runtime-base AS runtime-openvino
ARG GIT_SHA=unknown
# intel-opencl-icd / ocl-icd-libopencl1: Intel GPU userspace for the OpenVINO GPU
# device. curl: the container readiness health check probes /health/ready (a
# runtime diagnostic tool, not a build tool). No python / wheel / compiler is added.
RUN apt-get update && apt-get install -y --no-install-recommends \
      intel-opencl-icd ocl-icd-libopencl1 curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=onnx-native /opt/nubarca/ort-openvino /opt/nubarca/ort-openvino
ENV LD_LIBRARY_PATH=/opt/nubarca/ort-openvino \
    Ai__Onnx__OpenVino__NativeDir=/opt/nubarca/ort-openvino \
    NUBARCA_GIT_SHA=$GIT_SHA
# ENTRYPOINT / EXPOSE inherited from runtime-base (no new port).

# ---- default target: the lean runtime (LAST stage → default `docker build`) ----
FROM runtime-base AS runtime
