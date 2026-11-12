#!/usr/bin/env python3
"""Owner-authenticated, isolated, resumable real-PDF OCR comparison.

Host: start --root ROOT --email EMAIL; resume --root ROOT --run RUN.
Container phases are internal. No production volume or database access.
"""
import argparse
import datetime as dt
import getpass
import hashlib
import html
from html.parser import HTMLParser
import importlib.metadata
import json
import math
import os
from pathlib import Path
import random
import shutil
import signal
import subprocess
import sys
import time
import zipfile

VERSION = "1"
BASE = "nubarca-paddle-full:ocr3.7.0-pdx3.7.2"
IMAGE = "nubarca-ocr-real:v1"
OCR_REV = "c5630abae1d940eafe0697512a0325494b02ab42"
LAYOUT_REV = "e4f489dc536556fc1be02b973ba06756e0d4a2ac"


def read(path):
    return json.loads(Path(path).read_text())


def write(path, value):
    p = Path(path)
    tmp = p.with_suffix(p.suffix + ".tmp")
    tmp.write_text(json.dumps(value, ensure_ascii=False, indent=2))
    tmp.replace(p)


def run(cmd, **kwargs):
    return subprocess.run(cmd, check=True, **kwargs)


def space(path):
    if shutil.disk_usage(path).free < 8 * 1024**3:
        raise RuntimeError("Spazio libero inferiore a 8 GiB: prova interrotta, copie conservate.")


def inspect_pdf(path):
    import pypdfium2 as pdfium
    with pdfium.PdfDocument(str(path)) as doc:
        if not len(doc):
            raise ValueError("PDF senza pagine")
        for page in doc:
            try:
                text = page.get_textpage()
                try:
                    if any(c.isalnum() for c in text.get_text_range()):
                        return {"scanned": False, "pages": len(doc)}
                finally:
                    text.close()
            finally:
                page.close()
        return {"scanned": True, "pages": len(doc)}


def export(args):
    import requests
    target = Path("/data")
    session = requests.Session()
    session.trust_env = False
    base = args.api.rstrip("/")
    password = getpass.getpass("Password dell'utente NubArca (non SSH): ")
    response = session.post(base + "/api/auth/login",
                            json={"email": args.email, "password": password}, timeout=40)
    del password
    if response.status_code != 200:
        raise RuntimeError("Accesso NubArca non riuscito: HTTP " + str(response.status_code))
    try:
        response = session.get(base + "/api/auth/me", timeout=30)
        response.raise_for_status()
        me = response.json()
        if str(me.get("email", "")).casefold() != args.email.casefold():
            raise RuntimeError("Identita autenticata diversa da quella richiesta")
        response = session.get(base + "/api/search", params={"q": "pdf"}, timeout=120)
        response.raise_for_status()
        files = response.json()
        if not isinstance(files, list):
            raise RuntimeError("Formato elenco API inatteso: nessun file esportato")
        files = [f for f in files if str(f.get("name", "")).lower().endswith(".pdf")
                 or f.get("mimeType") == "application/pdf"]
        random.Random(1969).shuffle(files)
        selected, excluded, hashes = [], {}, set()
        def skip(reason):
            excluded[reason] = excluded.get(reason, 0) + 1
        for index, item in enumerate(files, 1):
            if len(selected) >= args.count:
                break
            space(target)
            candidate = target / "candidate.pdf"
            try:
                # UUID only: never interpolate an untrusted API path component.
                import uuid
                file_id = str(uuid.UUID(item["id"]))
                size = 0
                with session.get(base + "/api/files/" + file_id + "/content",
                                 stream=True, timeout=(15, 60)) as content:
                    content.raise_for_status()
                    with candidate.open("wb") as output:
                        for chunk in content.iter_content(1024 * 1024):
                            size += len(chunk)
                            if size > 256 * 1024**2:
                                raise ValueError("oltre_256_MiB")
                            output.write(chunk)
                # PDF parsing isolated: corrupt/hostile files cannot stall export forever.
                probe = subprocess.run([sys.executable, __file__, "inspect", str(candidate)],
                                       capture_output=True, text=True, timeout=60)
                if probe.returncode:
                    skip("pdf_non_leggibile_o_protetto")
                    continue
                details = json.loads(probe.stdout)
                if not details["scanned"]:
                    skip("contiene_testo_anche_solo_in_una_pagina")
                    continue
                with candidate.open("rb") as stream:
                    digest = hashlib.file_digest(stream, "sha256").digest()
                if digest in hashes:
                    skip("copia_identica")
                    continue
                hashes.add(digest)
                number = f"{len(selected) + 1:03d}"
                folder = target / number
                folder.mkdir()
                candidate.replace(folder / "originale.pdf")
                selected.append({"number": number, "name": item["name"],
                                 "pages": details["pages"], "bytes": size})
                write(target / "manifest.json", {"documents": selected, "excluded": excluded,
                      "requested": args.count, "selection_seed": 1969})
                print(f"Selezionati {len(selected)}/{args.count}; pagine totali: "
                      f"{sum(d['pages'] for d in selected)}", flush=True)
            except requests.RequestException:
                skip("errore_scaricamento")
            except subprocess.TimeoutExpired:
                skip("verifica_pdf_timeout")
            except ValueError:
                skip("formato_o_dimensione_non_ammessi")
            finally:
                candidate.unlink(missing_ok=True)
            if index % 50 == 0:
                print(f"Esaminati {index} PDF, selezionati {len(selected)}", flush=True)
        write(target / "manifest.json", {"documents": selected, "excluded": excluded,
              "requested": args.count, "selection_seed": 1969})
        print(f"Esportazione conclusa: {len(selected)}/{args.count} PDF, "
              f"{sum(d['pages'] for d in selected)} pagine. Esclusioni: {excluded}", flush=True)
        if not selected:
            raise RuntimeError("Nessun PDF composto esclusivamente da scansioni disponibile")
    finally:
        try:
            session.post(base + "/api/auth/logout", timeout=15)
        finally:
            session.close()


