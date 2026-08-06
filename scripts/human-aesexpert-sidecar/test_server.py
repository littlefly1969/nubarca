"""Contract + safety tests for the HumanAesExpert sidecar (FAKE-model mode).

Run with:  HUMANAES_FAKE=1 python -m pytest scripts/human-aesexpert-sidecar

These tests NEVER load the real weights (HUMANAES_FAKE=1). They lock down the
response contract, the Expert-head key mapping, the concurrency/queue guard, and
input rejection — the behaviors the .NET strict validator depends on.
"""
import os

os.environ["HUMANAES_FAKE"] = "1"

import pytest
from starlette.testclient import TestClient

import server  # noqa: E402


EXPECTED_KEYS = [
    "facial_brightness",
    "facial_feature_clarity",
    "facial_skin_tone",
    "facial_structure",
    "facial_contour_clarity",
    "facial_aesthetic",
    "outfit",
    "body_shape",
    "looks",
    "environment",
    "general_appearance_aesthetic",
    "overall_aesthetic",
]

# A 1x1 PNG (bytes content is irrelevant to fake scoring beyond its digest).
PNG_1PX = bytes.fromhex(
    "89504e470d0a1a0a0000000d4948445200000001000000010806000000"
    "1f15c4890000000d4944415478da6360000002000001e221bc330000000049454e44ae426082"
)


@pytest.fixture()
def client():
    with TestClient(server.app) as c:
        yield c


def _analyze(client, caps="expert_scores", contract="1", image=PNG_1PX):
    return client.post(
        "/analyze",
        data={
            "contractVersion": contract,
            "profileKey": "human-aesexpert-1b-expert-v1",
            "capabilities": caps,
            "language": "it",
            "preprocessingProfileKey": "human-aesexpert-official-v1",
        },
        files={"image": ("image", image, "image/png")},
    )


def test_health_ready_in_fake_mode(client):
    assert client.get("/health").json()["status"] == "ready"
    assert client.get("/ready").status_code == 200


def test_analyze_contract_shape(client):
    resp = _analyze(client)
    assert resp.status_code == 200
    body = resp.json()
    assert body["contractVersion"] == 1
    assert body["profileKey"] == "human-aesexpert-1b-expert-v1"
    assert body["completedCapabilities"] == ["expert_scores"]
    assert body["preprocessingProfileKey"] == "human-aesexpert-official-v1"
    assert body["modelName"] == "KlingTeam/HumanAesExpert-1B"
    assert isinstance(body["durationMs"], int)
    assert body["texts"] == []


def test_expert_head_mapping_and_scale(client):
    body = _analyze(client).json()
    keys = [m["key"] for m in body["metrics"]]
    assert keys == EXPECTED_KEYS  # exact order + set
    for m in body["metrics"]:
        assert m["scaleMin"] == 0.0 and m["scaleMax"] == 1.0
        assert 0.0 <= m["value"] <= 1.0
        assert m["version"] == 1
        assert m["confidence"] is None


def test_scores_are_deterministic_for_same_image(client):
    a = _analyze(client).json()["metrics"]
    b = _analyze(client).json()["metrics"]
    assert [m["value"] for m in a] == [m["value"] for m in b]


def test_rejects_wrong_contract_version(client):
    assert _analyze(client, contract="2").status_code == 400


def test_rejects_unsupported_capability(client):
    assert _analyze(client, caps="text_assessment").status_code == 400
    assert _analyze(client, caps="expert_scores,meta_voter").status_code == 400


def test_rejects_missing_image(client):
    resp = client.post(
        "/analyze",
        data={
            "contractVersion": "1",
            "profileKey": "p",
            "capabilities": "expert_scores",
            "language": "it",
            "preprocessingProfileKey": "human-aesexpert-official-v1",
        },
    )
    assert resp.status_code == 400


def test_busy_queue_returns_429(client, monkeypatch):
    # Simulate a full wait queue: the guard returns 429 before doing any work.
    monkeypatch.setattr(server, "_waiting", server.MAX_QUEUE)
    assert _analyze(client).status_code == 429
