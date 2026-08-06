# NubArca Roadmap

NubArca is a minimal, robust, secure personal cloud server. This document tracks
**direction only**. What the product does today is described by
[README.md](README.md) and [ARCHITECTURE.md](ARCHITECTURE.md); what shipped is
described by [CHANGELOG.md](CHANGELOG.md).

Nothing here is a commitment or a schedule, and nothing here is a defect.

## Near-term

- A private, owner-scoped **photo map** view on top of `file_item_locations`.
- Admin **user-management UI** — today users are managed through the CLI only.
- **Operationalise blob cleanup** — a safe admin-triggered reclaim, so recovering
  space is an explicit action rather than only background configuration.
- **Semantic-search calibration.** The result policy's score thresholds are
  deliberately disabled and effective behaviour is a deterministic top-300 cut.
  Calibrating them needs a representative corpus plus human relevance
  judgements; guessing a threshold would silently hide valid matches. Optional
  quality work, not unfinished work.

## Later

- A **sync foundation** and eventual desktop/mobile sync clients. The reserved
  mobile application identity exists; there is no mobile release.
- **Auth hardening**, e.g. two-factor authentication.
- Pluggable storage backends (S3/MinIO) and a faster search backend
  (Meilisearch/Typesense), once the local-first core warrants it.
- A dedicated derived/cache storage tier. Derived artifacts are content-addressed
  blobs in the same store as originals today, so they are not separately pathable.
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
