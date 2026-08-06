#!/usr/bin/env python3
"""On-host NLU sidecar benchmark (LOCAL only, synthetic corpus).

Drives the running NLU sidecar with a corpus and scores structured-command
accuracy + warm latency with the SAME semantics as the C# GalleryCommandBenchmark
(operation, person spans+modes, people logic, dates, metadata-vs-semantic,
favourite/rating/gps/sort). Enforces the production contract: strict JSON with at
most ONE local repair attempt; valid-DTO rate is measured after repair.

Privacy: NEVER prints command text or raw model output. Only case ids + field
misses + aggregate numbers. No network except the internal sidecar URL.

Usage:
  python3 benchmark.py --sidecar http://127.0.0.1:8090 \
      --corpus dev.json holdout.json --warm 3 --out report.json
"""
import argparse
import json
import statistics
import time
import unicodedata
import urllib.request

REFERENCE_NOW = "2026-07-12T12:00:00Z"

# Few-shot system prompt. Examples are authored from the DEV corpus only (the
# holdout is scored once, untouched). Teaches: operation values, person modes,
# all/any, date ISO expansion, metadata-vs-semantic, refine/clear.
SYSTEM_PROMPT = (
    "You convert a photo-gallery search command (Italian or English) into ONE COMPACT JSON object on a "
    "single line, minified (no spaces, no newlines). Output ONLY the JSON, no prose/fences/reasoning. "
    "Include ONLY the fields that apply; OMIT every field that would be null/false/default. Always "
    "include \"operation\". Never invent ids. operation: 'replace' (normal search), 'refine' (adds/removes "
    "from current filters: aggiungi/anche/togli/rimuovi/also/add/remove), 'clear' (azzera/cancella i "
    "filtri/reset/clear all). people:[{text,mode}] mode include|exclude (senza/without/tranne=exclude) or "
    "remove (togli/rimuovi <name>). peopleMatch 'any' for o/oppure/or else 'all' (omit if no people). "
    "favorite=true, minRating=int, hasGps=true|false, removeHasGps=true (togli filtro gps), "
    "collapseDuplicates=true, sort=created|name|size|datetaken + sortDirection asc|desc. Visual "
    "description -> semanticQuery; explicit title/filename/tag -> metadataSearch. Dates -> whole-day UTC: "
    "dateFrom/dateTo 'YYYY-MM-DDTHH:MM:SSZ'.\n"
    "Examples (now=2026-07-12):\n"
    'COMMAND: Anna e Marco al mare preferite\n'
    '{"operation":"replace","people":[{"text":"Anna","mode":"include"},{"text":"Marco","mode":"include"}],"peopleMatch":"all","favorite":true,"semanticQuery":"al mare"}\n'
    'COMMAND: Giulia o Paolo senza Luca\n'
    '{"operation":"replace","people":[{"text":"Giulia","mode":"include"},{"text":"Paolo","mode":"include"},{"text":"Luca","mode":"exclude"}],"peopleMatch":"any"}\n'
    'COMMAND: Foto dell\'estate 2024\n'
    '{"operation":"replace","dateFrom":"2024-06-01T00:00:00Z","dateTo":"2024-08-31T23:59:59Z"}\n'
    'COMMAND: foto con titolo vacanza\n'
    '{"operation":"replace","metadataSearch":"vacanza"}\n'
    'COMMAND: mostrami le foto di ieri con almeno 4 stelle\n'
    '{"operation":"replace","minRating":4,"dateFrom":"2026-07-11T00:00:00Z","dateTo":"2026-07-11T23:59:59Z"}\n'
    'COMMAND: Aggiungi anche Marco\n'
    '{"operation":"refine","people":[{"text":"Marco","mode":"include"}],"peopleMatch":"all"}\n'
    'COMMAND: Togli il filtro GPS e cerca foto di notte\n'
    '{"operation":"refine","removeHasGps":true,"semanticQuery":"di notte"}\n'
    'COMMAND: Azzera tutti i filtri\n'
    '{"operation":"clear"}'
)


def user_prompt(case):
    return f"locale={case['locale']}; now={REFERENCE_NOW}; tz=Europe/Rome\nCOMMAND: {case['command']}"


def normalize(s):
    if not s:
        return ""
    collapsed = " ".join(str(s).strip().split()).lower()
    d = unicodedata.normalize("NFD", collapsed)
    return "".join(c for c in d if unicodedata.category(c) != "Mn")


