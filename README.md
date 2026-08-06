# NubArca

> **Your files. Your hardware. A modern private cloud.**

**NubArca 0.3.0** is a self-hosted, local-first personal cloud for secure file storage and rich photo/video management. It combines immutable content-addressed storage, exact deduplication, resilient background processing and optional locally hosted AI — without turning your server into an oversized collaboration suite.

NubArca is designed for a **single operator and their users**, running on one small server through Docker Compose.

## Why NubArca stands out

- **Store once, use everywhere** — original files are immutable and addressed by SHA-256. Identical content is stored only once, while folders, albums and moves remain lightweight database operations.
- **Resilient ingestion** — large browser uploads can be staged in idempotent chunks and resumed from the missing parts. Server-side imports use persisted per-item manifests, survive interruptions and expose progress, cancellation and diagnostics.
- **A media experience, not just a file list** — responsive grid/list browsing, infinite scroll, bulk actions, image and video galleries, full-screen viewing, albums, metadata editing and privacy-safe downloads.
- **Adaptive private video** — optional FFmpeg-based fMP4 HLS creates a VOD ladder with an up-to-1080p high rendition and, when useful, a 480p low rendition. Compatible H.264/AAC sources can be remuxed instead of fully re-encoded; the web client uses native HLS or hls.js and falls back to HTTP Range playback when HLS is disabled.
- **Photo organization without rewriting files** — photos can be organized into date-based folders through previewable, cancellable background jobs. Moves are logical: original bytes, thumbnails, metadata and shares remain intact.
- **Local AI, under your control** — optional services provide semantic text-to-photo search, image similarity and post-ingestion face detection/embedding. The experimental **Aesthetics Lab** evaluates selected images locally and remains isolated from the main library.
- **Built for the living room** — the dedicated Fire TV / Android TV experience supports secure QR pairing, remote-first albums and slideshows, personal videos and live Party Mode refresh as new guest photos arrive.

## Current state — 0.3.0

The core product is implemented and usable for controlled self-hosted deployments:

- authenticated users, administrator role and owner-isolated libraries;
- files, folders, search, rename, move, Trash and restore;
- secure revocable share links with expiry and download limits;
- photo/video galleries, previews, posters, metadata and albums;
- direct video streaming with HTTP Range and optional adaptive HLS generation and serving;
- exact deduplication, quota accounting and storage integrity tooling;
- durable PostgreSQL-backed jobs with leases, heartbeats, retries, progress and cooperative cancellation;
- resumable staged uploads and resumable server-side imports;
- production Docker Compose deployment, reverse-proxy examples, migrations, backup/restore scripts and operational diagnostics.

Some capabilities are intentionally **opt-in**: background workers, staged uploads, server-side imports, cleanup services, HLS, FFmpeg media derivatives and AI sidecars. HLS requires an FFmpeg/FFprobe-enabled runtime and an active job worker. The Aesthetics Lab is **experimental and disabled by default**.

NubArca is pre-1.0 software for technical self-hosters. It is not currently a replacement for a full collaboration platform or a transparent sync service.

## Architecture

- **Backend:** ASP.NET Core / .NET 10, EF Core
- **Database:** PostgreSQL 17
- **Frontend:** React, TypeScript, Vite
- **Storage:** local immutable content-addressed blob store
- **Video:** HTTP Range streaming; optional fMP4 HLS VOD derivatives
- **Deployment:** Docker Compose behind Caddy, nginx or another TLS reverse proxy
- **Optional clients:** Expo / React Native mobile gallery and dedicated TV application
- **License:** AGPL-3.0

PostgreSQL owns the logical world — users, folders, files, metadata, shares and jobs. The filesystem owns the bytes. Physical paths, storage keys, hashes and sensitive embedded metadata are never part of normal user-facing APIs.

## Deliberate non-goals

NubArca does not currently provide WebDAV, desktop/mobile synchronization, DASH streaming, collaborative editing, calendar, contacts, chat, plugins or public registration.

## Getting started

To set up a workstation and build from source, start with the environment guide:

- [Development environment](docs/development-environment.md) — canonical toolchain
  versions, per-area prerequisites, validation commands and accepted warnings

To run NubArca, start with the production runbook:

- [First deployment](deploy/FIRST_DEPLOY.md)
- [Operations](docs/OPERATIONS.md)
- [Architecture](ARCHITECTURE.md)
- [Development state](DEVELOPMENT_STATE.md)
- [Changelog](CHANGELOG.md)

---

**NubArca 0.3.0** focuses on a clear promise: private ownership of your files, resilient storage mechanics and a media experience that feels modern — all on infrastructure you control.
