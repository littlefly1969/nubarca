# NubArca — development environment

## 1. Purpose

This file is the **canonical description of the toolchain** needed to develop,
build, test and validate NubArca. It exists so that a new workstation can be
brought to a working state without guessing versions, and so that every
tolerated warning and known version drift is a recorded decision rather than
folklore.

Scope boundaries:

- **Deployment** is *not* documented here. The production runbook is
  [`deploy/FAST_DEPLOY.md`](../deploy/FAST_DEPLOY.md) and it remains the only
  source of truth for production commands.
- **Product architecture** is *not* documented here — see
  [`ARCHITECTURE.md`](../ARCHITECTURE.md).
- Branch-level status lives in [`docs/current-work.md`](current-work.md).

Run [`scripts/check-development-environment.sh`](../scripts/check-development-environment.sh)
to compare this matrix against the machine you are sitting at. It is read-only:
it installs nothing, starts nothing, writes nothing and never reads `.env`.

## 2. Verified operating systems

| OS | Status |
| --- | --- |
| Manjaro / Arch (x86_64, kernel 6.12) | **verified** — the matrix below was captured here |
| Debian / Ubuntu (x86_64) | expected to work; same toolchain, different package names |
| macOS / Windows | **not verified**. The backend and frontend are portable, but the OpenVINO native stack (`linux-x64`) and `NetVips.Native.linux-x64` are Linux-only |

The production target is Linux x86_64 in every case.

## 3. Canonical version matrix

"Source of truth" is the file that *decides* the version. When it says
**not declared**, the repository does not pin the tool and the range column is
the tested expectation, not a guarantee.

