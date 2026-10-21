"""Text-recognition metrics for one page: CER, WER, NED, reading order,
omissions, hallucinations, formula match and tables (via `tables`).

Normalisation is deliberately MARKUP-BLIND and FURNITURE-AWARE:
- Markdown/HTML/LaTeX markup that VLM engines emit (#, **, |, <td>, $...$) is
  removed from both sides before character metrics, so a parser is not
  penalised for producing structure a plain OCR engine cannot.
- Running headers and footers are removed from the hypothesis (fuzzy, by line)
  and are absent from the body reference, so an engine told to skip page
  furniture (Xiaomi-OCR's prompt does) is not charged with omissions for it.
"""

from __future__ import annotations

import html
import re
import unicodedata
from itertools import combinations

from rapidfuzz import fuzz
from rapidfuzz.distance import Levenshtein

from .tables import extract_tables, match_tables, tree_from_rows

_TAG = re.compile(r"<[^>]+>")
_LATEX_CMD = {
    r"\lambda": "λ", r"\varepsilon": "ε", r"\epsilon": "ε", r"\sigma": "σ", r"\beta": "β",
    r"\alpha": "α", r"\theta": "θ", r"\sum": "Σ", r"\prod": "Π", r"\eta": "η",
    r"\cdot": "·", r"\times": "×", r"\approx": "≈", r"\le": "≤", r"\ge": "≥", r"\pi": "π",
    r"\mu": "µ", r"\Delta": "Δ", r"\infty": "∞",
}


def strip_markup(text: str) -> str:
    text = html.unescape(_TAG.sub(" ", text))
    text = re.sub(r"<(fcel|ecel|lcel|ucel|xcel|nl)>", " ", text)
    text = re.sub(r"(?m)^\s{0,3}#{1,6}\s*", "", text)  # markdown headings
    text = re.sub(r"\*\*|__|`", "", text)
    text = re.sub(r"(?m)^\s*\|?\s*:?-{2,}:?(\s*\|\s*:?-{2,}:?)*\s*\|?\s*$", "", text)
    text = text.replace("|", " ")
    text = re.sub(r"(?m)^\s*[•\-\*]\s+", "", text)  # bullets
    text = text.replace("$$", " ").replace("$", " ")
    text = re.sub(r"\\[\(\)\[\]]", " ", text)
    return text


def normalize(text: str) -> str:
    text = unicodedata.normalize("NFC", strip_markup(text))
    return re.sub(r"\s+", " ", text).strip()


def words(text: str) -> list[str]:
    return normalize(text).split()


def loose(text: str) -> str:
    """Case- and punctuation-insensitive form (for the lenient CER)."""
    text = normalize(text).casefold()
    text = "".join(ch for ch in text if ch.isalnum() or ch.isspace())
    return re.sub(r"\s+", " ", text).strip()


def cer(reference: str, hypothesis: str) -> float:
    ref, hyp = normalize(reference), normalize(hypothesis)
    if not ref:
        return 0.0 if not hyp else 1.0
    return Levenshtein.distance(ref, hyp) / len(ref)


def wer(reference: str, hypothesis: str) -> float:
    ref, hyp = words(reference), words(hypothesis)
    if not ref:
        return 0.0 if not hyp else 1.0
    return Levenshtein.distance(ref, hyp) / len(ref)


def ned(reference: str, hypothesis: str) -> float:
    return Levenshtein.normalized_distance(normalize(reference), normalize(hypothesis))


def drop_furniture(hypothesis: str, furniture: list[str], threshold: float = 85.0) -> str:
    if not furniture:
        return hypothesis
    keep = []
    for line in hypothesis.splitlines():
        n = normalize(line)
        if n and any(fuzz.ratio(n, normalize(f)) >= threshold for f in furniture):
            continue
        keep.append(line)
    return "\n".join(keep)