def render(pdf, index, output):
    import pypdfium2 as pdfium
    with pdfium.PdfDocument(str(pdf)) as doc:
        page = doc[index]
        try:
            width, height = page.get_size()
            scale = min(200 / 72, math.sqrt(20_000_000 / (width * height)))
            bitmap = page.render(scale=scale)
            try:
                image = bitmap.to_pil().convert("RGB")
                image.save(output)
                return {"dpi": round(scale * 72, 2), "width": image.width, "height": image.height}
            finally:
                bitmap.close()
        finally:
            page.close()


class Plain(HTMLParser):
    def __init__(self):
        super().__init__()
        self.parts = []
    def handle_data(self, data):
        self.parts.append(data)
    def handle_starttag(self, tag, attrs):
        if tag in ("tr", "p", "br", "table"):
            self.parts.append("\n")
        elif tag in ("td", "th"):
            self.parts.append("\t")


def extract_blocks(value):
    if isinstance(value, dict):
        if isinstance(value.get("parsing_res_list"), list):
            return value["parsing_res_list"]
        for nested in value.values():
            result = extract_blocks(nested)
            if result is not None:
                return result
    return None


def paddle_worker(args):
    import resource
    import torch
    from paddleocr import PaddleOCRVL
    torch.set_num_threads(6)
    torch.set_num_interop_threads(1)
    folder = Path(args.folder)
    event = folder / "event.json"
    write(event, {"stage": "loading", "at": time.time()})
    started = time.monotonic()
    pipeline = PaddleOCRVL(
        pipeline_version="v1.6", device="cpu", engine="transformers",
        engine_config={"dtype": "float32", "trust_remote_code": False,
                       "model_kwargs": {"local_files_only": True},
                       "processor_kwargs": {"local_files_only": True}},
        cpu_threads=6, layout_detection_model_dir="/models/pp-doclayoutv3-safetensors",
        vl_rec_model_dir="/models/paddleocr-vl-1.6", use_layout_detection=True,
        use_doc_orientation_classify=False, use_doc_unwarping=False,
        use_queues=False, markdown_ignore_labels=[])
    load_seconds = time.monotonic() - started
    for page_dir in sorted(folder.glob("p[0-9][0-9][0-9][0-9]")):
        metrics = page_dir / "paddle.json"
        if metrics.exists() or not (page_dir / "pagina.png").exists():
            continue
        write(event, {"stage": "page", "page": page_dir.name, "at": time.time()})
        started = time.monotonic()
        native = page_dir / "paddle-native"
        native.mkdir(exist_ok=True)
        try:
            outputs = list(pipeline.predict(str(page_dir / "pagina.png"), max_new_tokens=4096,
                                           vlm_extra_args={"use_cache": True}))
            seconds = time.monotonic() - started
            if len(outputs) != 1:
                raise RuntimeError("Numero risultati inatteso")
            outputs[0].save_to_json(save_path=str(native))
            outputs[0].save_to_markdown(save_path=str(native))
            blocks = None
            for source in native.glob("*.json"):
                blocks = extract_blocks(read(source))
                if blocks is not None:
                    break
            if blocks is None:
                raise RuntimeError("Struttura risultato Paddle non riconosciuta")
            contents = [str(b.get("block_content", "")) for b in blocks]
            plain = []
            for content in contents:
                if "<table" in content.lower():
                    parser = Plain()
                    parser.feed(content)
                    plain.append("".join(parser.parts))
                else:
                    plain.append(content)
            text = "\n\n".join(plain)
            (page_dir / "paddle.txt").write_text(text)
            write(page_dir / "blocks.json", contents)
            write(metrics, {"status": "ok", "seconds": round(seconds, 3),
                            "model_load_seconds": round(load_seconds, 3),
                            "characters": len(text),
                            "tables": sum(b.get("block_label") == "table" for b in blocks),
                            "peak_process_ram_mib": round(resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024, 1)})
        except Exception as exc:
            write(metrics, {"status": "error", "error_type": type(exc).__name__})
        write(event, {"stage": "between_pages", "at": time.time()})
    write(event, {"stage": "done", "at": time.time()})


