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

# BOTH LAYOUTS ARE FIXED AND EXPLICIT, and the `>` prefix is what makes them so:
# it selects big-endian AND standard sizes with NO alignment padding, so what
# Python packs is byte-for-byte what the C# client reads. A native-alignment
# format would silently insert a pad byte and shift every field after it — a
# mismatch that looks like "the renderer is unavailable" rather than like a
# protocol bug.
#
#   request  (20): magic[4] version[1] op[1] format[1] reserved[1]
#                  timeoutSeconds[4] maxOutputBytes[4] payloadLength[4]
#   response (12): magic[4] version[1] status[1] reason[2] payloadLength[4]
REQUEST_HEADER = struct.Struct(">4sBBBBIII")
RESPONSE_HEADER = struct.Struct(">4sBBHI")

assert REQUEST_HEADER.size == 20, REQUEST_HEADER.size
assert RESPONSE_HEADER.size == 12, RESPONSE_HEADER.size

# The closed format vocabulary: ordinal -> (extension, IMPORT filter, EXPORT
# filter). The value on the wire is an ordinal, so the API cannot name a filter
# and this worker cannot be persuaded to pick one.
#
# THE IMPORT FILTER IS PINNED, and that is the whole reason this table has three
# columns instead of two. Left to itself LibreOffice SNIFFS the input: hand it a
# plain text file called `source.docx` and it cheerfully imports it as a Writer
# document and produces a perfectly good PDF. That makes the declared format
# advisory, which is exactly the "untrusted input arriving somewhere written for
# a different structure" problem the API's own byte-probe exists to prevent —
# undone at the last step. With the filter pinned, a document that is not what
# the ordinal says fails to import and is refused.
FORMATS = {
    1: ("docx", "MS Word 2007 XML", "writer_pdf_Export"),
    2: ("xlsx", "Calc MS Excel 2007 XML", "calc_pdf_Export"),
    3: ("pptx", "Impress MS PowerPoint 2007 XML", "impress_pdf_Export"),
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



def terminate_group(process: subprocess.Popen) -> None:
    """Kill the whole process group and WAIT for it to be gone.

    Waiting matters as much as killing: the cleanup that follows removes the job
    directory, and a still-dying LibreOffice holding a file in it makes that
    removal fail — quietly, because it is best-effort. SIGTERM first so the
    engine can close its own files, then SIGKILL for whatever ignored it.
    """
    for signum in (signal.SIGTERM, signal.SIGKILL):
        try:
            os.killpg(os.getpgid(process.pid), signum)
        except (ProcessLookupError, PermissionError):
            return
        try:
            process.wait(timeout=5)
            return
        except subprocess.TimeoutExpired:
            continue

    # It did not die even to SIGKILL — a zombie parented elsewhere, most likely.
    # Reaped or not, the cleanup below is still attempted and reports honestly.
    log("a render process survived SIGKILL")


def convert(
    job_dir: str,
    source: str,
    import_filter: str,
    export_filter: str,
    timeout: int,
    max_output: int,
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
        # The declared format, made authoritative. Without this the engine
        # decides what the bytes are, and the ordinal becomes a suggestion.
        f"--infilter={import_filter}",
        "--convert-to", f"pdf:{export_filter}",
        "--outdir", out_dir,
        source,
    ]

    # Popen AND AN EXPLICIT GROUP KILL, not `subprocess.run(timeout=...)`.
    #
    # `run` kills the direct child on timeout and nothing else. LibreOffice's
    # launcher immediately execs `soffice.bin` and may fork further, so the
    # grandchildren survive, keep running, and keep the job directory OPEN — and
    # the recursive cleanup below then fails silently, leaving a copy of
    # somebody's document behind until the next restart. `start_new_session`
    # puts the whole tree in its own process group precisely so it can be killed
    # as one.
    try:
        process = subprocess.Popen(
            argv,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            start_new_session=True,
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
    except FileNotFoundError:
        return STATUS_UNAVAILABLE, REASON_RENDERER_UNAVAILABLE, b""
    except OSError:
        return STATUS_UNAVAILABLE, REASON_PROCESS_FAILED, b""

    try:
        returncode = process.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        terminate_group(process)
        return STATUS_UNAVAILABLE, REASON_TIMEOUT, b""

    if returncode != 0:
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

    extension, import_filter, export_filter = FORMATS[fmt]
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
            job_dir, source, import_filter, export_filter,
            effective_timeout, effective_max_output)
        respond(conn, status, reason, data)
        log(f"render format={extension} status={status} reason={reason} bytes={len(data)}")
    except OSError:
        respond(conn, STATUS_UNAVAILABLE, REASON_PROCESS_FAILED)
    finally:
        _slots.release()
        # RECURSIVE CLEANUP, always, and CHECKED. The profile, the source, the
        # output and anything LibreOffice scattered go together, and no
        # temporary file leaves this container's private volume.
        #
        # `ignore_errors=True` alone would turn "a copy of somebody's document
        # is still on disk" into silence. A killed engine can hold a file for a
        # moment after its group is signalled, so removal is retried briefly and
        # then REPORTED — the stale-job sweep at startup is the backstop, not the
        # plan.
        cleanup(job_dir)


def cleanup(job_dir: str) -> None:
    for attempt in range(5):
        shutil.rmtree(job_dir, ignore_errors=True)
        if not os.path.exists(job_dir):
            return
        time.sleep(0.2 * (attempt + 1))

    try:
        shutil.rmtree(job_dir)
    except OSError:
        # The path is opaque and is never logged; the fact that one survived is.
        log("a job directory could not be removed and will be swept at restart")


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
