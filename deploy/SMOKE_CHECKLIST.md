# NubArca — production smoke checklist

A short list of "did the deployment actually come up correctly?" checks.
Run this:

- **after a first deploy** (the runbook references it in step 10);
- **after every update** that touches schema, the auth pipeline, the
  reverse proxy, or `docker-compose.prod.yml`;
- **as a periodic drill** (suggested: monthly).

Checks are split into three buckets so it's clear which are safe at any
time vs. which would mutate production data.

> **Do not upload important data before a successful backup + restore
> drill.** A backup that has never been restored is a `pg_dump` you've
> never read. See the destructive-checks section below.

---

## Automated (safe, anytime)

Driven by [`deploy/smoke-check.sh`](smoke-check.sh). Unauthenticated only —
no login, no plaintext password, no state mutation:

```bash
BASE_URL=https://nubarca.example.com ./deploy/smoke-check.sh
```

The script verifies:

- `GET /health` returns `200` with `{"status":"ok"}`.
- The SPA bundle is served at `/` (the response is HTML, not a JSON 404).
- The api refuses a missing-cookie `GET /api/auth/me` with `401` (proves the
  auth pipeline is wired and the proxy is forwarding `/api/*` to the api
  container).
- Public share-link rate limit is wired: `GET /s/this-is-not-a-real-token`
  returns `404` (proves the public route is reachable AND that bogus tokens
  don't reveal anything).
- The response over HTTPS carries `Strict-Transport-Security` (when behind
  the example Caddy / nginx config that sets HSTS).

Exit code `0` = all checks passed. Non-zero = at least one failed; the
output names which.

---

## Manual (safe, requires a browser session)

Open a private browser window pointed at your domain. Sign in with the
admin user created by `users ensure`.

- [ ] **Login** succeeds. The page transitions from `/login` to the home
      view without a refresh loop.
- [ ] **Cookie is `HttpOnly` and `Secure`** under HTTPS. DevTools →
      Application → Cookies → `https://your-domain`. The cookie named
      `NubArca.Auth` must show ✓ HttpOnly and ✓ Secure. (If `Secure` is
      missing, the request reached the api over HTTP — fix the proxy.)
- [ ] **Folder browser** loads at `/`. Empty folder shows "This folder is
      empty.".
- [ ] **Create folder** from the inline form succeeds.
- [ ] **Upload a small file** (e.g. a 100-byte text file). The upload pill
      becomes "uploaded" and the file appears in the listing.
- [ ] **Download** the file via the row's Download link. The downloaded
      bytes match the original.
- [ ] **Rename** the file inline. The new name appears immediately.
- [ ] **Move** the file into the new folder. It disappears from the parent
      view and appears under the folder after navigating.
- [ ] **Delete (soft)** the file. It vanishes from the active listing.
- [ ] **Trash page** shows the deleted file. **Restore** it; it returns to
      the active listing.
- [ ] **Share link** create. The URL appears once; click **Copy**, then
      open the URL in a separate **private** browser window — the file
      downloads without prompting for credentials.
- [ ] **Existing links** section in the panel shows the link as **Active**.
      **Revoke** it. The badge flips to **Revoked**; refreshing the
      private window now returns a 404 (rate-limit headers may still be
      present).
- [ ] **Image gallery** (if you uploaded an image) shows the thumbnail.
      The placeholder appears for non-image rows whose thumbnail was
      skipped.
- [ ] **Gallery lightbox** opens on a thumbnail click and shows the medium
      preview; the metadata panel lists curated fields only (never GPS
      coordinates / serials / raw metadata).
- [ ] **Video** (if you uploaded an `.mp4`/`.webm`): a poster renders on the
      row and the Play button opens the in-app `<video>` player; playback
      seeks (Range requests work).
- [ ] **Albums**: create an album under `/albums`, add an image to it from
      the gallery lightbox, open the album and confirm the item is listed,
      then remove it. Deleting an album must NOT delete the underlying file.
- [ ] **Admin stats** (`/admin`, admin user only): the aggregate cards
      render (users / files / blobs / media / quota / …) with no per-file
      detail and no storage keys.
- [ ] **Sign out** clears the cookie; refreshing returns you to `/login`.

If any of these fail, fix the underlying issue before exposing the
deployment to real users.

---

## Operational (safe, mutates ./backups/ only)

- [ ] **Backup**: `./deploy/backup.sh` runs to completion. Output directory
      under `./backups/` contains `manifest.json`, `postgres.sql.gz`,
      `storage.tar.gz`. `manifest.json` lists matching SHA-256 values for
      both archives.
- [ ] **Restore dry-run**: `./deploy/restore.sh ./backups/<name>` prints
      `DRY RUN. No changes were made.` without mutating volumes.
- [ ] **`docker compose ps`** shows `postgres` healthy and `api` +
      `frontend` running.
- [ ] **Background jobs CLI** (read-only): `docker compose ... run --rm api
      dotnet NubArca.Api.dll jobs list` prints status counts (and recent
      rows) without exposing any payload. `jobs run-once` is safe to run too
      — it only processes already-queued jobs and is a no-op when the queue
      is empty.

- [ ] **Derived artifacts regenerate.** Optional check on a drill
      host: with the stack up, browse a gallery image (thumbnail + lightbox
      preview) and a video (poster) to confirm they render. Then, to prove the
      regenerable path, an operator may wipe the derived cache directory and
      re-open the same items — they must come back (the endpoints regenerate)
      — or run `dotnet NubArca.Api.dll media derivatives backfill`. Do this
      on a drill host, not production.

> **Derived artifacts (thumbnails / medium previews / video posters) are
> regenerable cache.** By default they live in the original storage volume, so
> `storage.tar.gz` already backs them up. If you split them onto a separate
> `Storage:DerivedRootPath`, a **full** backup must include that
> root too; an **essential** backup (DB + originals) may skip it. Either way
> they are rebuildable from the source blobs — `dotnet NubArca.Api.dll media
> derivatives backfill` (or the `media-derivatives-backfill` job), and the
> endpoints also regenerate on demand. Only the DB dump + the **originals**
> archive are a required matched pair; restore those together.

---

## Destructive (DO NOT run on a live host you care about)

These need a separate **drill host** (a VM, a laptop's Docker, anything
that is not your production box).

- [ ] **Full restore drill**. Replay the [backup/restore section of the
      README](../README.md#backup-and-restore) on a fresh host:
      1. Clone the same git ref the backup was taken from.
      2. Copy `.env.example` to `.env` and set the SAME
         `POSTGRES_PASSWORD` as production (the dump's role grants depend
         on it).
      3. `docker compose -f docker-compose.prod.yml --env-file .env up -d`
         once to create the volumes.
      4. `./deploy/restore.sh /path/to/backup --yes`.
      5. `curl http://127.0.0.1:8080/health` → `200`.
      6. Optional: `dotnet NubArca.Api.dll db migrate` if the dump
         pre-dates a schema change in the running code.
      7. Log in with a real user from production. Browse a folder.
         Download a file. Bytes must match the original.
      8. Tear the drill host down; rotate any temporary password used.

  Mark the deployment "trusted for real data" only after step 7
  succeeds.

- [ ] **Disaster recovery rehearsal**: power-cycle the host, watch the
      stack come back up via `restart: unless-stopped`. (Optional;
      reasonable for a "before I take the next vacation" check.)

---

## Failure → action mapping

| Smoke check fails | First thing to check |
|---|---|
| `/health` returns 5xx | `docker compose ps`, `docker compose logs api --tail=50` |
| `/health` returns 200 but `/` returns 5xx | reverse proxy route for `/` → frontend container |
| `/api/auth/me` returns 200 (instead of 401) when unauthenticated | api pipeline is mis-wired; almost certainly a regression — file an issue |
| Login 401 with the correct password | run `dotnet NubArca.Api.dll users ensure --update-password` to rehash |
| Cookie missing `Secure` flag | request reached the api over HTTP; check `ForwardedHeaders__Enabled` and the proxy's `X-Forwarded-Proto` |
| Share link works while logged in but 404s in private window | the proxy is sending `/s/*` to the SPA instead of the api; fix the path split |
| Backup script asks for `POSTGRES_USER` | typo / missing in `.env`; `grep ^POSTGRES_ .env` |
| Restore dry-run "checksum mismatch" | corrupted transfer; re-copy the backup directory |
