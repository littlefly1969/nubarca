#!/usr/bin/env python3
"""NubArca isolated Office document render worker.

WHY THIS IS A SEPARATE CONTAINER AT ALL.

Laying out a DOCX, XLSX or PPTX means running a real office suite over a file a
person uploaded. LibreOffice is a very large body of native code whose input is,
from NubArca's point of view, entirely hostile: it parses dozens of legacy
formats, follows document-declared relationships, and has a macro engine. The
API process holds database credentials, object-storage credentials, the
Assistant's model configuration and every owner's identity. Those two things
must not share an address space, a filesystem or a network namespace.

So the API sends BYTES and a FORMAT ENUM over a Unix socket, and gets a PDF
back. That is the entire vocabulary. There is no operation that names a path, a
filename, a command, an import filter, a binary or a URL — not because those
would be validated carefully, but because a protocol that cannot express them
has nothing to validate.

THE NETWORK SANDBOX IS THE EGRESS GUARANTEE. This container runs with
`network_mode: none`; the LibreOffice settings below are defence in depth, not
the boundary. A document that asks to fetch an external relationship gets no
route to try.

Nothing here logs a filename, an owner, a document's content or a temporary
path. The worker does not know any of them: it is never told.
"""

from __future__ import annotations

import errno
import os
import shutil
import signal
import socket
import struct
import subprocess
import sys
import threading
import time
import uuid

MAGIC = b"NBDR"
VERSION = 1

OP_READINESS = 1
OP_RENDER = 2

STATUS_OK = 0
STATUS_REJECTED = 1      # a verdict about the document; the same bytes fail again
STATUS_UNAVAILABLE = 2   # a statement about this container, right now

REASON_NONE = 0
REASON_UNSUPPORTED_FORMAT = 1
REASON_INVALID_SOURCE = 2
REASON_OUTPUT_TOO_LARGE = 3
REASON_TIMEOUT = 4
REASON_PROCESS_FAILED = 5
REASON_RENDERER_UNAVAILABLE = 6

REQUEST_HEADER = struct.Struct(">4sBBBBIII")
RESPONSE_HEADER = struct.Struct(">4sBBxHI")

# The closed format vocabulary. The value on the wire is an ordinal, so the API
# cannot name an import filter and this worker cannot be persuaded to pick one.
FORMATS = {
    1: ("docx", "writer_pdf_Export"),
    2: ("xlsx", "calc_pdf_Export"),
    3: ("pptx", "impress_pdf_Export"),
}

SOCKET_PATH = os.environ.get("NUBARCA_RENDER_SOCKET", "/run/nubarca-render/render.sock")
WORK_ROOT = os.environ.get("NUBARCA_RENDER_WORK_DIR", "/var/tmp/nubarca-render")
SOFFICE = os.environ.get("NUBARCA_SOFFICE_BIN", "/usr/bin/soffice")

# Hard ceilings the worker enforces regardless of what the caller asks for. A
# bound a client can raise without end is not a bound.
MAX_SOURCE_BYTES = int(os.environ.get("NUBARCA_RENDER_MAX_SOURCE_BYTES", 256 * 1024 * 1024))
MAX_OUTPUT_BYTES = int(os.environ.get("NUBARCA_RENDER_MAX_OUTPUT_BYTES", 512 * 1024 * 1024))
MAX_TIMEOUT_SECONDS = int(os.environ.get("NUBARCA_RENDER_MAX_TIMEOUT_SECONDS", 900))
MAX_CONCURRENCY = int(os.environ.get("NUBARCA_RENDER_MAX_CONCURRENCY", 1))

_slots = threading.Semaphore(MAX_CONCURRENCY)


def log(message: str) -> None:
    """Aggregates only. Never a filename, never an owner, never a job path."""
    sys.stderr.write(f"document-render-worker: {message}\n")
    sys.stderr.flush()


# ---------------------------------------------------------------------------
# LibreOffice invocation
# ---------------------------------------------------------------------------

def libreoffice_profile(job_dir: str) -> str:
    """A FRESH, PRIVATE PROFILE PER JOB, inside the job's own directory.

    A shared profile is state that survives one document and is read by the
    next, which is both a correctness problem (a crashed run leaves recovery
    prompts that hang the following one) and a boundary problem (one owner's
    document influencing how another's is laid out). Creating it under the job
    directory means the recursive cleanup below removes it too.
    """
    return "file://" + os.path.join(job_dir, "profile")


