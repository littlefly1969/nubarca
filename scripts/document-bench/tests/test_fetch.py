"""The lock and the fetch gate: what may be downloaded, and how a late pin is recorded."""

import json
import sys
from argparse import Namespace
from types import SimpleNamespace

import pytest

from docbench import fetch as fetch_mod
from docbench.lanes.common import LOCK
from docbench.ocr_engines import ENGINE_LOCK, ENGINES
from docbench.visual_encoders import VISUAL_MODELS

SHA = "0123456789abcdef0123456789abcdef01234567"


def _models():
    return json.loads(LOCK.read_text())["models"]


def test_every_entry_is_pinned_or_explicitly_pinned_at_fetch():
    for key, entry in _models().items():
        revision = entry["revision"]
        assert fetch_mod._is_sha(revision) or revision == "TODO", key
        if entry.get("pin_at_fetch"):
            assert revision == "TODO", f"{key}: pinned already, drop pin_at_fetch"


def test_review_shortlist_is_in_the_lock_and_wired():
    models = _models()
    for key in ("paddleocr-vl-1.6", "ovisocr2", "wevisdoc-2b"):
        assert models[key]["lane"] == "ocr"
        assert key in ENGINES and ENGINE_LOCK[key] == key
    assert models["wemm-emb-2b"]["lane"] == "visual"
    assert "wemm-emb-2b" in VISUAL_MODELS
    # A licence that is not settled says so, in the lock itself.
    assert models["wemm-emb-2b"]["license"].startswith("UNRESOLVED")


def test_unpinned_entries_are_refused_without_the_flag(tmp_path, capsys):
    args = Namespace(only="paddleocr-vl-1.6,qwen3-emb-0.6b-onnx", include_optional=False,
                     dry_run=False, dest=tmp_path, pin_unpinned=False)
    assert fetch_mod.fetch(args) == 1
    out = capsys.readouterr().out
    assert "REFUSED paddleocr-vl-1.6" in out and "--pin-unpinned" in out
    assert "REFUSED qwen3-emb-0.6b-onnx" in out
    assert not (tmp_path / "PINNED.json").exists()


def test_pin_unpinned_only_touches_pin_at_fetch_entries(tmp_path, capsys, monkeypatch):
    calls = []

    class FakeApi:
        def model_info(self, repo):
            calls.append(repo)
            return SimpleNamespace(sha=SHA, card_data={"license": "apache-2.0"})

    # A stand-in module: the test needs neither the network nor the package.
    monkeypatch.setitem(sys.modules, "huggingface_hub", SimpleNamespace(HfApi=FakeApi))
    args = Namespace(only="paddleocr-vl-1.6,qwen3-emb-0.6b-onnx", include_optional=False,
                     dry_run=True, dest=tmp_path, pin_unpinned=True)
    assert fetch_mod.fetch(args) == 0  # dry run: refusals are reported, not fatal
    out = capsys.readouterr().out
    assert f"PINNED paddleocr-vl-1.6: PaddlePaddle/PaddleOCR-VL-1.6@{SHA}" in out
    # An ordinary TODO is still refused: the flag is not a blanket "latest".
    assert "REFUSED qwen3-emb-0.6b-onnx" in out
    assert calls == ["PaddlePaddle/PaddleOCR-VL-1.6"]


def test_resolve_pin_fails_closed_and_reports_licence_mismatch():
    entry = {"repo": "x/y", "license": "Apache-2.0"}
    with pytest.raises(SystemExit):
        fetch_mod.resolve_pin(entry, SimpleNamespace(sha="main", card_data=None))
    sha, licence = fetch_mod.resolve_pin(entry, SimpleNamespace(sha=SHA, card_data={"license": "other"}))
    assert sha == SHA and licence == "other"
