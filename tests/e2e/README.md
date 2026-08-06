# NubArca browser verification

Reproducible end-to-end verification of the web surfaces, driven through real
browser engines against an ephemeral stack. Runs from a fresh clone. Needs no
production credentials and never contacts an installation.

## One command

```bash
cd tests/e2e
npm install
npm run e2e
```

### What `npm run e2e` certifies

NubArca 0.3.0 browser E2E certification covers Chromium desktop, mobile and
effective-200%-layout. Firefox and WebKit projects are retained as extended
compatibility diagnostics but are not certified release gates in 0.3.0.

`npm run e2e` therefore runs the three Chromium projects and is the release gate.
The full nine-project matrix is available separately and may report failures:

```bash
npm run e2e:extended
```

Those failures currently surface through the harness health guard rather than
through functional assertions, so they are not evidence that Firefox or WebKit is
either supported or defective — establishing that needs its own investigation.

`npm run e2e` performs the whole cycle and cleans up after itself even when a
test fails:

1. validate prerequisites (Docker, curl, node, npx, ffmpeg);
2. start the ephemeral `nubarca-e2e` Compose stack;
3. wait for the API to report healthy;
4. seed deterministic users, media, albums and semantic data;
5. run the three Chromium release-gate projects;
6. verify the result is authoritative (see below);
7. preserve diagnostics on failure;
8. stop the stack and remove its volumes.

### What makes the result authoritative

A scrolling line reporter is not a result, and neither is a pipeline's exit
status — `npx playwright test | tail` reports `tail`'s success, which has already
produced one false green here. Only `scripts/verify-report.sh` may declare the
gate passed, and it does so only when three independent sources agree:

- the exit code Playwright actually returned;
- the JSON report's own pass/fail totals, counted per test rather than read off a
  summary line;
- the test count the gate is required to run — **72** for the Chromium gate.

The third is the one a green run cannot supply for itself. A project that
silently stops matching, or a spec renamed out of `testDir`, yields zero failures
out of fewer tests; without a required count that reads as a pass. The JSON
report is written to `.artifacts/` rather than `test-results/` because Playwright
clears `outputDir` when a run starts, which deleted the machine-readable result
before anything could read it.

## Individual steps

```bash
npm run start    # bring the ephemeral stack up
npm run seed     # create the deterministic test state
npm run test     # run the matrix against an already-running stack
npm run stop     # tear the stack down, including volumes
npm run report   # open the last HTML report
```

Useful variations:

```bash
npm run test -- --project=chromium-desktop        # one project
npm run test -- specs/media-library.spec.ts       # one spec
E2E_RUNNER=host npm run test                      # native engines, see below
E2E_KEEP_STATE=1 npm run stop                      # keep .state for diagnostics
```

## Why the browsers run in a container

The default runner executes the engines inside
`mcr.microsoft.com/playwright:v<version>-noble`, matching the pinned
`@playwright/test`.

This is deliberate. WebKit's Linux build links against a specific set of system
libraries — ICU 66, `libwebp.so.6`, `libffi.so.7`, `libxml2.so.2` — that many
distributions do not ship and that cannot be installed without root. Running the
engines in the image Playwright publishes for its own version makes the matrix
reproducible on any host and keeps "works on my distribution" out of the result.

`E2E_RUNNER=host` runs them natively. It is faster, but it only covers the
engines your host can actually launch: expect WebKit to fail outside a
Debian/Ubuntu-family host.

## The nine projects

| project | engine | form factor |
| --- | --- | --- |
| `chromium-desktop` | Chromium | desktop 1280×800 |
| `firefox-desktop` | Firefox | desktop 1280×800 |
| `webkit-desktop` | WebKit | desktop 1280×800 |
| `chromium-mobile` | Chromium | Pixel 7 descriptor |
| `webkit-mobile` | WebKit | iPhone 14 descriptor |
| `firefox-mobile` | Firefox | 412×915 viewport |
| `chromium-desktop-zoom200` | Chromium | effective-200% layout |
| `firefox-desktop-zoom200` | Firefox | effective-200% layout |
| `webkit-desktop-zoom200` | WebKit | effective-200% layout |

**On the zoom projects.** They halve the CSS-pixel viewport and (where the engine
supports it) double the device pixel ratio, which is what a page *sees* at 200%
browser zoom. That makes them deterministic **effective-200%-layout** coverage:
they catch layout that only works when the viewport is wide in CSS pixels. They
are not proof that every native browser zoom implementation behaves identically,
and should not be described as such.

**On mobile Firefox.** Playwright ships no touch-enabled Firefox descriptor, so
that project is a narrow viewport without touch emulation: responsive-layout
coverage, not touch behaviour.

## The stack

`docker-compose.e2e.yml` uses its own Compose project, `nubarca-e2e`, so it can
never touch development or production containers, volumes or networks. The
database lives in tmpfs and every volume is anonymous, so a run always starts
empty and `npm run stop` leaves nothing behind.

`nginx.e2e.conf` is the single-origin front door: the built app plus a
same-origin `/api` proxy, exactly as production serves them. It forwards
`Host $http_host` — the address the browser used, **port included** — and not
nginx's normalized `$host`, which strips it. The API rejects a state-changing
`/api` request whose `Origin` disagrees with its own scheme/host/port, so a
stripped port makes every write fail `403`, login included. The suite runs on
`:5273`, a non-default port, which is what makes it able to catch that at all: on
`:443` the stripped port and the port the API infers happen to agree, and the
bug is invisible.

AI runs on the **deterministic** backend. That backend is dev/test only and is
not semantically meaningful — which is exactly what a browser test needs: the
same input always yields the same embeddings, so assertions are reproducible
without downloading a model.

This matters for how the semantic specs should be read. They verify the
**frontend semantic envelope**: that markers appear for a video and not a photo,
that they are ordered chronologically, placed proportionally, activated by
pointer, `Enter` and `Space`, and hand off the right timestamp. They do **not**
verify production-model relevance or ranking — deterministic fixtures cannot
reproduce that, and backend ranking is covered by the backend test suite.

## Fixtures

Media is generated with `ffmpeg` at seed time rather than committed: fixed
colours, sizes and durations make it reproducible, and it keeps binary out of the
repository. The seeded video is two visually distinct halves, which is what gives
the deterministic backend more than one segment to return.

Generated output — `node_modules/`, `playwright-report/`, `test-results/`,
`.state/`, `.artifacts/` — is ignored and never committed.

## Seeded state

Throwaway credentials for a throwaway database, defined in `src/env.ts`:

| account | purpose |
| --- | --- |
| `owner@nubarca.test` | the primary owner every spec signs in as |
| `other@nubarca.test` | a second owner, so isolation can be proven |

Seeded content: an unassigned photo, an album-assigned photo, an excluded photo,
an unassigned video with a real duration, an album-assigned video, one album with
membership, and one photo owned by the second owner that must never appear for
the first.
