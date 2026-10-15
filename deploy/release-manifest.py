#!/usr/bin/env python3
"""The signed release manifest: what one NubArca release is made of.

CI writes one manifest per commit on main. It names, for every component in
deploy/release-components.json, the immutable GHCR images (by digest) that
carry it and the commit they were built from. CI signs the manifest with
Sigstore (keyless, the GitHub Actions OIDC identity of release.yml on main),
and an installation installs nothing a verified manifest does not name.

A component is rebuilt only when its own paths change. A manifest may
therefore name an image built from an OLDER commit, and that is sound only
when none of the component's paths changed between that commit and the
release: `verify` checks exactly that against the checkout's Git history, so a
manifest cannot pass off stale bytes as the release.

Trust is fixed here, never taken from the environment or from the image:

  trust <owner|repository|release-image|identity|issuer>
      Print one trust constant.
  plan <checkout> <head-sha>
      JSON {component: last commit at or before head that touched its paths}.
  paths <checkout> <rev> <component>
      The component's paths, one per line, as the commit <rev> declares them.
  build <source-sha> <version> <components.json>
      Print a manifest from CI's resolved components (validated like verify,
      without Git).
  verify <release.json> <candidate-sha> <checkout>
      Validate a manifest for <candidate-sha>. Prints one line per image:
      "<component>\\t<variant>\\t<image@digest>\\t<source-sha>\\t<version>".

Every check fails closed: anything missing, malformed or unexpected exits 1
with the reason on stderr.
"""

import json
import re
import subprocess
import sys

# Changing any of these is a reviewed change to this file, never a runtime option.
TRUSTED_OWNER = "littlefly1969"
TRUSTED_REPOSITORY = f"{TRUSTED_OWNER}/nubarca"
REGISTRY = "ghcr.io"
RELEASE_IMAGE = f"{REGISTRY}/{TRUSTED_OWNER}/nubarca-release"
# The one workflow, on the one branch, whose signature an installation accepts.
SIGNER_IDENTITY = f"https://github.com/{TRUSTED_REPOSITORY}/.github/workflows/release.yml@refs/heads/main"
SIGNER_ISSUER = "https://token.actions.githubusercontent.com"

COMPONENTS_FILE = "deploy/release-components.json"
COMMIT = re.compile(r"[0-9a-f]{40}")
DIGEST = re.compile(r"sha256:[0-9a-f]{64}")
VERSION = re.compile(r"\d{1,4}\.\d{1,4}\.\d{1,4}")
NAME = re.compile(r"[a-z0-9][a-z0-9-]{0,62}")
PATH = re.compile(r"[A-Za-z0-9_][A-Za-z0-9_./-]{0,200}")


def fail(reason: str) -> None:
    print(f"release manifest refused: {reason}", file=sys.stderr)
    sys.exit(1)


def git(checkout: str, *args: str, check: bool = True) -> subprocess.CompletedProcess:
    result = subprocess.run(["git", "-C", checkout, *args], capture_output=True, text=True)
    if check and result.returncode != 0:
        fail(f"git {args[0]} failed")
    return result


def parse_components(text: str) -> dict:
    try:
        document = json.loads(text)
    except ValueError:
        fail(f"{COMPONENTS_FILE} is not JSON")
    if not isinstance(document, dict) or document.get("schemaVersion") != 1:
        fail(f"{COMPONENTS_FILE} has an unknown schema")
    components = document.get("components")
    if not isinstance(components, dict) or not components:
        fail(f"{COMPONENTS_FILE} declares no components")
    for name, component in components.items():
        if not NAME.fullmatch(name) or not isinstance(component, dict):
            fail(f"invalid component {name!r}")
        paths = component.get("paths")
        if not isinstance(paths, list) or not paths or not all(
                isinstance(p, str) and PATH.fullmatch(p) and ".." not in p for p in paths):
            fail(f"component {name} has invalid paths")
        images = component.get("images")
        if not isinstance(images, dict) or not images or not all(
                NAME.fullmatch(k) and isinstance(v, str) and NAME.fullmatch(v) for k, v in images.items()):
            fail(f"component {name} has invalid images")
        suffix = component.get("tagSuffix", "")
        if not isinstance(suffix, str) or not re.fullmatch(r"(-[a-z0-9-]{1,40})?", suffix):
            fail(f"component {name} has an invalid tag suffix")
    return components


def components_at(checkout: str, rev: str) -> dict:
    if not COMMIT.fullmatch(rev):
        fail("a commit must be a full 40-hex SHA")
    shown = git(checkout, "show", f"{rev}:{COMPONENTS_FILE}", check=False)
    if shown.returncode != 0:
        fail(f"{COMPONENTS_FILE} is missing at {rev}")
    return parse_components(shown.stdout)


