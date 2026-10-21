"""Deterministic PDF layout of an authored document, with reportlab.

Fonts are the DejaVu TTFs bundled INSIDE matplotlib, embedded into every PDF,
so the same pinned requirements produce byte-identical files on any machine
(reportlab runs in `invariant` mode: no timestamps, no random document id).
The text drawn is exactly the reference text from `truth.py`.
"""

from __future__ import annotations

import io
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402
from reportlab import rl_config  # noqa: E402
from reportlab.lib.pagesizes import A4, landscape  # noqa: E402
from reportlab.lib.utils import ImageReader, simpleSplit  # noqa: E402
from reportlab.pdfbase import pdfmetrics  # noqa: E402
from reportlab.pdfbase.ttfonts import TTFont  # noqa: E402
from reportlab.pdfgen import canvas  # noqa: E402

from .model import C, Doc, F, H, L, N, P, T, as_cell  # noqa: E402
from .truth import BULLET, ordered_blocks  # noqa: E402

rl_config.invariant = 1

FONT = "DejaVuSans"
BOLD = "DejaVuSans-Bold"
ITALIC = "DejaVuSans-Oblique"
SLIDE = (960.0, 540.0)
MARGIN = 50.0
GUTTER = 22.0


class LayoutOverflow(RuntimeError):
    pass


def register_fonts() -> None:
    if FONT in pdfmetrics.getRegisteredFontNames():
        return
    ttf = Path(matplotlib.get_data_path()) / "fonts" / "ttf"
    pdfmetrics.registerFont(TTFont(FONT, str(ttf / "DejaVuSans.ttf")))
    pdfmetrics.registerFont(TTFont(BOLD, str(ttf / "DejaVuSans-Bold.ttf")))
    pdfmetrics.registerFont(TTFont(ITALIC, str(ttf / "DejaVuSans-Oblique.ttf")))


def _png(fig, tight: bool = False) -> bytes:
    buffer = io.BytesIO()
    extra = {"bbox_inches": "tight", "pad_inches": 0.04} if tight else {}
    fig.savefig(buffer, format="png", dpi=200, metadata={"Software": None}, **extra)
    plt.close(fig)
    return buffer.getvalue()


def chart_png(chart: C) -> bytes:
    plt.rcParams.update({"font.family": "DejaVu Sans", "font.size": 9})
    if chart.kind == "diagram":
        fig, ax = plt.subplots(figsize=(6.4, 2.6))
        ax.set_axis_off()
        names = chart.categories
        xs = [0.08 + i * (0.84 / max(1, len(names) - 1)) for i in range(len(names))]
        for i, (x, name) in enumerate(zip(xs, names)):
            y = 0.62 if i % 2 == 0 else 0.28
            ax.add_patch(plt.Rectangle((x - 0.075, y - 0.08), 0.15, 0.16, fill=False, lw=1.2))
            ax.text(x, y, name, ha="center", va="center", fontsize=6)
            if i:
                px = xs[i - 1]
                py = 0.62 if (i - 1) % 2 == 0 else 0.28
                ax.annotate("", xy=(x - 0.075, y), xytext=(px + 0.075, py),
                            arrowprops={"arrowstyle": "->", "lw": 1.0})
        ax.set_title(chart.title)
        ax.set_xlim(0, 1)
        ax.set_ylim(0, 1)
        return _png(fig)
    if chart.kind == "pie":
        fig, ax = plt.subplots(figsize=(5.2, 3.0))
        values = chart.series[0][1]
        ax.pie(values, labels=chart.categories, autopct="%1.0f%%", startangle=90,
               counterclock=False, textprops={"fontsize": 8})
        ax.set_title(chart.title)
        return _png(fig)
    fig, ax = plt.subplots(figsize=(6.4, 2.9))
    xs = range(len(chart.categories))
    if chart.kind == "bar":
        width = 0.8 / len(chart.series)
        for i, (name, values) in enumerate(chart.series):
            offsets = [x + (i - (len(chart.series) - 1) / 2) * width for x in xs]
            bars = ax.bar(offsets, values, width=width, label=name)
            ax.bar_label(bars, fontsize=6, fmt="%g")
    else:
        for name, values in chart.series:
            ax.plot(list(xs), values, marker="o", label=name)
            for x, v in zip(xs, values):
                ax.annotate(f"{v:g}", (x, v), textcoords="offset points", xytext=(0, 5),
                            ha="center", fontsize=6)
    ax.set_xticks(list(xs))
    ax.set_xticklabels(chart.categories, fontsize=7)
    ax.set_title(chart.title)
    if len(chart.series) > 1:
        ax.legend(fontsize=7)
    ax.spines[["top", "right"]].set_visible(False)
    fig.tight_layout()
    return _png(fig)


