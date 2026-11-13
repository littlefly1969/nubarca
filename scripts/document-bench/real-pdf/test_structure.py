import copy
import importlib.util
from pathlib import Path
import tempfile
import unittest
import zipfile

import yaml

spec = importlib.util.spec_from_file_location("structure", Path(__file__).with_name("structure.py"))
structure = importlib.util.module_from_spec(spec)
spec.loader.exec_module(structure)


class StructureContracts(unittest.TestCase):
    def test_cpu_profile_keeps_tables_orientation_footers_and_local_models(self):
        original = yaml.safe_load(Path(__file__).with_name("structure-fixture.yaml").read_text())
        before = copy.deepcopy(original)
        config = structure.config_for_cpu(original, Path("/models"))
        self.assertEqual(original, before)
        self.assertEqual(config["markdown_ignore_labels"], [])
        self.assertTrue(config["use_table_recognition"])
        self.assertFalse(config["use_formula_recognition"])
        self.assertFalse(config["use_chart_recognition"])
        self.assertFalse(config["use_region_detection"])
        self.assertTrue(config["SubPipelines"]["DocPreprocessor"]["use_doc_orientation_classify"])
        self.assertFalse(config["SubPipelines"]["DocPreprocessor"]["use_doc_unwarping"])
        self.assertIsInstance(config["SubModules"]["LayoutDetection"]["threshold"], float)
        table = config["SubPipelines"]["TableRecognition"]
        self.assertEqual(table["SubModules"]["WiredTableStructureRecognition"]["model_name"], "SLANet_plus")
        required = [config["SubModules"]["LayoutDetection"],
                    config["SubPipelines"]["DocPreprocessor"]["SubModules"]["DocOrientationClassify"]]
        required.extend(config["SubPipelines"]["GeneralOCR"]["SubModules"].values())
        required.extend(table["SubModules"].values())
        required.extend(table["SubPipelines"]["GeneralOCR"]["SubModules"].values())
        for node in required:
            self.assertIn(node["model_name"], structure.NAMES)
            self.assertEqual(node["model_dir"], "/models/" + node["model_name"])

    def test_partial_report_and_zip_preserve_existing_results_and_escape_html(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            page = root / "001" / "p0001"
            page.mkdir(parents=True)
            structure.base.write(root / "manifest.json", {"documents": [{"number": "001", "name": "<script>bad</script>.pdf", "pages": 2}]})
            (page / "confronto.html").write_text("OLD COMPARISON")
            (root / "index.html").write_text("OLD INDEX")
            structure.base.write(page / "ppstructure.json", {"status": "ok", "seconds": 3, "tables": 1})
            structure.base.write(page / "ppstructure-blocks.json", ['<table onclick="bad"><tr><td>42</td></tr></table><script>bad</script>'])
            summary = structure.report(root, archive=True)
            self.assertEqual(summary["ok"], 1)
            self.assertEqual(summary["errors_or_pending"], 1)
            self.assertEqual(summary["seconds_per_successful_page"], 3)
            self.assertEqual((root / "index.html").read_text(), "OLD INDEX")
            self.assertEqual((page / "confronto.html").read_text(), "OLD COMPARISON")
            rendered = (page / "ppstructure.html").read_text()
            self.assertNotIn("<script>", rendered)
            self.assertNotIn('onclick="bad"', rendered)
            self.assertIn("<td>42</td>", rendered)
            with zipfile.ZipFile(root / "nubarca-ppstructure.zip") as archive:
                self.assertIn("index-ppstructure.html", archive.namelist())
                self.assertIn("001/p0002/ppstructure.html", archive.namelist())


if __name__ == "__main__":
    unittest.main()
