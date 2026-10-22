"""A Python mirror of NubArca's private-document lexical ranker.

Mirrors src/NubArca.Api/Rag/Text/RagText.cs (fold accents, lowercase, split on
non-alphanumerics, drop 1-character tokens and the IT+EN stopword set, no
stemming) and RagLexicalIndex.cs with the UserDocuments ranking profile
(BM25F, k1 1.2, b 0.75, title and section weight 2.5, body 1.0, body-length
normalisation only). `search(..., gated=True)` additionally applies the
acceptance rules (matched-term share, minimum score 0.35, relative floor 0.25).

It is a mirror for MEASUREMENT, kept deliberately close to the C# so that the
lexical lane predicts what NubArca does; if the C# changes, this must follow.
"""

from __future__ import annotations

import math
import unicodedata
from collections import Counter
from dataclasses import dataclass

STOPWORDS = frozenset("""
il lo la le gli un uno una di del dello della dei degli delle da dal dalla in nel nella con su sul
sulla per tra fra che chi cui non piu quale quali quando dove come cosa questo questa questi queste
quello quella si no ci vi ne mi ti se sono sei essere stato ho hai ha abbiamo hanno avere faccio fai
fa fare posso puoi puo possiamo devo deve voglio vuoi anche ancora sempre poi ed al allo alla ai agli
alle
the a an and or but if of to in on at by for with from as is are was were be been being do does did
doing have has had can could should would will shall may might must this that these those it its i
you he she we they my your our their there here what which who whom when where why how not no yes so
than then too very just also into about over any all some more most other
""".split())

K1 = 1.2
B = 0.75
TITLE_WEIGHT = 2.5
SECTION_WEIGHT = 2.5
BODY_WEIGHT = 1.0
MINIMUM_SCORE = 0.35
RELATIVE_FLOOR = 0.25


def fold(text: str) -> str:
    decomposed = unicodedata.normalize("NFD", text)
    kept = "".join(ch for ch in decomposed if unicodedata.category(ch) != "Mn")
    return unicodedata.normalize("NFC", kept)


def tokenize(text: str) -> list[str]:
    tokens, current = [], []
    for ch in fold(text or ""):
        if ch.isalnum():
            current.append(ch.lower())
        elif current:
            if len(current) > 1:
                tokens.append("".join(current))
            current = []
    if len(current) > 1:
        tokens.append("".join(current))
    return tokens


def content_tokens(text: str) -> list[str]:
    return [t for t in tokenize(text) if t not in STOPWORDS]


@dataclass
class _Indexed:
    key: str
    title: Counter
    section: Counter
    body: Counter
    body_length: int


class BM25F:
    def __init__(self, docs: list[tuple[str, str, str, str]]):
        """docs: (key, title, section, body)."""
        self.items = []
        for key, title, section, body in docs:
            body_tokens = content_tokens(body)
            self.items.append(_Indexed(key, Counter(content_tokens(title)),
                                       Counter(content_tokens(section)), Counter(body_tokens),
                                       max(1, len(body_tokens))))
        self.avg_body = max(1.0, sum(i.body_length for i in self.items) / max(1, len(self.items)))
        containing: Counter = Counter()
        for item in self.items:
            containing.update(set(item.title) | set(item.section) | set(item.body))
        n = len(self.items)
        self.idf = {t: max(0.01, math.log(1 + (n - c + 0.5) / (c + 0.5)))
                    for t, c in containing.items()}

    def _score(self, item: _Indexed, terms: list[str]) -> tuple[float, int, bool]:
        score, matched, high = 0.0, 0, False
        norm = 1 - B + B * item.body_length / self.avg_body
        for term in terms:
            idf = self.idf.get(term)
            if idf is None:
                continue
            t, s, b = item.title[term], item.section[term], item.body[term]
            if t + s + b == 0:
                continue
            tf = TITLE_WEIGHT * t + SECTION_WEIGHT * s + BODY_WEIGHT * b / norm
            score += idf * tf * (K1 + 1) / (tf + K1)
            matched += 1
            high |= (t + s) > 0
        return score, matched, high

    def search(self, query: str, take: int = 100, gated: bool = False) -> list[tuple[str, float]]:
        terms = list(dict.fromkeys(content_tokens(query)))
        if not terms:
            return []
        scored = []
        for item in self.items:
            score, matched, high = self._score(item, terms)
            if score > 0:
                scored.append((item.key, score, matched, high))
        if gated:
            required = 1 if len(terms) <= 1 else min(3, max(1, math.ceil(len(terms) * 0.4)))
            scored = [s for s in scored
                      if s[1] >= MINIMUM_SCORE and s[2] >= required and (s[3] or s[2] >= 2)]
            if scored:
                best = max(s[1] for s in scored)
                scored = [s for s in scored if s[1] >= best * RELATIVE_FLOOR]
        scored.sort(key=lambda s: (-s[1], s[0]))
        return [(key, score) for key, score, _, _ in scored[:take]]


def rrf(rankings: list[list[str]], k: int = 60, weights: list[float] | None = None,
        take: int = 100) -> list[tuple[str, float]]:
    """Reciprocal rank fusion, ranks 1-based; ties broken by key for determinism."""
    weights = weights or [1.0] * len(rankings)
    scores: dict[str, float] = {}
    for ranking, weight in zip(rankings, weights):
        for rank, key in enumerate(ranking, start=1):
            scores[key] = scores.get(key, 0.0) + weight / (k + rank)
    return sorted(scores.items(), key=lambda kv: (-kv[1], kv[0]))[:take]
