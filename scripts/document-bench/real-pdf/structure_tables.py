#!/usr/bin/env python3
"""PP-StructureV3 table/cell comparison profile, isolated from production and previous OCR results."""
import argparse
import copy
import hashlib
import html
import importlib.metadata
import importlib.util
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import time
import zipfile

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("ocr_real", HERE / "ocr-real.py")
base = importlib.util.module_from_spec(spec)
spec.loader.exec_module(base)
IMAGE = "nubarca-ppstructure:ocr3.7.0-paddle3.2.2-cpu-v2"
NAMES = ("PP-DocLayout-S", "PP-OCRv5_mobile_det", "latin_PP-OCRv5_mobile_rec",
         "PP-LCNet_x1_0_doc_ori", "PP-LCNet_x1_0_textline_ori", "PP-LCNet_x1_0_table_cls",
         "SLANet_plus", "SLANeXt_wired", "RT-DETR-L_wired_table_cell_det", "RT-DETR-L_wireless_table_cell_det")


def config_for_cpu(original, models):
    config = copy.deepcopy(original)
    config.update(batch_size=1, use_doc_preprocessor=True, use_table_recognition=True,
                  use_formula_recognition=False, use_chart_recognition=False,
                  use_seal_recognition=False, use_region_detection=False, markdown_ignore_labels=[])
    def model(node, name):
        node.update(model_name=name, model_dir=str(models / name), batch_size=1)
    model(config["SubModules"]["LayoutDetection"], "PP-DocLayout-S")
    # Small layout model has different class indices: do not reuse plus-L's thresholds by index.
    config["SubModules"]["LayoutDetection"].update(threshold=0.5, layout_merge_bboxes_mode="union")
    doc = config["SubPipelines"]["DocPreprocessor"]
    doc.update(batch_size=1, use_doc_orientation_classify=True, use_doc_unwarping=False)
    model(doc["SubModules"]["DocOrientationClassify"], "PP-LCNet_x1_0_doc_ori")
    def ocr(node):
        node.update(batch_size=1, use_doc_preprocessor=False, use_textline_orientation=True)
        model(node["SubModules"]["TextDetection"], "PP-OCRv5_mobile_det")
        node["SubModules"]["TextDetection"].update(limit_side_len=1280, limit_type="max")
        model(node["SubModules"]["TextRecognition"], "latin_PP-OCRv5_mobile_rec")
        model(node["SubModules"]["TextLineOrientation"], "PP-LCNet_x1_0_textline_ori")
    ocr(config["SubPipelines"]["GeneralOCR"])
    table = config["SubPipelines"]["TableRecognition"]
    table.update(use_layout_detection=False, use_doc_preprocessor=False, use_ocr_model=False)
    for key, name in {
        "TableClassification": "PP-LCNet_x1_0_table_cls",
        "WiredTableStructureRecognition": "SLANeXt_wired",
        "WirelessTableStructureRecognition": "SLANet_plus",
        "WiredTableCellsDetection": "RT-DETR-L_wired_table_cell_det",
        "WirelessTableCellsDetection": "RT-DETR-L_wireless_table_cell_det",
        "TableOrientationClassify": "PP-LCNet_x1_0_doc_ori",
    }.items():
        model(table["SubModules"][key], name)
    ocr(table["SubPipelines"]["GeneralOCR"])
    return config


def fetch():
    from huggingface_hub import HfApi, snapshot_download
    models = Path("/models")
    lock = base.read(models / "PINNED.json") if (models / "PINNED.json").exists() else {}
    for name in NAMES:
        repo = "PaddlePaddle/" + name
        revision = lock.get(name, {}).get("revision") or HfApi().model_info(repo).sha
        print("Download modello " + name + " @ " + revision[:12], flush=True)
        snapshot_download(repo, revision=revision, local_dir=str(models / name),
                          allow_patterns=["inference.*", "*.json", "*.yaml", "*.yml", "*.pdiparams", "*.pdmodel", "*.txt"])
        folder = models / name
        if not (folder / "inference.pdiparams").exists() or not (folder / "inference.yml").exists():
            raise RuntimeError("Formato Paddle nativo incompleto: " + name)
        lock[name] = {"repo": repo, "revision": revision}
        base.write(models / "PINNED.json", lock)
    print("MODELLI SCARICATI", flush=True)


