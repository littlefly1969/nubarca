"""Chunking configurations compared with the encoder held fixed.

`nubarca-char1600` reproduces what NubArca does today for these formats
(RichDocumentChunker over the per-format extraction blocks, see
docs/document-intelligence/architecture-baseline.md): one block per PDF page,
per DOCX paragraph/table, per slide body and slide notes, per sheet; a block
longer than 1600 characters is split on its last newline past a quarter of the
budget, else hard-cut; no overlap; the chunk text carries no heading.

`struct-tok<N>-ov<M>` is the structure-aware candidate: running headers and
footers dropped, consecutive blocks of the same top-level section (including
short sibling subsections, headings kept inline) packed up to N (estimated)
tokens, sentence overlap of up to M tokens inside a section,
tables kept apart and split by row groups with their header rows repeated, and
the document title plus section path prefixed to every chunk.

Token counts are an ENCODER-INDEPENDENT estimate (1.3 per word + 0.5 per
punctuation mark) so every encoder sees the same chunks; the encoders still
truncate at their own limit.
"""

from __future__ import annotations

import math
import re
from dataclasses import dataclass, field

from .golden import Golden

SENTENCE = re.compile(r"(?<=[.!?;:])\s+(?=[A-ZÀ-ÚÈÉ«(\d])")


@dataclass
class Chunk:
    chunk_id: str
    doc_id: str
    pages: list[int]
    heading: str
    text: str
    title: str
    kind: str = "text"
    meta: dict = field(default_factory=dict)


def approx_tokens(text: str) -> int:
    words = len(re.findall(r"\w+", text))
    punct = len(re.findall(r"[^\w\s]", text))
    return math.ceil(1.3 * words + 0.5 * punct)


def _title(doc: dict) -> str:
    return f"{doc['title']} {doc['files'][0]['name']}"


# ---- NubArca baseline --------------------------------------------------------

def _split_rich(text: str, budget: int) -> list[str]:
    if len(text) <= budget:
        return [text]
    pieces, start = [], 0
    while start < len(text):
        length = min(budget, len(text) - start)
        if start + length < len(text):
            window = text[start:start + length]
            brk = window.rfind("\n")
            if brk > budget // 4:
                length = brk + 1
        piece = text[start:start + length].strip()
        if piece:
            pieces.append(piece)
        start += length
    return pieces


def _strip_bullet(line: str) -> str:
    return line[2:] if line.startswith("• ") else line


def baseline_blocks(golden: Golden, doc: dict) -> list[tuple[str, str, int, str]]:
    """(heading, text, page, kind) as NubArca's extractors would emit them."""
    fmt = doc["format"]
    doc_id = doc["doc_id"]
    blocks: list[tuple[str, str, int, str]] = []
    if fmt in ("pdf", "scan", "photo"):
        for n in range(1, doc["pages"] + 1):
            blocks.append(("", golden.page_text(doc_id, n), n, "page"))
        return blocks
    if fmt == "pptx":
        for n in range(1, doc["pages"] + 1):
            truth = golden.pages[(doc_id, n)]
            title = next((b["text"] for b in truth["blocks"] if b["kind"] == "H"), "")
            body = "\n".join(_strip_bullet(line) for b in truth["blocks"]
                             for line in b["text"].splitlines())
            blocks.append((title, body, n, "slide"))
            if truth.get("notes"):
                blocks.append((title, truth["notes"], n, "notes"))
        return blocks
    if fmt == "xlsx":
        for n in range(1, doc["pages"] + 1):
            truth = golden.pages[(doc_id, n)]
            name = doc["sheet_names"][n - 1]
            for table in truth["tables"]:
                header = [c["text"] for c in table["rows"][0]]
                lines = [" | ".join(header)]
                for row in table["rows"][1:]:
                    lines.append(" | ".join(
                        f"{header[i]}={c['text']}" if i < len(header) and header[i] else c["text"]
                        for i, c in enumerate(row)))
                blocks.append((name, "\n".join(lines), n, "sheet"))
        return blocks
    # docx: one block per paragraph, heading path " › ", footnotes in one Note block
    path: list[str] = []
    notes: list[str] = []
    for n in range(1, doc["pages"] + 1):
        for b in golden.pages[(doc_id, n)]["blocks"]:
            if b["kind"] == "N":
                notes.append(b["text"])
                continue
            if b["kind"] == "H":
                level = b.get("level", 1)
                del path[level - 1:]
                path.append(b["text"])
            heading = " › ".join(path)
            if b["kind"] == "L":
                for line in b["text"].splitlines():
                    blocks.append((heading, _strip_bullet(line), n, "paragraph"))
            elif b["kind"] == "T":
                lines = b["text"].splitlines()
                if b.get("caption"):
                    blocks.append((heading, lines[0], n, "paragraph"))
                    lines = lines[1:]
                blocks.append((heading, "\n".join(lines), n, "table"))
            else:
                blocks.append((heading, b["text"], n, "paragraph"))
    if notes:
        blocks.append(("Note", "\n".join(notes), doc["pages"], "notes"))
    return blocks


