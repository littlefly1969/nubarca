#!/usr/bin/env python3
"""The signed release manifest: what one NubArca release is made of.

CI writes one manifest per commit on main. It names, for every component in
deploy/release-components.json, the immutable GHCR images (by digest) that
carry it and the commit they were built from, and it is signed: keylessly
(the GitHub Actions OIDC identity of release.yml on main) or with NubArca's
own key when deploy/release-signing.pub is committed. An installation
installs nothing a verified manifest does not name.

A component is rebuilt only when its own paths change. Otherwise CI carries
its entry over from the PREVIOUS signed release (never from a registry tag),
so a manifest may name an image built from an older commit. That is sound
only when none of the component's paths changed since: `verify` checks
exactly that against the checkout's Git history.

Signing trust changes only with the old trust's consent. Every manifest names
its signer, the release that introduced that signer (trustSince) and the
release before it (previousRelease). A release whose signer differs from the
previous release's is a TRANSITION: CI signs it a second time with the
previous signer (release.transition.sigstore.json), and an installation
accepts a new signer only through such a chain of transitions starting from
the signer it already holds.

Trust is fixed here, never taken from the environment or from the image:

  trust <owner|repository|release-image|identity|issuer>
      Print one trust constant.
  previous <checkout> <head-sha> <dir>
      CI: find the newest published release among head's first-parent
      ancestors, check its signature, and leave it in <dir>. Prints its
      commit, or nothing when there is none.
  plan <checkout> <head-sha> <previous-release.json|->
      CI: JSON {"previousRelease", "build": [components], "inherit": {...}}.
  assemble <checkout> <head-sha> <version> <built.json> <previous-release.json|->
      CI: print the manifest from the images built now and those inherited.
  signer <release.json> <current|previous> <key-out>
      Print the mode of the manifest's signer (or of the one it transitions
      from); a key is written to <key-out>.
  check-signature <release.json> <bundle> <current|previous>
      Exit 0 when the bundle signs the manifest as that signer.
  fetch-verified <candidate-sha> <checkout> <dir>
      Installation: fetch the candidate's manifest, prove its signature from
      the signer the checkout holds (through any transitions), and only then
      write <dir>/release.json.
  paths <checkout> <rev> <component>
      The component's paths, one per line, as the commit <rev> declares them.
  verify <release.json> <candidate-sha> <checkout>
      Validate a manifest for <candidate-sha>. Prints one line per image:
      "<component>\\t<variant>\\t<image@digest>\\t<source-sha>\\t<version>".

Every check fails closed: anything missing, malformed or unexpected exits 1
with the reason on stderr.
"""

import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

# Changing any of these is a reviewed change to this file, never a runtime option.
TRUSTED_OWNER = "littlefly1969"
TRUSTED_REPOSITORY = f"{TRUSTED_OWNER}/nubarca"
REGISTRY = "ghcr.io"
RELEASE_IMAGE = f"{REGISTRY}/{TRUSTED_OWNER}/nubarca-release"
# The one workflow, on the one branch, whose keyless signature an installation accepts.
SIGNER_IDENTITY = f"https://github.com/{TRUSTED_REPOSITORY}/.github/workflows/release.yml@refs/heads/main"
SIGNER_ISSUER = "https://token.actions.githubusercontent.com"

COMPONENTS_FILE = "deploy/release-components.json"
# Committed: the release is signed with NubArca's own key. Absent: keylessly.
SIGNING_KEY_FILE = "deploy/release-signing.pub"
PRIMARY_BUNDLE = "release.sigstore.json"
TRANSITION_BUNDLE = "release.transition.sigstore.json"
# How far back CI looks for the previous release, and how many trust
# transitions an installation follows to reach the candidate's signer.
MAX_PREVIOUS = 500
MAX_TRANSITIONS = 10

COMMIT = re.compile(r"[0-9a-f]{40}")
DIGEST = re.compile(r"sha256:[0-9a-f]{64}")
VERSION = re.compile(r"\d{1,4}\.\d{1,4}\.\d{1,4}")
NAME = re.compile(r"[a-z0-9][a-z0-9-]{0,62}")
PATH = re.compile(r"[A-Za-z0-9_.][A-Za-z0-9_./-]{0,200}")
PUBLIC_KEY = re.compile(r"-----BEGIN PUBLIC KEY-----\n[A-Za-z0-9+/=\n]{40,4000}\n-----END PUBLIC KEY-----\n")
KEYLESS = {"mode": "keyless"}


def fail(reason: str) -> None:
    print(f"release manifest refused: {reason}", file=sys.stderr)
    sys.exit(1)


