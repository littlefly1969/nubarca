"""The structured form every golden document is authored in.

A document is authored ONCE as pages of typed blocks. Every artifact — the
digital PDF, the scanned and photographed variants, the DOCX/PPTX/XLSX file,
the per-page reference text, the table trees and the canonical page images —
is derived from that one structure, so the ground truth cannot drift from the
files it describes.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Union


@dataclass(frozen=True)
class Cell:
    text: str
    colspan: int = 1
    rowspan: int = 1


CellLike = Union[str, Cell]


def as_cell(value: CellLike) -> Cell:
    return value if isinstance(value, Cell) else Cell(value)


@dataclass(frozen=True)
class H:
    """A heading. Level 1 is a section, 2 a subsection."""
    text: str
    level: int = 1
    column: int = 0


@dataclass(frozen=True)
class P:
    text: str
    column: int = 0
    size: float = 10.0


@dataclass(frozen=True)
class L:
    items: tuple[str, ...]
    column: int = 0


@dataclass(frozen=True)
class T:
    """A table in HTML shape: rows list only the cells that START there.

    `header_rows` counts how many leading rows are header rows (rendered bold,
    and repeated by the structure-aware chunker when a table is split).
    `table_id` is stable across a table continued on the next page, so the
    ground truth can say "the same table, second part".
    """
    table_id: str
    rows: tuple[tuple[CellLike, ...], ...]
    header_rows: int = 1
    caption: str = ""
    widths: tuple[float, ...] = ()
    column: int = 0


@dataclass(frozen=True)
class F:
    """A display formula: drawn as an image, with `plain` as its text layer."""
    formula_id: str
    latex: str
    plain: str
    column: int = 0


@dataclass(frozen=True)
class C:
    """A chart drawn with matplotlib. Its values exist ONLY in the image.

    `caption` is printed as text under the chart; everything else (bar labels,
    values, legend) is pixels, which is what makes a visual-only question
    visual-only. `labels` lists the strings the image contains so the OCR lane
    does not count reading them as hallucination.
    """
    chart_id: str
    kind: str  # bar | line | pie | diagram
    title: str
    categories: tuple[str, ...]
    series: tuple[tuple[str, tuple[float, ...]], ...]
    caption: str = ""
    column: int = 0

    @property
    def labels(self) -> tuple[str, ...]:
        names = [self.title, *self.categories, *(name for name, _ in self.series)]
        return tuple(n for n in names if n)


@dataclass(frozen=True)
class N:
    """A footnote, printed at the foot of the page in a smaller size."""
    text: str


Block = Union[H, P, L, T, F, C, N]


@dataclass(frozen=True)
class Page:
    blocks: tuple[Block, ...]
    notes: str = ""  # PPTX speaker notes; not printed on the page image


@dataclass(frozen=True)
class Doc:
    doc_id: str
    title: str
    filename: str
    lang: str  # it | en
    category: str
    format: str  # pdf | scan | photo | docx | pptx | xlsx
    pages: tuple[Page, ...]
    header: str = ""  # repeated on every page ("furniture")
    footer: str = ""  # may contain {n} and {total}
    layout: str = "single"  # single | two-column | slide | sheet
    sheet_names: tuple[str, ...] = ()
    distractor_of: str = ""  # near-duplicate of another document

    def header_text(self, n: int) -> str:
        return self.header.format(n=n, total=len(self.pages)) if self.header else ""

    def footer_text(self, n: int) -> str:
        return self.footer.format(n=n, total=len(self.pages)) if self.footer else ""


@dataclass(frozen=True)
class Rel:
    """One graded judgement: grade 2 answers the query, grade 1 is related.

    `span` is a passage that MUST occur verbatim in that page's reference text
    (a test enforces it). Chunk relevance is derived from it.
    """
    doc: str
    page: int
    grade: int = 2
    span: str = ""


@dataclass(frozen=True)
class Q:
    qid: str
    text: str
    lang: str
    types: tuple[str, ...]
    rels: tuple[Rel, ...] = ()
    answer: str = ""
    visual_only: bool = False

    @property
    def answerable(self) -> bool:
        return any(r.grade >= 2 for r in self.rels)


@dataclass
class PageTruth:
    doc_id: str
    page: int
    text: str
    body_text: str
    furniture: list[str] = field(default_factory=list)
    blocks: list[dict] = field(default_factory=list)
    tables: list[dict] = field(default_factory=list)
    formulas: list[dict] = field(default_factory=list)
    ignorable: list[str] = field(default_factory=list)
