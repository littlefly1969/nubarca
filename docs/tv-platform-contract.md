# NubArca TV — platform contract

One page answering "who owns this?" for every input, media and lifecycle
concern on the television. It exists because most of the defects this document
records were **two owners of one thing**, not a missing feature.

Generic Android TV / Google TV / Fire TV. Nothing here is Amazon-specific.

## Input

| Concern | Authority |
| --- | --- |
| D-pad on a focusable screen | Native Android / react-native-tvos focus |
| D-pad inside the fullscreen viewer | NubArca viewer remote policy (`src/video/remoteMap.ts`) |
| Key phase (down/up/undefined) | `src/lib/remoteEvent.ts`, once, for the whole app |
| MENU | Optional accelerator. Never the only route to anything |
| Dedicated transport keys | Accelerators inside a media context; ignored elsewhere |
| HOME | System owned, never intercepted |
| BACK | Hierarchical navigation, never a playback control |

The two D-pad modes never coexist: the viewer has no focusable views, which is
the precondition that lets it own LEFT/RIGHT for seeking.

**The five-way rule.** Every product FUNCTION must be reachable with
UP/DOWN/LEFT/RIGHT/SELECT/BACK alone. `src/lib/fiveWayCapability.ts` records all
of them as data and the suite fails on one without a route. Presentation —
QR corners, the face-filter indicator, the viewer's ambient chrome — is
information, not a function, and deliberately has no entry.

## Media

| Concern | Authority | NubArca's role |
| --- | --- | --- |
| MediaSession | expo-video / Media3 (`buildBasicMediaSession` per player) | **None.** Adding one would double-dispatch transport keys |
| Audio focus | expo-video `AudioFocusManager` | **None.** A second `AudioManager` owner is duplicate registration |
| Video keep-awake | expo-video `keepScreenOnWhilePlaying` | Set explicitly, so the invariant is visible and testable |
| Photo slideshow keep-awake | NubArca `useScreenAwake` | Only while actually rotating, in the foreground |
| Video player | Exactly one `expo-video` player | `ReadyPlayer` keyed by source; `useVideoPlayer` releases on unmount |
| Background release | NubArca (`src/video/playerLifecycle.ts`) | Snapshot, then unmount — the release is Expo's documented contract |
| Output-route loss | NubArca observer (`NubArcaTvOutputObserver.kt`) | **Reports only.** It never plays, pauses, focuses or routes |

### Why NubArca owns so little of this

Audits found expo-video already owns the MediaSession and audio focus. A second
owner of either is not extra safety — it is the classic double-toggle (one
physical Play/Pause reaching two authorities) and a duplicate focus
registration. The only gap the audit actually proved was output-route
observation: the installed expo-video has no `AudioDeviceCallback` and no
`ACTION_AUDIO_BECOMING_NOISY` handling, and Media3 leaves
`setHandleAudioBecomingNoisy` off by default.

### Lifecycle rules

- A genuine **background** transition snapshots position + intent, then unmounts
  the player. That releases ExoPlayer, its decoder, the MediaSession and the
  audio-focus registration in one move, through Expo's documented lifecycle.
- A transient **inactive** blip only pauses. It is not an Activity stop, and
  re-preparing ExoPlayer for an incidental focus change churns the decoder.
- Returning recreates exactly one player and restores the position **only** when
  it belongs to the same source. Changing item while backgrounded discards it.
- Nothing ever auto-resumes — not after background, not after the output route
  returns. The user presses SELECT.
- Output loss pauses and keeps the position. It never navigates, never closes
  the viewer, never resets.

## Device contract

| Fact | Value | Source of truth |
| --- | --- | --- |
| minSdk | 24 | the built APK |
| targetSdk | 36 | the built APK |
| Required ABIs | `armeabi-v7a`, `arm64-v8a` | `scripts/validate-tv-apk.mjs` |
| 16 KB page size | gated on 64-bit libraries only | same |
| touchscreen | `required=false` | generated manifest |
| leanback | declared | generated manifest |
| `android:screenOrientation` | **deliberately absent** | see below |

`@react-native-tvos/config-tv` deletes `android:screenOrientation` for leanback
builds (`removePortraitOrientation`). A leanback device is landscape by
construction. A hardening pass once added a plugin to put it back and removed it
again on finding this: restating something the TV toolchain removes on purpose
is a workaround against the platform, and the next clean prebuild wins anyway.
The generated-native suite now pins the ABSENCE so it is not reintroduced.

16 KB page size is a **64-bit** platform change. Gating 32-bit ABIs on it
reports a compliant build as broken — an earlier version of the validator did
exactly that, with eighteen false failures.
