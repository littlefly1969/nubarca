#!/usr/bin/env python3
"""Sequential overnight Tesseract / Paddle full / Xiaomi whole-page comparison."""
import argparse
import datetime as dt
import hashlib
import html
import importlib.util
import importlib.metadata
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import time
import urllib.request
import zipfile

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("ocr_real", HERE / "ocr-real.py")
base = importlib.util.module_from_spec(spec)
spec.loader.exec_module(base)
CODE_REV = "4abc240285b69a2965cf64762101ddb4ab35f32d"
MODEL_REPO = "xiaomi-research/Xiaomi-OCR-0"


def fetch():
    from huggingface_hub import HfApi, snapshot_download
    models = Path("/models")
    destination = models / "xiaomi-ocr-0"
    revision = HfApi().model_info(MODEL_REPO).sha
    lock = destination / "BENCH_PIN.json"
    if lock.exists():
        revision = base.read(lock)["revision"]
    print("Xiaomi modello fissato alla revisione " + revision, flush=True)
    snapshot_download(MODEL_REPO, revision=revision, local_dir=str(destination),
                      allow_patterns=["*.json", "*.jinja", "*.safetensors"])
    base.write(lock, {"repo": MODEL_REPO, "revision": revision, "source_revision": CODE_REV})
    post = models / "xiaomi-postprocess" / "postprocess"
    post.mkdir(parents=True, exist_ok=True)
    for name in ("__init__.py", "document.py", "otsl.py", "repetition_guard.py"):
        url = f"https://raw.githubusercontent.com/xiaomi-research/Xiaomi-OCR-0/{CODE_REV}/postprocess/{name}"
        with urllib.request.urlopen(url, timeout=60) as response:
            (post / name).write_bytes(response.read())
    with urllib.request.urlopen(f"https://raw.githubusercontent.com/xiaomi-research/Xiaomi-OCR-0/{CODE_REV}/LICENSE", timeout=60) as response:
        (post.parent / "LICENSE").write_bytes(response.read())
    # Fail before the overnight job if this installed Transformers lacks the architecture.
    from transformers import AutoConfig
    AutoConfig.from_pretrained(str(destination), local_files_only=True, trust_remote_code=False)
    print("Download e verifica configurazione completati.", flush=True)


def xiaomi_worker(folder):
    event = folder / "xiaomi-event.json"
    base.write(event, {"stage": "loading", "at": time.time()})
    import resource
    import torch
    from transformers import AutoModelForImageTextToText, AutoProcessor
    sys.path.insert(0, "/models/xiaomi-postprocess")
    from postprocess.document import finalize_document
    torch.set_num_threads(6)
    torch.set_num_interop_threads(1)
    started = time.monotonic()
    path = "/models/xiaomi-ocr-0"
    processor = AutoProcessor.from_pretrained(path, local_files_only=True, trust_remote_code=False)
    model = AutoModelForImageTextToText.from_pretrained(
        path, dtype=torch.float32, local_files_only=True, trust_remote_code=False).to("cpu").eval()
    loaded = time.monotonic() - started
    prompt = ("Extract all information from the main body of the document image and represent it "
              "in markdown format, ignoring headers and footers. Tables should be expressed in OTSL "
              "format, formulas in the document should be represented using LATEX format, and the "
              "parsing should be organized according to the reading order.")
    for page in sorted(folder.glob("p[0-9][0-9][0-9][0-9]")):
        if (page / "xiaomi.json").exists() or not (page / "pagina.png").exists():
            continue
        base.write(event, {"stage": "page", "page": page.name, "at": time.time()})
        started = time.monotonic()
        try:
            messages = [{"role": "user", "content": [
                {"type": "image", "url": str(page / "pagina.png")},
                {"type": "text", "text": prompt}]}]
            inputs = processor.apply_chat_template(messages, tokenize=True, add_generation_prompt=True,
                return_dict=True, return_tensors="pt", enable_thinking=False).to("cpu")
            with torch.inference_mode():
                generated = model.generate(**inputs, max_new_tokens=4096, do_sample=False, use_cache=True)
            tokens = generated[:, inputs["input_ids"].shape[1]:]
            raw = processor.batch_decode(tokens, skip_special_tokens=True)[0]
            (page / "xiaomi-raw.txt").write_text(raw)
            truncated = tokens.shape[1] >= 4096
            final = finalize_document(raw)
            if not isinstance(final, str):
                raise TypeError("Formato postprocess inatteso")
            (page / "xiaomi.md").write_text(final)
            base.write(page / "xiaomi.json", {"status": "truncated" if truncated else "ok",
                "seconds": round(time.monotonic() - started, 3), "characters": len(final),
                "generated_tokens": tokens.shape[1], "load_seconds_shared_per_document": round(loaded, 3),
                "mode": "whole-page", "dtype": "float32", "use_cache": True,
                "peak_process_ram_mib": round(resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024, 1)})
            del generated, tokens, inputs
        except Exception as exc:
            base.write(page / "xiaomi.json", {"status": "error", "error_type": type(exc).__name__})
            print("Xiaomi errore " + type(exc).__name__ + ": " + str(exc), flush=True)
        base.write(event, {"stage": "between_pages", "at": time.time()})
    base.write(event, {"stage": "done", "at": time.time()})