def post(sidecar, system, user, timeout):
    body = json.dumps({"system": system, "user": user, "maxTokens": 200}).encode()
    req = urllib.request.Request(sidecar + "/interpret", data=body,
                                 headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return json.loads(resp.read().decode())


def first_json_object(text):
    start = text.find("{")
    if start < 0:
        return None
    depth = 0
    for i in range(start, len(text)):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                try:
                    return json.loads(text[start:i + 1])
                except Exception:
                    return None
    return None


def interpret(sidecar, case, timeout):
    """Returns (parsed_dict_or_None, latency_seconds, repaired_bool)."""
    system, user = SYSTEM_PROMPT, user_prompt(case)
    t0 = time.monotonic()
    raw = post(sidecar, system, user, timeout)
    latency = time.monotonic() - t0
    parsed = first_json_object(raw.get("json") or raw.get("text") or "")
    if parsed is not None:
        return parsed, latency, False
    # one repair attempt
    raw2 = post(sidecar, system, user + "\n\nRespond with ONLY the JSON object, no prose.", timeout)
    latency += time.monotonic() - t0 - latency
    parsed2 = first_json_object(raw2.get("json") or raw2.get("text") or "")
    return parsed2, time.monotonic() - t0, True


def people_set(people):
    out = set()
    for p in people or []:
        if isinstance(p, str):  # model sometimes emits bare strings
            if p.strip():
                out.add((normalize(p), "include"))
            continue
        if not isinstance(p, dict):
            continue
        mode = p.get("mode")
        mode = mode if mode in ("exclude", "remove") else "include"
        out.add((normalize(p.get("text", "")), mode))
    return out


def date_ok(got_iso, expected_ymd):
    if not expected_ymd:
        return True
    if not got_iso:
        return False
    return str(got_iso)[:10] == expected_ymd


def score_case(exp, got):
    """Returns dict of per-field correctness + a misses list."""
    misses = []
    ok = lambda name, cond: cond or misses.append(name)

    ok("operation", (got.get("operation") or "replace") == exp.get("operation", "replace"))

    exp_people = people_set(exp.get("people"))
    got_people = people_set(got.get("people"))
    people_ok = exp_people == got_people
    ok("people", people_ok)

    people_applicable = len(exp_people) > 0
    match_norm = lambda m: "any" if str(m).lower() == "any" else "all"
    match_ok = match_norm(got.get("peopleMatch")) == match_norm(exp.get("peopleMatch", "all"))
    people_logic_ok = (not people_applicable) or (people_ok and match_ok)
    if people_applicable and not match_ok:
        misses.append("peopleMatch")

    ok("favorite", got.get("favorite") == exp.get("favorite"))
    ok("minRating", got.get("minRating") == exp.get("minRating"))
    gps_ok = (got.get("hasGps") == exp.get("hasGps")) and (bool(got.get("removeHasGps")) == bool(exp.get("removeHasGps")))
    ok("gps", gps_ok)
    ok("collapse", (got.get("collapseDuplicates")) == exp.get("collapseDuplicates"))

    got_has_date = bool(got.get("dateFrom") or got.get("dateTo"))
    date_ok_case = (got_has_date == bool(exp.get("hasDate"))) and date_ok(got.get("dateFrom"), exp.get("dateFrom")) and date_ok(got.get("dateTo"), exp.get("dateTo"))
    ok("date", date_ok_case)

    got_meta = bool((got.get("metadataSearch") or "").strip()) if got.get("metadataSearch") else False
    got_sem = bool((got.get("semanticQuery") or "").strip()) if got.get("semanticQuery") else False
    metasem_ok = (got_meta == bool(exp.get("metadata"))) and (got_sem == bool(exp.get("semantic")))
    ok("metadata_vs_semantic", metasem_ok)

    sort_applicable = exp.get("sort") is not None
    sort_ok = (str(got.get("sort") or "").lower() or None) == (exp.get("sort") or None)
    if sort_applicable and not sort_ok:
        misses.append("sort")

    return {
        "exact": len(misses) == 0,
        "misses": misses,
        "people_applicable": people_applicable,
        "people_logic_ok": people_logic_ok,
        "date_applicable": bool(exp.get("hasDate")),
        "date_ok": date_ok_case,
        "metasem_ok": metasem_ok,
        "span_tp": len(got_people & exp_people),
        "span_fp": len(got_people - exp_people),
        "span_fn": len(exp_people - got_people),
        "operation_ok": (got.get("operation") or "replace") == exp.get("operation", "replace"),
    }


def run(sidecar, cases, warm, timeout):
    latencies, results = [], []
    valid = 0
    span_tp = span_fp = span_fn = 0
    people_app = people_ok = date_app = date_ok_n = metasem_ok = op_ok = exact = 0

    # One global warm-up so the first scored case isn't cold (model stays warm).
    if cases:
        try:
            interpret(sidecar, cases[0], timeout)
        except Exception:
            pass

    for case in cases:
        parsed = None
        best_lat = None
        for _ in range(max(1, warm)):
            try:
                p, lat, _ = interpret(sidecar, case, timeout)
            except Exception:
                p, lat = None, timeout
            if p is not None:
                parsed = p
            best_lat = lat if best_lat is None else min(best_lat, lat)
        latencies.append(best_lat)
        if parsed is None:
            results.append({"id": case["id"], "valid": False, "misses": ["no_valid_dto"]})
            continue
        valid += 1
        try:
            s = score_case(case["expect"], parsed)
        except Exception as ex:  # never let one odd output abort the whole run
            results.append({"id": case["id"], "valid": True, "exact": False, "misses": [f"score_error:{type(ex).__name__}"]})
            continue
        results.append({"id": case["id"], "valid": True, "exact": s["exact"], "misses": s["misses"]})
        span_tp += s["span_tp"]; span_fp += s["span_fp"]; span_fn += s["span_fn"]
        op_ok += 1 if s["operation_ok"] else 0
        exact += 1 if s["exact"] else 0
        if s["people_applicable"]:
            people_app += 1; people_ok += 1 if s["people_logic_ok"] else 0
        if s["date_applicable"]:
            date_app += 1; date_ok_n += 1 if s["date_ok"] else 0
        metasem_ok += 1 if s["metasem_ok"] else 0

    n = max(1, len(cases))
    latencies.sort()
    prec = span_tp / (span_tp + span_fp) if (span_tp + span_fp) else 1.0
    rec = span_tp / (span_tp + span_fn) if (span_tp + span_fn) else 1.0
    f1 = 2 * prec * rec / (prec + rec) if (prec + rec) else 0.0
    p50 = latencies[max(0, int(0.50 * len(latencies)) - 1)] if latencies else 0
    p95 = latencies[max(0, int(0.95 * len(latencies)) - 1)] if latencies else 0
    return {
        "cases": len(cases),
        "valid_dto_rate": valid / n,
        "exact_match_rate": exact / n,
        "operation_acc": op_ok / n,
        "person_span_precision": prec,
        "person_span_recall": rec,
        "person_span_f1": f1,
        "people_logic_acc": (people_ok / people_app) if people_app else 1.0,
        "date_acc": (date_ok_n / date_app) if date_app else 1.0,
        "metadata_vs_semantic_acc": metasem_ok / n,
        "warm_p50_s": round(p50, 3),
        "warm_p95_s": round(p95, 3),
        "warm_max_s": round(max(latencies), 3) if latencies else 0,
        "non_exact": [r for r in results if not r.get("exact")],
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sidecar", default="http://127.0.0.1:8090")
    ap.add_argument("--corpus", nargs="+", required=True)
    ap.add_argument("--warm", type=int, default=3)
    ap.add_argument("--timeout", type=float, default=30.0)
    ap.add_argument("--out", default=None)
    args = ap.parse_args()

    report = {"reference_now": REFERENCE_NOW, "corpora": {}}
    for path in args.corpus:
        with open(path) as f:
            corpus = json.load(f)
        name = path.split("/")[-1]
        print(f"== {name} ({len(corpus['cases'])} cases) ==", flush=True)
        rep = run(args.sidecar, corpus["cases"], args.warm, args.timeout)
        report["corpora"][name] = rep
        for k in ("cases", "valid_dto_rate", "exact_match_rate", "operation_acc",
                  "person_span_f1", "people_logic_acc", "date_acc",
                  "metadata_vs_semantic_acc", "warm_p50_s", "warm_p95_s", "warm_max_s"):
            print(f"   {k:26s}: {rep[k]}")
        print(f"   non-exact ids: {[r['id'] for r in rep['non_exact']]}")
    if args.out:
        with open(args.out, "w") as f:
            json.dump(report, f, indent=2)
        print(f"wrote {args.out}")


if __name__ == "__main__":
    main()