| Component | Declared in repo | Detected (reference workstation) | Supported range | Required? | Source of truth | Drift |
| --- | --- | --- | --- | --- | --- | --- |
| Operating system | — | Manjaro, Linux 6.12.96, x86_64 | Linux x86_64 | required | not declared | — |
| Git | not declared | 2.55.0 | >= 2.30 | required | not declared | — |
| Bash | not declared | 5.3.15 | >= 4.0 | required (scripts) | not declared | — |
| **.NET SDK** | `10.0.104`, `rollForward: latestFeature` | 10.0.110 | 10.0.1xx feature band | required (backend) | [`global.json`](../global.json) | **none** — `latestFeature` accepts 10.0.110 by design |
| ASP.NET Core runtime | `net10.0` target | 10.0.10 | 10.0.x | required (backend) | `*.csproj` `TargetFramework` | — |
| `dotnet-ef` | `10.0.4`, `rollForward: false` | tool-manifest pinned | 10.0.4 exactly | required (migrations) | [`dotnet-tools.json`](../dotnet-tools.json) | — |
| **Node.js (frontend + TV)** | `22` | 22.22.3 | >= 22.13 | required | [`.nvmrc`](../.nvmrc) | none — the frontend Docker build image is also Node 22 |
| npm | not declared | 10.9.8 | >= 10 | required | not declared (ships with Node 22) | — |
| **Expo SDK** | `~56.0.17` | 56.0.17 | SDK 56 | required (TV) | [`tv/package.json`](../tv/package.json) | none — aligned in this slice |
| React Native | `npm:react-native-tvos@0.85-stable` (0.85.3-3) | 0.85.3-3 | pinned alias | required (TV) | `tv/package.json` | — |
| React (TV) | `19.2.3` | 19.2.3 | exact | required (TV) | `tv/package.json` | — |
| React (frontend) | `^19.1.0` | 19.x | ^19 | required (frontend) | `frontend/package.json` | — |
| **JDK** | 17 (21 works; **26 breaks**) | 17.0.19 | 17 or 21 | optional — APK build only | [`tv/README.md`](../tv/README.md) | — |
| Gradle | `9.3.1` (wrapper) | wrapper-provided | wrapper-pinned | optional — APK build only | [`tv/android/gradle/wrapper/gradle-wrapper.properties`](../tv/android/gradle/wrapper/gradle-wrapper.properties) | — |
| Android Gradle Plugin | **not declared** — version resolved by `expo-root-project` / RN Gradle plugin | — | RN 0.85 default | optional — APK build only | `tv/android/build.gradle` (unversioned classpath, deliberate) | — |
| Android SDK | `compileSdk`/`targetSdk` via `rootProject.ext` (Expo-generated) | SDK present, `ANDROID_HOME` unset | android-36 / build-tools 36 / NDK 27 | optional — APK build only | Expo prebuild + `tv/README.md` | env var not exported (see §9) |
| Docker Engine | not declared | 29.6.2 | >= 24 | optional per area | not declared | — |
| Docker Compose | not declared | 5.3.1 | >= 2.20 (compose plugin) | optional per area | not declared | — |
| **PostgreSQL** | `pgvector/pgvector:pg17` | container image | 17 (with pgvector) | required to run the API | [`docker-compose.yml`](../docker-compose.yml), `docker-compose.prod.yml` | — |
| FFmpeg | image installs distro `ffmpeg` | 8.1.2 | >= 6 | optional — media derivatives | `src/NubArca.Api/Dockerfile` | host 8.1.2 vs image distro build; see §12 |
| FFprobe | shipped with FFmpeg | 8.1.2 | >= 6 | optional — video metadata | same | — |
| **ONNX Runtime (managed)** | `1.24.1` | 1.24.1 | must equal ORT ABI | optional — AI | [`src/NubArca.Api/NubArca.Api.csproj`](../src/NubArca.Api/NubArca.Api.csproj) | — |
| **ONNX Runtime (native, OpenVINO)** | `ORT_OPENVINO_VERSION=1.24.1`, `ORT_ABI_VERSION=1.24.1` | build-time only | exact, SHA-256 verified | optional — OpenVINO only | [`scripts/openvino-direct/onnxruntime-openvino.lock`](../scripts/openvino-direct/onnxruntime-openvino.lock) | — |
| OpenVINO | `2025.4.1` (soname `2541`) | build-time only | exact, bundled in the pinned wheel | optional — OpenVINO only | same lock file | — |
| curl | not declared | 8.21.0 | any | optional | not declared | — |
| jq | not declared | **not installed** | any | optional — **deploy only** | not declared | used only by `deploy/backup.sh`, `deploy/restore.sh` |
| rsync | not declared | 3.4.4 | any | optional — deploy only | not declared | — |

### 3.1 What each area actually needs

