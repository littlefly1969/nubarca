# Google Cast

NubArca can send a **video** to a Google Cast receiver — a Chromecast, a Google
TV, an Android TV, or any other certified Cast device — from Chrome on a desktop
or on Android. The browser stays the remote control: play, pause, seek, volume
and stop all work from NubArca, and changes made on the television flow back.

This document describes the first casting target. It is not a general "cast to
anything" design: Fire TV, Matter Casting, Samsung Tizen, AirPlay and DLNA each
use a different receiver architecture and none of them is implemented here.

## Architecture

```text
Chrome (sender)
   └─ Google Cast Web Sender SDK
        └─ Google Default Media Receiver (on the television)
             └─ temporary NubArca Cast media URL
                  └─ HLS ladder, or the original video with Range
```

The television never holds a NubArca session. It cannot: the receiver is a page
on Google's origin running on a different device, and the owner's HTTP-only
cookie is not reachable from it. So NubArca does not try to authenticate the
receiver. It authorises **before** playback and hands the receiver the narrowest
possible capability instead.

Nothing about the owner's own playback changes. `GET /api/files/{id}/video` and
its ladder routes remain cookie-authenticated and owner-scoped, exactly as they
were; Cast uses a separate route family and never widens the old one.

## The temporary grant

A **Cast grant** is one row in `cast_media_grants`:

| Field | Meaning |
| --- | --- |
| `Id` | What the receiver's URL carries in its path |
| `UserId` | Whose authority the receiver plays with |
| `FileItemId` | The single video it can reach |
| `TokenHash` | SHA-256 of the secret. The secret itself is never stored |
| `CreatedAt` / `ExpiresAt` | Lifetime |
| `RevokedAt` | Set by "stop casting", by loading a replacement, and best-effort on disconnect |

The secret is 32 cryptographically random bytes, base64url-encoded, returned
**once** from the request that created the grant. The server stores only its
digest and finds the row by primary key, so a token on its own addresses nothing;
the digest comparison is constant-time.

Default lifetime is **6 hours**, configurable through `Cast__GrantLifetimeMinutes`
and clamped to the range 30 minutes – 12 hours. Expiry is the backstop for the
cases where revocation cannot run — a closed laptop, a killed tab — not the
normal path.

### A grant is not a standing authorisation

Every Cast media request — including **every HLS segment** — re-establishes, from
the database:

- the grant exists, is unexpired and unrevoked, and the presented secret matches;
- the owner's account is still active;
- the owner still holds `cast.access`;
- the file still exists, still belongs to them, and is still a video.

That is deliberate. It preserves NubArca's "permissions change on the next
request" model: removing `cast.access` from a role stops the next segment, with
no re-login and no session subsystem. The per-request lookup is a cheap indexed
read at home-HLS segment frequency.

Every failure answers the same bare `404`. Whether the grant never existed,
expired, was revoked or belongs to a disabled account is not something a caller
learns from us.

### What invalidates a grant

| Event | Effect |
| --- | --- |
| Explicit "stop casting" | Revoked immediately |
| Loading a different video into the session | Previous grant revoked before the new one is minted |
| Receiver disconnect / network loss | Revoked best-effort by the sender; expiry covers the rest |
| Account disabled | Immediate — the account check runs per request |
| `cast.access` removed from the role | Immediate — the permission check runs per request |
| File deleted | Immediate — the file check runs per request |
| Expiry | Immediate |
| **Password change** | **No effect on an existing grant** |

The password exception is a decision, not an oversight. `SecurityVersion` exists
to sign other *browsers* out after a credential event. A Cast grant is not a
browser session: it is a capability the user knowingly handed to a television in
their own home, scoped to one video and already time-bounded. Retracting it on a
password change would surprise the user without protecting anything the expiry
does not already cover. Disabling the account and removing the permission both
retract it at once.

## Permission

`cast.access` — *Trasmissione su TV* / *Cast to TV*.

> Permette di trasmettere video a dispositivi esterni compatibili.

It authorises the **delegation**, never the media: a grant can only ever be
minted for a video the caller can already play. Built-in defaults:

| Role | `cast.access` |
| --- | --- |
| Administrator | yes (the complete catalogue, re-synced on every boot) |
| Member | yes |
| Restricted | no |
| Existing custom roles | unchanged — an operator adds it deliberately |

The `AddCastMediaGrants` migration adds the key to **Member** on an existing
installation, because the role seeder never rewrites a role that already exists.

