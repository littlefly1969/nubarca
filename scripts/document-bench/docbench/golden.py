"""Read a generated golden set back, and derive per-level relevance judgements."""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path


def _jsonl(path: Path) -> list[dict]:
    with path.open(encoding="utf-8") as handle:
        return [json.loads(line) for line in handle if line.strip()]


@dataclass
class Golden:
    root: Path
    manifest: dict
    docs: dict[str, dict]
    pages: dict[tuple[str, int], dict]
    queries: list[dict]
    page_text_override: dict[tuple[str, int], str] = field(default_factory=dict)

    @classmethod
    def load(cls, root: Path) -> "Golden":
        manifest_path = root / "manifest.json"
        if not manifest_path.exists():
            raise SystemExit(f"no golden set at {root}: run `python -m docbench generate` first")
        manifest = json.loads(manifest_path.read_text())
        docs = {d["doc_id"]: d for d in _jsonl(root / "truth" / "docs.jsonl")}
        pages = {(p["doc_id"], p["page"]): p for p in _jsonl(root / "truth" / "pages.jsonl")}
        queries = _jsonl(root / "truth" / "queries.jsonl")
        return cls(root, manifest, docs, pages, queries)

    @property
    def version(self) -> str:
        return self.manifest["dataset_version"]

    def page_text(self, doc_id: str, page: int) -> str:
        """Reference text, unless an OCR run replaced it (end-to-end evaluation)."""
        return self.page_text_override.get((doc_id, page), self.pages[(doc_id, page)]["text"])

    def load_ocr_override(self, predictions: Path, formats=("scan", "photo")) -> int:
        """Use OCR output instead of reference text for scanned/photographed docs."""
        count = 0
        for row in _jsonl(predictions):
            doc = self.docs.get(row["doc_id"])
            if doc and doc["format"] in formats and row.get("variant") == doc["stored_variant"]:
                self.page_text_override[(row["doc_id"], row["page"])] = row.get("text") or ""
                count += 1
        return count

    def query_types(self) -> dict[str, list[str]]:
        return {q["qid"]: q["types"] for q in self.queries}

    def page_qrels(self) -> dict[str, dict[str, int]]:
        out = {}
        for q in self.queries:
            grades: dict[str, int] = {}
            for r in q["rels"]:
                key = page_key(r["doc"], r["page"])
                grades[key] = max(grades.get(key, 0), r["grade"])
            out[q["qid"]] = grades
        return out

    def doc_qrels(self) -> dict[str, dict[str, int]]:
        out = {}
        for q in self.queries:
            grades: dict[str, int] = {}
            for r in q["rels"]:
                grades[r["doc"]] = max(grades.get(r["doc"], 0), r["grade"])
            out[q["qid"]] = grades
        return out


def page_key(doc_id: str, page: int) -> str:
    return f"{doc_id}#p{page}"