| I want to work on… | I need |
| --- | --- |
| **Backend** (C#, EF Core, jobs) | .NET SDK 10 + Docker (for the Postgres container) |
| **Frontend** (React/Vite) | Node 22 + npm. A running API is optional for unit tests |
| **TV app — JS/OTA only** | Node 22 + npm. No JDK, no Android SDK |
| **TV app — native APK** | the above **plus** JDK 17/21 **plus** Android SDK (android-36, build-tools 36, NDK 27) |
| **Media derivatives** (posters, HLS) | FFmpeg + FFprobe, or run the API in its container |
| **OpenVINO AI** | Docker only — the native stack is fetched and verified *inside* the image build. Nothing is installed on the host |
| **Deploying** | see `deploy/FAST_DEPLOY.md`; additionally `jq` and `rsync` |

## 4. Backend setup

```bash
# .NET SDK 10 (10.0.1xx). global.json rolls forward to the newest 10.0.1xx.
dotnet --info

# restore the pinned EF Core CLI (dotnet-tools.json)
dotnet tool restore

dotnet build NubArca.sln
```

Do **not** install a .NET SDK newer than the 10.0.1xx feature band expecting it
to be used: `global.json` uses `rollForward: latestFeature`, which stays inside
the declared band.

## 5. Frontend setup

```bash
cd frontend
npm ci          # always `ci` for validation — never `install`
npm run lint    # this repo's `lint` IS the tsc typecheck (delegates to typecheck)
npm run build
npm run test:run
```

There is no ESLint configuration: `lint` runs the TypeScript compiler. That is
intentional. `lint` delegates to `typecheck`, which is `tsc -b --noEmit --force`
— the `--force` keeps the result independent of incremental `.tsbuildinfo`
state, so validation can never depend on a cache being correct.

**Do not pipe a validation command into `head`/`tail` without
`set -o pipefail`.** A shell pipeline reports the LAST command's exit status, so
`npm run lint 2>&1 | tail -5` exits 0 even when the type check failed. That is a
real false green this repo has already hit — it was mistaken for an incremental
build-cache problem. Run validation bare, or enable `pipefail` first.

## 6. TV setup

```bash
cd tv
npm ci
npm run lint          # tsc --noEmit
npm run config        # expo config --type introspect
npm test              # node --test over the TV unit suites
npx expo-doctor       # must be 21/21
npx expo export --platform android   # JS/Hermes bundle — NOT an APK
```

`tv/.npmrc` sets `legacy-peer-deps=true` because `react-native` is an npm alias
to the `react-native-tvos` fork. `react-native` is listed in
`expo.install.exclude`, so `expo install --fix` will not rewrite the alias.

## 7. Android / native APK setup

Only needed to produce an APK. See [`tv/README.md`](../tv/README.md) for the
full procedure.

```bash
export JAVA_HOME=/usr/lib/jvm/java-17-openjdk    # 17 or 21 — NOT 26
export ANDROID_HOME="$HOME/Android/Sdk"
export PATH="$ANDROID_HOME/platform-tools:$PATH"
```

**JDK 26 does not work.** The Gradle foojay toolchain resolver fails at
configuration time with `NoSuchFieldError: JvmVendorSpec ... IBM_SEMERU`.

`expo prebuild --clean` regenerates `tv/android/`; do not run it casually,
because it discards local native edits.

## 8. Docker and Compose

Two distinct stacks exist; do not confuse them.

**Development** — `docker-compose.yml`, self-contained, safe defaults:

```bash
docker compose -f docker-compose.yml config      # validate, no services started
docker compose up -d postgres                    # just the DB for local backend work
```

**Production** — four files, all required, documented in `deploy/FAST_DEPLOY.md`:

```
docker-compose.prod.yml              # in the repo
docker-compose.prod.local.yml        # host-local, NOT in the repo
docker-compose.facedirect-api.yml    # in the repo
docker-compose.release.local.yml     # host-local, NOT in the repo
```

Only two of the four are versioned here. **The full release stack therefore
cannot be validated from a development checkout** — the `*.local.yml` overrides
exist only on the production host. Locally you can validate the repo-tracked
subset:

```bash
docker compose --env-file .env.example -f docker-compose.prod.yml config >/dev/null
```

The `--env-file` is **not optional**: `docker-compose.prod.yml` declares
`POSTGRES_PASSWORD` with the required-variable syntax, so a bare
`docker compose -f docker-compose.prod.yml config` fails by design. Using
`.env.example` is the safe way to validate structure without touching secrets.

**Never `source` the production `.env`** — see `CLAUDE.md`. The
`ConnectionStrings__Postgres` value contains `;`, which a POSIX shell treats as
a command separator, silently producing a passwordless connection string.

### Image targets

`src/NubArca.Api/Dockerfile` is multi-stage. The targets are not
interchangeable:

| Target | Contents | Used by |
| --- | --- | --- |
| `runtime-base` | aspnet:10.0 + `libgssapi-krb5-2` + `ffmpeg` | shared base, not deployed directly |
| `runtime` | `runtime-base`, nothing added — **default target** | standard api/worker image |
| `runtime-openvino` | adds the pinned ORT+OpenVINO native stack and Intel GPU userspace | only containers running `Ai__Onnx__ExecutionProvider=openvino-direct` |

`frontend/Dockerfile` builds with `node:22-alpine` — matching the canonical
Node major in `.nvmrc` — and serves via `nginx:alpine`.

## 9. PostgreSQL

PostgreSQL **17** with pgvector, via `pgvector/pgvector:pg17` in both dev and
prod. Do not substitute `postgres:17-alpine`: it is musl-based and lacks
pgvector, and the image swap is documented in
[`docs/ai-photo-pgvector.md`](ai-photo-pgvector.md).

No PostgreSQL client or server is required on the host — everything runs in the
container.

## 10. FFmpeg / FFprobe

Optional locally. The API image already installs `ffmpeg` (which provides
`ffprobe`). They are only invoked when the optional providers are enabled:

- `Media__VideoPosterProvider=ffmpeg` — real poster frames
- `Media__VideoMetadataProvider=ffprobe` — duration, codecs, dimensions, fps, rotation

Both default to `synthetic` / `none`, so the binaries are present but unused
unless explicitly turned on. Required filters for the pipeline: `scale`,
`setsar`, autorotation, thumbnail extraction and container probing — all present
in any standard build from version 6 onward.

## 11. ONNX Runtime and OpenVINO

Nothing is installed on the host. The whole native stack is defined by one file:

```
scripts/openvino-direct/onnxruntime-openvino.lock
```

It pins the exact Intel-built `onnxruntime-openvino` wheel with its **full
SHA-256**, and `fetch-native-libs.sh` fails closed on checksum mismatch, a
missing library, or a bundled version that disagrees with the lock.

**Invariant:** the managed `Microsoft.ML.OnnxRuntime` NuGet version must equal
`ORT_ABI_VERSION` in the lock file. Both are currently `1.24.1`. Changing one
without the other is an ABI break. Never bump either to "latest" automatically.

## 12. `.env` file

`.env.example` is the documented template; copy it to `.env` (gitignored) for
production use. The **development** compose file does not read `.env` at all —
it carries its own throwaway credentials inline.

Never print, commit, or `source` a real `.env`.

## 13. New workstation bootstrap

```bash
git clone <repo> nubarca && cd nubarca

# 1. verify the toolchain before building anything
scripts/check-development-environment.sh

# 2. backend
dotnet tool restore
dotnet build NubArca.sln

# 3. frontend
(cd frontend && npm ci && npm run lint && npm run build)

# 4. TV (JS only)
(cd tv && npm ci && npm run lint && npm test && npx expo-doctor)

# 5. database for local backend work
docker compose up -d postgres
```

## 14. Validation commands

| Area | Command |
| --- | --- |
| Backend build | `dotnet build NubArca.sln` |
| Backend tests (fast) | `scripts/test-backend-fast.sh` |
| Backend tests (full) | `scripts/test-backend-full.sh` |
| Frontend | `cd frontend && npm ci && npm run lint && npm run build && npm run test:run` |
| TV | `cd tv && npm ci && npm run lint && npm run config && npm test && npx expo-doctor` |
| TV bundle | `cd tv && npx expo export --platform android` |
| Compose (dev) | `docker compose -f docker-compose.yml config >/dev/null` |
| Compose (prod subset) | `docker compose --env-file .env.example -f docker-compose.prod.yml config >/dev/null` |
| Environment | `scripts/check-development-environment.sh` |

`scripts/test-backend-fast.sh` filters `Category!=External`; the full script
additionally runs tests needing external dependencies, which may be skipped when
those dependencies are unavailable.

## 15. Known and accepted warnings

Recorded on 2026-07-29 at `main` `88e1625`. A warning listed here has been
understood, not silenced — the repository adds **no** `NoWarn` and disables **no**
analyzer.

### 15.1 Backend

| Code | Where | Category | Priority | Decision |
| --- | --- | --- | --- | --- |
| `NU1903` — `Microsoft.OpenApi` 2.0.0, high severity ([GHSA-v5pm-xwqc-g5wc](https://github.com/advisories/GHSA-v5pm-xwqc-g5wc)) | transitive via `Microsoft.AspNetCore.OpenApi` 10.0.4 | E — external dependency | **P2** | **Accepted, deferred.** Verified: every .NET 10 servicing patch up to **10.0.10** still depends on `Microsoft.OpenApi` **2.0.0**, so no patch-level upgrade removes it. The only fix is forcing `Microsoft.OpenApi` 3.x — a *major* jump against a library `Microsoft.AspNetCore.OpenApi` is compiled against, i.e. a likely runtime `MissingMethod` break. Exposure is limited: `app.MapOpenApi()` is gated behind `IsDevelopment()`, so the OpenAPI document is **not served in production**. Revisit when Microsoft ships an ASP.NET Core package referencing a patched `Microsoft.OpenApi`. |
| `NU1903` — `SQLitePCLRaw.lib.e_sqlite3` 2.1.11, high severity ([GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)) | transitive via `Microsoft.EntityFrameworkCore.Sqlite` 10.0.4 — **test project only** | E — external dependency | **P3** | **Accepted, deferred.** Verified: `Microsoft.EntityFrameworkCore.Sqlite` 10.0.10 still pins `SQLitePCLRaw` 2.1.11. SQLite is used **only** by the test suite (production is PostgreSQL), so this native library never ships. Fixing it requires a transitive override, which the repository deliberately avoids for a test-only, non-shipping dependency. |
| `xUnit1031` — blocking task operations in tests (10 sites: `OnnxDualPlacementTests`, `FaceSubstrateTests`) | test project | F — intentional | **P3** | **Accepted, will not fix.** In `OnnxDualPlacementTests` the blocking call *is the assertion*: `Assert.False(third.Wait(200ms))` proves that a third `Acquire()` blocks on a two-slot pool. Rewriting it as `await` would delete the property under test. The `FaceSubstrateTests` sites are `.GetAwaiter().GetResult()` inside synchronous `[Fact]` methods; converting them is a test-signature refactor with no correctness benefit, deliberately out of scope here. |

Fixed in this slice: `CS0168` (2 unused locals) and `xUnit2031` (4 × `Assert.Single(x.Where(p))` → `Assert.Single(x, p)`). Backend warnings went **22 → 16**; the remainder are the accepted entries above.

### 15.2 Frontend

| Warning | Category | Priority | Decision |
| --- | --- | --- | --- |
| Vite: "Some chunks are larger than 500 kB" (`index` 939 kB, `hls` 523 kB) | D — informational | P3 | **Accepted.** Default Vite threshold, not an error. Code-splitting is a product/performance decision, not an environment one. |
| `npm audit`: **`react-router` 7.15.1**, high — open redirect, XSS, DoS, CSRF | E — external dependency | ~~P1~~ → **resolved** | **Fixed 2026-07-29** by updating to **7.18.2** and dropping `react-router-dom`. The earlier entry here was **wrong**: it read npm's aggregate range `6.0.0 – 8.2.0` as one continuous range and concluded "no patched 7.x exists". That range is the *union of five independent advisories*, four of which are patched in **7.18.0** — including the only one applicable to this app. Full analysis: [react-router-security-assessment.md](react-router-security-assessment.md). |
| `npm audit`: **`react-router` GHSA-qwww-vcr4-c8h2**, high — RSC Mode CSRF bypass | E — external dependency | P3 | **Accepted, not applicable.** The only router advisory whose fix is 8.3.0 rather than 7.18.0. The advisory states it "only affects your application if you are using the unstable RSC APIs"; NubArca is a Declarative Mode SPA with no RSC surface. `npm audit` cannot express mode-scoped applicability, so it keeps reporting it. Documented, never suppressed — no `overrides`/`resolutions`. |
| `npm audit`: `esbuild`, `postcss`, `undici`, `@babel/core` | E — external dependency | P3 | **Accepted.** All dev-only/transitive (Vite, Vitest, jsdom). `npm audit --omit=dev` reports only the residual `react-router` RSC entry, confirming none of these ship to a browser. |

### 15.3 TV

| Warning | Category | Priority | Decision |
| --- | --- | --- | --- |
| `expo-doctor`: patch mismatch on `expo`, `expo-constants`, `expo-updates` | C — patch drift | P2 | **Fixed in this slice** (§16). `expo-doctor` is now 21/21. |
| `npm ci`: `npm warn deprecated uuid@7.0.3`, `glob@11.1.0` | E — external dependency | P3 | **Accepted.** Both are transitive build-time dependencies of the Expo/RN CLI toolchain. Neither is a direct dependency and neither ships in the Hermes bundle. |
| `npm audit`: 11 vulnerabilities (10 moderate, 1 high) | E — external dependency | P3 | **Accepted.** Expo/Metro build-tooling transitives. `npm audit fix --force` would rewrite the Expo toolchain and is forbidden. |

### 15.4 Docker

| Warning | Category | Priority | Decision |
| --- | --- | --- | --- |
| `docker compose -f docker-compose.prod.yml config` fails without `--env-file` | F — by design | P3 | **Accepted.** `POSTGRES_PASSWORD` uses the required-variable syntax deliberately, so a misconfigured deploy fails loudly instead of starting passwordless. Validate with `--env-file .env.example`. |
| Two of the four release Compose files are not in the repository | A — documented gap | P2 | **Accepted and documented** (§8). `*.local.yml` overrides are host-local by design; the consequence is that the full release stack is not locally validatable. |
| `pgvector/pgvector:pg17` pinned by tag, not digest | D — informational | P3 | **Accepted.** Release image immutability is handled by `docker-compose.release.local.yml` on the production host, per `deploy/FAST_DEPLOY.md`. |

## 16. Known drift and decisions

| Drift | Decision |
| --- | --- |
| **.NET SDK** — `global.json` says 10.0.104, workstation has 10.0.110 | **Not a drift.** `rollForward: latestFeature` is designed to accept any 10.0.1xx. No change. |
| **Expo patch drift** — `expo` 56.0.15→56.0.17, `expo-constants` 56.0.20→56.0.22, `expo-updates` 56.0.21→56.0.23 | **Fixed.** Applied with `npx expo install --fix` (the project's own tooling). Verified patch-level only: **React 19.2.3 and `react-native-tvos@0.85.3-3` are untouched**, and Gradle/AGP/Android SDK are unaffected. The lockfile cascade is confined to `@expo/*`/`expo-*` and their build-time transitives. Full TV suite green, `expo-doctor` 21/21. |
| **Node version** — no machine-readable pin existed; `tv/README.md` said ">= 22.13.x" in prose only | **Fixed.** Added [`.nvmrc`](../.nvmrc) = `22` as the **single** canonical source. `.node-version` and `.tool-versions` were deliberately *not* added — one pinning mechanism only. A major (not a patch) is pinned so the repo keeps its supported range. |
| ~~**Frontend Docker image builds on `node:20-alpine`**~~ while local dev and TV require Node 22 | **Resolved 2026-07-29** — aligned to `node:22-alpine`. Node 20.19+ did satisfy Vite 7, so this was maintenance (P3), never a defect. Verified the change is inert: `package.json`/`package-lock.json` byte-identical, and the produced bundles are **SHA-256 identical** to the Node 20 output, so the runtime image is bit-for-bit the same. |
| **`ANDROID_HOME` / `ANDROID_SDK_ROOT` unset** on the reference workstation despite an SDK being installed at `~/Android/Sdk` | **Workstation config, not a repo defect.** The diagnostic script reports it as a NOTE. Export it before running Gradle (§7). |
| **`jq` not installed** on the reference workstation | **Not required.** Used only by `deploy/backup.sh` and `deploy/restore.sh`, i.e. on the deploy host. |
| **Android Gradle Plugin version not declared** in `tv/android/build.gradle` | **Intentional.** The classpath is unversioned; `expo-root-project` and the React Native Gradle plugin resolve it. Pinning it manually would fight Expo's prebuild. |

## 17. What must NOT be updated automatically

Never run these against this repository:

```
npm update              npm audit fix           npm audit fix --force
expo prebuild --clean   (casually)              dotnet outdated / blanket package bumps
```

And never bump these without a dedicated, reviewed slice:

- **`ORT_ABI_VERSION` / `Microsoft.ML.OnnxRuntime`** — they must stay equal; the
  managed P/Invoke layer and `libonnxruntime.so.<ver>` share an ABI.
- **`OPENVINO_VERSION` / `WHEEL_SHA256`** — regenerating the lock is a
  deliberate, reviewed act.
- **`react-native` alias** — pinned to the `react-native-tvos` fork.
- **PostgreSQL major** — the data volume format is tied to it.
- **`dotnet-ef`** — `rollForward: false`, must match the EF Core packages.

### Recorded follow-ups (out of scope here)

1. ~~**`react-router` 7 → 8** (P1)~~ — **done differently, 2026-07-29.** The
   premise was wrong: 7.18.2 clears every applicable advisory, so no major
   upgrade was needed. React Router 8 remains an *optional*, non-security
   follow-up whose prerequisites (React 19, Node 20+, ESM, consolidated
   imports) are already met. See
   [react-router-security-assessment.md](react-router-security-assessment.md).
2. ~~**Frontend build image `node:20-alpine` → `node:22-alpine`** (P3)~~ —
   **done 2026-07-29.** One-line change; dependencies and lockfile untouched
   and the emitted bundles are SHA-256 identical, so the runtime image did not
   change.
3. **`Microsoft.OpenApi` 2.0.0** (P2) — track ASP.NET Core servicing until a
   package referencing a patched version ships.
4. **`xUnit1031` in `FaceSubstrateTests`** (P3) — optional async test-signature
   cleanup.

## 18. Common problems

| Symptom | Cause / fix |
| --- | --- |
| `docker compose ... config` errors with `POSTGRES_PASSWORD is required` | Missing `--env-file`. Use `--env-file .env.example` to validate structure. |
| api/worker crash-loop with `No password has been provided … SASL/SCRAM-SHA-256` | The prod `.env` was `source`d. Shell env beats `--env-file`, and `;` truncated the connection string. Use a fresh shell; see `CLAUDE.md`. |
| `./gradlew` fails with `NoSuchFieldError: JvmVendorSpec ... IBM_SEMERU` | JDK 26. Use JDK 17 or 21. |
| `expo-doctor` reports patch mismatches | `npx expo install --fix`, then re-run the full TV suite. |
| `npm ci` fails in `tv/` with peer-dependency errors | `tv/.npmrc` must keep `legacy-peer-deps=true` (the `react-native-tvos` alias). |
| `dotnet build` picks an unexpected SDK | Check `global.json`; `rollForward: latestFeature` stays inside 10.0.1xx. |

## 19. New workstation checklist

- [ ] Linux x86_64 (or accept the §2 caveats)
- [ ] Git >= 2.30
- [ ] .NET SDK 10.0.1xx — `dotnet --info`
- [ ] `dotnet tool restore` (pinned `dotnet-ef`)
- [ ] Node 22 (`nvm use` reads `.nvmrc`) + npm >= 10
- [ ] Docker Engine + Compose plugin (for Postgres and image builds)
- [ ] *TV APK only:* JDK 17 or 21, `JAVA_HOME` exported
- [ ] *TV APK only:* Android SDK android-36 / build-tools 36 / NDK 27, `ANDROID_HOME` exported
- [ ] *Media work only:* FFmpeg + FFprobe >= 6
- [ ] *Deploy only:* `jq`, `rsync`
- [ ] `scripts/check-development-environment.sh` exits 0
- [ ] `dotnet build NubArca.sln` succeeds
- [ ] `cd frontend && npm ci && npm run lint && npm run build` succeeds
- [ ] `cd tv && npm ci && npm run lint && npm test && npx expo-doctor` succeeds (21/21)
