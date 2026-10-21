"""Ranked-retrieval metrics, bootstrap intervals and a paired significance test.

Conventions (stated once, used everywhere):
- A judgement is a grade: 2 answers the query, 1 is related, absent is 0.
- Recall@k, MRR and hit@k count only grade >= 2 ("answer-bearing") items, so
  finding the related-but-not-answering page is not rewarded as a hit.
- nDCG@k uses graded gains 2^g - 1, so the related page still earns credit.
- Unanswerable queries have no grade-2 item and are excluded from these
  aggregates; they are scored separately by `abstention`.
"""

from __future__ import annotations

import math
from collections import defaultdict
from typing import Iterable, Mapping, Sequence

import numpy as np

HIT = 2


def recall_at_k(ranked: Sequence[str], grades: Mapping[str, int], k: int) -> float:
    relevant = {i for i, g in grades.items() if g >= HIT}
    if not relevant:
        return float("nan")
    return len(relevant.intersection(ranked[:k])) / len(relevant)


def hit_at_k(ranked: Sequence[str], grades: Mapping[str, int], k: int) -> float:
    if not any(g >= HIT for g in grades.values()):
        return float("nan")
    return 1.0 if any(grades.get(i, 0) >= HIT for i in ranked[:k]) else 0.0


def dcg(gains: Iterable[float]) -> float:
    return sum(g / math.log2(i + 2) for i, g in enumerate(gains))


def ndcg_at_k(ranked: Sequence[str], grades: Mapping[str, int], k: int) -> float:
    if not any(g >= HIT for g in grades.values()):
        return float("nan")
    gains = [2 ** grades.get(i, 0) - 1 for i in ranked[:k]]
    ideal = sorted((2 ** g - 1 for g in grades.values()), reverse=True)[:k]
    best = dcg(ideal)
    return dcg(gains) / best if best > 0 else 0.0


def mrr(ranked: Sequence[str], grades: Mapping[str, int], depth: int = 100) -> float:
    if not any(g >= HIT for g in grades.values()):
        return float("nan")
    for i, item in enumerate(ranked[:depth]):
        if grades.get(item, 0) >= HIT:
            return 1.0 / (i + 1)
    return 0.0


METRICS = {
    "recall@5": lambda r, g: recall_at_k(r, g, 5),
    "recall@10": lambda r, g: recall_at_k(r, g, 10),
    "recall@20": lambda r, g: recall_at_k(r, g, 20),
    "ndcg@5": lambda r, g: ndcg_at_k(r, g, 5),
    "ndcg@10": lambda r, g: ndcg_at_k(r, g, 10),
    "mrr": lambda r, g: mrr(r, g),
    "hit@1": lambda r, g: hit_at_k(r, g, 1),
}


def dedupe(ranked: Iterable[str]) -> list[str]:
    seen: set[str] = set()
    out = []
    for item in ranked:
        if item not in seen:
            seen.add(item)
            out.append(item)
    return out


def per_query(run: Mapping[str, Sequence[str]], qrels: Mapping[str, Mapping[str, int]]
              ) -> dict[str, dict[str, float]]:
    """{qid: {metric: value}} for every answerable query in qrels."""
    out = {}
    for qid, grades in qrels.items():
        if not any(g >= HIT for g in grades.values()):
            continue
        ranked = dedupe(run.get(qid, []))
        out[qid] = {name: fn(ranked, grades) for name, fn in METRICS.items()}
    return out


def bootstrap_ci(values: Sequence[float], resamples: int = 2000, seed: int = 20251021,
                 alpha: float = 0.05) -> tuple[float, float, float]:
    """Mean and percentile bootstrap (1 - alpha) interval over queries."""
    arr = np.asarray([v for v in values if not math.isnan(v)], dtype=np.float64)
    if arr.size == 0:
        return float("nan"), float("nan"), float("nan")
    rng = np.random.default_rng(seed)
    idx = rng.integers(0, arr.size, size=(resamples, arr.size))
    means = arr[idx].mean(axis=1)
    lo, hi = np.quantile(means, [alpha / 2, 1 - alpha / 2])
    return float(arr.mean()), float(lo), float(hi)


def paired_test(a: Sequence[float], b: Sequence[float], permutations: int = 10000,
                seed: int = 20251021) -> dict[str, float]:
    """Two-sided paired randomization (sign-flip) test of mean(b - a).

    Also returns a bootstrap CI of the mean difference, because a p-value alone
    says nothing about how large the change is.
    """
    diff = np.asarray(b, dtype=np.float64) - np.asarray(a, dtype=np.float64)
    diff = diff[~np.isnan(diff)]
    if diff.size == 0:
        return {"delta": float("nan"), "p_value": float("nan"), "ci_low": float("nan"),
                "ci_high": float("nan"), "n": 0}
    observed = abs(diff.mean())
    rng = np.random.default_rng(seed)
    signs = rng.choice((-1.0, 1.0), size=(permutations, diff.size))
    null = np.abs((signs * diff).mean(axis=1))
    p_value = (1 + np.count_nonzero(null >= observed - 1e-12)) / (permutations + 1)
    mean, lo, hi = bootstrap_ci(diff.tolist(), seed=seed)
    return {"delta": mean, "p_value": float(p_value), "ci_low": lo, "ci_high": hi,
            "n": int(diff.size)}


def aggregate(per_q: Mapping[str, Mapping[str, float]], query_types: Mapping[str, Sequence[str]],
              resamples: int = 2000) -> dict:
    """Overall and per-query-type means with bootstrap CIs."""
    def summarize(qids: list[str]) -> dict:
        out = {"n": len(qids)}
        for name in METRICS:
            mean, lo, hi = bootstrap_ci([per_q[q][name] for q in qids], resamples=resamples)
            out[name] = {"mean": mean, "ci_low": lo, "ci_high": hi}
        return out

    qids = sorted(per_q)
    by_type: dict[str, list[str]] = defaultdict(list)
    for q in qids:
        for t in query_types.get(q, ()):
            by_type[t].append(q)
    return {"overall": summarize(qids),
            "by_type": {t: summarize(v) for t, v in sorted(by_type.items())}}


def abstention(top_scores: Mapping[str, float], answerable: Mapping[str, bool]) -> dict:
    """How well the top-1 score separates answerable from unanswerable queries.

    This is the input to a per-profile minimum-score gate: AUC says whether any
    threshold can work, and the sweep says which threshold keeps 95% of the
    answerable queries and how many unanswerable ones it still lets through.
    """
    pos = [s for q, s in top_scores.items() if answerable.get(q)]
    neg = [s for q, s in top_scores.items() if not answerable.get(q)]
    if not pos or not neg:
        return {"auc": float("nan"), "n_answerable": len(pos), "n_unanswerable": len(neg)}
    wins = sum((p > n) + 0.5 * (p == n) for p in pos for n in neg)
    auc = wins / (len(pos) * len(neg))
    keep95 = float(np.quantile(pos, 0.05))
    return {
        "auc": auc,
        "n_answerable": len(pos),
        "n_unanswerable": len(neg),
        "threshold_keep95": keep95,
        "unanswerable_passing_keep95": sum(1 for n in neg if n >= keep95) / len(neg),
        "answerable_top1_median": float(np.median(pos)),
        "unanswerable_top1_median": float(np.median(neg)),
    }
