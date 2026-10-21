"""Table parsing (HTML, Markdown, OTSL) and TEDS.

TEDS (Zhong et al., "Image-based table recognition", 2020) is
1 - TreeEditDistance(A, B) / max(|A|, |B|) over the HTML trees, where renaming
a cell costs the normalized Levenshtein distance of its contents when the tag,
colspan and rowspan agree and 1 otherwise; insert/delete cost 1. TEDS-S is the
same with contents ignored (structure only). Header cells are compared as
ordinary cells: whether an engine marks <th> is not what is being measured.

This is an independent implementation on top of `apted` (MIT), not a copy of
the PubTabNet reference code.
"""

from __future__ import annotations

import re
from html.parser import HTMLParser

from apted import APTED, Config
from rapidfuzz.distance import Levenshtein


class Node:
    __slots__ = ("tag", "colspan", "rowspan", "text", "children")

    def __init__(self, tag: str, colspan: int = 1, rowspan: int = 1, text: str = "",
                 children: list["Node"] | None = None):
        self.tag = tag
        self.colspan = colspan
        self.rowspan = rowspan
        self.text = text
        self.children = children or []

    def size(self) -> int:
        return 1 + sum(c.size() for c in self.children)


def _cell_text(text: str) -> str:
    return re.sub(r"\s+", " ", text).strip()


def tree_from_rows(rows: list[list[dict]]) -> Node:
    """rows: [[{text, colspan, rowspan}, ...], ...] (only cells that start there)."""
    return Node("table", children=[
        Node("tr", children=[
            Node("td", int(c.get("colspan", 1)), int(c.get("rowspan", 1)), _cell_text(c["text"]))
            for c in row])
        for row in rows])


class _Config(Config):
    def __init__(self, structure_only: bool):
        self.structure_only = structure_only

    def rename(self, a: Node, b: Node) -> float:
        if a.tag != b.tag or a.colspan != b.colspan or a.rowspan != b.rowspan:
            return 1.0
        if a.tag != "td" or self.structure_only:
            return 0.0
        return Levenshtein.normalized_distance(a.text, b.text)

    def children(self, node: Node) -> list[Node]:
        return node.children


def teds(truth: Node, predicted: Node | None, structure_only: bool = False) -> float:
    if predicted is None:
        return 0.0
    distance = APTED(truth, predicted, _Config(structure_only)).compute_edit_distance()
    return max(0.0, 1.0 - distance / max(truth.size(), predicted.size()))


# ---- parsers -----------------------------------------------------------------

class _HtmlTables(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.tables: list[list[list[dict]]] = []
        self._stack: list[list[list[dict]]] = []
        self._cell: dict | None = None

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "table":
            self._stack.append([])
        elif tag == "tr" and self._stack:
            self._stack[-1].append([])
        elif tag in ("td", "th") and self._stack:
            if not self._stack[-1]:
                self._stack[-1].append([])

            def span(name):
                try:
                    return max(1, int(attrs.get(name) or 1))
                except ValueError:
                    return 1
            self._cell = {"text": "", "colspan": span("colspan"), "rowspan": span("rowspan")}
        elif tag == "br" and self._cell is not None:
            self._cell["text"] += " "

    def handle_endtag(self, tag):
        if tag in ("td", "th") and self._cell is not None and self._stack:
            self._stack[-1][-1].append(self._cell)
            self._cell = None
        elif tag == "table" and self._stack:
            self.tables.append([r for r in self._stack.pop() if r])

    def handle_data(self, data):
        if self._cell is not None:
            self._cell["text"] += data


def html_tables(text: str) -> list[list[list[dict]]]:
    parser = _HtmlTables()
    parser.feed(text)
    parser.close()
    return parser.tables


_MD_SEPARATOR = re.compile(r"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")


def markdown_tables(text: str) -> list[list[list[dict]]]:
    tables, current = [], []
    for line in text.splitlines() + [""]:
        stripped = line.strip()
        if stripped.startswith("|") and stripped.count("|") >= 2:
            if _MD_SEPARATOR.match(stripped):
                continue
            cells = [c.strip() for c in stripped.strip("|").split("|")]
            current.append([{"text": c, "colspan": 1, "rowspan": 1} for c in cells])
        elif current:
            tables.append(current)
            current = []
    return tables


_OTSL = re.compile(r"<(fcel|ecel|lcel|ucel|xcel|nl)>")


def otsl_tables(text: str) -> list[list[list[dict]]]:
    """OTSL (Lysak et al. 2023): fcel=filled, ecel=empty, lcel=merge left,
    ucel=merge up, xcel=merge both, nl=new row. Wrapped in <otsl>...</otsl>
    or bare. Converted to HTML-shaped rows with colspan/rowspan."""
    tables = []
    bodies = re.findall(r"<otsl>(.*?)</otsl>", text, flags=re.S) or (
        [text] if _OTSL.search(text) else [])
    for body in bodies:
        grid: list[list[tuple[str, str]]] = [[]]
        parts = _OTSL.split(body)
        # parts: [pre, tag, content, tag, content, ...]
        for i in range(1, len(parts), 2):
            tag, content = parts[i], parts[i + 1] if i + 1 < len(parts) else ""
            if tag == "nl":
                grid.append([])
            else:
                grid[-1].append((tag, content.strip()))
        grid = [r for r in grid if r]
        if not grid:
            continue
        rows = []
        for r, row in enumerate(grid):
            out = []
            for c, (tag, content) in enumerate(row):
                if tag not in ("fcel", "ecel"):
                    continue
                colspan = 1
                while c + colspan < len(row) and row[c + colspan][0] == "lcel":
                    colspan += 1
                rowspan = 1
                while r + rowspan < len(grid) and c < len(grid[r + rowspan]) \
                        and grid[r + rowspan][c][0] == "ucel":
                    rowspan += 1
                out.append({"text": content, "colspan": colspan, "rowspan": rowspan})
            rows.append(out)
        tables.append(rows)
    return tables


def extract_tables(text: str) -> list[Node]:
    found = html_tables(text) or otsl_tables(text) or markdown_tables(text)
    return [tree_from_rows(rows) for rows in found if rows]


def match_tables(truths: list[Node], predicted: list[Node]) -> list[dict]:
    """Greedy best-TEDS pairing; an unmatched truth table scores 0."""
    pairs = []
    for ti, t in enumerate(truths):
        for pi, p in enumerate(predicted):
            pairs.append((teds(t, p), ti, pi))
    pairs.sort(reverse=True)
    used_t, used_p, result = set(), set(), {}
    for score, ti, pi in pairs:
        if ti in used_t or pi in used_p:
            continue
        used_t.add(ti)
        used_p.add(pi)
        result[ti] = (score, teds(truths[ti], predicted[pi], structure_only=True))
    return [{"teds": result.get(i, (0.0, 0.0))[0], "teds_s": result.get(i, (0.0, 0.0))[1],
             "matched": i in result} for i in range(len(truths))]