def chunk_baseline(golden: Golden, budget: int = 1600) -> list[Chunk]:
    chunks = []
    for doc_id, doc in sorted(golden.docs.items()):
        ordinal = 0
        for heading, text, page, kind in baseline_blocks(golden, doc):
            if not text:
                continue
            for piece in _split_rich(text, budget):
                ordinal += 1
                chunks.append(Chunk(f"{doc_id}#c{ordinal:04d}", doc_id, [page], heading, piece,
                                    _title(doc), kind))
    return chunks


# ---- structure-aware ---------------------------------------------------------

@dataclass
class _Unit:
    section: tuple[str, ...]
    text: str
    page: int
    kind: str  # text | table | notes
    header_rows: int = 0
    caption: str = ""


def _units(golden: Golden, doc: dict) -> list[_Unit]:
    units: list[_Unit] = []
    path: list[str] = []
    for n in range(1, doc["pages"] + 1):
        key = (doc["doc_id"], n)
        if key in golden.page_text_override:
            # OCR text has no structure: paragraphs if blank lines exist, else lines.
            text = golden.page_text_override[key]
            parts = [p for p in re.split(r"\n\s*\n", text) if p.strip()]
            if len(parts) <= 1:
                parts = [p for p in text.splitlines() if p.strip()]
            units.extend(_Unit(tuple(path), p.strip(), n, "text") for p in parts)
            continue
        truth = golden.pages[key]
        for b in truth["blocks"]:
            if b["kind"] == "H":
                level = b.get("level", 1)
                if doc["layout"] == "slide":
                    level = 1
                del path[level - 1:]
                path.append(b["text"])
                continue
            if b["kind"] == "T":
                lines = b["text"].splitlines()
                caption = lines[0] if b.get("caption") else ""
                rows = lines[1:] if caption else lines
                units.append(_Unit(tuple(path), "\n".join(rows), n, "table",
                                   b.get("header_rows", 1), caption))
            else:
                units.append(_Unit(tuple(path), b["text"], n, "text"))
        if truth.get("notes"):
            units.append(_Unit(tuple(path), truth["notes"], n, "notes"))
    return units


def _sentences(text: str) -> list[str]:
    out = []
    for line in text.splitlines():
        out.extend(s for s in SENTENCE.split(line) if s.strip())
    return out


def _split_long_text(text: str, budget: int) -> list[str]:
    pieces, current = [], []
    for sentence in _sentences(text):
        if current and approx_tokens(" ".join(current + [sentence])) > budget:
            pieces.append(" ".join(current))
            current = []
        current.append(sentence)
    if current:
        pieces.append(" ".join(current))
    return pieces