def git(checkout: str, *args: str, check: bool = True) -> subprocess.CompletedProcess:
    result = subprocess.run(["git", "-C", checkout, *args], capture_output=True, text=True)
    if check and result.returncode != 0:
        fail(f"git {args[0]} failed")
    return result


def is_ancestor(checkout: str, ancestor: str, descendant: str) -> bool:
    return git(checkout, "merge-base", "--is-ancestor", ancestor, descendant, check=False).returncode == 0


def load_json(path: str, what: str) -> object:
    try:
        with open(path, encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, ValueError):
        fail(f"{what} is missing or not JSON")


# ---- Components -------------------------------------------------------------------

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
        version_file = component.get("versionFile")
        if version_file is not None and (
                not isinstance(version_file, str) or not PATH.fullmatch(version_file) or ".." in version_file):
            fail(f"component {name} has an invalid version file")
    return components


def components_at(checkout: str, rev: str) -> dict:
    if not COMMIT.fullmatch(rev):
        fail("a commit must be a full 40-hex SHA")
    shown = git(checkout, "show", f"{rev}:{COMPONENTS_FILE}", check=False)
    if shown.returncode != 0:
        fail(f"{COMPONENTS_FILE} is missing at {rev}")
    return parse_components(shown.stdout)


def version_at(checkout: str, rev: str, component: dict):
    if not component.get("versionFile"):
        return None
    shown = git(checkout, "show", f"{rev}:{component['versionFile']}", check=False)
    match = re.search(r"<Version>(\d{1,4}\.\d{1,4}\.\d{1,4})</Version>", shown.stdout) if shown.returncode == 0 else None
    return match.group(1) if match else None


# ---- Signers ----------------------------------------------------------------------

def key_signer(text: str) -> dict:
    lines = [line.strip() for line in text.strip().splitlines() if line.strip()]
    key = "\n".join(lines) + "\n"
    if not PUBLIC_KEY.fullmatch(key):
        fail(f"{SIGNING_KEY_FILE} is not a PEM public key")
    return {"mode": "key", "publicKey": key}


def signer_at(checkout: str, rev: str) -> dict:
    """The signer the commit <rev> declares."""
    if git(checkout, "cat-file", "-e", f"{rev}:{SIGNING_KEY_FILE}", check=False).returncode != 0:
        return dict(KEYLESS)
    return key_signer(git(checkout, "show", f"{rev}:{SIGNING_KEY_FILE}").stdout)


def installed_signer(checkout: str) -> dict:
    """The signer the installed checkout holds: the trust this installation starts from."""
    path = os.path.join(checkout, SIGNING_KEY_FILE)
    if not os.path.exists(path):
        return dict(KEYLESS)
    with open(path, encoding="utf-8") as handle:
        return key_signer(handle.read())


def valid_signer(signer: object) -> bool:
    if signer == KEYLESS:
        return True
    return (isinstance(signer, dict) and set(signer) == {"mode", "publicKey"} and signer["mode"] == "key"
            and isinstance(signer["publicKey"], str) and PUBLIC_KEY.fullmatch(signer["publicKey"]) is not None)


def describe(signer: dict) -> str:
    return "keyless (Release workflow on main)" if signer["mode"] == "keyless" else "NubArca release key"


def signature_holds(release_path: str, bundle_path: str, signer: dict) -> bool:
    """cosign proves that <signer> signed exactly these manifest bytes."""
    if not os.path.isfile(bundle_path) or not os.path.isfile(release_path):
        return False
    if signer["mode"] == "keyless":
        command = ["cosign", "verify-blob", "--bundle", bundle_path,
                   "--certificate-identity", SIGNER_IDENTITY, "--certificate-oidc-issuer", SIGNER_ISSUER,
                   release_path]
        return subprocess.run(command, capture_output=True).returncode == 0
    with tempfile.TemporaryDirectory() as scratch:
        key_path = os.path.join(scratch, "release-signing.pub")
        with open(key_path, "w", encoding="utf-8") as handle:
            handle.write(signer["publicKey"])
        # A NubArca key signs without a transparency-log entry (private repositories).
        command = ["cosign", "verify-blob", "--key", key_path, "--insecure-ignore-tlog=true",
                   "--bundle", bundle_path, release_path]
        return subprocess.run(command, capture_output=True).returncode == 0


# ---- Manifest shape ---------------------------------------------------------------