def create_pipeline():
    from paddleocr import PPStructureV3
    from paddlex.inference import load_pipeline_config
    config = config_for_cpu(load_pipeline_config("PP-StructureV3"), Path("/models"))
    pipeline = PPStructureV3(paddlex_config=config, device="cpu", engine="paddle_static",
                             cpu_threads=6, enable_mkldnn=True, enable_cinn=False)
    return pipeline, config


def process_page(pipeline, page, loaded):
    import resource
    native = page / "pptables-native"
    native.mkdir(exist_ok=True)
    started = time.monotonic()
    result = list(pipeline.predict(str(page / "pagina.png"),
        use_e2e_wired_table_rec_model=False, use_e2e_wireless_table_rec_model=False,
        use_table_orientation_classify=True, use_ocr_results_with_table_cells=True))
    seconds = time.monotonic() - started
    if len(result) != 1:
        raise RuntimeError("Numero di risultati inatteso")
    result[0].save_to_json(save_path=str(native))
    result[0].save_to_markdown(save_path=str(native))
    blocks = None
    for source in native.glob("*.json"):
        blocks = base.extract_blocks(base.read(source))
        if blocks is not None:
            break
    if blocks is None:
        raise RuntimeError("Blocchi di lettura mancanti nel risultato nativo")
    contents = [str(b.get("block_content", "")) for b in blocks]
    base.write(page / "pptables-blocks.json", contents)
    texts = []
    for content in contents:
        if "<table" in content.lower():
            plain = base.Plain()
            plain.feed(content)
            texts.append("".join(plain.parts))
        else:
            texts.append(content)
    (page / "pptables.txt").write_text("\n\n".join(texts))
    base.write(page / "pptables.json", {"status": "ok", "seconds": round(seconds, 3),
        "characters": sum(len(t) for t in texts), "tables": sum(b.get("block_label") == "table" for b in blocks),
        "load_seconds_shared_per_document": round(loaded, 3),
        "peak_process_ram_mib": round(resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024, 1)})
    print(f"PAGINA COMPLETATA {page.parent.name}/{page.name}: {seconds:.2f} secondi", flush=True)


def worker(folder, first_only=False):
    event = folder / "pptables-event.json"
    base.write(event, {"stage": "loading", "at": time.time()})
    started = time.monotonic()
    pipeline, config = create_pipeline()
    loaded = time.monotonic() - started
    base.write(folder.parent / "pptables-config.json", config)
    try:
        doc = next(d for d in base.read(folder.parent / "manifest.json")["documents"] if d["number"] == folder.name)
        for index in range(1 if first_only else doc["pages"]):
            base.space(folder)
            page = folder / f"p{index + 1:04d}"
            page.mkdir(exist_ok=True)
            if (page / "pptables.json").exists():
                continue
            base.write(event, {"stage": "page", "page": page.name, "at": time.time()})
            try:
                if not (page / "pagina.png").exists():
                    base.write(page / "render.json", base.render(folder / "originale.pdf", index, page / "pagina.png"))
                process_page(pipeline, page, loaded)
            except Exception as exc:
                base.write(page / "pptables.json", {"status": "error", "error_type": type(exc).__name__})
                print(type(exc).__name__ + ": " + str(exc), flush=True)
                if first_only:
                    raise
            base.write(event, {"stage": "between_pages", "at": time.time()})
    finally:
        pipeline.close()
    base.write(event, {"stage": "done", "at": time.time()})


