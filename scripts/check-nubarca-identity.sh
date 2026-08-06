#!/usr/bin/env bash
# Assert the NubArca identity contract over tracked source.
#
# This is a POSITIVE contract. It states what the product IS — solution name,
# assembly, namespaces, package identifiers, cookie prefix, runtime resource
# names, release version, operator-configuration surface — and fails when any of
# those stops being true.
#
# It deliberately encodes no knowledge of any previous name for this product.
# A checker built around a denylist has to carry the thing it forbids, which
# means the repository keeps a permanent record of it and every exception argues
# for one more. Asserting the current truth is strictly stronger: an identifier
# that drifts to ANY other spelling fails, not only to one remembered one.
#
# The second half of the contract is that source describes the PRODUCT and never
# one installation. Host names, IP addresses, logins, checkout directories and
# public origins are operator configuration; they must arrive through NUBARCA_*
# variables and must never appear as a tracked default.
#
#   scripts/check-nubarca-identity.sh             # check
#   scripts/check-nubarca-identity.sh --verbose   # also list what was asserted
#   scripts/check-nubarca-identity.sh --self-test # prove the detectors work
#
# Only git-tracked files are scanned, so .git, node_modules and build output are
# excluded by construction.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

exec python3 - "$@" <<'PYTHON'
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path.cwd()
ARGS = sys.argv[1:]
VERBOSE = "--verbose" in ARGS

PRODUCT = "NubArca"
SLUG = "nubarca"
SOLUTION = "NubArca.sln"
API_PROJECT = "src/NubArca.Api/NubArca.Api.csproj"
TEST_PROJECT = "tests/NubArca.Api.Tests/NubArca.Api.Tests.csproj"
API_NAMESPACE = "NubArca.Api"
TEST_NAMESPACE = "NubArca.Api.Tests"
FRONTEND_PACKAGE = "nubarca-frontend"
TV_ANDROID_PACKAGE = "it.littlefly.nubarca.tv"
TV_SLUG = "nubarca-tv"
COOKIE_PREFIX = "NubArca."
OPERATOR_CONFIG_LIB = "scripts/lib/operator-config.sh"

# The operator-configuration surface: every value that identifies a particular
# installation rather than the product. Each must be validated by the shared
# helper, so no script can invent its own silent fallback.
OPERATOR_VARIABLES = (
    "NUBARCA_PRODUCTION_SSH",
    "NUBARCA_PRODUCTION_CHECKOUT",
    "NUBARCA_PUBLIC_ORIGIN",
    "NUBARCA_SERVICE_ROOT",
    "NUBARCA_STORAGE_ROOT",
    "NUBARCA_IMPORT_ROOT",
    "NUBARCA_TV_APK_DIR",
    "NUBARCA_ENCRYPTED_BACKUP_TARGET",
)

SKIP_DIRS = (
    "node_modules/", "/bin/", "/obj/", "/dist/", "/build/",
    "TestResults/", "graphify-out/", "/.venv/", "__pycache__/",
    "playwright-report/", "test-results/",
)
SKIP_SUFFIXES = (
    ".png", ".jpg", ".jpeg", ".gif", ".ico", ".webp", ".svg",
    ".woff", ".woff2", ".ttf", ".otf", ".eot",
    ".pdf", ".zip", ".gz", ".tar", ".apk", ".aab", ".keystore", ".jks",
    ".onnx", ".bin", ".trx", ".so", ".dll", ".dylib", ".exe",
)
# Dependency manifests pin upstream package names and integrity hashes; their
# content is not ours to name.
SKIP_FILES = ("package-lock.json",)


# --------------------------------------------------------------------------
# Installation-specific value detectors
#
# Each returns a reason string when the line carries a value belonging to one
# installation, or None when the line is product source.
# --------------------------------------------------------------------------

