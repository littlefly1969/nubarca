# Target-server benchmark: copy-paste sequence

This is a short operator sequence for running [`benchmark.md`](benchmark.md)
on the target server tonight. Run the lanes in priority order: **text, then
OCR, then visual**. Every lane writes its own results, so an interrupted night
still produces something useful.

What this touches, and what it does not:

- **Read-only towards production.** It never runs `docker compose`, never uses
  `$DC`, never runs `exec` in a `nubarca-*` container, and never connects to
  PostgreSQL. Its only contact with the production stack is
  `docker stats --no-stream` and a `grep` of one `.env` line.
- The benchmark runs in throwaway containers with `--network none`. Its only
  network step is the explicit model fetch.
- Everything lives in two operator directories on the **data mount**, never on
  the root filesystem and never in the production checkout.
- The golden set is synthetic. No owner data is read.

## 0. Variables (from the operator; never written into source)

```bash
ssh "$NUBARCA_PRODUCTION_SSH"
tmux new -s docbench || screen -S docbench     # the run takes hours; survive a disconnect
set -o pipefail
export NUBARCA_PRODUCTION_CHECKOUT="…"         # only read: its git remote and one .env line
export NUBARCA_BENCH_MODELS_DIR="…"            # on the data mount, ~25–35 GB
export NUBARCA_BENCH_WORK_DIR="…"              # on the data mount, ~2 GB
export NUBARCA_BENCH_REF="…"                   # the branch or commit carrying scripts/document-bench
: "${NUBARCA_BENCH_MODELS_DIR:?}" "${NUBARCA_BENCH_WORK_DIR:?}" "${NUBARCA_BENCH_REF:?}"
mkdir -p "$NUBARCA_BENCH_MODELS_DIR" "$NUBARCA_BENCH_WORK_DIR"
```

## 1. Is the server quiet, and what is it?

```bash
uptime
docker stats --no-stream --format '{{.Name}} {{.CPUPerc}} {{.MemUsage}}'
```

If the worker is busy, for example with a backfill or an HLS transcode, wait
for it or record the load. Latency numbers taken next to a busy worker are
pessimistic.

```bash
{
  date -u; uname -r
  lscpu
  lscpu -e=CPU,CORE,SOCKET,MAXMHZ
  grep -o -w -E 'avx2|avx_vnni|avx512f|amx_tile|f16c|fma' /proc/cpuinfo | sort | uniq -c
  nproc; free -h
  lsblk -d -o NAME,SIZE,ROTA,TYPE
  ls /dev/dri 2>/dev/null; lspci 2>/dev/null | grep -iE 'vga|display'
  docker version --format 'docker {{.Server.Version}}'
  docker info --format 'cpus={{.NCPU}} mem={{.MemTotal}}'
  uptime
} > "$NUBARCA_BENCH_WORK_DIR/hardware.txt" 2>&1
# Optional, needs sudo: memory speed is not visible from inside a container.
sudo dmidecode -t memory 2>/dev/null | grep -E '^\s+(Size|Type|Speed|Configured Memory Speed):' \
  | sort | uniq -c >> "$NUBARCA_BENCH_WORK_DIR/hardware.txt"
```

Pin the bench to the P-cores. On a 12th-gen hybrid CPU these are the logical
CPUs with the highest `MAXMHZ`:

```bash
maxmhz=$(lscpu -e=MAXMHZ --noheadings | sort -n | tail -1)
export NUBARCA_BENCH_CPUSET=$(lscpu -e=CPU,MAXMHZ --noheadings | awk -v m="$maxmhz" '$2==m{print $1}' | paste -sd,)
export NUBARCA_BENCH_THREADS=$(lscpu -e=CORE,MAXMHZ --noheadings | awk -v m="$maxmhz" '$2==m{print $1}' | sort -u | wc -l)
echo "cpuset=$NUBARCA_BENCH_CPUSET threads=$NUBARCA_BENCH_THREADS" | tee -a "$NUBARCA_BENCH_WORK_DIR/hardware.txt"
```

Check the result before going on. If every CPU reports the same `MAXMHZ`,
`cpuset` lists all of them; in that case set both values by hand from
`lscpu -e` (P-cores have two threads per core).

## 2. Free space: check first

```bash
df -h "$NUBARCA_BENCH_MODELS_DIR" "$NUBARCA_BENCH_WORK_DIR" "$(docker info --format '{{.DockerRootDir}}')" /
grep '^BACKUP_DIR=' "$NUBARCA_PRODUCTION_CHECKOUT/.env"      # one line, never `source`
```

Stop if any of these does not hold:

- **Models dir:** at least 40 GB free.
- **Docker root** (where the bench image is built): at least 15 GB free, and
  after the build it still meets the `FAST_DEPLOY.md` §7 floor (below 90 % used
  and at least 10 GiB free).
- **Backup mount:** if the models dir is on the same filesystem as
  `BACKUP_DIR`, that filesystem must still hold a pre-migration backup
  (120 % of the database plus 1 GiB) after the fetch. Backups win.

Never prune anything to make room.

## 3. Source and images

Clone the branch into the work dir. The production checkout is not switched,
not dirtied and not given a worktree.

```bash
git clone --branch "$NUBARCA_BENCH_REF" --single-branch \
  "$(git -C "$NUBARCA_PRODUCTION_CHECKOUT" remote get-url origin)" "$NUBARCA_BENCH_WORK_DIR/src"
cd "$NUBARCA_BENCH_WORK_DIR/src"
git rev-parse HEAD | tee -a "$NUBARCA_BENCH_WORK_DIR/hardware.txt"

docker build -t nubarca-docbench:local scripts/document-bench
export BENCH_IMAGE=nubarca-docbench:local
# Optional, only with a llama.cpp tag or commit you have reviewed (for the GGUF OCR VLMs):
# docker build -t nubarca-docbench:vlm --target vlm \
#   --build-arg LLAMA_CPP_REF="$NUBARCA_BENCH_LLAMA_CPP_REF" scripts/document-bench
# export BENCH_IMAGE=nubarca-docbench:vlm
```

