"""Chunking configs, the BM25F mirror and RRF."""

import pytest

from docbench import chunking
from docbench.corpus.generate import generate
from docbench.golden import Golden
from docbench.lexical import BM25F, content_tokens, rrf, tokenize


def test_tokenizer_mirrors_ragtext():
    assert tokenize("Perché il CANONE è 950,00€?") == ["perche", "il", "canone", "950", "00"]
    assert content_tokens("come faccio a pagare il canone") == ["pagare", "canone"]
    assert tokenize("a b x1") == ["x1"]  # single characters dropped


def test_bm25f_prefers_title_and_section_and_gate_rejects_weak_matches():
    index = BM25F([
        ("a", "manuale caldaia", "codici errore", "il codice E10 indica pressione bassa"),
        ("b", "contratto", "canone", "la caldaia è a carico del locatore"),
        ("c", "ricetta", "", "farina acqua sale"),
    ])
    ranked = [k for k, _ in index.search("errore caldaia E10")]
    assert ranked[0] == "a"
    # One body-only term is never enough for NubArca's gate; a title hit is.
    assert index.search("farina", gated=True) == []
    assert index.search("ricetta farina", gated=True)[0][0] == "c"
    assert index.search("parola inesistente") == []


def test_rrf_is_deterministic_and_weighted():
    fused = rrf([["a", "b"], ["b", "c"]], k=60)
    assert [k for k, _ in fused] == ["b", "a", "c"]
    heavy = rrf([["a"], ["c"]], k=60, weights=[1.0, 3.0])
    assert heavy[0][0] == "c"


def test_split_rich_matches_nubarca_rules():
    text = ("x" * 100 + "\n") * 30  # 3030 chars with newlines
    pieces = chunking._split_rich(text, 1600)
    assert all(len(p) <= 1600 for p in pieces)
    assert "".join(pieces).replace("\n", "") == text.replace("\n", "")
    assert chunking._split_rich("y" * 3500, 1600) == ["y" * 1600, "y" * 1600, "y" * 300]


def test_span_coverage_tolerates_boundaries():
    assert chunking.span_coverage("canone di euro 950", "Il CANONE di euro 950 mensile") == 1.0
    assert chunking.span_coverage("alpha beta gamma delta", "zzz") == 0.0
    half = chunking.span_coverage("prima metà del testo seconda metà del testo",
                                  "... prima metà del testo")
    assert 0.4 < half < 0.7


@pytest.fixture(scope="module")
def golden(tmp_path_factory):
    out = tmp_path_factory.mktemp("golden-chunks")
    generate(out, ("clean",), {"it-boiler-manual", "en-saas-agreement", "it-budget-2026",
                               "it-project-kickoff"})
    return Golden.load(out)


@pytest.mark.parametrize("config", list(chunking.CONFIGS))
def test_every_config_covers_every_answer_span(golden, config):
    chunks = chunking.build(golden, config)
    qrels = chunking.chunk_qrels(golden, chunks)
    for q in golden.queries:
        if q["answerable"]:
            assert any(g >= 2 for g in qrels[q["qid"]].values()), (config, q["qid"])
    assert len({c.chunk_id for c in chunks}) == len(chunks)


def test_baseline_is_one_chunk_per_pdf_page_and_has_no_heading_prefix(golden):
    chunks = [c for c in chunking.build(golden, "nubarca-char1600")
              if c.doc_id == "it-boiler-manual"]
    assert len(chunks) == 5 and all(c.heading == "" for c in chunks)
    assert chunks[0].text.startswith("Termoviva CX 24 — Manuale")  # running header kept


def test_structured_drops_furniture_and_repeats_table_headers(golden):
    chunks = chunking.chunk_structured(golden, 64, 0)
    boiler = [c for c in chunks if c.doc_id == "it-boiler-manual"]
    assert not any("Rev. 03" in c.text for c in boiler)
    tables = [c for c in boiler if c.kind == "table" and "E25" in c.text]
    assert tables and tables[0].text.splitlines()[2].startswith("Codice | Tipo")


def test_xlsx_baseline_renders_header_equals_value(golden):
    sheet = [c for c in chunking.build(golden, "nubarca-char1600") if c.doc_id == "it-budget-2026"]
    assert "Reparto=IT | Consuntivo 2025=690.000" in sheet[0].text


def test_pptx_notes_are_their_own_baseline_chunk(golden):
    chunks = [c for c in chunking.build(golden, "nubarca-char1600")
              if c.doc_id == "it-project-kickoff"]
    assert any(c.kind == "notes" and "picco natalizio" in c.text for c in chunks)
