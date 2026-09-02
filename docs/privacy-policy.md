# NubArca — Privacy Policy

**Last updated: 2 September 2026**

## The one thing that explains everything else

NubArca is **self-hosted software**. It is not a service. There is no NubArca
cloud, no NubArca account, and no server operated by the developer that your
photos could reach.

The app talks to **one server: the one whose address you type on the sign-in
screen**. That server is run by you, or by whoever gave you the address. Your
photos, your account and everything derived from them live there and nowhere
else.

This creates two distinct roles, and the difference matters legally:

| Role | Who | What they hold |
| --- | --- | --- |
| **Developer** | the author of the NubArca applications | nothing — no server, no copy, no telemetry |
| **Operator** | whoever runs the NubArca server you sign in to | your account and all of your content |

Under the GDPR the **operator is the data controller**. If you run the server
yourself, you are the controller of your own data. The developer is not a
processor for you and never receives your content. Requests about your data —
access, export, correction, erasure — go to your operator, not to the developer.

## What the developer collects

**Nothing.**

The applications contain no analytics, no crash reporting, no advertising, no
attribution SDK and no third-party service that observes use. This is not a
promise about intent; it is a property of the build, and it is checkable: the
published dependency lists of the phone, TV and web clients contain no such
component.

The apps make no network request other than to the server address you supply.

## What the app reads on your device

**Photos and videos — only if you turn on sync, and only the ones you choose.**

The Android app requests granular photo and video access
(`READ_MEDIA_IMAGES`, `READ_MEDIA_VIDEO`). It is requested when you enable
device sync, never at startup, and it exists for exactly one purpose: to upload
the media you select to your own server. Selected media is not read for any
other reason, is not analysed on the device, and is not sent anywhere else.

The app also stores, on the device only:

* your **session cookie**, in the Android Keystore / iOS Keychain, pinned to
  this device while unlocked;
* the **server address** you last signed in to, so the login screen can prefill
  it;
* your **theme choice**;
* a **sync ledger** recording which local items have been uploaded, so a retry
  does not duplicate them.

Android backup is disabled for the app (`allowBackup="false"`), so none of this
can ride an unencrypted device backup off the phone.

## What the server stores

This is held by your operator, on their machine:

* **Account**: email address, display name, a password *hash* (never the
  password), language and interface preferences.
* **Files**: the photos, videos and documents you upload, stored as immutable
  content-addressed blobs, plus the folder names and file names you give them.
* **Metadata read from your files**: EXIF, including the **date taken** and, when
  your camera recorded it, **GPS coordinates**.
* **Derived artifacts**: thumbnails and preview renditions.
* **AI-derived data, only if your operator has enabled AI** — it is off by
  default: extracted text (OCR), captions and descriptions, visual and semantic
  embeddings, face detections, face groupings, and tags.
* **Audit records** of uploads, downloads, deletions and share creation or
  revocation.
* **Server logs**, which may include IP addresses, as any web server does.

## Who owns the derived data

**The owner of a file owns everything derived from it** — EXIF, GPS, date taken,
extracted text, captions, embeddings, faces, groupings and tags alike.

This is enforced, not merely stated:

* derived data is never exposed to another user;
* there is no search across owners, and no face grouping across owners;
* raw embeddings and raw model payloads are never exposed through the API, the
  command line, logs or diagnostics;
* GPS and date-taken are available in your own private views and are **not**
  included in public shares or in aggregates that could leak them.

## Sharing — what a recipient can see

Sharing is always something you do deliberately. Three mechanisms exist, and
each shows less than you might expect:

**Album sharing with another account.** You invite an exact email address; there
is no user directory and no autocomplete, so the server cannot be used to
discover who has an account. A recipient sees the album's media and the owner's
display name. A recipient never sees other members' email addresses or user
identifiers, and the owner sees only a **masked** address for their own members.

**Public share links.** A link carries an unguessable token, can be revoked at
any time, and can be given an expiry. Media reached through a public link
deliberately carries **no file name** — a file name is free text you wrote and
can hold a person's name — and no GPS, no date-taken, no AI-derived data and no
contributor identity.

**Party mode.** When you enable it for an album, guests holding the link can
view and, if you allow it, upload and post messages, subject to the approval
settings you choose. Guests see the album, not your library.

Nothing is shared by default. Nothing is public unless you make it so.

## Security

* Production builds of the apps **require HTTPS** and refuse to be built against
  a plaintext origin; unencrypted traffic is disabled in the shipped binaries.
* Session cookies are HTTP-only.
* Passwords are stored only as hashes.
* Authentication and public-share endpoints are rate-limited.
* Every download is authorised centrally, on every request.
* Physical storage paths, content hashes, share tokens and raw metadata are
  never exposed through the API, logs or diagnostics.

## Updates delivered over the air (NubArca TV)

The TV application can receive updates to its **JavaScript** layer from the same
server you sign in to. Those bundles are signed and are published by your
operator; the developer cannot push code to your device, and no native code is
ever replaced this way.

## Retention and deletion

Retention is your operator's decision, because the data is on their machine.
Deleting a file removes its derived artifacts with it — a preview or an index
never outlives the content it was made from. To delete an account, ask your
operator. If you are your own operator, deleting the data on your server deletes
it, completely, with no copy anywhere else.

## Children

NubArca is not directed at children and does not knowingly hold data about them.

## Your rights

Under the GDPR you have the right of access, rectification, erasure,
restriction, portability and objection. Exercise them with the **operator of the
server you use**, who holds the data. If you run the server, you already have
direct and complete access to everything described in this document.

## Changes to this policy

Material changes will be published in this document, with the date at the top
updated. The version that applies to a release is the one in that release's
repository.

## Contact

Questions about the applications themselves: `<CONTACT_EMAIL>`.

Questions about your data: your operator — the person or organisation whose
server address you sign in to.
