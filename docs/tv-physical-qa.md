# NubArca TV — physical acceptance plan

A runbook to EXECUTE, not results. Nothing here has been performed; every box
is unchecked on purpose. Source tests prove that routes and policies exist —
whether a specific D-pad press lands on a specific control, and whether a
decoder is really gone after HOME, only hardware can say.

Record the device, Fire OS / Android TV version, screen mode and app version
with every run.

## A. Five-way only — the headline pass

Do the **entire** product without touching MENU, Play/Pause, Rewind or
Fast-Forward. Only UP / DOWN / LEFT / RIGHT / SELECT / BACK.

- [ ] pairing → mode select → PIN
- [ ] Personal home, library, albums, album items
- [ ] semantic/metadata search, every filter row, the period editor
- [ ] People picker: search, include, exclude, ANY/ALL, clear
- [ ] open media, previous/next, start + pause + resume the photo slideshow
- [ ] video: play/pause, seek both ways, previous/next item
- [ ] Beauty Lab: add images, select, analyse, compare
- [ ] Party: album list, items, slideshow, exit a face filter
- [ ] Updates screen
- [ ] BACK out of every level to the launcher

**Any function that cannot be reached this way is a failure of the tranche, not
of the tester.**

## B. Fire TV remote — accelerators only

Repeat the media operations using MENU, Play/Pause, RW and FF.

- [ ] each one does the same thing its five-way route does
- [ ] none of them reaches a function A could not
- [ ] one physical press produces one action (no double-advance)

## C. Generic Android TV / Google TV

- [ ] physical device or the official TV emulator
- [ ] a remote with no MENU key completes pass A

## D. Gamepad

- [ ] D-pad and left stick move focus
- [ ] A = Select, B = Back
- [ ] no prompt names a vendor-specific button

## D2. Media Library surfaces

These are the reconciliation slice's user-visible outcomes. They are cheap to
check and easy to regress.

- [ ] **Paging counters.** Scroll past the first page in the Personal library.
      The grid badge and the viewer counter must never show `-1` — the viewer
      reads `7 / 137`, and the total does not change as later pages arrive
- [ ] **Selected kind.** Choose Video, close the menu, browse, reopen: the
      current kind is still marked, not merely focused
- [ ] **People picker with many people.** Open it on an owner with a large
      People list: it scrolls smoothly, focus never disappears while scrolling,
      and the highlighted row is always the one the remote is on
- [ ] **Search people.** Narrow the list by name, clear it, confirm the
      include/exclude selection is unchanged by searching
- [ ] **Long person names.** A long name truncates with an ellipsis at the SAME
      character in each of Off / Include / Exclude — the row must not appear to
      change identity as it is cycled
- [ ] **Filter editors.** The keyboard, the numeric pad and the date pad each
      show title, current value, every key row and the actions at once, with no
      page scroll and no clipped bottom row
- [ ] **Semantic search.** Confirmed absent from the filter list — it is
      modelled but deliberately not offered yet (see docs/current-work.md)

## D3. Personal viewer chrome

- [ ] Opening a photo shows the name, the position counter and (when running)
      the slideshow pill briefly
- [ ] After the idle window they all disappear together, and the photograph is
      not interrupted by their going
- [ ] MENU brings them back; MENU again hides them
- [ ] During a running slideshow they do NOT stay permanently visible — a slide
      advancing must not keep re-arming the overlay
- [ ] BACK still leaves the viewer in one press

## E. Photo keep-awake

- [ ] still photograph → screensaver/ambient is allowed to arrive
- [ ] paused slideshow → ambient allowed
- [ ] rotating slideshow → ambient prevented
- [ ] pause/resume flips the behaviour immediately

## F. Video keep-awake

- [ ] playing → screen stays awake
- [ ] paused → ambient allowed

## G. HOME lifecycle

The two media kinds behave differently on purpose, and both must be checked.

**Photo slideshow → HOME → return**

- [ ] rotation stops immediately (nothing advances behind the launcher)
- [ ] the wake lock is released — ambient/screensaver is allowed
- [ ] on return: the SAME photograph, and the slideshow is **paused**
- [ ] it does not restart by itself after a few seconds
- [ ] SELECT resumes it, once

**Party/personal VIDEO → HOME → return**

The video is not merely paused: the player is RELEASED, so the decoder, the
MediaSession and the audio-focus registration all go with it.

Play a video, note the position, press HOME.

- [ ] no audio continues
- [ ] `dumpsys media_session` shows no NubArca session still holding transport
- [ ] `dumpsys audio` shows no NubArca audio-focus owner
- [ ] `dumpsys meminfo` shows the decoder gone

Return.

- [ ] same item, approximately the same position
- [ ] **paused** — no sound starts by itself
- [ ] SELECT resumes — and **one press is enough**
- [ ] exactly one player
- [ ] no audio starts between returning and pressing SELECT

## H. Voice / system overlay

- [ ] repeat G with the assistant overlay instead of HOME
- [ ] no ghost audio, no duplicate player, no crash

