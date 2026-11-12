"""Local contract tests; no production service or AI model is needed."""
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

from PIL import Image
from reportlab.pdfgen import canvas
from reportlab.lib.utils import ImageReader

spec = importlib.util.spec_from_file_location("ocr_real", Path(__file__).with_name("ocr-real.py"))
tool = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tool)


def make_pdf(path, pages):
    image = Image.new("RGB", (200, 200), "white")
    pdf = canvas.Canvas(str(path))
    for digital in pages:
        pdf.drawImage(ImageReader(image), 20, 20, 200, 200)
        if digital:
            pdf.drawString(20, 250, "0650486E03")
        pdf.showPage()
    pdf.save()


class Contracts(unittest.TestCase):
    def test_all_pages_must_be_scans_and_same_raster_is_saved(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            scan, mixed = root / "scan.pdf", root / "mixed.pdf"
            make_pdf(scan, [False, False])
            make_pdf(mixed, [False, True])
            self.assertEqual(tool.inspect_pdf(scan), {"scanned": True, "pages": 2})
            self.assertFalse(tool.inspect_pdf(mixed)["scanned"])
            details = tool.render(scan, 1, root / "page.png")
            self.assertEqual(details["dpi"], 200)
            self.assertTrue((root / "page.png").is_file())

    def test_owner_login_download_filter_and_no_credentials_persisted(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            scan, mixed = root / "scan.pdf", root / "mixed.pdf"
            make_pdf(scan, [False])
            make_pdf(mixed, [False, True])
            payloads = [mixed.read_bytes(), scan.read_bytes(), scan.read_bytes()]
            ids = [f"00000000-0000-0000-0000-{i:012d}" for i in range(3)]
            calls = []
            class Response:
                status_code = 200
                def __init__(self, value=None, binary=None):
                    self.value, self.binary = value, binary
                def json(self): return self.value
                def raise_for_status(self): pass
                def iter_content(self, _): yield self.binary
                def __enter__(self): return self
                def __exit__(self, *args): pass
            class Session:
                def post(self, url, **kwargs):
                    calls.append(url)
                    return Response()
                def get(self, url, **kwargs):
                    calls.append(url)
                    if url.endswith("/me"):
                        return Response({"email": "owner@example.test"})
                    if url.endswith("/search"):
                        self.assertion = kwargs["params"] == {"q": "pdf"}
                        return Response([{"id": i, "name": "invoice.pdf", "mimeType": "application/pdf"} for i in ids])
                    return Response(binary=payloads[ids.index(url.split("/")[-2])])
                def close(self): pass
            requests = types.SimpleNamespace(Session=Session, RequestException=ConnectionError)
            real_path = tool.Path
            def redirected(path):
                return root / "out" if path == "/data" else real_path(path)
            (root / "out").mkdir()
            args = types.SimpleNamespace(api="http://localhost:8080", email="owner@example.test", count=20)
            with patch.dict(sys.modules, requests=requests), patch.object(tool, "Path", redirected), patch.object(tool.getpass, "getpass", return_value="SECRET"), patch.object(tool, "space"):
                tool.export(args)
            manifest = json.loads((root / "out" / "manifest.json").read_text())
            self.assertEqual(len(manifest["documents"]), 1)
            self.assertEqual(manifest["excluded"]["copia_identica"], 1)
            self.assertEqual(manifest["excluded"]["contiene_testo_anche_solo_in_una_pagina"], 1)
            self.assertTrue(calls[-1].endswith("/logout"))
            self.assertFalse(any("SECRET" in p.read_text(errors="ignore") for p in (root / "out").rglob("*") if p.is_file()))

    def test_html_escapes_names_text_and_model_markup(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            page = root / "001" / "p0001"
            page.mkdir(parents=True)
            tool.write(root / "manifest.json", {"requested": 20, "documents": [{"number": "001", "name": '<script>alert(1)</script>.pdf', "pages": 1}]})
            tool.write(page / "tesseract.json", {"status": "ok", "seconds": 1, "characters": 10})
            tool.write(page / "paddle.json", {"status": "timeout"})
            (page / "tesseract.txt").write_text('<img src="https://evil.example" onerror="x">')
            tool.write(page / "blocks.json", ['<table onclick="x"><tr><td colspan="2">42<img src="https://evil.example"></td></tr></table><script>alert(1)</script>'])
            summary = tool.report(root)
            rendered = (page / "confronto.html").read_text()
            self.assertNotIn("<script>", rendered)
            self.assertNotIn('onclick="x"', rendered)
            self.assertNotIn('<img src="https://', rendered)
            self.assertIn('<td colspan="2">42', rendered)
            self.assertEqual(summary["engines"]["paddle"]["errors"], 1)
            self.assertIn("Nessuna misura di accuratezza", summary["note"])

    def test_resume_does_not_repeat_completed_tesseract_or_paddle(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            page = root / "001" / "p0001"
            page.mkdir(parents=True)
            tool.write(root / "manifest.json", {"documents": [{"number": "001", "pages": 1}]})
            for name in ("render", "tesseract", "paddle"):
                tool.write(page / (name + ".json"), {"status": "ok"})
            with patch.object(tool, "space"), patch.object(tool.subprocess, "run") as run, patch.object(tool.subprocess, "Popen") as popen, patch.object(tool, "render") as render:
                tool.process_document(root / "001")
            run.assert_not_called()
            popen.assert_not_called()
            render.assert_not_called()

    def test_timeout_records_failed_page_and_continues(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            folder = root / "001"
            page = folder / "p0001"
            page.mkdir(parents=True)
            tool.write(root / "manifest.json", {"documents": [{"number": "001", "pages": 1}]})
            for name in ("render", "tesseract"):
                tool.write(page / (name + ".json"), {"status": "ok"})
            class Process:
                pid = 99999
                returncode = -9
                def __init__(self, *args, **kwargs):
                    tool.write(folder / "event.json", {"stage": "page", "page": "p0001", "at": 0})
                def poll(self): return None
                def wait(self): return -9
            with patch.object(tool, "space"), patch.object(tool.subprocess, "Popen", Process), patch.object(tool.os, "killpg") as kill:
                tool.process_document(folder)
            kill.assert_called_once()
            self.assertEqual(tool.read(page / "paddle.json")["status"], "timeout")


if __name__ == "__main__":
    unittest.main()
