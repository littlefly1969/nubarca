"""NubArca document-intelligence benchmark harness (evaluation tooling only).

Nothing in this package is a NubArca runtime component. It generates a
SYNTHETIC golden set, runs candidate models against it in a disposable
container, and writes manifests and reports. It never downloads weights on its
own: models are read from a directory the operator fetched with
`fetch-models.sh` beforehand.
"""

GENERATOR_VERSION = "golden-v1"
