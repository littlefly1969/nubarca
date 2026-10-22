"""OCR / document-parsing adapters. Each is optional: a missing runtime or
missing weights raise `Unavailable`, which the lane records as a skip with its
reason instead of failing the run.

Nothing here downloads anything. VLM engines run either through a llama.cpp
`llama-server` binary the operator provides (GGUF weights + vision projector)
or through transformers with `local_files_only=True`.
"""

from __future__ import annotations

import base64
import io
import json
import os
import shutil
import socket
import subprocess
import time
import urllib.error
import urllib.request
from pathlib import Path

from PIL import Image

from .encoders import Unavailable

XIAOMI_PAGE_PROMPT = (
    "Extract all information from the main body of the document image and represent it in "
    "markdown format, ignoring headers and footers. Tables should be expressed in OTSL format, "
    "formulas in the document should be represented using LATEX format, and the parsing should "
    "be organized according to the reading order.")

# Region prompts by block kind, per engine family (from the model cards).
PADDLE_PROMPTS = {"text": "OCR:", "table": "Table Recognition:", "formula": "Formula Recognition:",
                  "chart": "Chart Recognition:"}
# OvisOCR2 and WeVisDoc publish no prompt this environment could read; a neutral
# page-to-Markdown instruction, recorded in every run's describe(), so a later
# prompt from the card is a visible change rather than a silent one.
PAGE_TO_MARKDOWN_PROMPT = (
    "Convert this document page to Markdown. Keep the reading order, write tables as "
    "Markdown tables and formulas as LaTeX, and do not add anything that is not on the page.")
GLM_PROMPTS = {"text": "Text Recognition:", "table": "Table Recognition:",
               "formula": "Formula Recognition:", "chart": "Text Recognition:"}


def region_kind(kind: str) -> str:
    return {"T": "table", "F": "formula", "C": "chart"}.get(kind, "text")


def png_bytes(image: Image.Image) -> bytes:
    buffer = io.BytesIO()
    image.convert("RGB").save(buffer, format="PNG")
    return buffer.getvalue()


class Engine:
    name = "engine"
    supports_region = False
    load_seconds = 0.0

    def describe(self) -> dict:
        return {"engine": self.name}

    def page(self, image: Image.Image) -> str:
        raise NotImplementedError

    def region(self, image: Image.Image, kind: str) -> str:
        return self.page(image)

    def close(self) -> None:
        pass


class PdfTextLayer(Engine):
    """Not OCR: the digital PDF's own text layer, the upper bound for born-digital pages."""
    name = "pdf-textlayer"

    def __init__(self, golden_root: Path):
        import pypdfium2 as pdfium
        self.pdfium = pdfium
        self.root = golden_root

    def text_for(self, doc_id: str, page: int) -> str:
        pdf = self.pdfium.PdfDocument(str(self.root / "layout" / f"{doc_id}.pdf"))
        try:
            return pdf[page - 1].get_textpage().get_text_range()
        finally:
            pdf.close()

    def describe(self):
        from importlib import metadata
        return {"engine": self.name, "runtime": f"pypdfium2 {metadata.version('pypdfium2')}",
                "note": "text layer of the digital layout PDF; valid for the clean variant only"}


class Tesseract(Engine):
    name = "tesseract"

    # eng+ita is what NubArca configures (Ai__DocumentExtraction__OcrLanguages).
    # OMP_THREAD_LIMIT defaults to 1: with the limit at the container's full core count,
    # Tesseract's OpenMP workers were observed to spin past a 120 s timeout on pages that
    # one thread reads in under a second (cloud container, other load present). Raise it
    # with --tesseract-omp to measure multi-threaded Tesseract; the value is in the manifest.
    def __init__(self, omp_threads: int, timeout: float, langs: str = "eng+ita", psm: int = 3):
        if not shutil.which("tesseract"):
            raise Unavailable("tesseract binary not on PATH")
        available = subprocess.run(["tesseract", "--list-langs"], capture_output=True,
                                   text=True).stdout.split()
        missing = [lang for lang in langs.split("+") if lang not in available]
        if missing:
            raise Unavailable(f"tesseract language data missing: {missing}")
        self.langs, self.psm, self.timeout = langs, psm, timeout
        self.omp_threads = omp_threads
        self.env = {**os.environ, "OMP_THREAD_LIMIT": str(omp_threads)}
        self.version = subprocess.run(["tesseract", "--version"], capture_output=True,
                                      text=True).stdout.splitlines()[0]

    def describe(self):
        return {"engine": self.name, "runtime": self.version, "langs": self.langs,
                "psm": self.psm, "oem": 1, "timeout_s": self.timeout,
                "omp_thread_limit": self.omp_threads,
                "precision": "LSTM (tessdata from distro package)",
                "invocation": "tesseract - - -l <langs> --psm <psm> (PNG on stdin, as NubArca does)"}

    def page(self, image):
        proc = subprocess.run(["tesseract", "-", "-", "-l", self.langs, "--psm", str(self.psm),
                               "--oem", "1"], input=png_bytes(image), capture_output=True,
                              env=self.env, timeout=self.timeout)
        if proc.returncode != 0:
            raise RuntimeError(f"tesseract exit {proc.returncode}")
        return proc.stdout.decode("utf-8", errors="replace")