def validate_shape(manifest: object, components: dict) -> dict:
    """Checks everything that needs no Git history; returns the components."""
    if not isinstance(manifest, dict):
        fail("the manifest is not an object")
    if manifest.get("schemaVersion") != 1 or manifest.get("artifact") != "nubarca-release":
        fail("the manifest is not a NubArca release manifest")
    if manifest.get("repository") != TRUSTED_REPOSITORY:
        fail(f"the manifest is not for {TRUSTED_REPOSITORY}")
    source = manifest.get("sourceSha")
    if not isinstance(source, str) or not COMMIT.fullmatch(source):
        fail("the manifest carries no source commit")
    if not isinstance(manifest.get("version"), str) or not VERSION.fullmatch(manifest["version"]):
        fail("the manifest carries no release version")
    validate_lineage(manifest, source)
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


def validate_lineage(manifest: dict, source: str) -> None:
    """Signer, trustSince, previousRelease and transition agree with each other."""
    for key in ("signer", "trustSince", "previousRelease", "transition"):
        if key not in manifest:
            fail(f"the manifest does not say {key}")
    if not valid_signer(manifest["signer"]):
        fail("the manifest names no valid signer")
    since, previous, transition = manifest["trustSince"], manifest["previousRelease"], manifest["transition"]
    if not isinstance(since, str) or not COMMIT.fullmatch(since):
        fail("the manifest's trustSince is not a commit")
    if previous is not None and (not isinstance(previous, str) or not COMMIT.fullmatch(previous) or previous == source):
        fail("the manifest's previousRelease is not an earlier commit")
    if transition is not None:
        if not isinstance(transition, dict) or set(transition) != {"fromSigner", "fromTrustSince"}:
            fail("the manifest's transition is malformed")
        if not valid_signer(transition["fromSigner"]) or transition["fromSigner"] == manifest["signer"]:
            fail("the manifest's transition does not come from another valid signer")
        if not isinstance(transition["fromTrustSince"], str) or not COMMIT.fullmatch(transition["fromTrustSince"]):
            fail("the manifest's transition names no earlier trust")
        if previous is None or since != source:
            fail("a transition starts its own trust and follows a previous release")
    elif previous is None:
        if since != source:
            fail("a first release starts its own trust")
    elif since == source:
        fail("a release that keeps its signer keeps the previous release's trustSince")


def check_release(manifest: object, checkout: str, sha: str) -> dict:
    """A fetched manifest is the release of <sha>, signed by whom <sha> declares."""
    validate_shape(manifest, components_at(checkout, sha))
    if manifest["sourceSha"] != sha:
        fail(f"the release published as {sha} describes {manifest['sourceSha']}")
    if manifest["signer"] != signer_at(checkout, sha):
        fail(f"the release of {sha} names a signer its commit does not declare")
    return manifest


# ---- Release images ---------------------------------------------------------------

def docker(*args: str) -> subprocess.CompletedProcess:
    return subprocess.run(["docker", *args], capture_output=True, text=True)


def fetch_release(sha: str, into: str):
    """Copies /release out of the release image for <sha>; None when it is not published."""
    image = f"{RELEASE_IMAGE}:{sha}"
    if docker("manifest", "inspect", image).returncode != 0:
        return None
    if docker("pull", "--quiet", image).returncode != 0:
        fail(f"cannot pull the release image of {sha}")
    created = docker("create", image, "/bin/true")
    if created.returncode != 0:
        fail(f"cannot open the release image of {sha}")
    container = created.stdout.strip()
    try:
        os.makedirs(into, exist_ok=True)
        if docker("cp", f"{container}:/release/.", into).returncode != 0:
            fail(f"the release image of {sha} carries no /release")
    finally:
        docker("rm", "-f", container)
    return load_json(os.path.join(into, "release.json"), f"the manifest of {sha}")


# ---- CI ---------------------------------------------------------------------------

def previous(checkout: str, head: str, into: str) -> None:
    if not COMMIT.fullmatch(head):
        fail("the head must be a full 40-hex SHA")
    if git(checkout, "rev-parse", "--verify", "--quiet", f"{head}^", check=False).returncode != 0:
        return
    ancestors = git(checkout, "rev-list", "--first-parent", f"--max-count={MAX_PREVIOUS}", f"{head}^").stdout.split()
    for sha in ancestors:
        manifest = fetch_release(sha, into)
        if manifest is None:
            continue
        # Fail closed: a newest release that does not verify is never skipped for an older one.
        check_release(manifest, checkout, sha)
        if not signature_holds(os.path.join(into, "release.json"), os.path.join(into, PRIMARY_BUNDLE),
                               manifest["signer"]):
            fail(f"the previous release {sha} is not signed by the signer its commit declares")
        print(sha)
        return


