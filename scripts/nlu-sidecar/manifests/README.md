# NLU model checksum manifests (source of trust)

`install-nlu-model.sh` is **fail-closed**: it installs a model ONLY if a committed
manifest `<model-key>.sha256` exists here and every listed file's SHA-256 matches
the downloaded bytes. Missing manifest, missing file, or any mismatch aborts the
install with nothing activated. The installer **never** generates or updates a
manifest from downloaded files.

## Format

Each line: `<sha256>␠␠<relative/path>` (GNU `sha256sum` compatible), paths
relative to the model variant directory. Comments (`#`) and blank lines allowed.

## Populating a manifest (out-of-band, from the model host — NOT a local export)

The trusted hashes come from the model repository's published LFS metadata at the
**pinned revision**, obtained on a trusted workstation, e.g.:

```bash
python - <<'PY'
from huggingface_hub import HfApi
repo, rev = "microsoft/Phi-4-mini-instruct-onnx", "<PINNED_COMMIT_SHA>"
sub = "cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4"
api = HfApi()
for f in api.list_repo_files(repo, revision=rev):
    if f.startswith(sub + "/"):
        info = api.get_paths_info(repo, [f], revision=rev, expand=True)[0]
        # LFS files expose sha256; small non-LFS files must be hashed from the
        # trusted download on the workstation, then reviewed before commit.
        sha = getattr(info, "lfs", None) and info.lfs.get("sha256")
        print(sha or "REVIEW", f[len(sub)+1:])
PY
```

Review the output, fill any non-LFS `REVIEW` rows from a trusted download, commit
the manifest, and only then run the installer on the production host.

**No manifest is committed here yet** — it is added for the model that wins the
target-host holdout benchmark, after its revision is pinned in
`install-nlu-model.sh`.