def formula_png(formula: F) -> bytes:
    fig = plt.figure(figsize=(6.0, 0.7))
    fig.text(0.5, 0.5, f"${formula.latex}$", ha="center", va="center", fontsize=15)
    return _png(fig, tight=True)


class _Column:
    def __init__(self, x: float, width: float, top: float):
        self.x = x
        self.width = width
        self.y = top


class PageWriter:
    """Draws one page top-down; raises LayoutOverflow instead of clipping."""

    def __init__(self, c: canvas.Canvas, doc: Doc, size: tuple[float, float], scale: float):
        self.c = c
        self.doc = doc
        self.width, self.height = size
        self.scale = scale
        self.bottom = MARGIN + 16

    def _need(self, col: _Column, height: float, where: str) -> None:
        if col.y - height < self.bottom:
            raise LayoutOverflow(f"{self.doc.doc_id}: content overflows the page at {where}")

    def text(self, col: _Column, text: str, font: str, size: float, gap_after: float,
             indent: float = 0.0) -> None:
        size *= self.scale
        leading = size * 1.3
        lines = simpleSplit(text, font, size, col.width - indent)
        self._need(col, leading * len(lines), text[:40])
        self.c.setFont(font, size)
        for line in lines:
            col.y -= leading
            self.c.drawString(col.x + indent, col.y + size * 0.25, line)
        col.y -= gap_after * self.scale

    def image(self, col: _Column, png: bytes, max_height: float) -> None:
        reader = ImageReader(io.BytesIO(png))
        iw, ih = reader.getSize()
        w = min(col.width, max_height * iw / ih)
        h = w * ih / iw
        self._need(col, h, "image")
        col.y -= h
        self.c.drawImage(reader, col.x + (col.width - w) / 2, col.y, w, h)
        col.y -= 4

    def table(self, col: _Column, table: T) -> None:
        if table.caption:
            self.text(col, table.caption, ITALIC, 8.5, 2)
        ncols = sum(as_cell(c).colspan for c in table.rows[0])
        fractions = table.widths or tuple(1.0 / ncols for _ in range(ncols))
        widths = [f * col.width for f in fractions]
        xs = [col.x]
        for w in widths:
            xs.append(xs[-1] + w)
        size = 8.5 * self.scale
        leading = size * 1.25
        pad = 3.0

        # Occupancy grid: which (row, col) slots a rowspan from above covers.
        covered: set[tuple[int, int]] = set()
        placed = []  # (row, col0, cell, lines)
        heights = []
        for r, row in enumerate(table.rows):
            c0 = 0
            row_lines = 1
            for raw in row:
                while (r, c0) in covered:
                    c0 += 1
                cell = as_cell(raw)
                span_w = sum(widths[c0:c0 + cell.colspan])
                font = BOLD if r < table.header_rows else FONT
                lines = simpleSplit(cell.text, font, size, span_w - 2 * pad) or [""]
                placed.append((r, c0, cell, lines, font))
                for dr in range(1, cell.rowspan):
                    for dc in range(cell.colspan):
                        covered.add((r + dr, c0 + dc))
                if cell.rowspan == 1:
                    row_lines = max(row_lines, len(lines))
                c0 += cell.colspan
            heights.append(row_lines * leading + 2 * pad)
        total = sum(heights)
        self._need(col, total, f"table {table.table_id}")
        tops = [col.y]
        for h in heights:
            tops.append(tops[-1] - h)
        self.c.setLineWidth(0.6)
        for r, c0, cell, lines, font in placed:
            x0, x1 = xs[c0], xs[c0 + cell.colspan]
            y0, y1 = tops[r], tops[r + cell.rowspan]
            if r < table.header_rows:
                self.c.setFillGray(0.9)
                self.c.rect(x0, y1, x1 - x0, y0 - y1, stroke=0, fill=1)
                self.c.setFillGray(0)
            self.c.rect(x0, y1, x1 - x0, y0 - y1, stroke=1, fill=0)
            self.c.setFont(font, size)
            y = y0 - pad
            for line in lines:
                y -= leading
                self.c.drawString(x0 + pad, y + size * 0.3, line)
        col.y = tops[-1] - 8 * self.scale