class RapidOcr(Engine):
    """PP-OCRv5 (latin recogniser) ONNX through RapidOCR, from pinned local files."""
    name = "rapidocr-ppocrv5"

    def __init__(self, models_dir: Path, threads: int):
        base = models_dir / "ppocrv5-onnx"
        det = base / "detection" / "v5" / "det.onnx"
        rec = base / "languages" / "latin" / "rec.onnx"
        keys = base / "languages" / "latin" / "dict.txt"
        for f in (det, rec, keys):
            if not f.exists():
                raise Unavailable(f"PP-OCRv5 file missing: {f}")
        try:
            from rapidocr import RapidOCR
            import rapidocr
        except ImportError as exc:
            raise Unavailable(f"rapidocr not installed: {exc}") from exc
        start = time.perf_counter()
        self.engine = RapidOCR(params={
            "Global.use_cls": False,
            "Global.log_level": "error",
            "Det.model_path": str(det),
            "Rec.model_path": str(rec),
            "Rec.rec_keys_path": str(keys),
            "EngineConfig.onnxruntime.intra_op_num_threads": threads,
            "EngineConfig.onnxruntime.inter_op_num_threads": 1,
        })
        self.load_seconds = time.perf_counter() - start
        self.version = getattr(rapidocr, "__version__", "unknown")

    def describe(self):
        return {"engine": self.name, "runtime": f"rapidocr {self.version} + onnxruntime",
                "det": "PP-OCRv5 det (monkt/paddleocr-onnx detection/v5)",
                "rec": "PP-OCRv5 latin rec", "cls": "disabled", "precision": "fp32"}

    def page(self, image):
        import numpy as np

        result = self.engine(np.asarray(image.convert("RGB")))
        boxes = getattr(result, "boxes", None)
        texts = getattr(result, "txts", None)
        if boxes is None or texts is None:
            return ""
        items = []
        for box, text in zip(boxes, texts):
            ys = [p[1] for p in box]
            xs = [p[0] for p in box]
            items.append((min(ys), max(ys), min(xs), text))
        # Group boxes into lines by vertical overlap, then left-to-right.
        items.sort(key=lambda t: (t[0], t[2]))
        lines: list[list[tuple]] = []
        for item in items:
            if lines:
                top, bottom = lines[-1][0][0], lines[-1][0][1]
                overlap = min(bottom, item[1]) - max(top, item[0])
                if overlap > 0.5 * min(bottom - top, item[1] - item[0]):
                    lines[-1].append(item)
                    continue
            lines.append([item])
        return "\n".join(" ".join(t[3] for t in sorted(line, key=lambda t: t[2]))
                         for line in lines)