# Numeric literals that carry no installation identity.
#   loopback / unspecified / broadcast          — universal
#   127.0.0.11                                  — Docker's embedded DNS resolver
#   10.0.2.2                                    — Android emulator host alias
#   10.0.0.0/8, 172.16.0.0/12                   — container-subnet documentation
#   192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24 — RFC 5737 documentation
#
# 127.0.0.11 is fixed by Docker, identical inside every Compose network, and is
# the only way to defer upstream name resolution to request time. An operator
# cannot choose it and changing it would name nothing.
GENERIC_ADDRESSES = frozenset({
    "127.0.0.1", "0.0.0.0", "255.255.255.255", "10.0.2.2", "127.0.0.11",
})
GENERIC_ADDRESS_PREFIXES = ("10.", "192.0.2.", "198.51.100.", "203.0.113.") + tuple(
    f"172.{octet}." for octet in range(16, 32)
)
# An IPv4 literal, refusing to match inside a longer dotted-numeric token such as
# an OID (1.3.6.1.5.5.7.3.3) or a version string (1.2.3.4.5).
IPV4 = re.compile(r"(?<![\d.])(\d{1,3}(?:\.\d{1,3}){3})(?![\d.])")

# `user@host` where host is dotted — an ssh/scp destination or a real mail host.
# The final label must be alphabetic, so a dependency pin (`uuid@7.0.3`,
# `react-native-tvos@0.85-stable`) is not mistaken for a login on a server.
SSH_TARGET = re.compile(r"\b[A-Za-z0-9._-]+@((?:[A-Za-z0-9-]+\.)+[A-Za-z]{2,})\b")
# Hosts that cannot belong to a real installation: RFC 2606 / RFC 6761 reserved
# names, plus the loopback name. Test fixtures and documentation use these.
GENERIC_HOSTS = (
    "example.com", "example.org", "example.net", "localhost",
    ".example", ".invalid", ".test", ".local", ".localhost",
)
# Lines that actually contact a remote machine, as opposed to merely containing
# an `@`. Scoping the named-host rule to these keeps email fixtures out of it.
REMOTE_INVOCATION = re.compile(
    r"(\b(ssh|scp|rsync|sftp)\b|SSH\s*(target|destination)?\s*[:=]|NUBARCA_PRODUCTION_SSH)",
    re.IGNORECASE,
)

# A NUBARCA_* shell/Compose default that supplies a concrete value. Operator
# configuration must fail closed, so `:-` with a path, URL or login is wrong;
# `:?` (required) and a plain non-locational literal are fine.
OPERATOR_DEFAULT = re.compile(r"\$\{(NUBARCA_[A-Z0-9_]+):-([^}]*)\}")

# The installation's own web hostname, in any subdomain. A reversed-domain
# application id such as it.littlefly.nubarca.tv is an Android package, not a
# host, and does not match this shape.
OWNER_HOSTNAME = re.compile(r"\b[A-Za-z0-9-]+\.littlefly\.it\b")

# Operator-facing instructions: agent context, runbooks and the scripts that
# reach an installation. These are the files that must never name a directory on
# somebody's server — unlike a Dockerfile, where an absolute path is a location
# INSIDE the image we build and therefore genuinely ours.
OPERATOR_FACING = ("CLAUDE.md", "AGENTS.md", "deploy/", "docs/", "scripts/")
# `cd` into a directory where a deployment checkout actually lives on a host. The
# checkout is operator configuration, so a runbook changes into
# "$NUBARCA_PRODUCTION_CHECKOUT" and never spells the directory out.
#
# Restricted to host-checkout roots on purpose: `cd /storage` or `cd /app` inside
# a `docker run` one-liner is a path in a container we define, not a location on
# somebody's server.
CHECKOUT_LITERAL = re.compile(
    r"\bcd\s+(?:--\s+)?[\"']?(/(?:opt|srv|home|root|mnt|var/www)/[A-Za-z0-9._/-]+)"
)


def address_violation(path: str, line: str) -> str | None:
    for literal in IPV4.findall(line):
        if literal in GENERIC_ADDRESSES:
            continue
        if literal.startswith(GENERIC_ADDRESS_PREFIXES):
            continue
        return f"IP address literal {literal} names one installation"
    return None


# A login paired with a bare address always names a machine, whatever range the
# address is in. This is separate from `address_violation`, which tolerates
# container-subnet and documentation ranges appearing on their own.
SSH_TARGET_ADDRESS = re.compile(r"\b[A-Za-z0-9._-]+@(\d{1,3}(?:\.\d{1,3}){3})\b")


