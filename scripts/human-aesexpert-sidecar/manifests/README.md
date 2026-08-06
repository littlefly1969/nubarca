# HumanAesExpert model checksum manifests (source of trust)

`install-human-aesexpert-model.sh` is **fail-closed**: it installs the model ONLY
if a committed manifest `<model-key>.sha256` exists here and every listed file's
SHA-256 matches the downloaded bytes. Missing manifest, missing file, or any
mismatch aborts the install with nothing activated. The installer **never**
generates or updates a manifest from downloaded files.

## Format

Each line: `<sha256>␠␠<relative/path>` (GNU `sha256sum` compatible), paths
relative to the model directory root. Comments (`#`) and blank lines allowed.

Expected files for `KlingTeam/HumanAesExpert-1B` (verify at the pinned revision):
`config.json`, `configuration_internvl_chat.py`, `configuration_intern_vit.py`,
`modeling_internvl_chat.py`, `modeling_intern_vit.py`, `modeling_qwen.py`,
`conversation.py`, `model.safetensors`, `tokenizer_config.json`, `vocab.json`,
`merges.txt`, `generation_config.json`, `preprocessor_config.json`.

## Populating a manifest (out-of-band, from the model host — NOT a local export)

The trusted hashes come from the model repository's published metadata at the
**pinned revision**, obtained on a trusted workstation, e.g.:

```bash
python - <<'PY'
from huggingface_hub import HfApi
repo, rev = "KlingTeam/HumanAesExpert-1B", "<PINNED_COMMIT_SHA>"
api = HfApi()
for f in api.list_repo_files(repo, revision=rev):
    info = api.get_paths_info(repo, [f], revision=rev, expand=True)[0]
    sha = getattr(info, "lfs", None) and info.lfs.get("sha256")
    print(sha or "REVIEW", f)
PY
```

Review the output, fill any non-LFS `REVIEW` rows from a trusted download, commit
the manifest, and only then run the installer on the production host.

## License

`KlingTeam/HumanAesExpert-1B` and its custom code are released under the **MIT
license** (verify the repository `LICENSE` at the pinned revision before deploy).
The 1B checkpoint is built on InternVL2-1B (InternViT + Qwen2) and requires
`transformers==4.44.2` per the official model card.

The committed manifest `human-aesexpert-1b.sha256` pins revision
`b8f7ee3f3a1217ecd331fd6d57b6959f5c0da183` (16 files). `model.safetensors` uses
its upstream Git-LFS OID (== SHA-256); the small non-LFS files use SHA-256
computed from the bytes served at that immutable commit. The installer was
run against it end-to-end: all 16 checksums verified, and a deliberately
tampered hash was correctly rejected (fail-closed).