def load_previous(checkout: str, path: str):
    if path == "-":
        return None
    manifest = load_json(path, "the previous release")
    # Already proved by `previous`; checked again so nothing else can stand in for it.
    return check_release(manifest, checkout, manifest.get("sourceSha", "") if isinstance(manifest, dict) else "")


def compute_plan(checkout: str, head: str, prior) -> dict:
    components = components_at(checkout, head)
    if prior is not None and not is_ancestor(checkout, prior["sourceSha"], head):
        fail("the previous release is not an ancestor of the head")
    build, inherit = [], {}
    for name, component in components.items():
        entry = prior["components"].get(name) if prior is not None else None
        # Carried over only when the previous signed release names it for exactly
        # these images and nothing it is built from changed since its source.
        if (entry is not None and set(entry["images"]) == set(component["images"])
                and is_ancestor(checkout, entry["sourceSha"], head)
                and git(checkout, "diff", "--quiet", entry["sourceSha"], head, "--", *component["paths"],
                        check=False).returncode == 0):
            inherit[name] = entry
        else:
            build.append(name)
    return {"previousRelease": prior["sourceSha"] if prior is not None else None,
            "build": sorted(build), "inherit": inherit}


def plan(checkout: str, head: str, previous_path: str) -> None:
    print(json.dumps(compute_plan(checkout, head, load_previous(checkout, previous_path)), indent=2, sort_keys=True))


def assemble(checkout: str, head: str, version: str, built_path: str, previous_path: str) -> None:
    prior = load_previous(checkout, previous_path)
    planned = compute_plan(checkout, head, prior)
    components = components_at(checkout, head)
    built = load_json(built_path, "the built images")
    if not isinstance(built, dict) or sorted(built) != planned["build"]:
        fail("the images built in this run are not exactly the components the plan builds")
    resolved = dict(planned["inherit"])
    for name in planned["build"]:
        entry = {"sourceSha": head, "images": built[name]}
        component_version = version_at(checkout, head, components[name])
        if component_version is not None:
            entry["version"] = component_version
        resolved[name] = entry
    signer = signer_at(checkout, head)
    if prior is None:
        lineage = {"trustSince": head, "previousRelease": None, "transition": None}
    elif prior["signer"] == signer:
        lineage = {"trustSince": prior["trustSince"], "previousRelease": prior["sourceSha"], "transition": None}
    else:
        lineage = {"trustSince": head, "previousRelease": prior["sourceSha"],
                   "transition": {"fromSigner": prior["signer"], "fromTrustSince": prior["trustSince"]}}
    manifest = {
        "schemaVersion": 1,
        "artifact": "nubarca-release",
        "repository": TRUSTED_REPOSITORY,
        "sourceSha": head,
        "version": version,
        "signer": signer,
        **lineage,
        "components": resolved,
    }
    validate_shape(manifest, components)
    print(json.dumps(manifest, indent=2, sort_keys=True))


def role_signer(release_path: str, role: str) -> dict:
    manifest = load_json(release_path, "the manifest")
    if not isinstance(manifest, dict) or role not in ("current", "previous"):
        fail("unknown signer role")
    validate_lineage(manifest, manifest.get("sourceSha"))
    if role == "current":
        return manifest["signer"]
    if manifest["transition"] is None:
        fail("the manifest is not a transition")
    return manifest["transition"]["fromSigner"]


def signer(release_path: str, role: str, key_out: str) -> None:
    chosen = role_signer(release_path, role)
    if chosen["mode"] == "key":
        with open(key_out, "w", encoding="utf-8") as handle:
            handle.write(chosen["publicKey"])
    print(chosen["mode"])


def check_signature(release_path: str, bundle_path: str, role: str) -> None:
    if not signature_holds(release_path, bundle_path, role_signer(release_path, role)):
        fail(f"{os.path.basename(bundle_path)} is not the {role} signer's signature of the manifest")


# ---- Installation -----------------------------------------------------------------

