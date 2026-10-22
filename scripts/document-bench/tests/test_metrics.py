"""Metric implementations checked against hand-computed values and naive code."""

import math

import pytest

from docbench.metrics import ocr, retrieval, tables


def naive_levenshtein(a, b):
    prev = list(range(len(b) + 1))
    for i, x in enumerate(a, 1):
        cur = [i]
        for j, y in enumerate(b, 1):
            cur.append(min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (x != y)))
        prev = cur
    return prev[-1]


# ---- retrieval ----------------------------------------------------------------

def test_recall_counts_only_answer_bearing_items():
    grades = {"a": 2, "b": 2, "c": 1}
    assert retrieval.recall_at_k(["c", "a", "x"], grades, 2) == 0.5
    assert retrieval.recall_at_k(["c", "a", "b"], grades, 3) == 1.0


def test_mrr_skips_related_but_not_answering():
    assert retrieval.mrr(["c", "x", "a"], {"a": 2, "c": 1}) == pytest.approx(1 / 3)
    assert retrieval.mrr(["x", "y"], {"a": 2}) == 0.0


def test_ndcg_hand_computed():
    grades = {"a": 2, "b": 1}
    # ranked: b (gain 1) at rank 1, a (gain 3) at rank 2
    dcg = 1 / math.log2(2) + 3 / math.log2(3)
    ideal = 3 / math.log2(2) + 1 / math.log2(3)
    assert retrieval.ndcg_at_k(["b", "a"], grades, 5) == pytest.approx(dcg / ideal)
    assert retrieval.ndcg_at_k(["a", "b"], grades, 5) == pytest.approx(1.0)


def test_unanswerable_queries_are_nan_and_excluded():
    assert math.isnan(retrieval.recall_at_k(["a"], {}, 5))
    pq = retrieval.per_query({"q1": ["a"], "q2": ["b"]}, {"q1": {"a": 2}, "q2": {}})
    assert set(pq) == {"q1"}


def test_dedupe_keeps_first_occurrence():
    assert retrieval.dedupe(["a", "b", "a", "c", "b"]) == ["a", "b", "c"]


def test_bootstrap_ci_brackets_mean_and_is_deterministic():
    values = [0, 1] * 50
    first = retrieval.bootstrap_ci(values)
    assert first == retrieval.bootstrap_ci(values)
    mean, lo, hi = first
    assert mean == 0.5 and lo < 0.5 < hi


def test_paired_test_detects_consistent_improvement_and_not_noise():
    a = [0.0] * 40
    b = [1.0] * 40
    assert retrieval.paired_test(a, b)["p_value"] < 0.001
    same = retrieval.paired_test([0.3, 0.6, 0.9, 0.1] * 10, [0.3, 0.6, 0.9, 0.1] * 10)
    assert same["delta"] == 0.0 and same["p_value"] == 1.0


def test_abstention_auc_perfect_separation():
    result = retrieval.abstention({"a": 0.9, "b": 0.8, "u": 0.2}, {"a": True, "b": True, "u": False})
    assert result["auc"] == 1.0
    assert result["unanswerable_passing_keep95"] == 0.0


# ---- OCR ----------------------------------------------------------------------

@pytest.mark.parametrize("ref,hyp", [
    ("kitten", "sitting"), ("Contratto di locazione", "Contrato di locazione"),
    ("abc", ""), ("euro 950,00", "euro 95O,00"),
])
def test_cer_matches_naive_levenshtein(ref, hyp):
    assert ocr.cer(ref, hyp) == pytest.approx(naive_levenshtein(ref, hyp) / len(ref))


def test_wer_is_word_level():
    assert ocr.wer("il canone è mensile", "il canone e mensile") == pytest.approx(0.25)
    assert ocr.wer("a b c d", "a b c d") == 0.0


def test_markup_is_not_penalised():
    ref = "Codice | Tipo\nE10 | Arresto"
    hyp = "<table><tr><td>Codice</td><td>Tipo</td></tr><tr><td>E10</td><td>Arresto</td></tr></table>"
    assert ocr.cer(ref, hyp) == 0.0
    assert ocr.cer("Titolo", "## **Titolo**") == 0.0


def test_furniture_lines_are_dropped_from_hypothesis():
    hyp = "Pagina 1 di 4\nTesto vero\nRep. n. 4471/2025"
    out = ocr.drop_furniture(hyp, ["Pagina 1 di 4", "Rep. n. 4471/2025"])
    assert out == "Testo vero"


