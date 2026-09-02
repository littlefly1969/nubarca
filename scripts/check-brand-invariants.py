#!/usr/bin/env python3
"""
NubArca brand invariant checker.

Stage A verifies canonical brand primitives, token consistency and contract
structure. `--report-debt` additionally reports hard-coded color literals in
migration scopes without failing the build.

Stage B (BRAND-APP-01 §G) enforces the mobile client: the splash configuration,
the mobile token values against the contract, approved product spelling, and
colour literals in `mobile/app` and `mobile/src/ui` — those paths have moved
from report to STRICT, which is what stops the identity work this slice landed
from being undone one convenient hex at a time.
"""

from __future__ import annotations

import argparse
import hashlib
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

# --- Stage B: mobile strict ------------------------------------------------

MOBILE_STRICT_PATHS = ("mobile/app", "mobile/src/ui")
# Alternate spellings of the PRODUCT. Two things that look like one and are
# not: `NUBARCA_` prefixed names are operator configuration variables, and
# `NUBARCA-UX-01` style tokens are slice and contract identifiers. Neither is
# ever shown to a user, so neither is a spelling of the product.
SPELLING_RE = re.compile(r"\bNubarca\b|\bNUBARCA(?![_-])\b|\bNub\s+Arca\b")


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def check_mobile_splash(errors: list[str], contract: dict) -> None:
    """BRAND-SPLASH-01: the splash is the approved flat mark on Midnight Navy."""
    splash = contract["mobile"]["splash"]
    consumer = ROOT / splash["consumerAsset"]
    source = ROOT / splash["sourceAsset"]
    if not consumer.exists():
        fail(errors, f"missing mobile splash asset: {splash['consumerAsset']}")
    elif source.exists() and consumer.read_bytes() != source.read_bytes():
        fail(errors, f"{splash['consumerAsset']} is not byte-identical to its canonical source")

    config_path = ROOT / "mobile/app.config.js"
    if not config_path.exists():
        fail(errors, "missing mobile/app.config.js")
        return
    config = read(config_path)
    if "'expo-splash-screen'" not in config:
        fail(errors, "mobile/app.config.js does not configure the expo-splash-screen plugin")
        return
    expectations = {
        f"backgroundColor: '{splash['background']}'": "splash background must be Midnight Navy",
        f"imageWidth: {splash['imageWidth']}": f"splash imageWidth must be {splash['imageWidth']}",
        f"resizeMode: '{splash['resizeMode']}'": "splash must contain, never crop",
        f"image: './assets/brand/{Path(splash['consumerAsset']).name}'": (
            "splash art must be the approved flat on-dark mark"
        ),
    }
    for needle, message in expectations.items():
        if needle not in config:
            fail(errors, message)
    # BRAND-SPLASH-01 forbids reusing the launcher artwork as splash art.
    splash_block = config[config.index("'expo-splash-screen'"):]
    splash_block = splash_block[: splash_block.index("],")]
    if "expo-app-icon" in splash_block or "adaptive-foreground" in splash_block:
        fail(errors, "launcher artwork is used as splash art")


def check_mobile_tokens(errors: list[str], contract: dict) -> None:
    """The mobile client must carry the contract's own numbers, not near misses."""
    geometry = load_json(ROOT / contract["tokens"]["geometry"])
    tokens = read(ROOT / "mobile/src/ui/tokens.ts")
    for role, value in geometry["radius"].items():
        if f"{role}: {value}," not in tokens:
            fail(errors, f"mobile tokens.ts is missing radius.{role} = {value}")
    if f"mobileMinimum" in geometry["touch"] and f"minSize: {geometry['touch']['mobileMinimum']}" not in tokens:
        fail(errors, "mobile touch target must stay at the 48 dp class")

    motion = load_json(ROOT / contract["tokens"]["motion"])
    for role, value in motion["durationMs"].items():
        if f"{role}: {value}," not in tokens:
            fail(errors, f"mobile tokens.ts is missing motion duration {role} = {value}")
    if motion["easing"]["standard"] not in tokens:
        fail(errors, "mobile tokens.ts does not carry the canonical easing")

    typography = load_json(ROOT / contract["tokens"]["typography"])
    fonts = read(ROOT / "mobile/src/ui/fonts.ts")
    for family in (typography["families"]["display"], typography["families"]["ui"]):
        if family.replace(" ", "") not in fonts:
            fail(errors, f"mobile fonts.ts does not bundle {family}")
    # BRAND-TYPE-01: no UI weight above 600, so no Exo 2 Bold may be bundled.
    if "Exo2-Bold" in fonts:
        fail(errors, "Exo 2 is bundled above weight 600")

    primitives = load_json(ROOT / contract["tokens"]["primitives"])
    palette = read(ROOT / "mobile/src/ui/palette.ts")
    for theme_key in ("dark", "light"):
        semantic = load_json(ROOT / contract["tokens"][theme_key])
        for role, token in semantic["identity"].items():
            value = resolve_token(token, primitives)
            if value not in palette and value.lower() not in palette.lower():
                fail(errors, f"mobile palette.ts is missing the {role} identity value {value}")