def supervise(folder, pages):
    while True:
        pending = [folder / f"p{i+1:04d}" for i in range(pages)
                   if not (folder / f"p{i+1:04d}" / "pptables.json").exists()]
        if not pending:
            return
        event = folder / "pptables-event.json"
        event.unlink(missing_ok=True)
        with (folder / "pptables-worker.log").open("a") as log:
            child = subprocess.Popen([sys.executable, __file__, "worker", "--run", str(folder)],
                                     stdout=log, stderr=log, start_new_session=True)
            started, previous, timeout = time.time(), None, False
            while child.poll() is None:
                state = base.read(event) if event.exists() else {"stage": "loading", "at": started}
                current = (state.get("stage"), state.get("page"))
                if current != previous:
                    print(f"PP-Structure PDF {folder.name}: {current}", flush=True)
                    previous = current
                if time.time() - state["at"] > 300:
                    timeout = True
                    os.killpg(child.pid, signal.SIGKILL)
                    break
                time.sleep(2)
            child.wait()
        remaining = [p for p in pending if not (p / "pptables.json").exists()]
        if remaining:
            state = base.read(event) if event.exists() else {}
            affected = remaining if state.get("stage") == "loading" or not state else remaining[:1]
            for page in affected:
                page.mkdir(exist_ok=True)
                base.write(page / "pptables.json", {"status": "timeout" if timeout else "worker_error", "exit_code": child.returncode})


def report(root, archive=False):
    manifest = base.read(root / "manifest.json")
    summary = {"documents": len(manifest["documents"]), "pages": sum(d["pages"] for d in manifest["documents"]),
        "ok": 0, "errors_or_pending": 0, "seconds": 0, "tables": 0,
        "profile": "CPU: mobile OCR latino, layout S, SLANeXt_wired + SLANet_plus + rilevamento celle RT-DETR per entrambi i tipi, orientamento attivo; nessuna formula/grafico/timbro; intestazioni e pie di pagina conservati.",
        "note": "Profilo personalizzato: non replica il benchmark ufficiale. Nessuna accuratezza misurabile senza trascrizioni di riferimento."}
    rows = []
    comparison = {"common_successful_pages": 0, "light_seconds": 0, "table_cells_seconds": 0}
    for doc in manifest["documents"]:
        links = []
        for index in range(doc["pages"]):
            page = root / doc["number"] / f"p{index+1:04d}"
            page.mkdir(exist_ok=True)
            info = base.read(page / "pptables.json") if (page / "pptables.json").exists() else {"status": "pending"}
            summary["ok" if info["status"] == "ok" else "errors_or_pending"] += 1
            summary["seconds"] += info.get("seconds", 0)
            summary["tables"] += info.get("tables", 0)
            baseline = base.read(page / "ppstructure.json") if (page / "ppstructure.json").exists() else {"status": "missing"}
            if baseline["status"] == "ok" and info["status"] == "ok":
                comparison["common_successful_pages"] += 1
                comparison["light_seconds"] += baseline.get("seconds", 0)
                comparison["table_cells_seconds"] += info.get("seconds", 0)
            panels = []
            for block in base.read(page / "pptables-blocks.json") if (page / "pptables-blocks.json").exists() else []:
                if "<table" in block.lower():
                    parser = base.SafeTable()
                    parser.feed(block)
                    panels.append("".join(parser.parts))
                else:
                    panels.append('<div class="block">' + html.escape(block) + '</div>')
            old_panels = []
            for block in base.read(page / "ppstructure-blocks.json") if (page / "ppstructure-blocks.json").exists() else []:
                if "<table" in block.lower():
                    parser = base.SafeTable()
                    parser.feed(block)
                    old_panels.append("".join(parser.parts))
                else:
                    old_panels.append('<div class="block">' + html.escape(block) + '</div>')
            title = html.escape(doc["name"]) + " — " + page.name
            compare = '<a href="confronto.html">Confronto precedente</a> · ' if (page / "confronto.html").exists() else ""
            body = '<h1>' + title + '</h1><p>' + compare + '<a href="../../index-pptables.html">Indice PP-Structure</a> · <a href="../originale.pdf">PDF originale</a></p>'
            body += '<div class="grid"><section><h2>Scansione originale</h2><a href="pagina.png"><img src="pagina.png" alt="Scansione"></a></section><section><h2>Profilo leggero precedente</h2><p>' + html.escape(str(baseline)) + '</p>' + ''.join(old_panels) + '</section><section><h2>Tabelle con rilevamento celle</h2><p>' + html.escape(str(info)) + '</p>' + ''.join(panels) + '</section></div>'
            (page / "pptables.html").write_text('<!doctype html><html lang="it"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>PP-Structure tabelle</title><style>' + base.CSS + '</style>' + body + '</html>')
            links.append(f'<a href="{doc["number"]}/{page.name}/pptables.html">{page.name}: {html.escape(info["status"])}</a>')
        rows.append('<tr><td>' + html.escape(doc["name"]) + '</td><td>' + ' · '.join(links) + '</td></tr>')
    summary["seconds"] = round(summary["seconds"], 2)
    summary["seconds_per_successful_page"] = round(summary["seconds"] / summary["ok"], 2) if summary["ok"] else None
    comparison["time_ratio_table_cells_over_light"] = round(comparison["table_cells_seconds"] / comparison["light_seconds"], 3) if comparison["light_seconds"] else None
    for key in ("light_seconds", "table_cells_seconds"):
        comparison[key] = round(comparison[key], 3)
    summary["comparison_on_same_successful_pages"] = comparison
    base.write(root / "pptables-summary.json", summary)
    (root / "index-pptables.html").write_text('<!doctype html><html lang="it"><meta charset="utf-8"><title>PP-StructureV3 CPU</title><style>' + base.CSS + '</style><h1>PP-StructureV3 — prova CPU</h1><p><a href="index.html">Risultati precedenti, se disponibili</a></p><pre>' + html.escape(json.dumps(summary, ensure_ascii=False, indent=2)) + '</pre><table>' + ''.join(rows) + '</table></html>')
    if archive:
        base.space(root)
        output = root / "nubarca-pptables.zip"
        with zipfile.ZipFile(output.with_suffix(".zip.tmp"), "w", zipfile.ZIP_DEFLATED, allowZip64=True) as zipped:
            for path in sorted(root.rglob("*")):
                if path.is_file() and path.suffix not in (".zip", ".tmp", ".log") and not path.name.endswith("event.json"):
                    zipped.write(path, str(path.relative_to(root)))
        output.with_suffix(".zip.tmp").replace(output)
        print("ARCHIVIO PRONTO: " + str(output), flush=True)
    return summary