def process_document(folder):
    details = next(d for d in read(folder.parent / "manifest.json")["documents"] if d["number"] == folder.name)
    for index in range(details["pages"]):
        space(folder)
        page = folder / f"p{index + 1:04d}"
        page.mkdir(exist_ok=True)
        print(f"PDF {folder.name}, pagina {index + 1}/{details['pages']}: Tesseract", flush=True)
        if not (page / "render.json").exists():
            try:
                write(page / "render.json", render(folder / "originale.pdf", index, page / "pagina.png"))
            except Exception as exc:
                for engine in ("tesseract", "paddle"):
                    write(page / (engine + ".json"), {"status": "render_error", "error_type": type(exc).__name__})
                continue
        if not (page / "tesseract.json").exists():
            started = time.monotonic()
            try:
                env = dict(os.environ, OMP_THREAD_LIMIT="1", OMP_NUM_THREADS="1")
                result = subprocess.run(["tesseract", str(page / "pagina.png"), str(page / "tesseract"),
                                         "-l", "ita+eng", "--oem", "1", "--psm", "3"],
                                        capture_output=True, timeout=120, env=env)
                if result.returncode:
                    raise RuntimeError("Tesseract non riuscito")
                text = (page / "tesseract.txt").read_text()
                info = {"status": "ok", "seconds": round(time.monotonic() - started, 3), "characters": len(text)}
            except Exception as exc:
                info = {"status": "error", "error_type": type(exc).__name__}
            write(page / "tesseract.json", info)
    # Reuse one model per PDF; restart after a page timeout/crash, without losing completed pages.
    while True:
        pending = [p for p in sorted(folder.glob("p[0-9][0-9][0-9][0-9]")) if not (p / "paddle.json").exists()]
        if not pending:
            break
        event = folder / "event.json"
        event.unlink(missing_ok=True)
        with (folder / "worker.log").open("a") as log:
            worker = subprocess.Popen([sys.executable, __file__, "worker", str(folder)],
                                      stdout=log, stderr=log, start_new_session=True)
            last = None
            started = time.time()
            timed_out = False
            while worker.poll() is None:
                state = read(event) if event.exists() else {"stage": "loading", "at": started}
                identity = (state.get("stage"), state.get("page"))
                if identity != last:
                    print(f"PDF {folder.name}: Paddle {identity}", flush=True)
                    last = identity
                if time.time() - state["at"] > 300:
                    timed_out = True
                    os.killpg(worker.pid, signal.SIGKILL)
                    break
                time.sleep(2)
            worker.wait()
        remaining = [p for p in pending if not (p / "paddle.json").exists()]
        if remaining:
            # A failed initialization affects all remaining pages; a failed page only itself.
            state = read(event) if event.exists() else {}
            affected = remaining if state.get("stage") == "loading" or not state else remaining[:1]
            for page in affected:
                write(page / "paddle.json", {"status": "timeout" if timed_out else "worker_error",
                                            "exit_code": worker.returncode})