## API

Authenticated (cookie, `cast.access`, ordinary owner authorisation, existing CSRF
policy, 20 grant creations per minute per user):

```text
POST   /api/cast/videos/{fileId}/grant
DELETE /api/cast/grants/{grantId}
```

`POST` answers `201` with the grant, `202` + `Retry-After` while the HLS ladder is
still being produced (the sender polls), or `404` for anything the caller could
not play anyway. `DELETE` is idempotent, owner-scoped, and answers `204` either
way.

Grant-scoped, no cookie:

```text
GET|HEAD /api/cast/media/{grantId}/video?token=...
GET|HEAD /api/cast/media/{grantId}/hls/{rendition}/{file}?token=...
GET|HEAD /api/cast/media/{grantId}/poster?token=...
```

Only `GET`, `HEAD` and the CORS preflight `OPTIONS` are answered. There is no
directory listing, no metadata endpoint and no original-download route inside the
grant — only the resources playback needs.

The creation response returns **origin-relative** paths. The sender joins them
onto the origin the browser is already on, so no untrusted `Host` header ever
decides where a television is pointed.

## HLS

Cast reuses the existing ladder. There is no second transcoder and no second
copy of the media.

An HLS URI is resolved against the *playlist's* URL, and URI resolution discards
the query string — so a variant playlist served at
`…/hls/high/stream.m3u8?token=X` would resolve its own `seg-0.m4s` to a URL with
**no token**, and the receiver would stall on the first segment. Both the master
and every variant are therefore rewritten to carry signed, grant-scoped URLs.

The rewrite is not string surgery. Every URI found in a playlist is validated
against the same whitelist the storage layer enforces
(`HlsDerivativeStorage.IsServableRelativePath`) before anything is emitted, and a
playlist containing even one URI that does not validate is rejected **whole** — a
404 rather than an unchecked URL handed to a television. Traversal, percent-encoded
traversal, absolute foreign URLs, unexpected file names and unknown rendition
directories all fail that check. The Cast route asks the serving service for the
**raw** master (`VideoHlsMasterForm.Raw`) precisely so it never has to un-pick
somebody else's rewrite.

Where the HLS provider is disabled, Cast serves the original bytes with full
Range support (`206`, `Accept-Ranges`, `Content-Range`, `Content-Length`, the
server-detected video MIME) and streams rather than buffering.

## CORS

CORS is **not** enabled globally. One policy exists and it is attached to the
grant-scoped media routes and to nothing else:

- allowed origins: exactly the configured `Cast__AllowedReceiverOrigins__N`;
- methods: `GET`, `HEAD`, `OPTIONS`;
- request headers: `Range`, `Accept`, `Accept-Encoding`, `Content-Type`;
- exposed headers: `Content-Type`, `Content-Length`, `Content-Range`,
  `Accept-Ranges` — a player that cannot read these cannot seek;
- no credentials, and **never** `Access-Control-Allow-Origin: *`.

A wildcard on a URL that carries a bearer secret would let any page on the
internet read protected media the moment it learned the URL.

With no receiver origin configured, grant creation still works and no origin is
advertised. That is the safe direction to fail in: the operator sees a television
that will not start, never a server that quietly allowed an unknown origin.

### Capturing the receiver origin

The Default Media Receiver's `Origin` is captured **once**, from a real device,
during the first physical test. Watch the sanitised Cast access log while
pressing Cast:

```bash
sudo tail -f /var/log/nginx/nubarca-cast.log
```

Each line carries the method, the path, the status and the origin — and no query
string, so the bearer token cannot appear:

```text
203.0.113.7 - [09/Aug/2026:12:00:00 +0000] "GET /api/cast/media/<grant-id>/video" 200 1234 origin="https://…"
```

Record the `origin="…"` value only. Never the token, never the complete media
URL. Then configure the exact value:

```text
Cast__AllowedReceiverOrigins__0=<the observed origin>
```

Do not guess it, and do not use `*`.

## The bearer token is a secret

It has to appear in a URL — the receiver cannot be given a header or a cookie —
so it is treated as a secret everywhere else:

- it lives only in the sender tab's memory (a ref inside `CastProvider`);
- it is never written to `localStorage`, `sessionStorage` or browser history;
- it is never logged, never put in an audit payload, never in frontend telemetry;
- audit events record the grant id, the user, the file, the expiry and the
  revocation reason — never the secret;