def reading_order_error(blocks: list[str], hypothesis: str, min_score: float = 70.0) -> float:
    """Normalised Kendall-tau distance between the reference block order and the
    order in which those blocks are found in the hypothesis (0 = same order).
    Blocks that cannot be located are left out here and counted as omissions."""
    hyp = normalize(hypothesis)
    positions = []
    for block in blocks:
        b = normalize(block)
        if len(b) < 12:
            continue
        probe = b[:160]
        align = fuzz.partial_ratio_alignment(probe, hyp)
        if align is not None and align.score >= min_score:
            positions.append(align.dest_start)
    pairs = list(combinations(range(len(positions)), 2))
    if not pairs:
        return 0.0
    discordant = sum(1 for i, j in pairs if positions[i] > positions[j])
    return discordant / len(pairs)


def line_coverage(lines: list[str], text: str, threshold: float = 70.0) -> tuple[int, int]:
    """(characters of `lines` NOT found in `text`, total characters)."""
    target = normalize(text)
    missing = total = 0
    for line in lines:
        n = normalize(line)
        if len(re.sub(r"\W", "", n)) < 3:
            continue
        total += len(n)
        if not target or fuzz.partial_ratio(n, target) < threshold:
            missing += len(n)
    return missing, total


def formula_form(text: str) -> str:
    for cmd, char in _LATEX_CMD.items():
        text = text.replace(cmd, char)
    text = re.sub(r"\\(frac|dfrac|tfrac|left|right|mathrm|text|mathit|operatorname)", " ", text)
    text = text.replace("−", "-").replace("⁴", "^4").replace("exp", "e^")
    return re.sub(r"[\s{}\\]", "", text)


def formula_score(formula: dict, hypothesis: str) -> float:
    hyp = formula_form(hypothesis)
    if not hyp:
        return 0.0
    return max(fuzz.partial_ratio(formula_form(formula["plain"]), hyp),
               fuzz.partial_ratio(formula_form(formula["latex"]), hyp)) / 100.0


def score_page(truth: dict, hypothesis: str) -> dict:
    """All per-page metrics. `truth` is a row of truth/pages.jsonl."""
    body_ref = truth["body_text"]
    hyp = drop_furniture(hypothesis or "", truth.get("furniture", []))
    ref_words = words(body_ref)
    hyp_words = words(hyp)

    omitted, ref_total = line_coverage(body_ref.splitlines(), hyp)
    allowed = "\n".join([truth["text"], *truth.get("ignorable", [])])
    invented, hyp_total = line_coverage(
        [line for line in strip_markup(hyp).splitlines()], allowed)

    ref_bag: dict[str, int] = {}
    for w in (loose(x) for x in ref_words):
        ref_bag[w] = ref_bag.get(w, 0) + 1
    found = 0
    for w in (loose(x) for x in hyp_words):
        if ref_bag.get(w, 0) > 0:
            ref_bag[w] -= 1
            found += 1

    result = {
        "cer": cer(body_ref, hyp),
        "cer_loose": (Levenshtein.distance(loose(body_ref), loose(hyp)) / max(1, len(loose(body_ref)))),
        "wer": wer(body_ref, hyp),
        "ned": ned(body_ref, hyp),
        "word_recall": found / max(1, len(ref_words)),
        "reading_order_error": reading_order_error(
            [b["text"] for b in truth.get("blocks", []) if b["kind"] in ("H", "P", "L", "N")], hyp),
        "omission_rate": omitted / max(1, ref_total),
        "hallucination_rate": invented / max(1, hyp_total),
        "ref_chars": len(normalize(body_ref)),
        "hyp_chars": len(normalize(hyp)),
    }
    formulas = truth.get("formulas", [])
    if formulas:
        scores = [formula_score(f, hypothesis or "") for f in formulas]
        result["formula_score"] = sum(scores) / len(scores)
        result["formula_match"] = sum(1 for s in scores if s >= 0.8) / len(scores)
    tables = truth.get("tables", [])
    if tables:
        truths = [tree_from_rows(t["rows"]) for t in tables]
        matched = match_tables(truths, extract_tables(hypothesis or ""))
        result["teds"] = sum(m["teds"] for m in matched) / len(matched)
        result["teds_s"] = sum(m["teds_s"] for m in matched) / len(matched)
        result["tables_found"] = sum(1 for m in matched if m["matched"]) / len(matched)
    return result