class SafeTable(HTMLParser):
    allowed = {"table", "thead", "tbody", "tfoot", "tr", "td", "th", "br"}
    def __init__(self):
        super().__init__()
        self.parts = []
    def handle_data(self, data):
        self.parts.append(html.escape(data))
    def handle_starttag(self, tag, attrs):
        if tag in self.allowed:
            safe = "".join(f' {k}="{v}"' for k, v in attrs
                           if k in ("colspan", "rowspan") and v and v.isdecimal() and len(v) < 4)
            self.parts.append("<" + tag + safe + ">")
    def handle_endtag(self, tag):
        if tag in self.allowed:
            self.parts.append("</" + tag + ">")


CSS = "body{font:16px system-ui;margin:20px}a{color:#0645ad}.grid{display:grid;grid-template-columns:1.2fr 1fr 1fr;gap:16px}img{width:100%;height:auto}pre{white-space:pre-wrap;overflow-wrap:anywhere}table{border-collapse:collapse;font-size:13px}td,th{border:1px solid #888;padding:4px}section{min-width:0;border:1px solid #bbb;padding:12px}.block{white-space:pre-wrap;margin:10px 0}@media(max-width:1100px){.grid{grid-template-columns:1fr}}"


def report(root):
    manifest = read(root / "manifest.json")
    summary = {"documents": len(manifest["documents"]), "requested_documents": manifest["requested"],
               "pages": sum(d["pages"] for d in manifest["documents"]), "engines": {},
               "note": "Nessuna misura di accuratezza: trascrizioni di riferimento non disponibili.",
               "render": "200 dpi; limite 20 megapixel con dpi effettivi registrati per pagina",
               "max_tokens_per_region": 4096, "timeout_seconds_per_page": 300,
               "models": {"ocr": OCR_REV, "layout": LAYOUT_REV}}
    rows = []
    for engine in ("tesseract", "paddle"):
        summary["engines"][engine] = {"ok": 0, "errors": 0, "seconds": 0, "characters": 0}
    for doc in manifest["documents"]:
        folder = root / doc["number"]
        links = []
        for page in sorted(folder.glob("p[0-9][0-9][0-9][0-9]")):
            results = {}
            for engine in ("tesseract", "paddle"):
                info = read(page / (engine + ".json")) if (page / (engine + ".json")).exists() else {"status": "not_run"}
                results[engine] = info
                counts = summary["engines"][engine]
                counts["ok" if info["status"] == "ok" else "errors"] += 1
                counts["seconds"] += info.get("seconds", 0)
                counts["characters"] += info.get("characters", 0)
            text = (page / "tesseract.txt").read_text() if (page / "tesseract.txt").exists() else "Nessun testo disponibile"
            contents = []
            for block in read(page / "blocks.json") if (page / "blocks.json").exists() else []:
                if "<table" in block.lower():
                    clean = SafeTable()
                    clean.feed(block)
                    contents.append("<div>" + "".join(clean.parts) + "</div>")
                else:
                    contents.append('<div class="block">' + html.escape(block) + "</div>")
            title = html.escape(doc["name"]) + " — " + page.name
            body = f'<h1>{title}</h1><p><a href="../../index.html">Indice</a> · <a href="../originale.pdf">PDF originale</a></p>'
            body += '<div class="grid"><section><h2>Pagina originale</h2><a href="pagina.png"><img src="pagina.png" alt="Scansione"></a></section>'
            body += '<section><h2>Tesseract</h2><p>' + html.escape(str(results["tesseract"])) + '</p><pre>' + html.escape(text) + '</pre></section>'
            body += '<section><h2>Paddle completo</h2><p>' + html.escape(str(results["paddle"])) + '</p>' + "".join(contents) + '</section></div>'
            (page / "confronto.html").write_text('<!doctype html><html lang="it"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>Confronto OCR</title><style>' + CSS + '</style>' + body + '</html>')
            links.append(f'<a href="{doc["number"]}/{page.name}/confronto.html">{page.name}</a>')
        rows.append(f'<tr><td>{doc["number"]}</td><td><a href="{doc["number"]}/originale.pdf">{html.escape(doc["name"])}</a></td><td>{doc["pages"]}</td><td>{" · ".join(links)}</td></tr>')
    for counts in summary["engines"].values():
        counts["seconds"] = round(counts["seconds"], 2)
    write(root / "summary.json", summary)
    header = '<h1>Confronto OCR su PDF reali</h1><p>Copie locali; nessun indice NubArca modificato. Tutte le pagine dei PDF selezionati. Non sono disponibili trascrizioni di riferimento: lunghezza e tempi non misurano accuratezza.</p>'
    header += '<p>I tempi Paddle escludono caricamento modello e salvataggio. Tesseract usa un thread, Paddle sei; non e un confronto a parita di calcolo. Il caricamento modello, i limiti e gli errori sono nei dettagli per pagina.</p>'
    header += '<pre>' + html.escape(json.dumps(summary, ensure_ascii=False, indent=2)) + '</pre>'
    (root / "index.html").write_text('<!doctype html><html lang="it"><meta charset="utf-8"><title>Confronto OCR</title><style>' + CSS + '</style>' + header + '<table><tr><th>N.</th><th>Documento</th><th>Pagine</th><th>Confronti</th></tr>' + ''.join(rows) + '</table></html>')
    return summary


