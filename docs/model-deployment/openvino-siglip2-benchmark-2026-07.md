# SigLIP2 OpenVINO target-host benchmark (2026-07)

This report records the production-host benchmark used to choose the execution
strategy for `google/siglip2-so400m-patch14-384`. It is the authoritative record
for the meaning of **mixed CPU+GPU** in this project.

## Sample and method

- 35,137 blobs inspected; 25,735 images recognized.
- 100 large photographs sampled.
- Resolution range: 24.5–100 megapixels; median 36.2 megapixels.
- File-size range: 4.8–52 MiB.
- Common preprocessing median: 340 ms.
- The same preprocessed tensors were passed to every runtime configuration.
- Quality comparisons used all 4,950 image-to-image cosine pairs.

The preprocessing time is not included in the image-tower figures below. Add
about 340 ms for a non-pipelined end-to-end estimate on this sample.

## Image tower

| Configuration | Median | Throughput |
| --- | ---: | ---: |
| ONNX Runtime CPU baseline | 2,483 ms | 0.399 img/s |
| OpenVINO CPU FP32, six physical P-cores | 1,563 ms | 0.639 img/s |
| OpenVINO GPU FP32 | 2,291 ms | 0.437 img/s |
| OpenVINO GPU FP16 | **746 ms** | **1.343 img/s** |

### FP32 quality

ONNX Runtime CPU versus OpenVINO CPU FP32:

- minimum cosine between corresponding embeddings: 0.999999881;
- mean delta over the 4,950 pairwise cosines: 2.45 × 10^-7;
- maximum delta: 1.82 × 10^-6;
- top-5, top-10 and top-20 identical, including order, for 100/100 images.

ONNX Runtime CPU versus OpenVINO GPU FP32:

- minimum cosine between corresponding embeddings: 0.999999881;
- maximum pairwise-cosine delta: 1.49 × 10^-6;
- top-5, top-10 and top-20 identical, including order, for 100/100 images.

For this corpus, both OpenVINO FP32 paths are operationally equivalent to the
baseline.

### FP16 quality

ONNX Runtime CPU versus OpenVINO GPU FP16:

- corresponding-embedding cosine: mean 0.999721, minimum 0.999185;
- mean pairwise-cosine delta: 0.00290;
- maximum delta: 0.00813;
- top-5 overlap: mean 98.4%, minimum 80%;
- top-10 overlap: mean 98.4%;
- top-20 overlap: mean 98.95%.

FP16 retains high quality but is not equivalent to FP32 and changes some
retrieval results.

## Text tower

The text test used 100 queries covering 25 concepts with Italian and English
variants, using the production tokenizer and attention-mask contract.

| Configuration | Median | Queries/s |
| --- | ---: | ---: |
| ONNX Runtime CPU baseline | 136 ms | 7.53 |
| OpenVINO CPU FP32 | 126 ms | 7.94 |
| OpenVINO GPU FP32 | 96 ms | 10.31 |
| OpenVINO GPU FP16 | **53 ms** | **18.19** |

FP32 preserved text-to-image ranking exactly: the mean delta over 10,000 scores
was about 8 × 10^-8, and top-5/top-10/top-20 order was identical for 100/100
queries.

With both towers in FP16:

- minimum cosine between corresponding text embeddings: 0.999962;
- mean text-to-image top-5 overlap: 98.2%;
- mean top-10 overlap: 97.7%;
- mean top-20 overlap: 97.65%.

The 126 ms OpenVINO CPU text latency is already suitable for online queries and
can keep the GPU free for background image throughput. GPU placement remains a
valid lower-latency policy when background image work is not using it.

## CPU topology and mixed execution

| Strategy | Throughput |
| --- | ---: |
| Six physical P-cores | 0.677 img/s |
| Twelve P-core hardware threads (HT) | 0.659 img/s |
| Concurrent CPU FP32 + GPU FP32 | **0.929 img/s** |
| Concurrent CPU FP32 + GPU FP16 | **1.679 img/s** |
| OpenVINO HETERO FP32 | 0.439 img/s |

Hyper-Threading reduced throughput by about 2.9%; one thread per physical
P-core is correct on this host.

The original winning **mixed** measurement did not use OpenVINO's `MULTI`
plugin. The recovered benchmark artifact
(`/tmp/nubarca-siglip-scheduling-probe.py` on the target host) compiled one
CPU FP32 model and one GPU FP32 model, created one `InferRequest` for each, and
fed them from a shared `queue.Queue` using two Python threads. The CPU processed
34 images and the GPU 26 in the same interval. No NubArca application code was
changed, but the benchmark harness itself supplied the bounded scheduling.

It is also not OpenVINO `HETERO`: `HETERO` did not partition this graph, assigned
it wholly to the GPU and performed accordingly.

Mixed CPU+GPU FP32 delivered about 37% more throughput than the six-P-core
OpenVINO run and 2.33× the ONNX Runtime baseline while preserving the measured
rankings exactly. Mixed CPU FP32 + GPU FP16 delivered about 4.2× baseline
throughput with the documented small retrieval changes.

## Operational interpretation

- Current production uses OpenVINO CPU FP32 for SigLIP2 images; it does **not**
  currently exploit concurrent CPU+GPU requests.
- The highest-quality throughput target is bounded concurrent CPU+GPU FP32.
- FP16 must remain an explicit quality/performance choice, never an invisible
  replacement for persisted FP32 embeddings.
- `HETERO` and P-core HT should not be selected for this graph on this host.
- OpenVINO `MULTI:CPU,GPU` needs at least two requests in flight to have any
  chance of using both devices. A sequential production-path probe measured
  1,620 ms versus 1,630 ms for CPU alone; the concurrent results below ultimately
  rejected MULTI on this host as well.

## Production-path reproduction (2026-07-15)

The recovered method was reproduced through the real HTTP/.NET path with two
job slots and two simultaneous 20-image streams:

| Runtime policy | End-to-end throughput |
| --- | ---: |
| CPU FP32, sequential reference | about 0.613 img/s |
| `MULTI:CPU,GPU`, latency | 0.601 img/s |
| `MULTI:GPU,CPU`, latency | 0.432 img/s |
| `MULTI:CPU,GPU`, cumulative throughput, 2 requests | 0.634 img/s |
| Explicit bounded `DUAL:CPU,GPU`, latency | **0.826 img/s** |

DUAL includes blob read, large-image decode/preprocessing, HTTP and
normalization and is about 34.7% above the comparable CPU path. The original
tensor-only script was also rerun unchanged: six P-cores reached 0.664 img/s,
HT reached 0.650 img/s, and explicit CPU+GPU reached 0.8917 img/s with the same
34/26 distribution (historical run: 0.929 img/s). This confirms the same
relative gain without conflating tensor-only and end-to-end numbers.

The production choice is therefore bounded DUAL FP32, not the OpenVINO MULTI
plugin.