Without the `vlm` image, the two GGUF OCR engines are **recorded skips**.
Nothing fails because of them.

## 4. Fetch the pinned weights

This is the only step with network access. Each lane's models go in a separate
call, so the text lane can start before the rest has arrived:

```bash
F=scripts/document-bench/fetch-models.sh
$F --dry-run                                                      # shows repo@sha per entry
$F --only e5-small,granite-97m,granite-311m,qwen3-emb-0.6b,qwen3-emb-0.6b-ov-int8
$F --only ppocrv5-onnx,glm-ocr,paddleocr-vl-1.5-gguf,xiaomi-ocr-0-gguf
$F --only siglip2-so400m,neomme-260m
# The #216 review shortlist: not pinned yet, resolved to today's commit here.
$F --pin-unpinned --only paddleocr-vl-1.6,ovisocr2,wevisdoc-2b,wemm-emb-2b
cat "$NUBARCA_BENCH_MODELS_DIR/PINNED.json"                       # send this back to be committed
# Optional, if time and disk allow:
# $F --only qwen3-reranker-0.6b,bge-reranker-v2-m3
# $F --only vultron-flash-0.8b,qwen3-vl-emb-2b,neomme-260m-st-late,bge-m3
du -sh "$NUBARCA_BENCH_MODELS_DIR"/*
```

Each model directory gets a `FETCHED.json` with the repo, the revision and the
SHA-256 of every file. `qwen3-emb-0.6b-onnx` is refused on purpose: its pin is
`TODO`. The four review-shortlist entries are fetched only with
`--pin-unpinned`, which prints `PINNED <key>: <repo>@<sha>` and any licence
mismatch; `wemm-emb-2b` is measured but cannot be promoted until its licence is
resolved.

## 5. Run the lanes, offline, in priority order

```bash
bench_run() {
  docker run --rm --network none \
    --user "$(id -u):$(id -g)" -e HOME=/tmp \
    --cpuset-cpus "$NUBARCA_BENCH_CPUSET" --memory 20g --memory-swap 20g \
    -v "$NUBARCA_BENCH_MODELS_DIR":/models:ro \
    -v "$NUBARCA_BENCH_WORK_DIR":/work \
    -e BENCH_THREADS="$NUBARCA_BENCH_THREADS" \
    -e BENCH_ENVIRONMENT="target: $(lscpu | sed -n 's/^Model name: *//p'); load $(cut -d' ' -f1-3 /proc/loadavg)" \
    --entrypoint /bench/run-suite.sh "$BENCH_IMAGE" "$@" 2>&1 | tee -a "$NUBARCA_BENCH_WORK_DIR/run.log"
}

bench_run golden lexical text chunking report     # 1. text
bench_run ocr ocr-vlm report                      # 2. OCR
bench_run visual fuse report                      # 3. visual
# optional: bench_run rerank report
```

`--memory 20g` is the AI budget, so peak RSS is measured inside it. A step that
fails prints `!! step failed`, and the suite carries on.

| Lane | Weights on disk (estimate) | Wall time (estimate) |
|---|---|---|
| Image build | Docker root: ~4–6 GB (+1–2 GB for `vlm`) | 10–20 min (+10–15 min for `vlm`) |
| Fetch | — | depends on bandwidth: ~25 GB at 20 MB/s ≈ 20 min |
| golden + lexical | — (golden set ~45 MB) | ~5 min |
| text + chunking | ~9–11 GB | 45–90 min |
| OCR (Tesseract, RapidOCR) | ~0.1 GB | 15–30 min |
| OCR VLMs (`--subset core`) | ~4–6 GB | 1–4 h; the slowest lane |
| visual + fuse | ~5.5 GB (+7–10 GB optional) | 30–90 min |
| rerank (optional) | ~3.5 GB | 20–40 min |

**All of these are estimates.** The benchmark exists to measure them. If the
night runs short, stop after the OCR VLM lane's first engine (Ctrl-C), run
`bench_run visual fuse report`, and resume the VLMs another night.

## 6. Results, and sending them back

Results land in:

- `$NUBARCA_BENCH_WORK_DIR/results/<UTC stamp>-<lane>-<name>/`, with
  `manifest.json`, `metrics.json` and per-query/per-page JSONL;
- `results/SUMMARY.md`, plus `hardware.txt` and `run.log` next to them.

```bash
cd "$NUBARCA_BENCH_WORK_DIR"
tar czf docbench-results.tar.gz results hardware.txt run.log
sha256sum docbench-results.tar.gz | tee docbench-results.tar.gz.sha256
```

From the workstation, copy the archive and attach it, together with its
`.sha256`, to the Document Intelligence thread:

```bash
scp "$NUBARCA_PRODUCTION_SSH:$NUBARCA_BENCH_WORK_DIR/docbench-results.tar.gz*" .
```

The archive holds synthetic data only. It must still not be committed or posted
publicly, because the per-query files contain the golden questions
([`benchmark.md`](benchmark.md) §1).

## 7. Afterwards

Keep the weights and the clone for re-runs. Removing the bench images is a
separate decision for the operator, and targeted:
`docker image rm nubarca-docbench:local nubarca-docbench:vlm`. Never use a
prune command (`FAST_DEPLOY.md` §7).