def ssh_target_violation(path: str, line: str) -> str | None:
    match = SSH_TARGET_ADDRESS.search(line)
    if match:
        return f"login@address literal for {match.group(1)} names one installation"
    # A named host only counts when the line is actually reaching a machine.
    # Plain `user@domain` text is overwhelmingly an email address — a fixture, an
    # i18n placeholder, a masked recipient — and those are not deployment targets.
    #
    # Known boundary: a bare login-at-domain in prose, with no remote-invocation
    # signal on the line, is not flagged — it is genuinely ambiguous with an email
    # address. The unambiguous shapes are each caught by their own detector above:
    # a login with an IP, this installation's own hostname, a located default.
    if not REMOTE_INVOCATION.search(line):
        return None
    for host in SSH_TARGET.findall(line):
        lowered = host.lower()
        if lowered.endswith(GENERIC_HOSTS) or ".example." in f".{lowered}.":
            continue
        return f"login@host literal for {host} names one installation"
    return None


def operator_default_violation(path: str, line: str) -> str | None:
    for name, default in OPERATOR_DEFAULT.findall(line):
        value = default.strip().strip("'\"")
        if value.startswith("/") or "://" in value or "@" in value:
            return (
                f"{name} falls back to {value!r}; operator configuration must be "
                f"required (use ${{{name}:?...}}), never defaulted to a location"
            )
    return None


def owner_hostname_violation(path: str, line: str) -> str | None:
    match = OWNER_HOSTNAME.search(line)
    if match:
        return f"public hostname {match.group(0)} names one installation"
    return None


def checkout_literal_violation(path: str, line: str) -> str | None:
    """A runbook or deploy script must not spell out a directory on a server."""
    if not path.startswith(OPERATOR_FACING):
        return None
    if not (path.endswith(".md") or path.endswith(".sh")):
        return None
    match = CHECKOUT_LITERAL.search(line)
    if not match:
        return None
    return (
        f"`cd {match.group(1)}` assumes a directory on somebody's host; change "
        'into "$NUBARCA_PRODUCTION_CHECKOUT" instead'
    )


DETECTORS = (
    address_violation,
    ssh_target_violation,
    operator_default_violation,
    owner_hostname_violation,
    checkout_literal_violation,
)


def installation_violation(path: str, line: str) -> str | None:
    for detector in DETECTORS:
        reason = detector(path, line)
        if reason:
            return reason
    return None


# --------------------------------------------------------------------------
# Contract
# --------------------------------------------------------------------------

class Contract:
    def __init__(self) -> None:
        self.failures: list[str] = []
        self.asserted: list[str] = []

    def require(self, ok: bool, statement: str, detail: str = "") -> bool:
        if ok:
            self.asserted.append(statement)
        else:
            self.failures.append(f"{statement}{chr(10) + '      ' + detail if detail else ''}")
        return ok

    def read(self, path: str) -> str | None:
        try:
            return (ROOT / path).read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError):
            self.failures.append(f"required file is missing or unreadable: {path}")
            return None


def tracked_files() -> list[str]:
    out = subprocess.run(
        ["git", "ls-files", "-z"], capture_output=True, text=True, check=True
    ).stdout
    files = []
    for path in out.split("\0"):
        if not path:
            continue
        if any(part in f"/{path}" for part in SKIP_DIRS):
            continue
        if path.endswith(SKIP_SUFFIXES) or Path(path).name in SKIP_FILES:
            continue
        files.append(path)
    return files


def check_solution(c: Contract) -> None:
    sln = c.read(SOLUTION)
    if sln is None:
        return
    c.require(
        API_PROJECT.replace("/", "\\") in sln or API_PROJECT in sln,
        f"solution {SOLUTION} contains {API_PROJECT}",
    )
    c.require(
        TEST_PROJECT.replace("/", "\\") in sln or TEST_PROJECT in sln,
        f"solution {SOLUTION} contains {TEST_PROJECT}",
    )


def check_version(c: Contract) -> str | None:
    csproj = c.read(API_PROJECT)
    package_json = c.read("frontend/package.json")
    if csproj is None or package_json is None:
        return None
    match = re.search(r"<Version>([^<]+)</Version>", csproj)
    if not c.require(match is not None, f"{API_PROJECT} declares <Version>"):
        return None
    backend_version = match.group(1).strip()
    frontend_version = json.loads(package_json).get("version", "")
    c.require(
        re.fullmatch(r"\d+\.\d+\.\d+", backend_version) is not None,
        f"release version is a plain semantic version ({backend_version})",
    )
    c.require(
        backend_version == frontend_version,
        "backend and frontend declare the same release version",
        f"{API_PROJECT}={backend_version}, frontend/package.json={frontend_version}",
    )
    changelog = c.read("CHANGELOG.md")
    if changelog is not None:
        c.require(
            backend_version in changelog,
            f"CHANGELOG.md documents release {backend_version}",
        )
    return backend_version