def batch(args):
    root = Path("/data")
    started = time.monotonic()
    write(root / "status.json", {"status": "running", "started_utc": dt.datetime.now(dt.timezone.utc).isoformat()})
    failure = None
    try:
        versions = {name: importlib.metadata.version(name) for name in ("paddleocr", "paddlex", "torch", "transformers", "pypdfium2")}
        versions["tesseract"] = subprocess.check_output(["tesseract", "--version"], text=True).splitlines()[0]
        versions["script_sha256"] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
        write(root / "versions.json", versions)
        for doc in read(root / "manifest.json")["documents"]:
            process_document(root / doc["number"])
            report(root)
    except Exception as exc:
        failure = type(exc).__name__
        print("Interruzione: " + str(exc), flush=True)
    summary = report(root)
    summary["wall_seconds"] = round(time.monotonic() - started, 2)
    write(root / "summary.json", summary)
    write(root / "status.json", {"status": "interrupted" if failure else "finished",
                                "error_type": failure, "summary": summary})
    space(root)
    archive = root / "nubarca-ocr-20pdf.zip"
    with zipfile.ZipFile(archive.with_suffix(".zip.tmp"), "w", zipfile.ZIP_DEFLATED, allowZip64=True) as out:
        for path in sorted(root.rglob("*")):
            if path.is_file() and path.suffix not in (".zip", ".tmp", ".log") and path.name != "event.json":
                out.write(path, str(path.relative_to(root)))
    archive.with_suffix(".zip.tmp").replace(archive)
    print(json.dumps(summary, ensure_ascii=False, indent=2), flush=True)
    print("Archivio pronto: " + str(archive), flush=True)