def _free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class LlamaServerVlm(Engine):
    """A GGUF VLM behind llama.cpp's OpenAI-compatible server, on CPU."""
    supports_region = True

    def __init__(self, name: str, models_dir: Path, lock: str, model_file: str, mmproj_file: str,
                 page_prompt: str, region_prompts: dict, binary: str, threads: int,
                 max_tokens: int, timeout: float, ctx: int = 16384):
        self.name = name
        base = models_dir / lock
        model, mmproj = base / model_file, base / mmproj_file
        for f in (model, mmproj):
            if not f.exists():
                raise Unavailable(f"GGUF file missing: {f}")
        exe = shutil.which(binary) or (binary if Path(binary).exists() else None)
        if not exe:
            raise Unavailable(f"llama-server binary not found ({binary}); build the image with "
                              "the llama.cpp stage or set BENCH_LLAMA_SERVER")
        self.page_prompt, self.region_prompts = page_prompt, region_prompts
        self.max_tokens, self.timeout = max_tokens, timeout
        self.port = _free_port()
        version = subprocess.run([exe, "--version"], capture_output=True, text=True)
        self.version = (version.stdout + version.stderr).strip().splitlines()[-1:] or ["unknown"]
        cmd = [exe, "-m", str(model), "--mmproj", str(mmproj), "--host", "127.0.0.1",
               "--port", str(self.port), "--threads", str(threads), "--ctx-size", str(ctx),
               "--n-gpu-layers", "0", "--temp", "0", "--parallel", "1", "--no-webui"]
        self.cmd = cmd
        start = time.perf_counter()
        self.proc = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        deadline = time.time() + 600
        while time.time() < deadline:
            if self.proc.poll() is not None:
                err = self.proc.stderr.read().decode(errors="replace")[-800:]
                raise Unavailable(f"llama-server exited during load: {err}")
            try:
                with urllib.request.urlopen(f"http://127.0.0.1:{self.port}/health", timeout=2) as r:
                    if r.status == 200:
                        break
            except (urllib.error.URLError, OSError):
                pass
            time.sleep(1)
        else:
            self.close()
            raise Unavailable("llama-server did not become healthy in 600 s")
        self.load_seconds = time.perf_counter() - start
        self.model_files = {"model": model_file, "mmproj": mmproj_file}

    def describe(self):
        return {"engine": self.name, "runtime": f"llama.cpp llama-server {self.version[0]}",
                "files": self.model_files, "page_prompt": self.page_prompt,
                "region_prompts": self.region_prompts, "max_tokens": self.max_tokens,
                "temperature": 0, "command": [Path(self.cmd[0]).name, *self.cmd[1:]]}

    def _ask(self, image: Image.Image, prompt: str) -> str:
        data = base64.b64encode(png_bytes(image)).decode()
        body = {"messages": [{"role": "user", "content": [
            {"type": "image_url", "image_url": {"url": f"data:image/png;base64,{data}"}},
            {"type": "text", "text": prompt}]}],
            "temperature": 0, "max_tokens": self.max_tokens, "stream": False}
        request = urllib.request.Request(f"http://127.0.0.1:{self.port}/v1/chat/completions",
                                         data=json.dumps(body).encode(),
                                         headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=self.timeout) as response:
            payload = json.loads(response.read())
        return payload["choices"][0]["message"].get("content") or ""

    def page(self, image):
        return self._ask(image, self.page_prompt)

    def region(self, image, kind):
        return self._ask(image, self.region_prompts.get(region_kind(kind), self.page_prompt))

    def close(self):
        if getattr(self, "proc", None) and self.proc.poll() is None:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=20)
            except subprocess.TimeoutExpired:
                self.proc.kill()


class TransformersVlm(Engine):
    """A small VLM through transformers on CPU (fp32), local files only."""
    supports_region = True

    def __init__(self, name: str, models_dir: Path, lock: str, model_classes: tuple[str, ...],
                 page_prompt: str, region_prompts: dict, threads: int, max_tokens: int):
        self.name = name
        path = models_dir / lock
        if not path.exists():
            raise Unavailable(f"weights not found at {path}")
        try:
            import torch
            import transformers
            from transformers import AutoProcessor
        except ImportError as exc:
            raise Unavailable(f"transformers/torch not installed: {exc}") from exc
        model_class = next((c for c in model_classes if hasattr(transformers, c)), None)
        if model_class is None:
            raise Unavailable(f"transformers {transformers.__version__} has none of {model_classes}")
        cls = getattr(transformers, model_class)
        torch.set_num_threads(threads)
        start = time.perf_counter()
        # Never trust_remote_code: a model that needs its own Python to load is
        # a recorded skip here, not code executed from a download.
        try:
            self.processor = AutoProcessor.from_pretrained(str(path), local_files_only=True)
            self.model = cls.from_pretrained(str(path), local_files_only=True,
                                             torch_dtype=torch.float32).eval()
        except Exception as exc:  # noqa: BLE001
            raise Unavailable(f"load failed: {type(exc).__name__}: {str(exc)[:300]}") from exc
        self.load_seconds = time.perf_counter() - start
        self.torch = torch
        self.page_prompt, self.region_prompts = page_prompt, region_prompts
        self.max_tokens = max_tokens
        self.info = {"transformers": transformers.__version__, "class": model_class,
                     "precision": "fp32", "torch_threads": torch.get_num_threads()}

    def describe(self):
        return {"engine": self.name, "runtime": "transformers", **self.info,
                "page_prompt": self.page_prompt, "region_prompts": self.region_prompts,
                "max_new_tokens": self.max_tokens, "do_sample": False}

    def _ask(self, image, prompt):
        messages = [{"role": "user", "content": [{"type": "image", "image": image.convert("RGB")},
                                                 {"type": "text", "text": prompt}]}]
        inputs = self.processor.apply_chat_template(messages, tokenize=True,
                                                    add_generation_prompt=True, return_dict=True,
                                                    return_tensors="pt")
        with self.torch.inference_mode():
            out = self.model.generate(**inputs, max_new_tokens=self.max_tokens, do_sample=False)
        new = out[0][inputs["input_ids"].shape[1]:]
        return self.processor.decode(new, skip_special_tokens=True)

    def page(self, image):
        return self._ask(image, self.page_prompt)

    def region(self, image, kind):
        return self._ask(image, self.region_prompts.get(region_kind(kind), self.page_prompt))