def check_namespaces(c: Contract, files: list[str]) -> None:
    for root, expected in (("src/", API_NAMESPACE), ("tests/", TEST_NAMESPACE)):
        offenders = []
        checked = 0
        for path in files:
            if not path.startswith(root) or not path.endswith(".cs"):
                continue
            text = (ROOT / path).read_text(encoding="utf-8", errors="replace")
            declared = re.findall(r"^\s*namespace\s+([A-Za-z0-9_.]+)", text, re.MULTILINE)
            if not declared:
                continue
            checked += 1
            for namespace in declared:
                if namespace != expected and not namespace.startswith(expected + "."):
                    offenders.append(f"{path}: namespace {namespace}")
        c.require(
            not offenders and checked > 0,
            f"every namespace under {root} is {expected} or below ({checked} files)",
            "; ".join(offenders[:5]),
        )


def check_runtime_identity(c: Contract) -> None:
    dockerfile = c.read("src/NubArca.Api/Dockerfile")
    if dockerfile is not None:
        c.require(
            f"{API_NAMESPACE}.dll" in dockerfile,
            f"the API image entrypoint runs {API_NAMESPACE}.dll",
        )

    program = c.read("src/NubArca.Api/Program.cs")
    if program is not None:
        cookies = re.findall(r"Cookie\.Name\s*=\s*\"([^\"]+)\"", program)
        c.require(
            bool(cookies) and all(name.startswith(COOKIE_PREFIX) for name in cookies),
            f"every auth cookie name starts with {COOKIE_PREFIX!r} ({', '.join(cookies) or 'none found'})",
        )

    package_json = c.read("frontend/package.json")
    if package_json is not None:
        c.require(
            json.loads(package_json).get("name") == FRONTEND_PACKAGE,
            f"frontend package is named {FRONTEND_PACKAGE}",
        )

    tv_config = c.read("tv/app.config.js")
    if tv_config is not None:
        c.require(
            f"'{TV_ANDROID_PACKAGE}'" in tv_config or f'"{TV_ANDROID_PACKAGE}"' in tv_config,
            f"the TV Android package is {TV_ANDROID_PACKAGE}",
        )
        c.require(
            f"'{TV_SLUG}'" in tv_config or f'"{TV_SLUG}"' in tv_config,
            f"the TV Expo slug is {TV_SLUG}",
        )


def check_compose(c: Contract) -> None:
    compose = c.read("docker-compose.prod.yml")
    if compose is None:
        return
    c.require(
        re.search(rf"^name:\s*{SLUG}\s*$", compose, re.MULTILINE) is not None,
        f"the production Compose project is named {SLUG}",
    )
    named = re.findall(r"^\s*(?:container_name|name):\s*([A-Za-z0-9_.-]+)\s*$", compose, re.MULTILINE)
    offenders = [name for name in named if not name.startswith(SLUG)]
    c.require(
        not offenders and len(named) > 1,
        f"every Compose container, volume and network name starts with {SLUG!r} ({len(named)} names)",
        ", ".join(offenders[:6]),
    )


def check_operator_configuration(c: Contract) -> None:
    lib = c.read(OPERATOR_CONFIG_LIB)
    if lib is None:
        return
    missing = [name for name in OPERATOR_VARIABLES if name not in lib]
    c.require(
        not missing,
        f"{OPERATOR_CONFIG_LIB} validates all {len(OPERATOR_VARIABLES)} operator variables",
        "missing: " + ", ".join(missing),
    )
    c.require(
        "operator_config_fail" in lib,
        f"{OPERATOR_CONFIG_LIB} fails closed on missing operator configuration",
    )

    # Agent and deploy documentation must send the reader to the operator for the
    # installation's location rather than printing one.
    for path in ("CLAUDE.md", "deploy/FAST_DEPLOY.md"):
        text = c.read(path)
        if text is None:
            continue
        c.require(
            "NUBARCA_PRODUCTION_CHECKOUT" in text and "NUBARCA_PRODUCTION_SSH" in text,
            f"{path} directs the reader to NUBARCA_PRODUCTION_SSH / NUBARCA_PRODUCTION_CHECKOUT",
        )