def convert(
    job_dir: str, source: str, export_filter: str, timeout: int, max_output: int
) -> tuple[int, int, bytes]:
    out_dir = os.path.join(job_dir, "out")
    os.makedirs(out_dir, mode=0o700, exist_ok=True)

    argv = [
        SOFFICE,
        "--headless",
        "--invisible",
        "--nodefault",
        "--nolockcheck",
        "--nologo",
        "--norestore",
        "--nofirststartwizard",
        # MACROS ARE NEVER EXECUTED. The API already refuses macro-enabled
        # packages upstream; this is the second, independent statement, made
        # where the engine that would run them lives.
        "-env:MacroSecurityLevel=3",
        f"-env:UserInstallation={libreoffice_profile(job_dir)}",
        "--convert-to", f"pdf:{export_filter}",
        "--outdir", out_dir,
        source,
    ]

    try:
        completed = subprocess.run(
            argv,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=timeout,
            # A NEW PROCESS GROUP, so a hung conversion can be killed WHOLE.
            # LibreOffice forks; killing the parent alone leaves the child
            # holding the job directory open and the next cleanup fails.
            start_new_session=True,
            check=False,
            # An empty-ish environment: nothing this container holds is a
            # secret, and passing none is still the right default.
            env={
                "PATH": "/usr/bin:/bin",
                "HOME": job_dir,
                "TMPDIR": job_dir,
                "SAL_DISABLE_OPENCL": "1",
                "SAL_USE_VCLPLUGIN": "svp",
            },
        )
    except subprocess.TimeoutExpired:
        return STATUS_UNAVAILABLE, REASON_TIMEOUT, b""
    except FileNotFoundError:
        return STATUS_UNAVAILABLE, REASON_RENDERER_UNAVAILABLE, b""
    except OSError:
        return STATUS_UNAVAILABLE, REASON_PROCESS_FAILED, b""

    if completed.returncode != 0:
        # The engine's own message can carry a path; it is never forwarded.
        return STATUS_UNAVAILABLE, REASON_PROCESS_FAILED, b""

    produced = [
        os.path.join(out_dir, name)
        for name in sorted(os.listdir(out_dir))
        if name.lower().endswith(".pdf")
    ]
    if not produced:
        # It ran and produced nothing. That is a verdict about the document.
        return STATUS_REJECTED, REASON_INVALID_SOURCE, b""

    path = produced[0]
    # NO SYMLINKS. The output directory is ours and nothing should have put one
    # there, which is exactly why an unexpected one is refused rather than
    # followed.
    if os.path.islink(path):
        return STATUS_UNAVAILABLE, REASON_PROCESS_FAILED, b""

    size = os.path.getsize(path)
    if size > max_output:
        return STATUS_REJECTED, REASON_OUTPUT_TOO_LARGE, b""

    with open(path, "rb") as handle:
        data = handle.read(max_output + 1)

    if len(data) > max_output:
        return STATUS_REJECTED, REASON_OUTPUT_TOO_LARGE, b""

    return STATUS_OK, REASON_NONE, data


# ---------------------------------------------------------------------------
# request handling
# ---------------------------------------------------------------------------

def read_exactly(conn: socket.socket, count: int) -> bytes | None:
    chunks = []
    remaining = count
    while remaining > 0:
        chunk = conn.recv(min(remaining, 1 << 20))
        if not chunk:
            return None
        chunks.append(chunk)
        remaining -= len(chunk)
    return b"".join(chunks)


def respond(conn: socket.socket, status: int, reason: int, payload: bytes = b"") -> None:
    conn.sendall(RESPONSE_HEADER.pack(MAGIC, VERSION, status, reason, len(payload)))
    if payload:
        conn.sendall(payload)