def process_xiaomi(folder):
    while True:
        pending = [p for p in sorted(folder.glob("p[0-9][0-9][0-9][0-9]"))
                   if (p / "pagina.png").exists() and not (p / "xiaomi.json").exists()]
        if not pending:
            return
        event = folder / "xiaomi-event.json"
        event.unlink(missing_ok=True)
        with (folder / "xiaomi-worker.log").open("a") as log:
            worker = subprocess.Popen([sys.executable, __file__, "worker", "--run", str(folder)],
                                       stdout=log, stderr=log, start_new_session=True)
            started, last, timeout = time.time(), None, False
            while worker.poll() is None:
                state = base.read(event) if event.exists() else {"stage": "loading", "at": started}
                identity = (state.get("stage"), state.get("page"))
                if identity != last:
                    print(f"PDF {folder.name}: Xiaomi {identity}", flush=True)
                    last = identity
                if time.time() - state["at"] > (300 if state["stage"] == "loading" else 900):
                    timeout = True
                    os.killpg(worker.pid, signal.SIGKILL)
                    break
                time.sleep(2)
            worker.wait()
        remaining = [p for p in pending if not (p / "xiaomi.json").exists()]
        if remaining:
            state = base.read(event) if event.exists() else {}
            for page in remaining if state.get("stage") == "loading" or not state else remaining[:1]:
                base.write(page / "xiaomi.json", {"status": "timeout" if timeout else "worker_error", "exit_code": worker.returncode})


def pack(root, archive=True):
    summary = base.report(root)
    counts = {"ok": 0, "errors_or_incomplete": 0, "seconds": 0}
    manifest = base.read(root / "manifest.json")
    for doc in manifest["documents"]:
        for page in sorted((root / doc["number"]).glob("p[0-9][0-9][0-9][0-9]")):
            info = base.read(page / "xiaomi.json") if (page / "xiaomi.json").exists() else {"status": "not_run"}
            counts["ok" if info["status"] == "ok" else "errors_or_incomplete"] += 1
            counts["seconds"] += info.get("seconds", 0)
            content = (page / "xiaomi.md").read_text() if (page / "xiaomi.md").exists() else "Risultato non disponibile"
            # Whitelist HTML tables; escape document/model content and forbid active elements.
            clean = base.SafeTable()
            clean.feed(content)
            panel = '<section><h2>Xiaomi — pagina intera</h2><p>' + html.escape(str(info)) + '</p><div class="block">' + ''.join(clean.parts) + '</div><p><a href="xiaomi-raw.txt">Output originale</a></p></section>'
            report = page / "confronto.html"
            report.write_text(report.read_text().replace("</div></html>", panel + "</div></html>").replace("1.2fr 1fr 1fr", "1.2fr 1fr 1fr 1fr"))
    counts["seconds"] = round(counts["seconds"], 2)
    summary["engines"]["xiaomi"] = counts
    summary["xiaomi_scope"] = "Pagina intera con prompt ufficiale (che esclude intestazioni e pie di pagina); non pipeline regioni e non replica dei benchmark pubblicati. Limite 4096 token/pagina, timeout 900s."
    base.write(root / "summary.json", summary)
    index = root / "index.html"
    index.write_text(index.read_text().replace("<h1>", '<p><strong>Xiaomi: prima prova su pagina intera, senza layout a regioni. Non assumere qualita o velocita equivalenti alla pipeline completa.</strong></p><pre>' + html.escape(json.dumps(summary["engines"], indent=2)) + '</pre><h1>', 1))
    if archive:
        base.space(root)
        output = root / "nubarca-ocr-notte.zip"
        with zipfile.ZipFile(output.with_suffix(".zip.tmp"), "w", zipfile.ZIP_DEFLATED, allowZip64=True) as zipped:
            for path in sorted(root.rglob("*")):
                if path.is_file() and path.suffix not in (".zip", ".tmp", ".log") and not path.name.endswith("event.json"):
                    zipped.write(path, str(path.relative_to(root)))
        output.with_suffix(".zip.tmp").replace(output)
        print("ARCHIVIO PRONTO: " + str(output), flush=True)
    return summary


