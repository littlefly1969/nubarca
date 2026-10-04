# NubArca media tools: FFmpeg and FFprobe, built from pinned upstream sources.
#
# Why a build of our own rather than Ubuntu's package: the distribution's
# ffmpeg (6.1 on noble) cannot read HEIF/HEIC stills — the iPhone's photo
# format — and it is built "with everything", 213 shared libraries and ~240 MB
# (speech synthesis voices, a ham-radio codec, SVG rendering, three AV1 and
# HEVC *encoders*, X11 and audio-server clients) that NubArca never calls. This
# build carries what NubArca does: every decoder and demuxer of FFmpeg itself,
# dav1d for AV1, x264 for HLS, zimg for colour and HDR conversion, zlib — and
# nothing that reaches a network (`--disable-network`): the tools only ever read
# and write local files.
#
# Published as its own image, rebuilt only when a pin below changes, and copied
# into the API image by digest — so an API build never recompiles FFmpeg, and
# "staying current" is changing a version and its checksum here.
#
# Every source is verified by SHA-256 before it is unpacked. FFmpeg's tarball
# was also checked against the FFmpeg release signing key
# (FCF9 86EA 15E6 E293 A564 4F10 B432 2F04 D676 58D8) when it was pinned.

ARG UBUNTU=ubuntu:24.04

FROM ${UBUNTU} AS build
ARG DEBIAN_FRONTEND=noninteractive

ARG FFMPEG_VERSION=9.0.2
ARG FFMPEG_SHA256=8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e
ARG DAV1D_VERSION=1.5.4
ARG DAV1D_SHA256=686616b7c69eb88d44459391ab25cac13b6647a3b288835c5784e71c1514a5c5
ARG ZIMG_VERSION=3.0.6
ARG ZIMG_SHA256=be89390f13a5c9b2388ce0f44a5e89364a20c1c57ce46d382b1fcc3967057577
# x264 has no release tarballs: the head of its `stable` branch, by commit.
ARG X264_COMMIT=b35605ace3ddf7c1a5d67a2eb553f034aef41d55
ARG X264_SHA256=6eeb82934e69fd51e043bd8c5b0d152839638d1ce7aa4eea65a3fedcf83ff224

RUN apt-get update && apt-get install -y --no-install-recommends \
      ca-certificates curl xz-utils bzip2 \
      build-essential nasm pkg-config meson ninja-build autoconf automake libtool \
      zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src
ENV PREFIX=/opt/build
ENV PKG_CONFIG_PATH=/opt/build/lib/pkgconfig

# fetch <url> <file> <sha256>: download, verify, or fail the build.
RUN printf '#!/bin/sh\nset -eu\ncurl -fsSL --retry 3 -o "$2" "$1"\necho "$3  $2" | sha256sum -c -\n' \
      > /usr/local/bin/fetch && chmod +x /usr/local/bin/fetch

RUN fetch "https://code.videolan.org/videolan/x264/-/archive/${X264_COMMIT}/x264-${X264_COMMIT}.tar.bz2" \
        x264.tar.bz2 "${X264_SHA256}" \
    && mkdir x264 && tar -xjf x264.tar.bz2 -C x264 --strip-components=1 \
    && cd x264 && ./configure --prefix="$PREFIX" --enable-static --enable-pic --disable-cli \
    && make -j"$(nproc)" && make install

RUN fetch "https://downloads.videolan.org/pub/videolan/dav1d/${DAV1D_VERSION}/dav1d-${DAV1D_VERSION}.tar.xz" \
        dav1d.tar.xz "${DAV1D_SHA256}" \
    && mkdir dav1d && tar -xJf dav1d.tar.xz -C dav1d --strip-components=1 \
    && cd dav1d && meson setup build --prefix="$PREFIX" --libdir=lib --buildtype=release \
         --default-library=static -Denable_tools=false -Denable_tests=false \
    && ninja -C build install

RUN fetch "https://github.com/sekrit-twc/zimg/archive/refs/tags/release-${ZIMG_VERSION}.tar.gz" \
        zimg.tar.gz "${ZIMG_SHA256}" \
    && mkdir zimg && tar -xzf zimg.tar.gz -C zimg --strip-components=1 \
    && cd zimg && ./autogen.sh && ./configure --prefix="$PREFIX" --enable-static --disable-shared \
    && make -j"$(nproc)" && make install

# FFmpeg itself. --disable-autodetect so nothing the build host happens to have
# sneaks in; every external is named. The binaries link the externals
# statically and need only glibc and libstdc++ (zimg is C++), both present in
# the .NET runtime image.
RUN fetch "https://ffmpeg.org/releases/ffmpeg-${FFMPEG_VERSION}.tar.xz" \
        ffmpeg.tar.xz "${FFMPEG_SHA256}" \
    && mkdir ffmpeg && tar -xJf ffmpeg.tar.xz -C ffmpeg --strip-components=1 \
    && cd ffmpeg && ./configure \
         --prefix=/opt/nubarca/media \
         --pkg-config-flags=--static \
         --extra-cflags="-I$PREFIX/include" \
         --extra-ldflags="-L$PREFIX/lib" \
         --extra-libs="-lpthread -lm -lstdc++" \
         --disable-autodetect \
         --disable-shared --enable-static \
         --disable-ffplay --disable-doc --disable-debug \
         --disable-network \
         --enable-gpl \
         --enable-libx264 --enable-libdav1d --enable-libzimg --enable-zlib \
    && make -j"$(nproc)" && make install \
    && strip /opt/nubarca/media/bin/ffmpeg /opt/nubarca/media/bin/ffprobe \
    && rm -rf /opt/nubarca/media/include /opt/nubarca/media/lib /opt/nubarca/media/share

# What was built, from what, under which licences — shipped beside the binaries.
RUN mkdir -p /opt/nubarca/media/licenses \
    && cp ffmpeg/COPYING.GPLv2 ffmpeg/LICENSE.md /opt/nubarca/media/licenses/ \
    && cp x264/COPYING /opt/nubarca/media/licenses/x264-COPYING \
    && cp dav1d/COPYING /opt/nubarca/media/licenses/dav1d-COPYING \
    && cp zimg/COPYING /opt/nubarca/media/licenses/zimg-COPYING \
    && printf '{"ffmpeg":"%s","dav1d":"%s","zimg":"%s","x264":"%s","base":"%s"}\n' \
         "$FFMPEG_VERSION" "$DAV1D_VERSION" "$ZIMG_VERSION" "$X264_COMMIT" "ubuntu:24.04" \
         > /opt/nubarca/media/versions.json

# The artifact: nothing but the tools, their licences and their manifest.
FROM scratch AS tools
COPY --from=build /opt/nubarca/media /opt/nubarca/media

# A runnable check image for CI: the tools on the very base the API runs on,
# with the verification script and its fixtures.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS verify
COPY --from=build /opt/nubarca/media /opt/nubarca/media
COPY scripts/media-tools/verify-media-tools.sh /usr/local/bin/verify-media-tools
COPY scripts/media-tools/fixtures /opt/nubarca/media-fixtures
ENV PATH=/opt/nubarca/media/bin:$PATH
ENTRYPOINT ["/usr/local/bin/verify-media-tools"]