## I. Output routes

During playback, where the hardware allows:

- [ ] HDMI disconnect / receiver input change → playback pauses
      (the display path, caught by the device callback)
- [ ] Bluetooth speaker disconnect → playback pauses
      (the active route, caught by ACTION_AUDIO_BECOMING_NOISY)
- [ ] disconnecting a Bluetooth device that is **not** carrying the audio —
      a phone, a controller, a second speaker — does **not** pause anything.
      This is the false positive the device callback was narrowed to avoid
- [ ] restoring the output does **not** auto-resume
- [ ] SELECT resumes from the same position

## J. Rapid item changes

- [ ] move quickly between videos
- [ ] never two audible streams
- [ ] never two decoders in `dumpsys meminfo`
- [ ] no crash

## K. Long session

- [ ] 30+ minutes of slideshow, video and large-library navigation
- [ ] memory does not climb without bound
- [ ] focus latency does not degrade
- [ ] no accumulating timers, listeners or players

## L. Resolution

- [ ] 720p and 1080p, plus any other mode the panel offers
- [ ] Actions launcher, filters and People stay inside the safe area
- [ ] no focus ring is clipped

## M. Party takeover (paired Fire TV)

The takeover's decisions are pure and tested (`personal/flow.ts`,
`lib/partyDisplayWatchdog.ts`, `lib/partyDisplayGrant.ts`,
`lib/assignmentView.ts`), and the lobby layout is measured in a real browser
(`frontend/scripts/check-party-stage-layout.mjs`). What only a panel can say is
listed here. For the run, flip `TV_DEBUG_MEDIA` in `tv/src/debug.ts` locally
(never commit it) and follow `adb logcat | grep '\[tv\]'`: the `control`
and `party` lines name every assignment, presentation, grant, mount, heartbeat,
recovery and fallback — never a token, a grant, a cookie or a URL.

Set up: a party with a game and at least two enabled activities, a few photos in
its album, the party LIVE, and the TV assigned to it from the web.

**Lobby layout** (the 1080p panel, i.e. a 960x540 logical viewport)

- [ ] eyebrow, title and subtitle are the approved size (compare with a photo
      of the previous build)
- [ ] the title starts at the top safe area, not in the middle
- [ ] the QR is far larger than before and entirely visible
- [ ] its white quiet zone is intact on all four sides
- [ ] nothing is clipped, nothing touches the physical edge, no scrollbar
- [ ] a phone scans it from a normal sofa distance and opens the game

**Takeover**

- [ ] TV on the party slideshow; the host switches the game on → lobby within
      about five seconds, with no remote press
- [ ] no white or black flash and no browser error on the way: the native
      "NubArca Party" card covers the change
- [ ] a TV left on the mode selector is taken over the same way
- [ ] a TV sitting in the Personal Area is taken over; re-entering the Personal
      Area afterwards asks for the code again

**End of game**

- [ ] the host ends the game: the closing card is readable for ~15 s
- [ ] the TV then returns to the slideshow on its own and it plays
- [ ] the control room still says FINISHED

**Restart**

- [ ] while the slideshow plays after FINISHED, the host restarts the game → the
      lobby returns within about five seconds, with the same QR

**Recovery** — the native shell must survive every one of these

- [ ] kill the renderer (`adb shell am kill` is not enough; use
      `chrome://inspect` → Terminate, or memory pressure): replaced within a few
      seconds
- [ ] a renderer that never starts (block the frontend host on the router
      before takeover): the native card stays, then the fallback, then the stage
      comes back by itself when the host is unblocked
- [ ] wedge (pause the page's JS from `chrome://inspect`): replaced after ~10 s
- [ ] Wi-Fi off/on during a round: the stage shows its last scene marked
      "reconnecting", then catches up
- [ ] backend container stopped/started: the shell keeps its pairing, waits,
      and resumes
- [ ] frontend container stopped/started: fallback, then the stage returns
- [ ] grant renewal: shorten `PartyDisplayService.GrantLifetime` on a test
      server to a few minutes; the stage renews with at most a brief native card
- [ ] HOME during the game and back: the stage continues; HOME during the
      slideshow and back: the slideshow restarts by itself
- [ ] BACK on the lobby, on the native fallback and on the slideshow closes the
      app; relaunching returns straight to the party

**Two televisions** on the same party: both show the game; unplugging one, or
assigning it elsewhere, changes nothing on the other; FINISHED and restart move
both.

## Evidence to capture

Run at: video playing · video paused · after HOME · after return · after output
disconnect · after rapid item changes.

```bash
adb shell dumpsys media_session
adb shell dumpsys audio
adb shell dumpsys meminfo it.littlefly.nubarca.tv
adb shell dumpsys activity activities
adb shell dumpsys package it.littlefly.nubarca.tv
```

Looking for: a stale MediaSession, a stale audio-focus owner, retained decoder
resources, and memory accumulation across the cycle. Platform evidence, not
screenshots.