- it rides in the **query string**, not the path, because a path is the part of a
  URL that everything from a proxy log to an error page treats as safe to print.

### Reverse-proxy logging

nginx's default `combined` format logs `$request`, which includes the query
string — and therefore the token. This is not hypothetical: an installation that
has never been hardened **is** writing Cast tokens to disk from the first cast.
`deploy/nginx.conf.example` defines a `nubarca_cast` format that logs the method,
the **sanitised path**, the status and the `Origin` — no `$args`, no `$request`,
no `$request_uri` — and applies it to `location /api/cast/media/`. Logging for
every other NubArca route is unchanged.

**Hardening the proxy is a precondition of the first physical cast**, not a
follow-up: the receiver origin is discovered *from* this log, so the log has to
be safe before it is read.

Where the reverse proxy is operator-local, make the same change there and prove
it with one request whose query string carries a deliberate marker:

```bash
curl -s -o /dev/null "https://<origin>/api/cast/media/00000000-0000-0000-0000-000000000000/video?token=probe&x=marker1"
sudo grep -c 'marker1' /var/log/nginx/nubarca-cast.log   # must print 0
sudo grep -c 'token='  /var/log/nginx/nubarca-cast.log   # must print 0
```

A `404` is the expected status — the point is what reaches the log, not what the
request returns. Check the *general* access log too: a `location` that does not
match leaves the request on the default format.

## Requirements and limitations

- **Chrome on desktop or Android.** The Web Sender is a Chromium feature exposed
  through `chrome.cast`.
- **Chrome on iPhone and iPad is not supported by the Google Web Sender.** Every
  iOS browser is WebKit underneath, so the bridge does not exist there. NubArca
  detects the bridge rather than sniffing the user agent, which is the only way
  to get iOS Chrome right.
- **A secure origin is mandatory**, and the origin must be one the television can
  resolve. NubArca disables casting with a specific explanation for each case:
  unsupported browser, insecure origin, loopback address the TV cannot reach,
  SDK load failure, or missing permission. It never shows a Cast button that is
  present and doomed.
- **Codec support varies by receiver generation.** Where the HLS provider is
  enabled, the ladder gives a controlled playback contract and is preferred.
  Where it is not, the original container is offered with its detected MIME — and
  a receiver that refuses it produces a plain compatibility error, never a
  silent fall-back to some other download path.
- Videos only. No images, no audio-only, no queues.

## Receiver application

Phase 1 uses `chrome.cast.media.DEFAULT_MEDIA_RECEIVER_APP_ID` with
`AutoJoinPolicy.ORIGIN_SCOPED`. There is **no** custom receiver, so there is no
Cast Developer Console application and no application id to register.

A custom receiver would be worth building for a custom TV UI, receiver-side
login, authorisation refresh during playback, custom analytics, custom messaging
or DRM. None of those apply: NubArca authorises before playback and the receiver
is handed only a scoped playable URL.

## Verifying with a real device

1. Chrome and the receiver on the same reachable network.
2. Open NubArca over trusted HTTPS.
3. Open a video, press **Trasmetti**, choose the receiver.
4. Playback starts at roughly the local position and local playback pauses.
5. Exercise play, pause, seek and volume from NubArca, then pause and resume from
   the TV remote — the sender must reflect both.
6. Close the viewer: the television keeps playing and the mini controller stays.
7. **Stop casting**: the television stops, the grant is revoked, and the local
   position is preserved (paused — the user presses Play if they want it).
8. Re-request the revoked URL: no playback.
9. Check the proxy log: no `token=`.

With HLS, confirm the master, the variants and the segments all load with no CORS
errors in the receiver's network activity.

## Troubleshooting

| Symptom | Cause |
| --- | --- |
| No Cast button at all | The account lacks `cast.access` |
| Button present but disabled | Read its tooltip: browser, origin, reachability or SDK load |
| Picker opens, nothing plays | Receiver origin not in `Cast__AllowedReceiverOrigins` |
| Playback stops after a few seconds | Segment requests failing — check CORS and the grant's validity |
| "Il dispositivo non è in grado di riprodurre questo video" | Receiver cannot decode the container/codec |
| Playback dies exactly at a role change | Working as designed: `cast.access` is re-read per request |

## Other casting targets

Fire TV / Fire Stick uses a different receiver architecture and is not covered by
this document or this implementation.
