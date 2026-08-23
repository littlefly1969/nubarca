#!/usr/bin/env python3
"""Validate the immutable three-file TV APK publication bundle."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
from pathlib import Path
from typing import NoReturn


DESCRIPTOR_KEYS = {
    "schemaVersion",
    "package",
    "version",
    "versionCode",
    "runtimeVersion",
    "channel",
    "apkFile",
    "apkSha256",
    "apkBytes",
}


def fail(message: str) -> NoReturn:
    print(f"TV APK bundle invalid: {message}", file=sys.stderr)
    raise SystemExit(1)


def load_json(path: Path, description: str) -> dict[str, object]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as error:
        fail(f"cannot read {description} {path}: {error}")
    if not isinstance(value, dict):
        fail(f"{description} must be a JSON object")
    return value


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("bundle", type=Path)
    parser.add_argument("contract", type=Path)
    parser.add_argument(
        "--values",
        action="store_true",
        help="print shell-safe tab-separated APK name, hash, size, version code",
    )
    args = parser.parse_args()

    bundle = args.bundle.resolve()
    if not bundle.is_dir():
        fail(f"bundle directory does not exist: {bundle}")

    contract = load_json(args.contract, "release contract")
    descriptor_path = bundle / "nubarca-tv.release.json"
    descriptor = load_json(descriptor_path, "release descriptor")

    if set(descriptor) != DESCRIPTOR_KEYS:
        missing = sorted(DESCRIPTOR_KEYS - set(descriptor))
        extra = sorted(set(descriptor) - DESCRIPTOR_KEYS)
        fail(f"descriptor keys differ (missing={missing}, extra={extra})")

    version_code = descriptor.get("versionCode")
    if type(version_code) is not int or version_code <= 0:
        fail("descriptor versionCode must be a positive integer")

    apk_name = descriptor.get("apkFile")
    expected_apk_name = f"nubarca-tv-v{version_code}.apk"
    if apk_name != expected_apk_name:
        fail(f"descriptor apkFile must be {expected_apk_name!r}")
    if not isinstance(apk_name, str) or Path(apk_name).name != apk_name:
        fail("descriptor apkFile must be a plain filename")

    expected_files = {
        apk_name,
        f"{apk_name}.sha256",
        "nubarca-tv.release.json",
    }
    actual_files: set[str] = set()
    for entry in bundle.iterdir():
        if entry.is_symlink() or not entry.is_file():
            fail(f"bundle entry must be a regular non-symlink file: {entry.name}")
        actual_files.add(entry.name)
    if actual_files != expected_files:
        fail(
            "bundle files differ "
            f"(missing={sorted(expected_files - actual_files)}, "
            f"extra={sorted(actual_files - expected_files)})"
        )

    contract_fields = {
        "package": "package",
        "version": "version",
        "versionCode": "versionCode",
        "runtimeVersion": "runtimeVersion",
        "channel": "channel",
    }
    for descriptor_key, contract_key in contract_fields.items():
        if descriptor.get(descriptor_key) != contract.get(contract_key):
            fail(f"descriptor {descriptor_key} does not match the release contract")
    if descriptor.get("schemaVersion") != 1:
        fail("unsupported descriptor schemaVersion")

    apk_path = bundle / apk_name
    actual_hash = sha256_file(apk_path)
    actual_bytes = apk_path.stat().st_size
    if descriptor.get("apkSha256") != actual_hash:
        fail("descriptor APK SHA-256 does not match the APK bytes")
    if descriptor.get("apkBytes") != actual_bytes:
        fail("descriptor APK byte count does not match the APK bytes")

    checksum_path = bundle / f"{apk_name}.sha256"
    expected_checksum = f"{actual_hash}  {apk_name}\n"
    try:
        actual_checksum = checksum_path.read_text(encoding="ascii")
    except (OSError, UnicodeDecodeError) as error:
        fail(f"cannot read checksum sidecar: {error}")
    if actual_checksum != expected_checksum:
        fail("checksum sidecar is not the canonical SHA-256 line")

    if args.values:
        # All fields are validated as non-path/non-whitespace values before use.
        print(f"{apk_name}\t{actual_hash}\t{actual_bytes}\t{version_code}")
    else:
        print(
            f"TV APK BUNDLE VALID: {apk_name} "
            f"({actual_bytes} bytes, sha256={actual_hash})"
        )


if __name__ == "__main__":
    main()