# The mobile Palette is an ADAPTER of the design semantic tokens. This is the
# mapping between them, and it is the whole reason the two cannot drift apart.
SEMANTIC_ROLES = (
    ("surface", "canvas", "canvas"),
    ("surface", "raised", "surface"),
    ("surface", "overlay", "surfaceOverlay"),
    ("surface", "subtle", "surfaceSubtle"),
    ("text", "primary", "textPrimary"),
    ("text", "secondary", "textSecondary"),
    ("text", "muted", "textTertiary"),
    ("text", "onAccent", "textOnAccent"),
    ("action", "accentText", "accent"),
    ("action", "primaryFill", "accentStrong"),
    ("action", "subtle", "accentSubtle"),
    ("signal", "focus", "signalFocus"),
    ("signal", "connected", "signalConnected"),
    ("signal", "intelligence", "signalIntelligence"),
    ("signal", "danger", "danger"),
    ("signal", "success", "signalSuccess"),
)

PALETTE_LITERAL_RE = re.compile(r"^\s{2}(\w+): (?:'([^']+)'|brand\.(\w+)),\s*$", re.M)


def parse_palette(source: str, export_name: str, brand_colors: dict) -> dict:
    """Read one palette object literal into a role -> value mapping.

    Per-key extraction rather than a whole-file substring search: a value that
    moves to another role, or a role that quietly disappears, is then a real
    difference instead of a string that still happens to be somewhere.
    """
    start = source.index(f"export const {export_name}: Palette = {{")
    body = source[start : source.index("\n};", start)]
    values = {}
    for match in PALETTE_LITERAL_RE.finditer(body):
        role, literal, brand_ref = match.group(1), match.group(2), match.group(3)
        values[role] = literal if literal is not None else brand_colors.get(brand_ref, "")
    return values


def check_semantic_parity(errors: list[str], contract: dict) -> None:
    """The mobile adapter must say exactly what the design contract says."""
    primitives = load_json(ROOT / contract["tokens"]["primitives"])["color"]
    palette_source = read(ROOT / "mobile/src/ui/palette.ts")
    identity_values = parse_palette_object(palette_source, "identity", primitives)

    for theme_key, export_name in (("dark", "darkPalette"), ("light", "lightPalette")):
        semantic = load_json(ROOT / contract["tokens"][theme_key])
        adapter = parse_palette(palette_source, export_name, primitives)
        for group, key, role in SEMANTIC_ROLES:
            expected = resolve_token(semantic[group][key], {"color": primitives})
            actual = adapter.get(role)
            if actual is None:
                fail(errors, f"mobile palette {export_name} has no role {role}")
            elif actual.upper() != expected.upper():
                fail(
                    errors,
                    f"{export_name}.{role} is {actual}, contract {group}.{key} is {expected}",
                )
        for role, token in semantic["identity"].items():
            expected = resolve_token(token, {"color": primitives})
            actual = identity_values.get(role)
            if actual is None or actual.upper() != expected.upper():
                fail(errors, f"mobile identity.{role} is {actual}, contract is {expected}")


def parse_palette_object(source: str, export_name: str, brand_colors: dict) -> dict:
    start = source.index(f"export const {export_name} = {{")
    body = source[start : source.index("\n} as const;", start)]
    values = {}
    for match in PALETTE_LITERAL_RE.finditer(body):
        role, literal, brand_ref = match.group(1), match.group(2), match.group(3)
        values[role] = literal if literal is not None else brand_colors.get(brand_ref, "")
    return values


APPROVED_WEIGHTS = {"SpaceGrotesk": [500, 600, 700], "Exo2": [400, 500, 600]}


def check_font_integrity(errors: list[str]) -> None:
    """BRAND-TYPE-01: the bundled faces are the declared ones, byte for byte.

    An approved product weight is not the same as an upstream named instance —
    Space Grotesk 600 is generated from the official variable binary — so the
    manifest's provenance is not documentation, it is the only thing that
    distinguishes an approved derivative from an arbitrary font somebody
    dropped into the directory.
    """
    fonts_dir = ROOT / "mobile/assets/fonts"
    manifest_path = fonts_dir / "fonts-manifest.json"
    if not manifest_path.exists():
        fail(errors, "missing mobile/assets/fonts/fonts-manifest.json")
        return
    manifest = load_json(manifest_path)
    declared = {entry["file"]: entry for entry in manifest["fonts"]}

    for name, entry in declared.items():
        path = fonts_dir / name
        if not path.exists():
            fail(errors, f"manifested font is missing: {name}")
            continue
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        if digest != entry["sha256"]:
            fail(errors, f"{name} does not match its manifest SHA-256")
        if not entry.get("namedInstance", False) and not entry.get("derivation", "").strip():
            fail(errors, f"{name} is a derivative with no recorded derivation")

    for source, weights in APPROVED_WEIGHTS.items():
        actual = sorted(e["weight"] for e in manifest["fonts"] if e["source"] == source)
        if actual != weights:
            fail(errors, f"{source} weights are {actual}, approved are {weights}")

    # Every binary in the directory is accounted for: an unmanifested font is a
    # font nobody approved.
    for path in fonts_dir.glob("*.ttf"):
        if path.name not in declared:
            fail(errors, f"unmanifested font binary: {path.name}")

    fonts_ts = read(ROOT / "mobile/src/ui/fonts.ts")
    for required in re.findall(r"assets/fonts/([\w-]+\.ttf)", fonts_ts):
        if required not in declared:
            fail(errors, f"mobile fonts.ts loads an unmanifested font: {required}")
    if re.search(r"https?://", fonts_ts):
        fail(errors, "mobile fonts.ts introduces a runtime font fetch")


