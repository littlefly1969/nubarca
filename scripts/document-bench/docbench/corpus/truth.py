"""Reference text and structure derived from the authored documents.

The renderer draws exactly these strings, in exactly this order, so the
reference text of a page IS its text layer. Reading order is the authored
order, except that a two-column page reads column 0 top to bottom and then
column 1, and footnotes come after the body.
"""

from __future__ import annotations

from .model import C, Doc, F, H, L, N, P, Page, PageTruth, T, as_cell

BULLET = "•"


def table_lines(table: T) -> list[str]:
    lines = [table.caption] if table.caption else []
    for row in table.rows:
        cells = [as_cell(c).text for c in row]
        lines.append(" | ".join(cells))
    return lines


def block_lines(block) -> list[str]:
    if isinstance(block, (H, P)):
        return [block.text]
    if isinstance(block, L):
        return [f"{BULLET} {item}" for item in block.items]
    if isinstance(block, T):
        return table_lines(block)
    if isinstance(block, F):
        return [block.plain]
    if isinstance(block, C):
        return [block.caption] if block.caption else []
    if isinstance(block, N):
        return [block.text]
    raise TypeError(type(block))


def ordered_blocks(doc: Doc, page: Page) -> list:
    body = [b for b in page.blocks if not isinstance(b, N)]
    notes = [b for b in page.blocks if isinstance(b, N)]
    if doc.layout == "two-column":
        body = [b for b in body if getattr(b, "column", 0) == 0] + \
               [b for b in body if getattr(b, "column", 0) == 1]
    return body + notes


def table_tree(table: T) -> dict:
    """The table as an HTML-shaped tree, the input TEDS compares."""
    rows = []
    for index, row in enumerate(table.rows):
        cells = []
        for raw in row:
            cell = as_cell(raw)
            cells.append({
                "text": cell.text,
                "colspan": cell.colspan,
                "rowspan": cell.rowspan,
                "header": index < table.header_rows,
            })
        rows.append(cells)
    return {"table_id": table.table_id, "caption": table.caption, "rows": rows}


def table_html(tree: dict) -> str:
    out = ["<table>"]
    for row in tree["rows"]:
        out.append("<tr>")
        for cell in row:
            tag = "th" if cell["header"] else "td"
            attrs = ""
            if cell["colspan"] > 1:
                attrs += f' colspan="{cell["colspan"]}"'
            if cell["rowspan"] > 1:
                attrs += f' rowspan="{cell["rowspan"]}"'
            out.append(f"<{tag}{attrs}>{cell['text']}</{tag}>")
        out.append("</tr>")
    out.append("</table>")
    return "".join(out)


def page_truth(doc: Doc, number: int) -> PageTruth:
    page = doc.pages[number - 1]
    body_lines: list[str] = []
    blocks: list[dict] = []
    tables: list[dict] = []
    formulas: list[dict] = []
    ignorable: list[str] = []
    for block in ordered_blocks(doc, page):
        lines = block_lines(block)
        kind = type(block).__name__
        if lines:
            blocks.append({"kind": kind, "text": "\n".join(lines)})
        body_lines.extend(lines)
        if isinstance(block, T):
            tree = table_tree(block)
            tree["html"] = table_html(tree)
            tables.append(tree)
        elif isinstance(block, F):
            formulas.append({"formula_id": block.formula_id, "latex": block.latex,
                             "plain": block.plain})
        elif isinstance(block, C):
            ignorable.extend(block.labels)
    furniture = [t for t in (doc.header_text(number), doc.footer_text(number)) if t]
    head = [doc.header_text(number)] if doc.header else []
    foot = [doc.footer_text(number)] if doc.footer else []
    body = "\n".join(body_lines)
    return PageTruth(
        doc_id=doc.doc_id,
        page=number,
        text="\n".join(head + body_lines + foot),
        body_text=body,
        furniture=furniture,
        blocks=blocks,
        tables=tables,
        formulas=formulas,
        ignorable=ignorable,
        notes=page.notes,
    )


def all_page_truths(docs) -> list[PageTruth]:
    return [page_truth(d, n) for d in docs for n in range(1, len(d.pages) + 1)]