def validate_shape(manifest: object, components: dict) -> dict:
    """Checks everything that needs no Git history; returns the components."""
    if not isinstance(manifest, dict):
        fail("the manifest is not an object")
    if manifest.get("schemaVersion") != 1 or manifest.get("artifact") != "nubarca-release":
        fail("the manifest is not a NubArca release manifest")
    if manifest.get("repository") != TRUSTED_REPOSITORY:
        fail(f"the manifest is not for {TRUSTED_REPOSITORY}")
    if not isinstance(manifest.get("sourceSha"), str) or not COMMIT.fullmatch(manifest["sourceSha"]):
        fail("the manifest carries no source commit")
    if not isinstance(manifest.get("version"), str) or not VERSION.fullmatch(manifest["version"]):
        fail("the manifest carries no release version")
    declared = manifest.get("components")
    if not isinstance(declared, dict) or set(declared) != set(components):
        fail("the manifest does not name exactly the release's components")
    for name, component in components.items():
        entry = declared[name]
        if not isinstance(entry, dict):
            fail(f"component {name} is not an object")
        if not isinstance(entry.get("sourceSha"), str) or not COMMIT.fullmatch(entry["sourceSha"]):
            fail(f"component {name} carries no source commit")
        version = entry.get("version")
        if version is not None and (not isinstance(version, str) or not VERSION.fullmatch(version)):
            fail(f"component {name} has an invalid version")
        images = entry.get("images")
        if not isinstance(images, dict) or set(images) != set(component["images"]):
            fail(f"component {name} does not name exactly its images")
        for variant, repository_name in component["images"].items():
            reference = images[variant]
            repository, separator, digest = reference.partition("@") if isinstance(reference, str) else ("", "", "")
            if not separator or not DIGEST.fullmatch(digest):
                fail(f"{name}/{variant} is not addressed by an immutable sha256 digest")
            expected = f"{REGISTRY}/{TRUSTED_OWNER}/{repository_name}"
            if repository != expected:
                fail(f"{name}/{variant} must come from {expected}, not {repository or '(none)'}")
    return declared


def plan(checkout: str, head: str) -> None:
    components = components_at(checkout, head)
    result = {}
    for name, component in components.items():
        last = git(checkout, "log", "-1", "--format=%H", head, "--", *component["paths"]).stdout.strip()
        if not COMMIT.fullmatch(last):
            fail(f"no commit touches component {name}")
        result[name] = last
    print(json.dumps(result, indent=2, sort_keys=True))


def paths(checkout: str, rev: str, name: str) -> None:
    components = components_at(checkout, rev)
    if name not in components:
        fail(f"unknown component {name}")
    print("\n".join(components[name]["paths"]))


def build(source: str, version: str, resolved_path: str) -> None:
    if not COMMIT.fullmatch(source):
        fail("the source must be a full 40-hex SHA")
    with open(COMPONENTS_FILE, encoding="utf-8") as handle:
        components = parse_components(handle.read())
    try:
        with open(resolved_path, encoding="utf-8") as handle:
            resolved = json.load(handle)
    except (OSError, ValueError):
        fail("the resolved components are missing or not JSON")
    manifest = {
        "schemaVersion": 1,
        "artifact": "nubarca-release",
        "repository": TRUSTED_REPOSITORY,
        "sourceSha": source,
        "version": version,
        "components": resolved,
    }
    validate_shape(manifest, components)
    print(json.dumps(manifest, indent=2, sort_keys=True))


def verify(release_path: str, candidate: str, checkout: str) -> None:
    if not COMMIT.fullmatch(candidate):
        fail("the candidate must be a full 40-hex SHA")
    try:
        with open(release_path, encoding="utf-8") as handle:
            manifest = json.load(handle)
    except (OSError, ValueError):
        fail("the manifest is missing or not JSON")
    components = components_at(checkout, candidate)
    declared = validate_shape(manifest, components)
    if manifest["sourceSha"] != candidate:
        fail(f"the manifest is for {manifest['sourceSha']}, not the candidate {candidate}")
    lines = []
    for name, component in components.items():
        entry = declared[name]
        source = entry["sourceSha"]
        if git(checkout, "cat-file", "-e", f"{source}^{{commit}}", check=False).returncode != 0:
            fail(f"component {name} source {source} is not in this checkout's history")
        if git(checkout, "merge-base", "--is-ancestor", source, candidate, check=False).returncode != 0:
            fail(f"component {name} source {source} is not an ancestor of the candidate")
        # An image from an older commit is the release's only if nothing it is made of changed since.
        if git(checkout, "diff", "--quiet", source, candidate, "--", *component["paths"],
               check=False).returncode != 0:
            fail(f"component {name} changed after {source}; its image is not this release's")
        for variant in sorted(component["images"]):
            lines.append("\t".join([name, variant, entry["images"][variant], source, entry.get("version") or ""]))
    print("\n".join(lines))


def main(argv: list) -> None:
    if len(argv) == 2 and argv[0] == "trust":
        values = {
            "owner": TRUSTED_OWNER,
            "repository": TRUSTED_REPOSITORY,
            "release-image": RELEASE_IMAGE,
            "identity": SIGNER_IDENTITY,
            "issuer": SIGNER_ISSUER,
        }
        if argv[1] not in values:
            fail(f"unknown trust constant {argv[1]!r}")
        print(values[argv[1]])
    elif len(argv) == 3 and argv[0] == "plan":
        plan(argv[1], argv[2])
    elif len(argv) == 4 and argv[0] == "paths":
        paths(argv[1], argv[2], argv[3])
    elif len(argv) == 4 and argv[0] == "build":
        build(argv[1], argv[2], argv[3])
    elif len(argv) == 4 and argv[0] == "verify":
        verify(argv[1], argv[2], argv[3])
    else:
        print(__doc__, file=sys.stderr)
        sys.exit(2)


if __name__ == "__main__":
    main(sys.argv[1:])