# Files declared brand-clean. BRAND-DEBT-01 says the baseline can only shrink,
# so this list can only GROW: a screen joins it when a slice migrates it, and
# from then on the deprecated aliases and raw type declarations are errors
# there. Everything not listed is still carrying declared debt.
MIGRATED_FILES = (
    "mobile/app/login.tsx",
    "mobile/app/(tabs)/_layout.tsx",
    "mobile/app/(tabs)/photos.tsx",
    "mobile/src/ui/components.tsx",
    "mobile/src/ui/states.tsx",
    "mobile/src/ui/fields.tsx",
    "mobile/src/ui/BrandLockup.tsx",
    "mobile/src/ui/BrandTabBar.tsx",
    "mobile/src/ui/BrandBootState.tsx",
    # BRAND-APP-03 — the media experience.
    "mobile/src/components/MediaGrid.tsx",
    "mobile/src/components/MediaTile.tsx",
    "mobile/src/components/MediaFilterChips.tsx",
    "mobile/src/components/MediaFilterSheet.tsx",
    "mobile/src/components/MediaSelectionBar.tsx",
    "mobile/app/media/[id].tsx",
)

# `tokens.ts` and `palette.ts` DECLARE the deprecated aliases; they are the one
# place allowed to name them.
DEPRECATED_USE_RE = re.compile(
    r"\bradii\.\w+"
    r"|\btype\.(?:title|sectionTitle|body|secondary|badge)\b"
    r"|colors\.surfaceMuted\b"
)
# A heading weight with no family is the system face wearing a brand size.
RAW_HEADING_RE = re.compile(r"fontWeight: '[67]00'")
# Five radius roles cover every shape the product draws, so a literal here is a
# sixth geometry nobody agreed to.
LOCAL_RADIUS_RE = re.compile(r"borderRadius: \d")


def check_migrated_files(errors: list[str]) -> None:
    for rel in MIGRATED_FILES:
        path = ROOT / rel
        if not path.exists():
            fail(errors, f"migrated file no longer exists: {rel}")
            continue
        for number, line in enumerate(read(path).splitlines(), 1):
            match = DEPRECATED_USE_RE.search(line)
            if match:
                fail(errors, f"{rel}:{number}: deprecated token {match.group(0)}")
            if RAW_HEADING_RE.search(line) and "fontFamily" not in line:
                fail(errors, f"{rel}:{number}: heading weight with no brand family")
            if LOCAL_RADIUS_RE.search(line):
                fail(errors, f"{rel}:{number}: literal radius where a role exists")


def check_product_spelling(errors: list[str]) -> None:
    """BRAND-NAME-01: the product is spelled NubArca, everywhere it is user-facing."""
    for rel in MOBILE_STRICT_PATHS:
        base = ROOT / rel
        if not base.exists():
            continue
        for path in base.rglob("*"):
            if not path.is_file() or path.suffix not in {".ts", ".tsx"}:
                continue
            for number, line in enumerate(read(path).splitlines(), 1):
                if SPELLING_RE.search(line):
                    fail(errors, f"{path.relative_to(ROOT)}:{number}: alternate product spelling")


def check_mobile_color_literals(errors: list[str], contract: dict) -> None:
    """BRAND-DEBT-01, strict for mobile: a component may not state a colour."""
    allowed = {str((ROOT / p).resolve()) for p in contract["enforcement"]["allowedColorSourceFiles"]}
    for rel in MOBILE_STRICT_PATHS:
        base = ROOT / rel
        if not base.exists():
            continue
        for path in base.rglob("*"):
            if not path.is_file() or path.suffix not in {".ts", ".tsx"}:
                continue
            if str(path.resolve()) in allowed:
                continue
            for number, line in enumerate(read(path).splitlines(), 1):
                for match in HEX_RE.finditer(line):
                    fail(
                        errors,
                        f"{path.relative_to(ROOT)}:{number}: hard-coded colour {match.group(0)}",
                    )


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
    if not errors:
        contract = load_json(CONTRACT_PATH)
        check_mobile_splash(errors, contract)
        check_mobile_tokens(errors, contract)
        check_semantic_parity(errors, contract)
        check_font_integrity(errors)
        check_migrated_files(errors)
        check_product_spelling(errors)
        check_mobile_color_literals(errors, contract)

    if errors:
        print("NubArca brand invariant check FAILED:", file=sys.stderr)
        for error in errors:
            print(f"  - {error}", file=sys.stderr)
        return 1

    print("NubArca brand invariants: contract OK, mobile strict OK")
    if args.report_debt:
        report_color_debt()
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
