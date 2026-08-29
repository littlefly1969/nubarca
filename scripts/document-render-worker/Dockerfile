# NubArca isolated Office document render worker.
#
# LibreOffice headless, a Unix socket, and nothing else. This container exists
# so that laying out a person's DOCX/XLSX/PPTX — which means running a large
# body of native code over hostile input — happens somewhere that holds no
# database credentials, no object-storage credentials, no Assistant
# configuration and no owner identity, and that has no network route at all.
#
# It contains no NubArca application code, no .NET runtime, and no client for
# anything. Its entire vocabulary is "are you ready" and "here are bytes of
# format N, give me a PDF" — see server.py.
FROM debian:trixie-slim

# Writer, Calc and Impress only. `libreoffice` would pull Base, Draw, Math and a
# Java runtime; the three filters this worker exposes need none of them, and
# every megabyte of unused native code is attack surface for a format nobody
# asked it to open.
#
# fonts-dejavu-core / fonts-liberation2: layout without fonts is layout of
# nothing. Pinned to a small, stable, open set so the same document lays out the
# same way on every installation — which is what makes
# `libreoffice-office-pdf-v1` a meaningful render identity rather than a label.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        libreoffice-writer-nogui \
        libreoffice-calc-nogui \
        libreoffice-impress-nogui \
        fonts-dejavu-core \
        fonts-liberation2 \
        python3-minimal \
    && rm -rf /var/lib/apt/lists/*

# Non-root, with a fixed uid/gid so the shared socket directory's group can be
# granted to the API container without either side running as root.
RUN groupadd --gid 10002 nubarca-render \
    && useradd --uid 10002 --gid 10002 --no-create-home --shell /usr/sbin/nologin nubarca-render \
    && mkdir -p /run/nubarca-render /var/tmp/nubarca-render \
    && chown -R nubarca-render:nubarca-render /run/nubarca-render /var/tmp/nubarca-render \
    && chmod 0770 /run/nubarca-render \
    && chmod 0700 /var/tmp/nubarca-render

COPY server.py /app/server.py

ENV NUBARCA_RENDER_SOCKET=/run/nubarca-render/render.sock \
    NUBARCA_RENDER_WORK_DIR=/var/tmp/nubarca-render \
    NUBARCA_SOFFICE_BIN=/usr/bin/soffice \
    NUBARCA_RENDER_MAX_CONCURRENCY=1 \
    PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1

USER nubarca-render

# NO EXPOSE, NO PORT, NO HEALTHCHECK OVER HTTP. The worker speaks one Unix
# socket on a shared volume; readiness is an operation on that socket, which the
# API asks before it sends a document.
ENTRYPOINT ["python3", "/app/server.py"]
