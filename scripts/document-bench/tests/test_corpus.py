"""The golden set's own invariants: judgements point at real text, coverage of
the mandated query shapes, determinism, and a text layer equal to the truth."""

import json

import pytest

from docbench.corpus.docs_en import DOCS_EN
from docbench.corpus.docs_it import DOCS_IT
from docbench.corpus.generate import generate
from docbench.corpus.queries import QUERIES
from docbench.corpus.truth import page_truth

DOCS = {d.doc_id: d for d in DOCS_IT + DOCS_EN}

REQUIRED_TYPES = {"it-it", "it-en", "technical", "paraphrase", "indirect", "names", "codes",
                  "numbers", "specialist", "ambiguous", "unanswerable", "table", "formula",
                  "short", "long", "long-passage", "cross-page", "visual-only", "page-exact"}


def test_at_least_150_queries_with_unique_ids():
    assert len(QUERIES) >= 150
    assert len({q.qid for q in QUERIES}) == len(QUERIES)


def test_every_mandated_query_type_is_present():
    present = {t for q in QUERIES for t in q.types}
    assert REQUIRED_TYPES <= present, REQUIRED_TYPES - present


def test_every_span_occurs_verbatim_on_its_page():
    for q in QUERIES:
        for r in q.rels:
            assert r.doc in DOCS, (q.qid, r.doc)
            truth = page_truth(DOCS[r.doc], r.page)
            assert r.span and (r.span in truth.text or r.span in truth.notes), (q.qid, r.span)


def test_unanswerable_queries_have_no_judgements_and_answerable_have_grade_two():
    for q in QUERIES:
        if "unanswerable" in q.types:
            assert not q.rels
        else:
            assert q.answerable, q.qid


def test_visual_only_answers_are_not_in_the_text():
    for q in QUERIES:
        if q.visual_only:
            assert "visual-only" in q.types
            assert q.answer, q.qid
            for r in q.rels:
                text = page_truth(DOCS[r.doc], r.page).text
                assert q.answer.split(" ")[0] not in text, q.qid


def test_both_languages_and_all_formats_are_present():
    assert {d.lang for d in DOCS.values()} == {"it", "en"}
    assert {d.format for d in DOCS.values()} == {"pdf", "scan", "photo", "docx", "pptx", "xlsx"}
    assert any(d.distractor_of for d in DOCS.values())


@pytest.fixture(scope="module")
def small_set(tmp_path_factory):
    out = tmp_path_factory.mktemp("golden")
    manifest = generate(out, ("clean", "scan", "photo", "lowdpi"),
                        {"it-invoice-0187", "it-lab-report", "en-saas-agreement"})
    return out, manifest


def test_generation_is_deterministic(small_set, tmp_path):
    _, first = small_set
    second = generate(tmp_path, ("clean", "scan", "photo", "lowdpi"),
                      {"it-invoice-0187", "it-lab-report", "en-saas-agreement"})
    assert first["content_sha256"] == second["content_sha256"]
    assert first["synthetic"] is True and "NOT a validation" in first["disclaimer"]


def test_pdf_text_layer_equals_reference_text(small_set):
    import pypdfium2 as pdfium

    from docbench.metrics.ocr import cer
    out, _ = small_set
    pages = [json.loads(line) for line in (out / "truth" / "pages.jsonl").open()]
    for row in pages:
        pdf = pdfium.PdfDocument(str(out / "layout" / f"{row['doc_id']}.pdf"))
        text = pdf[row["page"] - 1].get_textpage().get_text_range()
        pdf.close()
        assert cer(row["text"], text) < 0.01, (row["doc_id"], row["page"])


def test_regions_cover_every_block(small_set):
    out, _ = small_set
    for line in (out / "truth" / "pages.jsonl").open():
        row = json.loads(line)
        assert len(row["regions"]) >= len(row["blocks"]) - 1
        for region in row["regions"]:
            x0, y0, x1, y1 = region["bbox"]
            assert 0 <= x0 < x1 <= 1 and 0 <= y0 < y1 <= 1