def _draw_page(c: canvas.Canvas, doc: Doc, number: int, size: tuple[float, float]) -> None:
    page = doc.pages[number - 1]
    slide = doc.layout == "slide"
    scale = 1.6 if slide else 1.0
    w, h = size
    writer = PageWriter(c, doc, size, scale)
    top = h - MARGIN
    if doc.header:
        c.setFont(FONT, 7.5)
        c.setFillGray(0.35)
        c.drawString(MARGIN, h - MARGIN + 14, doc.header_text(number))
        c.setFillGray(0)
        c.setLineWidth(0.4)
        c.line(MARGIN, h - MARGIN + 10, w - MARGIN, h - MARGIN + 10)
        top -= 6
    content_w = w - 2 * MARGIN
    if doc.layout == "two-column":
        cw = (content_w - GUTTER) / 2
        columns = [_Column(MARGIN, cw, top), _Column(MARGIN + cw + GUTTER, cw, top)]
    else:
        columns = [_Column(MARGIN, content_w, top)]

    notes = [b for b in page.blocks if isinstance(b, N)]
    for block in ordered_blocks(doc, page):
        if isinstance(block, N):
            continue
        col = columns[getattr(block, "column", 0)] if len(columns) > 1 else columns[0]
        if isinstance(block, H):
            size = (13.0 if block.level == 1 else 11.0)
            if slide and block.level == 1:
                size = 17.0
            writer.text(col, block.text, BOLD, size, 5)
        elif isinstance(block, P):
            writer.text(col, block.text, FONT, block.size, 6)
        elif isinstance(block, L):
            for item in block.items:
                writer.text(col, f"{BULLET} {item}", FONT, 10.0, 3, indent=8)
            col.y -= 3
        elif isinstance(block, T):
            writer.table(col, block)
        elif isinstance(block, F):
            # Image for the eye, invisible text for the text layer: the shape of
            # an OCR'd or LaTeX-generated PDF, and what the reference text says.
            y_before = col.y
            writer.image(col, formula_png(block), 40)
            # Text render mode is graphics state and outlives ET: without the
            # save/restore every later string on the page is invisible too.
            c.saveState()
            text = c.beginText()
            text.setTextRenderMode(3)
            text.setFont(FONT, 9)
            text.setTextOrigin(col.x, (y_before + col.y) / 2)
            text.textLine(block.plain)
            c.drawText(text)
            c.restoreState()
        elif isinstance(block, C):
            writer.image(col, chart_png(block), 230 if not slide else 330)
            if block.caption:
                writer.text(col, block.caption, ITALIC, 8.5, 6)

    if notes:
        lowest = min(col.y for col in columns)
        size = 7.5
        lines = []
        for note in notes:
            lines.extend(simpleSplit(note.text, FONT, size, content_w))
        needed = len(lines) * size * 1.3 + 8
        if lowest - needed < writer.bottom:
            raise LayoutOverflow(f"{doc.doc_id}: footnotes overflow page {number}")
        y = writer.bottom + needed
        c.setLineWidth(0.4)
        c.line(MARGIN, y, MARGIN + 120, y)
        y -= 4
        c.setFont(FONT, size)
        for line in lines:
            y -= size * 1.3
            c.drawString(MARGIN, y + 2, line)

    if doc.footer:
        c.setFont(FONT, 7.5)
        c.drawCentredString(w / 2, MARGIN - 18, doc.footer_text(number))


def page_size(doc: Doc) -> tuple[float, float]:
    if doc.layout == "slide":
        return SLIDE
    if doc.layout == "sheet":
        return landscape(A4)
    return A4


def render_pdf(doc: Doc, path: Path) -> None:
    """The canonical layout PDF: real text layer, embedded fonts, no clipping."""
    register_fonts()
    size = page_size(doc)
    c = canvas.Canvas(str(path), pagesize=size, invariant=1, pageCompression=1)
    c.setTitle(doc.title)
    c.setAuthor("NubArca document-bench (synthetic)")
    c.setCreator("docbench golden-v1")
    c.setProducer("docbench")
    for n in range(1, len(doc.pages) + 1):
        _draw_page(c, doc, n, size)
        c.showPage()
    c.save()