def fetch_verified(candidate: str, checkout: str, into: str) -> None:
    if not COMMIT.fullmatch(candidate):
        fail("the candidate must be a full 40-hex SHA")
    held = installed_signer(checkout)
    candidate_dir = os.path.join(into, "candidate")
    release = fetch_release(candidate, candidate_dir)
    if release is None:
        fail(f"no release is published for {candidate}")
    check_release(release, checkout, candidate)

    # Walk back from the candidate's signer, one transition at a time, to the
    # signer this installation holds. Nothing read here is believed until the
    # whole chain is proved forwards below.
    chain = []
    era_signer, era_since = release["signer"], release["trustSince"]
    while era_signer != held:
        if len(chain) == MAX_TRANSITIONS:
            fail("no chain of signed trust transitions leads from this installation's signer to the candidate's")
        if era_since == candidate:
            transition, transition_dir = release, candidate_dir
        else:
            transition_dir = os.path.join(into, era_since)
            transition = fetch_release(era_since, transition_dir)
            if transition is None:
                fail(f"the release {era_since} that introduced a signer is not published")
            check_release(transition, checkout, era_since)
            if not is_ancestor(checkout, era_since, candidate):
                fail(f"the transition release {era_since} is not an ancestor of the candidate")
        if (transition["transition"] is None or transition["signer"] != era_signer
                or transition["trustSince"] != era_since):
            fail("the candidate's signer was never handed over by this installation's signer: "
                 "no signed trust transition leads to it")
        chain.append((transition, transition_dir))
        era_signer = transition["transition"]["fromSigner"]
        era_since = transition["transition"]["fromTrustSince"]

    trusted = held
    for transition, transition_dir in reversed(chain):
        # The signer already trusted authorizes the next one: it signed the
        # transition release, whose manifest names the new signer.
        if not signature_holds(os.path.join(transition_dir, "release.json"),
                               os.path.join(transition_dir, TRANSITION_BUNDLE), trusted):
            fail(f"the trust transition at {transition['sourceSha']} is not signed by the signer "
                 "this installation trusts; nothing from it is installed")
        trusted = transition["signer"]
    if not signature_holds(os.path.join(candidate_dir, "release.json"),
                           os.path.join(candidate_dir, PRIMARY_BUNDLE), trusted):
        fail("the release manifest signature does not verify; nothing from it is installed")
    shutil.copyfile(os.path.join(candidate_dir, "release.json"), os.path.join(into, "release.json"))
    note = f"signed by {describe(trusted)}"
    if chain:
        note += f", reached from {describe(held)} through {len(chain)} signed trust transition(s)"
    print(note)


def paths(checkout: str, rev: str, name: str) -> None:
    components = components_at(checkout, rev)
    if name not in components:
        fail(f"unknown component {name}")
    print("\n".join(components[name]["paths"]))


def verify(release_path: str, candidate: str, checkout: str) -> None:
    if not COMMIT.fullmatch(candidate):
        fail("the candidate must be a full 40-hex SHA")
    manifest = load_json(release_path, "the manifest")
    components = components_at(checkout, candidate)
    declared = validate_shape(manifest, components)
    if manifest["sourceSha"] != candidate:
        fail(f"the manifest is for {manifest['sourceSha']}, not the candidate {candidate}")
    if manifest["signer"] != signer_at(checkout, candidate):
        fail("the manifest names a signer the candidate does not declare")
    for key in ("previousRelease", "trustSince"):
        sha = manifest[key]
        if sha is not None and sha != candidate and not is_ancestor(checkout, sha, candidate):
            fail(f"the manifest's {key} {sha} is not an ancestor of the candidate")
    lines = []
    for name, component in components.items():
        entry = declared[name]
        source = entry["sourceSha"]
        if git(checkout, "cat-file", "-e", f"{source}^{{commit}}", check=False).returncode != 0:
            fail(f"component {name} source {source} is not in this checkout's history")
        if not is_ancestor(checkout, source, candidate):
            fail(f"component {name} source {source} is not an ancestor of the candidate")
        # An image from an older commit is the release's only if nothing it is made of changed since.
        if git(checkout, "diff", "--quiet", source, candidate, "--", *component["paths"],
               check=False).returncode != 0:
            fail(f"component {name} changed after {source}; its image is not this release's")
        for variant in sorted(component["images"]):
            lines.append("\t".join([name, variant, entry["images"][variant], source, entry.get("version") or ""]))
    print("\n".join(lines))


def main(argv: list) -> None:
    commands = {
        ("previous", 4): previous,
        ("plan", 4): plan,
        ("assemble", 6): assemble,
        ("signer", 4): signer,
        ("check-signature", 4): check_signature,
        ("fetch-verified", 4): fetch_verified,
        ("paths", 4): paths,
        ("verify", 4): verify,
    }
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
    elif argv and (argv[0], len(argv)) in commands:
        commands[(argv[0], len(argv))](*argv[1:])
    else:
        print(__doc__, file=sys.stderr)
        sys.exit(2)


if __name__ == "__main__":
    main(sys.argv[1:])