def handle(conn: socket.socket) -> None:
    header = read_exactly(conn, REQUEST_HEADER.size)
    if header is None:
        return

    magic, version, op, fmt, _reserved, timeout, requested_max_output, payload_len = \
        REQUEST_HEADER.unpack(header)

    if magic != MAGIC or version != VERSION:
        respond(conn, STATUS_UNAVAILABLE, REASON_PROCESS_FAILED)
        return

    if op == OP_READINESS:
        ready = os.path.isfile(SOFFICE) and os.access(SOFFICE, os.X_OK)
        respond(conn, STATUS_OK if ready else STATUS_UNAVAILABLE,
                REASON_NONE if ready else REASON_RENDERER_UNAVAILABLE)
        return

    if op != OP_RENDER:
        respond(conn, STATUS_UNAVAILABLE, REASON_PROCESS_FAILED)
        return

    if fmt not in FORMATS:
        respond(conn, STATUS_REJECTED, REASON_UNSUPPORTED_FORMAT)
        return

    if payload_len <= 0 or payload_len > MAX_SOURCE_BYTES:
        respond(conn, STATUS_REJECTED, REASON_OUTPUT_TOO_LARGE)
        return

    payload = read_exactly(conn, payload_len)
    if payload is None:
        return

    extension, export_filter = FORMATS[fmt]
    effective_timeout = max(1, min(int(timeout) or 1, MAX_TIMEOUT_SECONDS))
    # THE SMALLER OF THE TWO BOUNDS. The caller states what it is willing to
    # receive and the worker states what it is willing to produce; neither can
    # raise the other's limit.
    effective_max_output = max(1, min(int(requested_max_output) or MAX_OUTPUT_BYTES, MAX_OUTPUT_BYTES))

    if not _slots.acquire(timeout=effective_timeout):
        respond(conn, STATUS_UNAVAILABLE, REASON_TIMEOUT)
        return

    # AN OPAQUE JOB DIRECTORY. The name is a fresh UUID and the source file is
    # called `source.<ext>`: never the original filename, never an owner id,
    # never a storage key, never anything the caller chose. A filename is
    # owner-authored text, and the one place it must not appear is an argv the
    # worker passes to a native process.
    job_dir = os.path.join(WORK_ROOT, uuid.uuid4().hex)
    try:
        os.makedirs(job_dir, mode=0o700, exist_ok=False)
        source = os.path.join(job_dir, f"source.{extension}")
        with open(os.open(source, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), "wb") as handle:
            handle.write(payload)

        status, reason, data = convert(
            job_dir, source, export_filter, effective_timeout, effective_max_output)
        respond(conn, status, reason, data)
        log(f"render format={extension} status={status} reason={reason} bytes={len(data)}")
    except OSError:
        respond(conn, STATUS_UNAVAILABLE, REASON_PROCESS_FAILED)
    finally:
        _slots.release()
        # RECURSIVE CLEANUP, always. The profile, the source, the output and
        # anything LibreOffice scattered go together, and no temporary file
        # leaves this container's private volume.
        shutil.rmtree(job_dir, ignore_errors=True)


def sweep_stale() -> None:
    """Whatever a crash left behind, removed at start.

    A worker that was killed mid-conversion leaves a job directory holding a
    private document. Cleaning up on the next start bounds how long that can
    survive to one restart, without needing a scheduler inside a container whose
    whole point is being small.
    """
    try:
        entries = os.listdir(WORK_ROOT)
    except OSError:
        return

    for name in entries:
        shutil.rmtree(os.path.join(WORK_ROOT, name), ignore_errors=True)


def serve() -> int:
    os.makedirs(WORK_ROOT, mode=0o700, exist_ok=True)
    sweep_stale()

    directory = os.path.dirname(SOCKET_PATH)
    os.makedirs(directory, mode=0o770, exist_ok=True)
    try:
        os.unlink(SOCKET_PATH)
    except OSError as error:
        if error.errno != errno.ENOENT:
            log("could not remove a stale socket")
            return 1

    server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    server.bind(SOCKET_PATH)
    # The API's container is the only other member of the shared volume; group
    # access is what lets it connect without either side running as root.
    os.chmod(SOCKET_PATH, 0o660)
    server.listen(8)

    stopping = threading.Event()

    def stop(_signum, _frame):
        stopping.set()
        try:
            server.close()
        except OSError:
            pass

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)

    log(f"listening concurrency={MAX_CONCURRENCY}")

    while not stopping.is_set():
        try:
            conn, _ = server.accept()
        except OSError:
            if stopping.is_set():
                break
            time.sleep(0.05)
            continue

        thread = threading.Thread(target=_serve_one, args=(conn,), daemon=True)
        thread.start()

    return 0


def _serve_one(conn: socket.socket) -> None:
    try:
        handle(conn)
    except Exception:  # noqa: BLE001 - one bad connection must not stop the worker
        log("connection failed")
    finally:
        try:
            conn.close()
        except OSError:
            pass


if __name__ == "__main__":
    sys.exit(serve())