def chunk_structured(golden: Golden, budget_total: int, overlap: int) -> list[Chunk]:
    chunks: list[Chunk] = []
    for doc_id, doc in sorted(golden.docs.items()):
        ordinal = 0
        # The title/section prefix is part of what the encoder reads.
        budget = max(64, budget_total - approx_tokens(doc["title"]) - 16)

        def emit(section, body, pages, kind):
            nonlocal ordinal
            ordinal += 1
            heading = " › ".join(section)
            prefix = doc["title"] + (f" › {heading}" if heading else "")
            chunks.append(Chunk(f"{doc_id}#s{ordinal:04d}", doc_id, sorted(set(pages)), heading,
                                f"{prefix}\n{body}", _title(doc), kind))

        buffer: list[str] = []
        buffer_pages: list[int] = []
        section: tuple[str, ...] | None = None

        def flush(keep_overlap: bool):
            nonlocal buffer, buffer_pages
            if not buffer:
                return
            emit(section or (), "\n".join(buffer), buffer_pages, "text")
            carry: list[str] = []
            if keep_overlap and overlap > 0:
                for sentence in reversed(_sentences("\n".join(buffer))):
                    if approx_tokens(" ".join([sentence] + carry)) > overlap:
                        break
                    carry.insert(0, sentence)
            buffer = [" ".join(carry)] if carry else []
            buffer_pages = buffer_pages[-1:] if carry else []

        for unit in _units(golden, doc):
            if unit.section != section:
                # A new top-level section always starts a new chunk. A new
                # SUBsection of the same parent is packed into the running chunk
                # when it fits, with its heading kept inline, so short sibling
                # sections are not each embedded alone.
                same_parent = section is not None and unit.section[:1] == section[:1] \
                    and len(unit.section) > 1
                if not same_parent or not buffer:
                    flush(keep_overlap=False)
                    buffer, buffer_pages = [], []
                    section = unit.section
                else:
                    heading_line = unit.section[-1]
                    if approx_tokens("\n".join(buffer + [heading_line, unit.text])) > budget:
                        flush(keep_overlap=False)
                        buffer, buffer_pages = [], []
                        section = unit.section
                    else:
                        buffer.append(heading_line)
            if unit.kind == "table":
                flush(keep_overlap=False)
                buffer, buffer_pages = [], []
                rows = unit.text.splitlines()
                header, body = rows[:unit.header_rows], rows[unit.header_rows:]
                head = ([unit.caption] if unit.caption else []) + header
                group: list[str] = []
                for row in body:
                    if group and approx_tokens("\n".join(head + group + [row])) > budget:
                        emit(section, "\n".join(head + group), [unit.page], "table")
                        group = []
                    group.append(row)
                emit(section, "\n".join(head + group), [unit.page], "table")
                continue
            pieces = [unit.text] if approx_tokens(unit.text) <= budget \
                else _split_long_text(unit.text, budget)
            for piece in pieces:
                if buffer and approx_tokens("\n".join(buffer + [piece])) > budget:
                    flush(keep_overlap=True)
                buffer.append(piece)
                buffer_pages.append(unit.page)
        flush(keep_overlap=False)
    return chunks


CONFIGS = {
    "nubarca-char1600": lambda g: chunk_baseline(g, 1600),
    "struct-tok512-ov64": lambda g: chunk_structured(g, 512, 64),
    "struct-tok256-ov32": lambda g: chunk_structured(g, 256, 32),
    "struct-tok512-ov0": lambda g: chunk_structured(g, 512, 0),
}


def build(golden: Golden, config: str) -> list[Chunk]:
    if config not in CONFIGS:
        raise SystemExit(f"unknown chunking config {config!r}; known: {', '.join(CONFIGS)}")
    return CONFIGS[config](golden)


# ---- chunk-level relevance ---------------------------------------------------

def _norm(text: str) -> str:
    return re.sub(r"\s+", " ", text).strip().casefold()


def span_coverage(span: str, text: str) -> float:
    """Fraction of `span` present in `text`, tolerant of chunk boundaries."""
    from difflib import SequenceMatcher

    s, t = _norm(span), _norm(text)
    if not s:
        return 0.0
    if s in t:
        return 1.0
    blocks = SequenceMatcher(None, s, t, autojunk=False).get_matching_blocks()
    return sum(b.size for b in blocks if b.size >= 6) / len(s)


def chunk_qrels(golden: Golden, chunks: list[Chunk], threshold: float = 0.6
                ) -> dict[str, dict[str, int]]:
    by_doc: dict[str, list[Chunk]] = {}
    for c in chunks:
        by_doc.setdefault(c.doc_id, []).append(c)
    out = {}
    for q in golden.queries:
        grades: dict[str, int] = {}
        for r in q["rels"]:
            for c in by_doc.get(r["doc"], []):
                if r["page"] not in c.pages:
                    continue
                if span_coverage(r["span"], c.text) >= threshold:
                    grades[c.chunk_id] = max(grades.get(c.chunk_id, 0), r["grade"])
        out[q["qid"]] = grades
    return out