ENGINES = ("pdf-textlayer", "tesseract", "rapidocr-ppocrv5", "paddleocr-vl-1.5-gguf",
           "xiaomi-ocr-0-gguf", "glm-ocr", "xiaomi-ocr-0", "paddleocr-vl-1.5", "paddleocr-vl-1.6",
           "ovisocr2", "wevisdoc-2b")

ENGINE_LOCK = {"rapidocr-ppocrv5": "ppocrv5-onnx", "paddleocr-vl-1.5-gguf": "paddleocr-vl-1.5-gguf",
               "xiaomi-ocr-0-gguf": "xiaomi-ocr-0-gguf", "glm-ocr": "glm-ocr",
               "xiaomi-ocr-0": "xiaomi-ocr-0", "paddleocr-vl-1.5": "paddleocr-vl-1.5",
               "paddleocr-vl-1.6": "paddleocr-vl-1.6", "ovisocr2": "ovisocr2",
               "wevisdoc-2b": "wevisdoc-2b"}


def load_engine(name: str, args) -> Engine:
    if name == "pdf-textlayer":
        return PdfTextLayer(args.golden)
    if name == "tesseract":
        return Tesseract(getattr(args, "tesseract_omp", 1), args.timeout)
    if name == "rapidocr-ppocrv5":
        return RapidOcr(args.models_dir, args.threads)
    if name == "paddleocr-vl-1.5-gguf":
        return LlamaServerVlm(name, args.models_dir, "paddleocr-vl-1.5-gguf", "PaddleOCR-VL-1.5.gguf",
                              "PaddleOCR-VL-1.5-mmproj.gguf", PADDLE_PROMPTS["text"],
                              PADDLE_PROMPTS, args.llama_server, args.threads, args.max_tokens,
                              args.timeout)
    if name == "xiaomi-ocr-0-gguf":
        return LlamaServerVlm(name, args.models_dir, "xiaomi-ocr-0-gguf", "Xiaomi-OCR-0.Q4_K_M.gguf",
                              "Xiaomi-OCR-0.mmproj-bf16.gguf", XIAOMI_PAGE_PROMPT,
                              {"text": XIAOMI_PAGE_PROMPT}, args.llama_server, args.threads,
                              args.max_tokens, args.timeout)
    if name == "glm-ocr":
        return TransformersVlm(name, args.models_dir, "glm-ocr",
                               ("AutoModelForMultimodalLM", "AutoModelForImageTextToText"),
                               GLM_PROMPTS["text"], GLM_PROMPTS, args.threads, args.max_tokens)
    if name == "xiaomi-ocr-0":
        return TransformersVlm(name, args.models_dir, "xiaomi-ocr-0",
                               ("AutoModelForImageTextToText", "AutoModelForMultimodalLM"),
                               XIAOMI_PAGE_PROMPT, {"text": XIAOMI_PAGE_PROMPT}, args.threads,
                               args.max_tokens)
    if name == "paddleocr-vl-1.5":
        return TransformersVlm(name, args.models_dir, "paddleocr-vl-1.5",
                               ("AutoModelForImageTextToText",), PADDLE_PROMPTS["text"],
                               PADDLE_PROMPTS, args.threads, args.max_tokens)
    if name == "paddleocr-vl-1.6":
        return TransformersVlm(name, args.models_dir, "paddleocr-vl-1.6",
                               ("AutoModelForImageTextToText",), PADDLE_PROMPTS["text"],
                               PADDLE_PROMPTS, args.threads, args.max_tokens)
    if name in ("ovisocr2", "wevisdoc-2b"):
        return TransformersVlm(name, args.models_dir, name,
                               ("AutoModelForImageTextToText", "AutoModelForMultimodalLM"),
                               PAGE_TO_MARKDOWN_PROMPT, {"text": PAGE_TO_MARKDOWN_PROMPT},
                               args.threads, args.max_tokens)
    raise SystemExit(f"unknown OCR engine {name!r}; known: {', '.join(ENGINES)}")
