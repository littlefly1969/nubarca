#!/usr/bin/env python3
"""
NubArca brand invariant checker.

Stage A is intentionally conservative: it verifies canonical brand primitives,
token consistency and contract structure. `--report-debt` additionally reports
hard-coded color literals in migration scopes without failing the build.

Slice BRAND-APP-01 should move mobile paths from report to strict enforcement.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CONTRACT_PATH = ROOT / "design/brand-contract.json"
HEX_RE = re.compile(r"(?<![A-Za-z0-9_])#[0-9A-Fa-f]{6}(?![A-Fa-f0-9])")

def load_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))

def fail(errors: list[str], message: str) -> None:
    errors.append(message)

def resolve_token(value: str, primitives: dict) -> str:
    if not (isinstance(value, str) and value.startswith("{color.") and value.endswith("}")):
        return value
    key = value[len("{color."):-1]
    return primitives["color"][key]

def check_contract(errors: list[str]) -> None:
    contract = load_json(CONTRACT_PATH)
    manifest_path = ROOT / contract["canonicalBrandManifest"]
    if not manifest_path.exists():
        fail(errors, f"missing canonical manifest: {manifest_path.relative_to(ROOT)}")
        return

    manifest = load_json(manifest_path)
    primitives = load_json(ROOT / contract["tokens"]["primitives"])

    manifest_palette = manifest.get("palette", {})
    expected = {
        "midnightNavy": manifest_palette.get("midnight"),
        "deepBlue": manifest_palette.get("deep"),
        "electricBlue": manifest_palette.get("blue"),
        "cyanGlow": manifest_palette.get("cyan"),
        "softViolet": manifest_palette.get("violet"),
        "cloudWhite": manifest_palette.get("white"),
    }
    if primitives.get("color") != expected:
        fail(errors, "design/tokens/brand.primitives.json does not match brand-manifest.json palette")

    if contract.get("productNames") != ["NubArca", "NubArca TV"]:
        fail(errors, "productNames must be exactly NubArca / NubArca TV")

    geometry = load_json(ROOT / contract["tokens"]["geometry"])
    canonical_radius = {"compact": 8, "control": 12, "card": 16, "hero": 24, "pill": 999}
    if geometry.get("radius") != canonical_radius:
        fail(errors, f"geometry.radius must be {canonical_radius}")

    typography = load_json(ROOT / contract["tokens"]["typography"])
    if typography["families"]["display"] != "Space Grotesk":
        fail(errors, "display family must be Space Grotesk")
    if typography["families"]["ui"] != "Exo 2":
        fail(errors, "UI family must be Exo 2")
    if typography["weights"]["display"] != [500, 600, 700]:
        fail(errors, "Space Grotesk weights must be 500/600/700")
    if typography["weights"]["ui"] != [400, 500, 600]:
        fail(errors, "Exo 2 weights must be 400/500/600")

    motion = load_json(ROOT / contract["tokens"]["motion"])
    if motion["durationMs"] != {"fast": 120, "standard": 180, "navigation": 240, "deliberate": 320}:
        fail(errors, "motion durations drifted from brand contract")

    for theme_key in ("dark", "light"):
        semantic = load_json(ROOT / contract["tokens"][theme_key])
        identity = semantic.get("identity", {})
        if resolve_token(identity.get("bootBackground"), primitives) != "#0A0F1A":
            fail(errors, f"{theme_key} boot background must resolve to Midnight Navy")
        if resolve_token(identity.get("bootForeground"), primitives) != "#F5F7FB":
            fail(errors, f"{theme_key} boot foreground must resolve to Cloud White")
        if resolve_token(identity.get("bootActivity"), primitives) != "#00D4FF":
            fail(errors, f"{theme_key} boot activity must resolve to Cyan Glow")

def report_color_debt() -> int:
    contract = load_json(CONTRACT_PATH)
    allowed = {str((ROOT / p).resolve()) for p in contract["enforcement"]["allowedColorSourceFiles"]}
    findings = []
    extensions = {".ts", ".tsx", ".js", ".jsx", ".css"}
    for rel in contract["enforcement"]["migrationReportPaths"]:
        base = ROOT / rel
        if not base.exists():
            continue
        files = [base] if base.is_file() else base.rglob("*")
        for path in files:
            if not path.is_file() or path.suffix not in extensions:
                continue
            if str(path.resolve()) in allowed:
                continue
            try:
                text = path.read_text(encoding="utf-8")
            except UnicodeDecodeError:
                continue
            for number, line in enumerate(text.splitlines(), 1):
                for match in HEX_RE.finditer(line):
                    findings.append((path.relative_to(ROOT), number, match.group(0), line.strip()))

    if findings:
        print("\nBrand migration debt (report only):")
        for path, line_no, value, line in findings:
            print(f"  {path}:{line_no}: {value}  {line[:120]}")
        print(f"\n{len(findings)} color literal occurrence(s) reported.")
    else:
        print("No hard-coded color debt found in report scopes.")
    return len(findings)

def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--report-debt", action="store_true")
    args = parser.parse_args()

    errors: list[str] = []
    check_contract(errors)

    if errors:
        print("NubArca brand invariant check FAILED:", file=sys.stderr)
        for error in errors:
            print(f"  - {error}", file=sys.stderr)
        return 1

    print("NubArca brand invariant contract: OK")
    if args.report_debt:
        report_color_debt()
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
