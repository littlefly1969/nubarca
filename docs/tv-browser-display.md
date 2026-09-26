# A NubArca display in a browser

A NubArca TV is either:

- the **native TV app** (Fire TV, Google TV), or
- a **paired browser display** — any modern browser on `/tv`: a mini-PC under
  the television, a Raspberry-class Chromium box, a laptop on a projector, a Mac.

Both are the same thing to the server and to the owner. They pair the same way,
appear in the same **TV Devices** list, are assigned to the general experience
or to a party the same way, are taken over by a party's game and handed back
the same way, and are revoked the same way. There is no "browser TV" category
and no second pairing: after pairing, a browser holds exactly what a Fire TV
holds — one TV session, in an HTTP-only cookie scoped to `/api/tv`.

`/party/{token}/tv` is **not** a NubArca display. It is a public party page
reached with a party's own link and it is outside this contract.

## What the display does

| | |
|---|---|
| **Boot** | `GET /api/tv/session` decides. Only a `401` sends the display to pairing. No network, a timeout, a server that is restarting: the display keeps its pairing and asks again on a capped backoff (2 s … 30 s), showing "Riconnessione…". |
| **Admission** | The first answer carries the assignment. A display assigned to a party starts **in** the party — slideshow, game or "unavailable" — and never passes through the mode selector. |
| **Control plane** | While the page is visible: one read every 5 s, never two at once, a heartbeat once a minute. The owner's assignment takes the screen from whatever is on it (a Personal Area is locked on the way). |
| **Party** | Photos on the party's own timing, **real video** (HLS or progressive) with the party's cap measured on media time, guest uploads arriving live, the greetings band and Hero cards, the challenge that holds the wall, a guest's face search sent to the screen, the game on the canonical stage — all authorised by the TV session and a display grant minted from it, never by a party token. |
| **Party → another party** | A different party is a different mount: nothing of the previous one (photograph, Hero, face filter, video position, challenge, game grant) survives. |
| **BACK** | Never leaves an assigned party — that is the owner's decision. On an assigned party it can only leave fullscreen. |
| **Resume** | Coming back from sleep, a switched-off screen, a hidden tab or a frozen page, the display asks for the wake lock again and re-reads the control plane and everything on screen **at once**. A sleep the browser did not announce is detected from the clock. |
| **Revocation** | The next request answers `401`: media stops, the Personal Area grant is dropped, the party is unmounted, and the display shows "session revoked" beside a fresh pairing code. |

The rules behind all of this are the native app's. The browser carries a port
of them (`frontend/src/tv/semantics/`) and
`frontend/src/tv/semantics/nativeParity.test.ts` loads the app's own modules
from `tv/src` and runs both on the same cases: a timing, a threshold or a
transition changed on one side only fails the web test suite.

## Browsers

Supported as appliances: **Chrome, Chromium and Edge** (Windows, Linux, macOS).
Safari and Firefox on macOS work where their media support allows; they are not
part of the acceptance matrix.

Video follows the same `/video` contract as every NubArca client: the server's
HLS ladder where one exists (native HLS in Safari, hls.js over MSE elsewhere),
the progressive stream otherwise. A video the browser cannot play, or a
transcode that is still being prepared, is skipped after 10 seconds; a stream
that stops moving is treated as broken after 20. No original file is ever made
public to make a browser happy.

A browser refuses sound without a user gesture. The display then plays the
video **muted** rather than showing a still frame, and says so; the first key or
click brings the sound back. A kiosk started with
`--autoplay-policy=no-user-gesture-required` plays with sound from the start.

## Requirements

- **HTTPS.** The TV session cookie is `Secure`, and the Screen Wake Lock API
  exists only in a secure context. (`http://localhost` counts as secure, which
  is what the end-to-end test uses; a mini-PC in a living room must use the
  installation's HTTPS origin.)
- A keyboard-like remote works as is: arrows, Enter/Space, Escape/Backspace,
  and the media keys. An HDMI-CEC, USB or Bluetooth remote that the browser
  sees as a keyboard needs nothing else.

## A dedicated mini-PC

None of this is required for `/tv` to work — it works in an ordinary window —
but a screen nobody is standing next to benefits from:

- **Kiosk mode**, so there is no browser chrome and no fullscreen button to
  press:

  ```bash
  chromium --kiosk --noerrdialogs --disable-session-crashed-bubble \
    --autoplay-policy=no-user-gesture-required \
    "https://<your NubArca origin>/tv"
  ```

  (`msedge --kiosk "https://<origin>/tv" --edge-kiosk-type=fullscreen` on Windows.)
- **Autostart** of that command at login (a systemd user service or the
  desktop's autostart on Linux; a Startup shortcut or Assigned Access on
  Windows), so a power cut ends with the party back on screen.
- **The operating system's own sleep switched off.** The display holds a
  screen wake lock while it is doing its job, and re-asks for it after every
  return, but a browser cannot wake a machine that has already been suspended:
  that belongs to the OS power settings.

Without kiosk mode the display offers a "Schermo intero" button when the
pointer moves; losing fullscreen never stops the display.

## When a screen went black

The display keeps a short, leak-free log of what happened to it — session
restored or revoked, assignment and presentation changes, network lost and
restored, resumes, wake lock acquired or refused, video errors and skips. It
never contains a token, a URL, a Personal Area code or a greeting.

- In the browser's developer console: `__nubarcaTvDiagnostics()`.
- In a kiosk, start the browser with `--enable-logging=stderr --v=0` (or
  `--enable-logging` to its log file) and look for lines starting
  `[nubarca-tv]`.

## Proving it

`scripts/tv-browser-e2e.sh` brings up a throwaway stack (PostgreSQL in Docker,
the API, an owner, the built frontend behind the same origin) and drives a
headless Chrome through the whole life of a display: pair, assign, slideshow,
game takeover, game hand-back, reload straight into the party, revoke. It runs
in CI on every pull request (`Browser TV end-to-end`); `E2E_SHOTS=<dir>` keeps a
screenshot of each step.

What it cannot prove is the hardware: sleep and resume of a real machine, a
real remote, a real television's overscan, Windows and Edge. Those are the
manual acceptance matrix and are recorded with the release, not claimed here.
