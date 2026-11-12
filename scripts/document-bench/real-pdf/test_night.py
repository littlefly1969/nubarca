import importlib.util
from pathlib import Path
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location("night", Path(__file__).with_name("night.py"))
night = importlib.util.module_from_spec(spec)
spec.loader.exec_module(night)


class NightReport(unittest.TestCase):
    def test_partial_archive_includes_original_and_safe_fourth_panel(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            page = root / "001" / "p0001"
            page.mkdir(parents=True)
            (root / "001" / "originale.pdf").write_bytes(b"test-original")
            night.base.write(root / "manifest.json", {"requested": 20, "documents": [{"number": "001", "name": "invoice.pdf", "pages": 1}]})
            for engine in ("tesseract", "paddle"):
                night.base.write(page / (engine + ".json"), {"status": "ok", "seconds": 1})
            night.base.write(page / "xiaomi.json", {"status": "truncated", "seconds": 5})
            (page / "xiaomi.md").write_text('<table onclick="evil"><tr><td>42</td></tr></table><img src="http://evil">')
            (page / "worker.log").write_text("not for archive")
            summary = night.pack(root)
            self.assertEqual(summary["engines"]["xiaomi"]["errors_or_incomplete"], 1)
            rendered = (page / "confronto.html").read_text()
            self.assertIn("Xiaomi — pagina intera", rendered)
            self.assertIn("<td>42</td>", rendered)
            self.assertNotIn('onclick="evil"', rendered)
            self.assertNotIn('<img src="http', rendered)
            with zipfile.ZipFile(root / "nubarca-ocr-notte.zip") as archive:
                self.assertIn("001/originale.pdf", archive.namelist())
                self.assertIn("001/p0001/confronto.html", archive.namelist())
                self.assertNotIn("001/p0001/worker.log", archive.namelist())
            # Regenerating a partial report must not duplicate the Xiaomi panel.
            night.pack(root, archive=False)
            self.assertEqual((page / "confronto.html").read_text().count("Xiaomi — pagina intera"), 1)


if __name__ == "__main__":
    unittest.main()