def test_reading_order_error_detects_swapped_columns():
    blocks = ["Prima colonna con testo lungo abbastanza", "Seconda colonna con altro testo lungo",
              "Terza parte del documento in fondo alla pagina"]
    assert ocr.reading_order_error(blocks, "\n".join(blocks)) == 0.0
    swapped = "\n".join([blocks[1], blocks[0], blocks[2]])
    assert ocr.reading_order_error(blocks, swapped) == pytest.approx(1 / 3)


def test_hallucination_and_omission_rates():
    text = "Il canone annuo è convenuto in euro\nLa caldaia si blocca sotto 0,6 bar"
    truth = {"text": text, "body_text": text, "furniture": [], "blocks": [], "ignorable": []}
    perfect = ocr.score_page(truth, text)
    assert perfect["omission_rate"] == 0.0 and perfect["hallucination_rate"] == 0.0
    invented = ocr.score_page(truth, text + "\nTotale da pagare 9999 inventato")
    assert invented["hallucination_rate"] > 0.3
    misread = ocr.score_page(truth, text.replace("canone", "canonc"))
    assert misread["hallucination_rate"] == 0.0
    missing = ocr.score_page(truth, "Il canone annuo è convenuto in euro")
    assert missing["omission_rate"] == pytest.approx(0.5, abs=0.06)


def test_formula_match_accepts_latex_or_plain():
    f = {"latex": r"E = \varepsilon \sigma T^4", "plain": "E = ε σ T⁴"}
    assert ocr.formula_score(f, "$$E = \\varepsilon \\sigma T^{4}$$") >= 0.8
    assert ocr.formula_score(f, "E = ε σ T⁴") == 1.0
    assert ocr.formula_score(f, "nessuna formula qui") < 0.8


# ---- tables / TEDS -----------------------------------------------------------

ROWS = [
    [{"text": "A", "colspan": 2, "rowspan": 1}],
    [{"text": "b", "colspan": 1, "rowspan": 1}, {"text": "c", "colspan": 1, "rowspan": 1}],
]


def test_teds_identical_is_one():
    t = tables.tree_from_rows(ROWS)
    assert tables.teds(t, tables.tree_from_rows(ROWS)) == 1.0


def test_teds_hand_computed_missing_cell():
    truth = tables.tree_from_rows(ROWS)  # table, 2 tr, 3 td = 6 nodes
    pred = tables.tree_from_rows([ROWS[0], [ROWS[1][0]]])  # one td fewer = 5 nodes
    assert tables.teds(truth, pred) == pytest.approx(1 - 1 / 6)


def test_teds_span_mismatch_costs_full_rename():
    pred_rows = [[{"text": "A", "colspan": 1, "rowspan": 1}], ROWS[1]]
    truth = tables.tree_from_rows(ROWS)
    pred = tables.tree_from_rows(pred_rows)
    assert tables.teds(truth, pred) == pytest.approx(1 - 1 / 6)
    assert tables.teds(truth, pred, structure_only=True) == pytest.approx(1 - 1 / 6)


def test_teds_content_error_is_partial():
    pred_rows = [ROWS[0], [{"text": "b", "colspan": 1, "rowspan": 1},
                           {"text": "x", "colspan": 1, "rowspan": 1}]]
    truth = tables.tree_from_rows(ROWS)
    pred = tables.tree_from_rows(pred_rows)
    assert tables.teds(truth, pred) == pytest.approx(1 - 1 / 6)
    assert tables.teds(truth, pred, structure_only=True) == 1.0


def test_parsers_agree_on_the_same_table():
    html = '<table><tr><th colspan="2">A</th></tr><tr><td>b</td><td>c</td></tr></table>'
    otsl = "<otsl><fcel>A<lcel><nl><fcel>b<fcel>c<nl></otsl>"
    truth = tables.tree_from_rows(ROWS)
    assert tables.teds(truth, tables.extract_tables(html)[0]) == 1.0
    assert tables.teds(truth, tables.extract_tables(otsl)[0]) == 1.0
    md = "| A | |\n|---|---|\n| b | c |"
    parsed = tables.extract_tables(md)[0]
    assert len(parsed.children) == 2


def test_otsl_rowspan():
    otsl = "<fcel>x<fcel>y<nl><ucel><fcel>z<nl>"
    rows = tables.otsl_tables(otsl)[0]
    assert rows[0][0]["rowspan"] == 2 and rows[1][0]["text"] == "z"


def test_no_predicted_table_scores_zero():
    truth = tables.tree_from_rows(ROWS)
    assert tables.match_tables([truth], []) == [{"teds": 0.0, "teds_s": 0.0, "matched": False}]
