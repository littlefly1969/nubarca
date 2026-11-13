import importlib.util
from pathlib import Path
import tempfile
import unittest
import yaml

def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

light = load("light", "structure.py")
tables = load("tables", "structure_tables.py")


class TableComparison(unittest.TestCase):
    def test_profile_changes_only_wired_structure_model(self):
        original = yaml.safe_load(Path(__file__).with_name("structure-fixture.yaml").read_text())
        old = light.config_for_cpu(original, Path("/models"))
        new = tables.config_for_cpu(original, Path("/models"))
        old_node = old["SubPipelines"]["TableRecognition"]["SubModules"]["WiredTableStructureRecognition"]
        new_node = new["SubPipelines"]["TableRecognition"]["SubModules"]["WiredTableStructureRecognition"]
        self.assertEqual(old_node["model_name"], "SLANet_plus")
        self.assertEqual(new_node["model_name"], "SLANeXt_wired")
        old["SubPipelines"]["TableRecognition"]["SubModules"]["WiredTableStructureRecognition"] = new_node
        self.assertEqual(old, new)

    def test_prediction_enables_cell_detector_paths_for_both_types(self):
        with tempfile.TemporaryDirectory() as directory:
            page = Path(directory) / "001" / "p0001"
            page.mkdir(parents=True)
            calls = []
            class Result:
                def save_to_json(self, save_path):
                    tables.base.write(Path(save_path) / "native.json", {"parsing_res_list": [{"block_label": "table", "block_content": "<table><tr><td>42</td></tr></table>"}]})
                def save_to_markdown(self, save_path): pass
            class Pipeline:
                def predict(self, source, **kwargs):
                    calls.append(kwargs)
                    return [Result()]
            tables.process_page(Pipeline(), page, 1)
            self.assertFalse(calls[0]["use_e2e_wired_table_rec_model"])
            self.assertFalse(calls[0]["use_e2e_wireless_table_rec_model"])
            self.assertTrue(calls[0]["use_ocr_results_with_table_cells"])
            self.assertEqual(tables.base.read(page / "pptables.json")["tables"], 1)

    def test_report_preserves_baseline_and_compares_same_successful_pages(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            page = root / "001" / "p0001"
            page.mkdir(parents=True)
            tables.base.write(root / "manifest.json", {"documents": [{"number": "001", "name": "invoice.pdf", "pages": 2}]})
            tables.base.write(page / "ppstructure.json", {"status": "ok", "seconds": 2})
            tables.base.write(page / "pptables.json", {"status": "ok", "seconds": 3})
            tables.base.write(page / "ppstructure-blocks.json", ["OLD RESULT"])
            tables.base.write(page / "pptables-blocks.json", ["NEW RESULT"])
            (page / "ppstructure.html").write_text("OLD PAGE")
            summary = tables.report(root)
            comparison = summary["comparison_on_same_successful_pages"]
            self.assertEqual(comparison["common_successful_pages"], 1)
            self.assertEqual(comparison["time_ratio_table_cells_over_light"], 1.5)
            self.assertEqual((page / "ppstructure.html").read_text(), "OLD PAGE")
            self.assertEqual(tables.base.read(page / "ppstructure.json")["seconds"], 2)
            rendered = (page / "pptables.html").read_text()
            self.assertIn("OLD RESULT", rendered)
            self.assertIn("NEW RESULT", rendered)


if __name__ == "__main__":
    unittest.main()