def batch(root):
    base.write(root / "pptables-versions.json", {name: importlib.metadata.version(name) for name in ("paddleocr", "paddlex", "paddlepaddle")})
    failure = None
    base.write(root / "pptables-status.json", {"status": "running"})
    try:
        for doc in base.read(root / "manifest.json")["documents"]:
            supervise(root / doc["number"], doc["pages"])
            report(root)
    except Exception as exc:
        failure = type(exc).__name__
        print(str(exc), flush=True)
    finally:
        summary = report(root, archive=True)
        base.write(root / "pptables-status.json", {"status": "interrupted" if failure else "finished", "error_type": failure, "summary": summary})
        print(json.dumps(summary, ensure_ascii=False, indent=2), flush=True)


def host(args):
    root = Path(args.root).resolve()
    active = subprocess.check_output(["docker", "ps", "--format", "{{.Names}}"], text=True).splitlines()
    if any(n.startswith(("nubarca-ocr-real-", "nubarca-ocr-night-", "nubarca-ppstructure-", "nubarca-pptables-")) for n in active):
        raise RuntimeError("Un benchmark OCR e gia attivo: attendi o fermalo prima di avviare questo")
    data = Path((root / "real-batch" / "LAST_RUN").read_text().strip())
    manifest = base.read(data / "manifest.json")
    if not manifest["documents"]:
        raise RuntimeError("Campione PDF vuoto")
    for doc in manifest["documents"]:
        for path in (data / doc["number"]).glob("p[0-9][0-9][0-9][0-9]/pptables.json"):
            if base.read(path).get("status") != "ok":
                path.replace(path.with_name("pptables.previous-" + str(int(time.time())) + ".json"))
    base.space(root)
    build = root / "ppstructure-build"
    build.mkdir(exist_ok=True)
    (build / "Dockerfile").write_text('FROM nubarca-paddle-full:ocr3.7.0-pdx3.7.2\nUSER root\nRUN python -m pip install --no-cache-dir "paddlepaddle==3.2.2" "paddlex[ocr]==3.7.2" "paddleocr[doc-parser]==3.7.0" && python -m pip check\nUSER bench\n')
    base.run(["docker", "build", "-t", IMAGE, str(build)], env=dict(os.environ, DOCKER_BUILDKIT="0"))
    models = root / "models" / "ppstructure-cpu-v1"
    models.mkdir(exist_ok=True)
    shared = ["docker", "run", "--user", f"{os.getuid()}:{os.getgid()}", "--cpuset-cpus", "0,2,4,6,8,10",
        "--memory", "8g", "--memory-swap", "8g", "-e", "HOME=/tmp", "-e", "HF_HOME=/tmp/hf",
        "-e", "OMP_NUM_THREADS=6", "-e", "MKL_NUM_THREADS=6", "-e", "OPENBLAS_NUM_THREADS=6",
        "-e", "PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK=True", "-v", f"{HERE}:/tool:ro"]
    base.run(shared + ["--rm", "-v", f"{models}:/models", "--entrypoint", "python", IMAGE, "-u", "/tool/structure_tables.py", "fetch"])
    offline = shared + ["--network", "none", "-e", "HF_HUB_OFFLINE=1", "-e", "TRANSFORMERS_OFFLINE=1",
                       "-v", f"{models}:/models:ro", "-v", f"{data}:/data"]
    first = manifest["documents"][0]["number"]
    print("CONTROLLO OFFLINE DELLA PRIMA PAGINA...", flush=True)
    base.run(offline + ["--rm", "--entrypoint", "/usr/bin/timeout", IMAGE, "--signal=TERM", "--kill-after=20s", "5m",
                        "python", "-u", "/tool/structure_tables.py", "smoke", "--run", "/data/" + first])
    info = base.read(data / first / "p0001" / "pptables.json")
    if info["status"] != "ok":
        raise RuntimeError("Prima pagina non riuscita: nessuna prova notturna avviata")
    base.write(data / "pptables-models.lock.json", base.read(models / "PINNED.json"))
    image_id = json.loads(subprocess.check_output(["docker", "image", "inspect", IMAGE]))[0]["Id"]
    base.write(data / "pptables-environment.json", {"image": image_id, "cpu_affinity": "0,2,4,6,8,10", "memory_limit_gib": 8,
        "script_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest()})
    name = "nubarca-pptables-" + str(int(time.time()))
    base.run(offline + ["-d", "--name", name, "--log-opt", "max-size=20m", "--log-opt", "max-file=2",
        "--entrypoint", "python", IMAGE, "-u", "/tool/structure_tables.py", "batch", "--run", "/data"])
    alias = root / "real-batch" / "nubarca-pptables.zip"
    if alias.is_symlink():
        alias.unlink()
    if not alias.exists():
        alias.symlink_to(data / "nubarca-pptables.zip")
    print(f"\nPP-TABELLE AVVIATO: {len(manifest['documents'])} PDF. Prima pagina: {info['seconds']} secondi.", flush=True)
    print("Segui: docker logs -f " + name)
    print("ZIP finale: " + str(alias))


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("start", "fetch", "smoke", "worker", "batch", "pack"))
    parser.add_argument("--root")
    parser.add_argument("--run")
    args = parser.parse_args()
    if args.action == "start":
        host(args)
    elif args.action == "fetch":
        fetch()
    elif args.action in ("worker", "smoke"):
        worker(Path(args.run), first_only=args.action == "smoke")
    elif args.action == "pack":
        report(Path(args.run), archive=True)
    else:
        batch(Path(args.run))


if __name__ == "__main__":
    main()
