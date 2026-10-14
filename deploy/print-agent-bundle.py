#!/usr/bin/env python3
"""The trust checks for a Print Agent bundle before the server publishes it.

The bundle arrives as a GHCR image addressed by digest. A digest proves the
bytes do not change; it does not prove WHO built them. Anyone can push an image
to their own namespace whose labels and manifest name a genuine NubArca
commit, so nothing self-declared inside the image is a trust root. The trust
root is WHERE the image lives: only NubArca's own GHCR repository, written by
this project's release workflow, is accepted. Inside a trusted image the
manifest, the labels and the zip must then agree with each other, and the
source commit must be on the deployed main.

  image <reference>
      Accepts only TRUSTED_REPOSITORY@sha256:<64 lowercase hex>.
  validate <bundle-dir> <artifact-label> <revision-label> <checkout>
      Checks the manifest, the zip (name, size, SHA-256) and the labels, and
      that the source commit is an ancestor of the checkout's HEAD. Prints
      "<zip name>\t<sha256>\t<version>\t<size>".

Every check fails closed: anything missing, malformed or unexpected exits 1
with the reason on stderr, and nothing is published.
"""

import hashlib
import json
import os
import re
import subprocess
import sys

# The one repository a Print Agent bundle is accepted from: where NubArca's
# "Print Agent release" workflow pushes it (ghcr.io/<repository owner>/...).
# Changing it is a reviewed change to this file, never a runtime option.
TRUSTED_REPOSITORY = "ghcr.io/littlefly1969/nubarca-print-agent"

DIGEST = re.compile(r"sha256:[0-9a-f]{64}")
VERSION = re.compile(r"\d{1,4}\.\d{1,4}\.\d{1,4}")
SHA256 = re.compile(r"[0-9a-f]{64}")
COMMIT = re.compile(r"[0-9a-f]{40}")
RUNTIME = "linux-x64"
MAX_BYTES = 512 * 1024 * 1024


def fail(reason: str) -> None:
    print(f"print-agent bundle refused: {reason}", file=sys.stderr)
    sys.exit(1)


def check_image(reference: str) -> str:
    repository, separator, digest = reference.partition("@")
    if not separator or not DIGEST.fullmatch(digest):
        fail("the image must be addressed by an immutable sha256 digest")
    if repository != TRUSTED_REPOSITORY:
        fail(f"only {TRUSTED_REPOSITORY} is trusted, not {repository or '(none)'}")
    return f"{repository}@{digest}"


def validate(root: str, artifact_label: str, revision_label: str, checkout: str) -> str:
    if artifact_label != "print-agent":
        fail("the image is not a NubArca Print Agent bundle")
    if not COMMIT.fullmatch(revision_label):
        fail("the image carries no source commit")
    try:
        with open(os.path.join(root, "nubarca-print-agent.release.json"), encoding="utf-8") as handle:
            manifest = json.load(handle)
    except (OSError, ValueError):
        fail("the manifest is missing or not JSON")
    if not isinstance(manifest, dict):
        fail("the manifest is not an object")

    version = manifest.get("version")
    name = manifest.get("fileName")
    digest = manifest.get("sha256")
    size = manifest.get("sizeBytes")
    commit = manifest.get("sourceCommit")
    if manifest.get("artifact") != "print-agent":
        fail("the manifest is not a Print Agent bundle")
    if not isinstance(version, str) or not VERSION.fullmatch(version):
        fail("invalid version")
    if manifest.get("runtime") != RUNTIME:
        fail(f"only {RUNTIME} is published")
    if name != f"nubarca-print-agent-{version}-{RUNTIME}.zip":
        fail("unexpected file name")
    if not isinstance(digest, str) or not SHA256.fullmatch(digest):
        fail("invalid sha256")
    if not isinstance(size, int) or isinstance(size, bool) or not 0 < size <= MAX_BYTES:
        fail("invalid size")
    if commit != revision_label:
        fail("the manifest's commit differs from the image's")

    path = os.path.join(root, name)
    if not os.path.isfile(path) or os.path.getsize(path) != size:
        fail("the zip is missing or not the size the manifest says")
    hashed = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            hashed.update(chunk)
    if hashed.hexdigest() != digest:
        fail("the zip does not hash to the manifest")

    on_main = subprocess.run(
        ["git", "-C", checkout, "merge-base", "--is-ancestor", commit, "HEAD"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
    if on_main.returncode != 0:
        fail(f"source {commit} is not on the deployed main")
    return f"{name}\t{digest}\t{version}\t{size}"


def main(argv: list[str]) -> None:
    if len(argv) == 2 and argv[0] == "image":
        print(check_image(argv[1]))
    elif len(argv) == 5 and argv[0] == "validate":
        print(validate(*argv[1:]))
    else:
        fail("usage: print-agent-bundle.py image <reference> | validate <dir> <artifact> <revision> <checkout>")


if __name__ == "__main__":
    main(sys.argv[1:])