def host(args):
    os.umask(0o077)
    root = Path(args.root).resolve()
    models = root / "models"
    lock = read(root / "full-pipeline" / "models.lock.json")
    if lock.get("ocr", {}).get("revision") != OCR_REV or lock.get("layout", {}).get("revision") != LAYOUT_REV:
        raise RuntimeError("Revisioni dei modelli diverse da quelle del confronto precedente")
    for model in ("paddleocr-vl-1.6", "pp-doclayoutv3-safetensors"):
        if not (models / model / "config.json").is_file():
            raise RuntimeError("Modello assente: " + model)
    run(["docker", "image", "inspect", BASE], stdout=subprocess.DEVNULL)
    package = root / "real-batch-tool"
    package.mkdir(exist_ok=True)
    script = package / "ocr-real.py"
    if Path(__file__).resolve() != script:
        shutil.copyfile(__file__, script)
    dockerfile = package / "Dockerfile"
    dockerfile.write_text("FROM " + BASE + "\nUSER root\nRUN apt-get update && apt-get install -y --no-install-recommends tesseract-ocr tesseract-ocr-ita tesseract-ocr-eng && rm -rf /var/lib/apt/lists/*\nUSER bench\n")
    run(["docker", "build", "-t", IMAGE, str(package)], env=dict(os.environ, DOCKER_BUILDKIT="0"))
    if args.action == "start":
        run_id = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        data = root / "real-batch" / run_id
        data.mkdir(parents=True)
    else:
        data = Path(args.run).resolve()
        if not (data / "manifest.json").is_file():
            raise RuntimeError("Cartella di prova non valida")
        run_id = data.name
    space(data)
    common = ["docker", "run", "--user", f"{os.getuid()}:{os.getgid()}",
              "--cpuset-cpus", args.cpus, "--memory", "8g", "--memory-swap", "8g",
              "-e", "HOME=/tmp", "-e", "HF_HOME=/tmp/hf", "-e", "HF_HUB_OFFLINE=1",
              "-e", "TRANSFORMERS_OFFLINE=1", "-e", "OMP_NUM_THREADS=6",
              "-e", "MKL_NUM_THREADS=6", "-e", "OPENBLAS_NUM_THREADS=6",
              "-e", "TOKENIZERS_PARALLELISM=false", "-v", f"{script}:/tool/ocr-real.py:ro",
              "-v", f"{data}:/data"]
    if args.action == "start":
        run(common + ["--rm", "-it", "--network", "container:" + args.api_container,
                      "--entrypoint", "python", IMAGE, "/tool/ocr-real.py", "export",
                      "--email", args.email, "--count", str(args.count), "--api", args.api])
    write(data / "models.lock.json", lock)
    provenance = {"base_image": json.loads(subprocess.check_output(["docker", "image", "inspect", BASE]))[0]["Id"],
                  "batch_image": json.loads(subprocess.check_output(["docker", "image", "inspect", IMAGE]))[0]["Id"],
                  "cpu_affinity": args.cpus, "memory_limit_gib": 8, "tool_version": VERSION}
    write(data / "environment.json", provenance)
    name = "nubarca-ocr-real-" + run_id.lower() + "-" + str(int(time.time()))
    run(common + ["-d", "--network", "none", "--name", name,
                  "--log-opt", "max-size=20m", "--log-opt", "max-file=2",
                  "-v", f"{models}:/models:ro", "--entrypoint", "python", IMAGE,
                  "-u", "/tool/ocr-real.py", "batch"])
    (root / "real-batch" / "LAST_RUN").write_text(str(data) + "\n")
    alias = root / "real-batch" / "nubarca-ocr-20pdf.zip"
    if alias.is_symlink():
        alias.unlink()
    if not alias.exists():
        alias.symlink_to(data / "nubarca-ocr-20pdf.zip")
    print("\nProva avviata in background. Puoi chiudere PuTTY.")
    print("Segui: docker logs -f " + name)
    print("Risultato: " + str(data / "nubarca-ocr-20pdf.zip"))
    print("Stato: cat " + str(data / "status.json"))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="action", required=True)
    for action in ("start", "resume"):
        p = sub.add_parser(action)
        p.add_argument("--root", required=True)
        p.add_argument("--cpus", default="0,2,4,6,8,10")
        p.add_argument("--email", required=action == "start")
        p.add_argument("--count", type=int, default=20)
        p.add_argument("--api-container", default="nubarca-api")
        p.add_argument("--api", default="http://localhost:8080")
        p.add_argument("--run", required=action == "resume")
    p = sub.add_parser("export")
    p.add_argument("--email", required=True)
    p.add_argument("--count", type=int, required=True)
    p.add_argument("--api", required=True)
    p = sub.add_parser("inspect")
    p.add_argument("path")
    p = sub.add_parser("worker")
    p.add_argument("folder")
    sub.add_parser("batch")
    args = parser.parse_args()
    if args.action in ("start", "resume"):
        host(args)
    elif args.action == "export":
        export(args)
    elif args.action == "inspect":
        print(json.dumps(inspect_pdf(args.path)))
    elif args.action == "worker":
        paddle_worker(args)
    else:
        batch(args)


if __name__ == "__main__":
    main()
