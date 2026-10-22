"""Hardware/runtime description and resource measurement for run manifests."""

from __future__ import annotations

import json
import os
import platform
import re
import resource
import shutil
import subprocess
import threading
import time
from importlib import metadata
from pathlib import Path

INTERESTING_FLAGS = ("avx", "avx2", "fma", "f16c", "avx512f", "avx512bw", "avx512vl",
                     "avx512_vnni", "avx512_bf16", "avx_vnni", "amx_bf16", "amx_int8", "amx_tile",
                     "sse4_2")

LIBRARIES = ("numpy", "onnxruntime", "openvino", "torch", "transformers", "sentence-transformers",
             "tokenizers", "huggingface-hub", "rapidocr", "pytesseract", "optimum", "optimum-intel",
             "pillow", "pypdfium2", "rapidfuzz", "apted", "psutil")


def _run(cmd: list[str]) -> str:
    if not shutil.which(cmd[0]):
        return ""
    try:
        return subprocess.run(cmd, capture_output=True, text=True, timeout=20).stdout
    except (OSError, subprocess.SubprocessError):
        return ""


def _cgroup_limits() -> dict:
    out = {}
    cpu = Path("/sys/fs/cgroup/cpu.max")
    if cpu.exists():
        quota, period = cpu.read_text().split()[:2]
        out["cpu_quota"] = None if quota == "max" else int(quota) / int(period)
    mem = Path("/sys/fs/cgroup/memory.max")
    if mem.exists():
        value = mem.read_text().strip()
        out["memory_max_bytes"] = None if value == "max" else int(value)
    return out


def hardware() -> dict:
    info: dict = {"machine": platform.machine(), "kernel": platform.release(),
                  "python": platform.python_version(), "os_cpu_count": os.cpu_count()}
    lscpu = _run(["lscpu"])
    for line in lscpu.splitlines():
        key, _, value = line.partition(":")
        key, value = key.strip(), value.strip()
        if key in ("Model name", "Thread(s) per core", "Core(s) per socket", "Socket(s)",
                   "CPU(s)", "CPU max MHz", "L2 cache", "L3 cache", "Vendor ID", "Hypervisor vendor"):
            info[key.lower().replace(" ", "_").replace("(s)", "s")] = value
        if key == "Flags":
            flags = set(value.split())
            info["flags"] = {f: f in flags for f in INTERESTING_FLAGS}
    if "flags" not in info and Path("/proc/cpuinfo").exists():
        text = Path("/proc/cpuinfo").read_text()
        match = re.search(r"^flags\s*:\s*(.*)$", text, re.M)
        if match:
            flags = set(match.group(1).split())
            info["flags"] = {f: f in flags for f in INTERESTING_FLAGS}
    if Path("/proc/meminfo").exists():
        match = re.search(r"MemTotal:\s+(\d+) kB", Path("/proc/meminfo").read_text())
        if match:
            info["ram_total_gib"] = round(int(match.group(1)) / 1024 / 1024, 2)
    dmi = _run(["dmidecode", "-t", "memory"])
    speeds = sorted(set(re.findall(r"Configured Memory Speed:\s*(\d+ MT/s)", dmi)))
    info["memory_speed"] = speeds or "unavailable (dmidecode missing or not permitted)"
    info["cgroup"] = _cgroup_limits()
    return info


def library_versions(extra: tuple[str, ...] = ()) -> dict:
    out = {}
    for name in LIBRARIES + extra:
        try:
            out[name] = metadata.version(name)
        except metadata.PackageNotFoundError:
            pass
    tess = _run(["tesseract", "--version"]).splitlines()
    if tess:
        out["tesseract"] = tess[0].strip()
    return out


def thread_env() -> dict:
    return {k: os.environ[k] for k in ("OMP_NUM_THREADS", "MKL_NUM_THREADS", "OPENBLAS_NUM_THREADS",
                                       "OV_NUM_THREADS", "BENCH_THREADS") if k in os.environ}


def peak_rss_mib() -> float:
    """Peak RSS of THIS process (Linux reports KiB). One model per process, so
    this is the model's peak, load included."""
    return resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024.0


class RssSampler:
    """Samples RSS of this process plus its children (e.g. a llama-server)."""

    def __init__(self, interval: float = 0.2):
        self.interval = interval
        self.peak_bytes = 0
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._loop, daemon=True)

    def _loop(self):
        import psutil

        me = psutil.Process()
        while not self._stop.is_set():
            try:
                total = me.memory_info().rss
                for child in me.children(recursive=True):
                    try:
                        total += child.memory_info().rss
                    except psutil.Error:
                        pass
                self.peak_bytes = max(self.peak_bytes, total)
            except psutil.Error:
                pass
            time.sleep(self.interval)

    def __enter__(self):
        self._thread.start()
        return self

    def __exit__(self, *exc):
        self._stop.set()
        self._thread.join(timeout=2)

    @property
    def peak_mib(self) -> float:
        return self.peak_bytes / 1024 / 1024


def percentiles(values_ms: list[float]) -> dict:
    if not values_ms:
        return {"n": 0}
    s = sorted(values_ms)

    def q(p):
        return s[min(len(s) - 1, max(0, round(p * (len(s) - 1))))]
    return {"n": len(s), "p50_ms": q(0.5), "p95_ms": q(0.95), "mean_ms": sum(s) / len(s),
            "max_ms": s[-1]}


def write_json(path: Path, data) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False, default=str) + "\n")
