# NubArca Roadmap

NubArca is a minimal, robust, secure personal cloud server. This document tracks
**direction only**. What the product does today is described by
[README.md](README.md) and [ARCHITECTURE.md](ARCHITECTURE.md); what shipped is
described by [CHANGELOG.md](CHANGELOG.md).

Nothing here is a commitment or a schedule, and nothing here is a defect.

## Near-term

- Complete the **physical DNP DS620 acceptance matrix** for the headless Print
  Agent: Windows service install/upgrade, driver-exposed 10×15 media, USB
  disconnect, paper/error recovery and one-copy delivery across an agent
  restart. The fake adapter and server contract are automated; hardware claims
  wait for hardware evidence.
- A private, owner-scoped **photo map** view on top of `file_item_locations`.
- **Operationalise blob cleanup** — a safe admin-triggered reclaim, so recovering
  space is an explicit action rather than only background configuration.
- **Semantic-search calibration.** The result policy's score thresholds are
  deliberately disabled and effective behaviour is a deterministic top-300 cut.
  Calibrating them needs a representative corpus plus human relevance
  judgements; guessing a threshold would silently hide valid matches. Optional
  quality work, not unfinished work.

## Later

- Build event/Party print experiences on the general Print bounded context only
  after station and DS620 acceptance; Party must not own a second printer queue,
  credential or retry model.
- A **desktop sync client**, once the local-first core warrants it.
- **Auth hardening**, e.g. two-factor authentication.
- Pluggable storage backends (S3/MinIO) and a faster search backend
  (Meilisearch/Typesense), once the local-first core warrants it.
- Optional native/Rust media worker; perceptual (near-duplicate) image
  deduplication.

## Non-goals

NubArca intentionally does **not** aim to be a Nextcloud clone. Out of scope:

- WebDAV.
- DASH streaming. (Adaptive **HLS** VOD is implemented and optional.)
- Collaborative document editing, calendar, contacts, chat.
- A plugin system.
- Advanced multi-tenant permission models.
- Public user registration.