def batch(root):
    os.environ["OCR_PAGE_TIMEOUT_SECONDS"] = "900"
    base.write(root / "night-versions.json", {name: importlib.metadata.version(name) for name in ("torch", "transformers", "paddleocr", "paddlex")})
    base.write(root / "night-status.json", {"status": "running"})
    # Retry errors from the interrupted previous run; retain their diagnostic record.
    stamp = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    for doc in base.read(root / "manifest.json")["documents"]:
        for page in (root / doc["number"]).glob("p[0-9][0-9][0-9][0-9]"):
            for engine in ("paddle", "tesseract", "xiaomi"):
                path = page / (engine + ".json")
                if path.exists() and base.read(path).get("status") != "ok":
                    path.replace(page / (engine + ".previous-" + stamp + ".json"))
    failure = None
    try:
        for doc in base.read(root / "manifest.json")["documents"]:
            folder = root / doc["number"]
            print("INIZIO PDF " + doc["number"], flush=True)
            base.process_document(folder)
            process_xiaomi(folder)
            pack(root, archive=False)
    except Exception as exc:
        failure = type(exc).__name__
        print("Interruzione: " + str(exc), flush=True)
    finally:
        base.write(root / "night-status.json", {"status": "interrupted" if failure else "finished", "error_type": failure})
        print(json.dumps(pack(root), ensure_ascii=False, indent=2), flush=True)


def host(args):
    os.umask(0o077)
    root = Path(args.root).resolve()
    # Never overlap two OCR jobs against the same files or consume two memory budgets.
    active = subprocess.check_output(["docker", "ps", "--format", "{{.Names}}"], text=True).splitlines()
    if any(n.startswith(("nubarca-ocr-real-", "nubarca-ocr-night-")) for n in active):
        raise RuntimeError("Esiste gia un benchmark OCR attivo: fermalo prima di avviare la notte")
    base.run(["docker", "image", "inspect", base.IMAGE], stdout=subprocess.DEVNULL)
    for path in (root / "models" / "paddleocr-vl-1.6" / "config.json", root / "models" / "pp-doclayoutv3-safetensors" / "config.json"):
        if not path.exists():
            raise RuntimeError("Modello Paddle mancante")
    shared = ["docker", "run", "--user", f"{os.getuid()}:{os.getgid()}", "--cpuset-cpus", "0,2,4,6,8,10",
              "--memory", "10g", "--memory-swap", "10g", "-e", "HOME=/tmp", "-e", "HF_HOME=/tmp/hf",
              "-e", "OMP_NUM_THREADS=6", "-e", "MKL_NUM_THREADS=6", "-e", "OPENBLAS_NUM_THREADS=6",
              "-e", "TOKENIZERS_PARALLELISM=false", "-v", f"{HERE}:/tool:ro"]
    print("Preparazione Xiaomi: download pesi, nessun documento inviato in rete.", flush=True)
    base.run(shared + ["--rm", "-v", f"{root / 'models'}:/models", "--entrypoint", "python", base.IMAGE, "-u", "/tool/night.py", "fetch"])
    last = root / "real-batch" / "LAST_RUN"
    data = Path(last.read_text().strip()) if last.exists() else None
    if not data or not (data / "manifest.json").exists() or len(base.read(data / "manifest.json")["documents"]) != 20:
        data = root / "real-batch" / dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        data.mkdir(parents=True)
        base.run(shared + ["--rm", "-it", "--network", "container:nubarca-api", "-v", f"{data}:/data",
                          "--entrypoint", "python", base.IMAGE, "/tool/ocr-real.py", "export", "--email", args.email,
                          "--count", "20", "--api", "http://localhost:8080"])
        last.write_text(str(data) + "\n")
    count = len(base.read(data / "manifest.json")["documents"])
    if count != 20:
        raise RuntimeError(f"Disponibili solo {count}/20 PDF scansiti: copie conservate, prova non avviata")
    base.space(data)
    base.write(data / "xiaomi-model.lock.json", base.read(root / "models" / "xiaomi-ocr-0" / "BENCH_PIN.json"))
    shutil.copyfile(__file__, data / "night-procedure.py")
    name = "nubarca-ocr-night-" + str(int(time.time()))
    base.run(shared + ["-d", "--network", "none", "--name", name, "-e", "HF_HUB_OFFLINE=1", "-e", "TRANSFORMERS_OFFLINE=1",
        "--log-opt", "max-size=20m", "--log-opt", "max-file=2", "-v", f"{data}:/data", "-v", f"{root / 'models'}:/models:ro",
        "--entrypoint", "python", base.IMAGE, "-u", "/tool/night.py", "batch", "--run", "/data"])
    alias = root / "real-batch" / "nubarca-ocr-notte.zip"
    if alias.is_symlink():
        alias.unlink()
    if not alias.exists():
        alias.symlink_to(data / "nubarca-ocr-notte.zip")
    pages = sum(d["pages"] for d in base.read(data / "manifest.json")["documents"])
    print(f"\nNOTTE AVVIATA: 20 PDF, {pages} pagine. Puoi chiudere PuTTY.", flush=True)
    print("Segui: docker logs -f " + name)
    print("ZIP finale: " + str(alias))


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("start", "fetch", "batch", "worker", "pack"))
    parser.add_argument("--root")
    parser.add_argument("--run")
    parser.add_argument("--email")
    args = parser.parse_args()
    if args.action == "start":
        host(args)
    elif args.action == "fetch":
        fetch()
    elif args.action == "worker":
        xiaomi_worker(Path(args.run))
    elif args.action == "pack":
        pack(Path(args.run))
    else:
        batch(Path(args.run))


if __name__ == "__main__":
    main()