def check_no_installation_values(c: Contract, files: list[str]) -> None:
    violations: list[str] = []
    scanned = 0
    for path in files:
        try:
            text = (ROOT / path).read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError):
            continue
        scanned += 1
        for lineno, line in enumerate(text.splitlines(), 1):
            reason = installation_violation(path, line)
            if reason:
                snippet = line.strip()
                if len(snippet) > 120:
                    snippet = snippet[:117] + "..."
                violations.append(f"{path}:{lineno}: {reason}\n        {snippet}")
    c.require(
        not violations,
        f"no tracked file carries an installation-specific value ({scanned} text files)",
        "\n      ".join(violations[:25])
        + (f"\n      ... and {len(violations) - 25} more" if len(violations) > 25 else ""),
    )


# --------------------------------------------------------------------------
# Self-test: prove the detectors actually detect.
# --------------------------------------------------------------------------

def self_test() -> int:
    # Every rejecting fixture ASSEMBLES its offending literal from fragments.
    # That is not cosmetic. This file is tracked and therefore scanned, so a
    # fixture written out in full would make the contract fail on its own test
    # data — and the fix for that must never be an exemption, because an
    # exemption is exactly where a real value would then hide.
    ip_host = "192.168.1." + "180"
    ip_dev = "192.168.1." + "100"
    ip_public = "88.44.22." + "11"
    ip_private = "10.9.9." + "9"
    owner_host = "media.littlefly" + ".it"
    # Split at "${" so OPERATOR_DEFAULT cannot match the fixture itself: the
    # regex anchors on "${" immediately followed by NUBARCA_, which no line here
    # ever spells contiguously.
    opener = "${"
    apk_dir_default = opener + "NUBARCA_TV_APK_DIR:-/srv/somewhere/tv-apk}"
    apk_mount_default = opener + "NUBARCA_TV_APK_DIR:-/srv/somewhere}"
    origin_default = opener + "NUBARCA_PUBLIC_ORIGIN:-https://cloud.example.invalid}"
    host_checkout = "cd /opt/" + "example"
    scp_host = "deploy@media.somewhere" + ".net"
    ssh_var_host = "deploy@fileserver" + ".lan"

    must_reject = [
        ("CLAUDE.md", f"- SSH target: `admin@{ip_host}`"),
        ("deploy/FAST_DEPLOY.md", f"- SSH: `deploy@{ip_private}`"),
        ("deploy/FAST_DEPLOY.md", f"{host_checkout} && git pull"),
        ("deploy/publish-tv-apk.sh", f'target="{opener}NUBARCA_PRODUCTION_SSH:-admin@{ip_host}}}"'),
        ("deploy/publish-tv-apk.sh", f'dir="{apk_dir_default}"'),
        ("docker-compose.prod.yml", f"      - {apk_mount_default}:/usr/share/x:ro"),
        ("docs/x.md", f"open https://{owner_host}/tv.apk"),
        ("tv/app.config.js", f"const DEV_DEFAULT = 'http://{ip_dev}:5177';"),
        ("mobile/app.json", f'      "apiBaseUrl": "http://{ip_dev}:5177"'),
        ("README.md", f"curl https://{ip_public}:8080/health"),
        ("deploy/x.sh", f'origin="{origin_default}"'),
        ("deploy/publish-tv-apk.sh", f'scp "$apk" {scp_host}:/srv/x/tv.apk'),
        ("docs/tv-apk-distribution.md",
         f"defaults to `NUBARCA_PRODUCTION_SSH={ssh_var_host}`; override it"),
    ]
    must_allow = [
        # Universal / platform / documentation addresses.
        ("deploy/restore.sh", "curl http://127.0.0.1:8080/health"),
        ("src/NubArca.Api/Program.cs", 'builder.WebHost.UseUrls("http://0.0.0.0:8080");'),
        ("mobile/src/screens/LoginScreen.tsx", "  ? 'http://10.0.2.2:5177'"),
        (".env.example", "# ForwardedHeaders__KnownNetworks__0=172.18.0.0/16"),
        (".env.example", "# ForwardedHeaders__KnownProxies__0=10.0.0.5"),
        ("tests/NubArca.Api.Tests/Audit/ForwardedHeadersTests.cs",
         'client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.5");'),
        ("tests/NubArca.Api.Tests/Audit/ForwardedHeadersTests.cs",
         'Assert.Equal("198.51.100.42", row.IpAddress);'),
        # Dotted-numeric tokens that are not addresses.
        ("src/NubArca.Api/TvUpdates/TvUpdateStore.cs", '.Any(oid => oid.Value == "1.3.6.1.5.5.7.3.3");'),
        ("tv/scripts/code-signing-certificate.cjs", "const CODE_SIGNING_OID = '1.3.6.1.5.5.7.3.3';"),
        # Generic hosts and generic addresses in documentation.
        ("deploy/FIRST_DEPLOY.md", "ssh operator@example.com"),
        (".env.example", "NUBARCA_ADMIN_EMAIL=admin@example.com"),
        # A reversed-domain application id is not a hostname.
        ("tv/app.config.js", "      package: 'it.littlefly.nubarca.tv',"),
        # Email addresses: fixtures, i18n placeholders and masked recipients are
        # not deployment targets, whatever their domain looks like.
        ("frontend/src/i18n/it.ts", "'albumShare.emailPlaceholder': 'nome@esempio.it',"),
        ("tests/NubArca.Api.Tests/Albums/RecipientEmailMaskTests.cs",
         '[InlineData("a.b.c@mail.example.co.uk", "a\u2022\u2022\u2022c@mail.example.co.uk")]'),
        ("tests/NubArca.Api.Tests/Metadata/MetadataServiceVideoTests.cs",
         'Email = "o@x.com", DisplayName = "O",'),
        ("frontend/packages/api-client/src/albumSharing.ts",
         '// Masked account address ("m\u2022\u2022\u2022i@nubarca.local"), owner-only.'),
        # Operator configuration referenced correctly.
        ("deploy/FAST_DEPLOY.md", 'ssh "$NUBARCA_PRODUCTION_SSH"'),
        ("deploy/FAST_DEPLOY.md", 'cd "$NUBARCA_PRODUCTION_CHECKOUT"'),
        ("docker-compose.prod.yml", "      - ${NUBARCA_TV_APK_DIR:?set it in .env}:/usr/share/x:ro"),
        # A non-locational default is deployment tuning, not an installation identity.
        ("docker-compose.facedirect-api.yml", 'FaceDetectorDevice: "${NUBARCA_FACE_DETECTOR_DEVICE:-GPU}"'),
        ("deploy/backup.sh", 'KEEP_UP="${NUBARCA_KEEP_UP:-false}"'),
        ("docker-compose.openvino-direct.yml", "GIT_SHA: ${NUBARCA_GIT_SHA:-unknown}"),
    ]

    failures: list[str] = []
    for path, line in must_reject:
        if installation_violation(path, line) is None:
            failures.append(f"should be REJECTED but was allowed:\n      {path}: {line}")
    for path, line in must_allow:
        reason = installation_violation(path, line)
        if reason is not None:
            failures.append(f"should be ALLOWED but was rejected ({reason}):\n      {path}: {line}")

    total = len(must_reject) + len(must_allow)
    if failures:
        print(f"self-test: {len(failures)} of {total} cases failed\n", file=sys.stderr)
        for failure in failures:
            print(f"  - {failure}", file=sys.stderr)
        return 1
    print(
        f"self-test: {total}/{total} cases correct "
        f"({len(must_reject)} correctly rejected, {len(must_allow)} correctly allowed)"
    )
    return 0


def main() -> int:
    if "--self-test" in ARGS:
        return self_test()

    files = tracked_files()
    c = Contract()
    check_solution(c)
    version = check_version(c)
    check_namespaces(c, files)
    check_runtime_identity(c)
    check_compose(c)
    check_operator_configuration(c)
    check_no_installation_values(c, files)

    if VERBOSE:
        for statement in c.asserted:
            print(f"  ok  {statement}")
        print()

    if c.failures:
        print(
            f"{len(c.failures)} {PRODUCT} identity contract failure(s):\n",
            file=sys.stderr,
        )
        for failure in c.failures:
            print(f"  - {failure}", file=sys.stderr)
        print(
            f"\nThe product is {PRODUCT}. Source describes the product; a host, login,\n"
            "checkout path, storage mount or public origin belongs to one installation\n"
            "and must reach the code through NUBARCA_* operator configuration.",
            file=sys.stderr,
        )
        return 1

    print(
        f"{PRODUCT} identity contract holds: {len(c.asserted)} assertions over "
        f"{len(files)} tracked files"
        + (f", release {version}" if version else "")
    )
    return 0


sys.exit(main())
PYTHON
