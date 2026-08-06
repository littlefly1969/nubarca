# React Router — security assessment and v8 readiness

Assessed 2026-07-29 on `main` `c197d4e`, branch
`chore/frontend-router-security-hardening`.

**Decision: Esito B** — update to the latest React Router 7, adopt v8-compatible
imports, stay on v7. A major upgrade to v8 is **not** justified by the evidence.

| | Before | After |
| --- | --- | --- |
| `react-router-dom` | 7.15.1 (direct) | **removed** |
| `react-router` | 7.15.1 (transitive) | **7.18.2 (direct, exact)** |
| React / React DOM | 19.2.6 | 19.2.6 (unchanged) |
| Vite | 7.3.3 | 7.3.3 (unchanged) |
| Node (host / `.nvmrc`) | 22.22.3 / 22 | unchanged |
| Node (frontend Docker) | `node:20-alpine` | unchanged |
| Router advisories (`npm audit`) | 5, across 2 packages | **1**, not applicable |
| Frontend tests | 82 files / 624 | 85 files / **651** |

## 1. Router mode

NubArca is a **Declarative Mode** SPA. Determined from the source, not assumed:

- [`frontend/src/App.tsx`](../frontend/src/App.tsx) uses `<BrowserRouter>` with
  `<Routes>` / `<Route>`.
- No `RouterProvider`, `createBrowserRouter`, `createHashRouter` or
  `createRoutesFromElements` anywhere.
- No `loader:` / `action:` / `useLoaderData` / `useActionData` / `useMatches`.
- No `@react-router/dev`, `@react-router/node`, `@react-router/serve`, no
  `react-router.config.*`, no `entry.server.*` / `entry.client.*`, no
  `ServerRouter` / `HydratedRouter`.

So: **no SSR, no Framework Mode, no RSC, no data-router loaders or actions.**
Tests use `MemoryRouter`. This classification is what decides the applicability
of four of the five advisories below.

## 2. Advisories

npm reports the aggregate range `6.0.0 - 8.2.0` for `react-router`. That is the
**union of five independent advisories**, not one continuous vulnerable range —
reading it as a single range is what produced the earlier, incorrect conclusion
that "no patched 7.x exists". Each advisory was checked individually against the
GitHub Security Advisory API (primary source).

| GHSA | CVE | Severity | Vulnerable | First patched | Applies to NubArca? |
| --- | --- | --- | --- | --- | --- |
| [GHSA-wrjc-x8rr-h8h6](https://github.com/advisories/GHSA-wrjc-x8rr-h8h6) | CVE-2026-53669 | moderate | `>=6.0.0 <7.18.0` | **7.18.0** | **YES — demonstrated** |
| [GHSA-chx6-hx7r-mcp5](https://github.com/advisories/GHSA-chx6-hx7r-mcp5) | CVE-2026-55685 | high | `>=7.0.0 <7.18.0` | 7.18.0 | No — Framework Mode only |
| [GHSA-337j-9hxr-rhxg](https://github.com/advisories/GHSA-337j-9hxr-rhxg) | CVE-2026-53666 | moderate | `>=6.4.0 <7.18.0` | 7.18.0 | No — SSR hydration only |
| [GHSA-h8fp-f39c-q6mh](https://github.com/advisories/GHSA-h8fp-f39c-q6mh) | CVE-2026-53667 | moderate | `>=7.11.0 <7.18.0` | 7.18.0 | No — unstable RSC APIs only |
| [GHSA-qwww-vcr4-c8h2](https://github.com/advisories/GHSA-qwww-vcr4-c8h2) | — | high | `>=7.12.0 <8.3.0` | **8.3.0** | No — unstable RSC APIs only |

Advisory wording, quoted verbatim, is what excludes the non-applicable ones:

- GHSA-chx6-hx7r-mcp5: *"This only impacts Framework Mode applications. This does
  not impact your application if you are using Declarative or Data Mode."*
- GHSA-337j-9hxr-rhxg: *"This does not impact your application if you are using
  Declarative Mode. This only impacts Framework Mode and Data Mode applications
  doing manual SSR/hydration."*
- GHSA-h8fp-f39c-q6mh and GHSA-qwww-vcr4-c8h2: *"This only affects your
  application if you are using the unstable RSC APIs."*

### 2.1 The one that did apply — exploitability demonstrated

GHSA-wrjc-x8rr-h8h6 is a client-side open redirect in `<Link>` and
`useNavigate`. It carries **no mode restriction**, and Declarative Mode uses
exactly those APIs, so NubArca was inside the affected surface — not merely
inside the semver range.

The reachable sink is `returnTo`:
[`ProtectedRoute.tsx`](../frontend/src/auth/ProtectedRoute.tsx) captures
`location.pathname + search + hash` for an anonymous visitor and hands it to
[`LoginPage.tsx`](../frontend/src/pages/LoginPage.tsx), which redirects there
after login. It is the only navigation target in the app derived from the URL
the visitor arrived on.

`LoginPage` already guards it:

```ts
typeof requestedReturn === 'string'
  && requestedReturn.startsWith('/')
  && !requestedReturn.startsWith('//')
```

That blocks absolute URLs, `javascript:`, `data:` and protocol-relative `//host`
— **but not `/\evil.example`**, which starts with a single `/`. Browsers
normalise `\` to `/`, so that string becomes `//evil.example`: a cross-origin
navigation.

Confirmed empirically by running
[`LoginPage.returnTo.test.tsx`](../frontend/src/pages/LoginPage.returnTo.test.tsx)
against both versions:

```
react-router 7.15.1  ->  "/\evil.example"   backslash PRESERVED  (vulnerable)
react-router 7.18.2  ->  "/evil.example"    normalised, same-origin
```

So the test is a genuine regression guard: it **fails** on the vulnerable
version. The fix is entirely library-side — no application code needed to
change, and the existing guard remains correct for the other vectors.

### 2.2 Residual advisory

After the upgrade, `npm audit` still reports **GHSA-qwww-vcr4-c8h2** (high), the
only advisory whose patched version is 8.3.0 rather than 7.18.0.

**Not applicable.** It is an RSC-mode CSRF bypass, and the advisory states it
only affects applications using the unstable RSC APIs. NubArca has no RSC
surface at all (§1). `npm audit` cannot express "not applicable in this mode", so
it keeps reporting it; npm's own suggested remedy is
`npm audit fix --force` → `react-router@8.3.0`, *"which is a breaking change"*.

It is therefore **documented, not suppressed**. No `overrides`, no `resolutions`,
no audit exclusions were added.

## 3. Import strategy

All 49 import sites moved from `react-router-dom` to `react-router`, and
`react-router-dom` was removed from the dependency tree.

In v7 `react-router-dom` is a re-export shim whose only dependency is
`react-router` at the identical version. Verified against the installed 7.18.2
that `react-router` exports every symbol this app uses — `BrowserRouter`,
`MemoryRouter`, `Routes`, `Route`, `Link`, `NavLink`, `Navigate`, `Outlet`,
`useParams`, `useNavigate`, `useSearchParams`, `useLocation` — and that the only
export `react-router-dom` adds is `HydratedRouter`, a Framework-Mode hydration
entry point NubArca does not use.

The codebase had exactly one import form (single-quoted
`from 'react-router-dom'`) and zero non-import references, so the rewrite could
not silently miss a site. `react-router-dom` now appears nowhere in `src/` or
`package-lock.json`.

## 4. v8 readiness

Already satisfied:

- React 19.2.6 and React DOM 19.2.6 (v8 needs React 18+).
- Node 22 locally / `.nvmrc`, Node 20 in the build image (v8 needs Node 20+).
- Vite 7.3.3, full ESM.
- Imports already on the consolidated `react-router` package.
- No deprecated or removed v7 API in use; no v7 future flags required for
  Declarative Mode.

Remaining before a v8 slice:

- Re-read the official v8 upgrade guide — Declarative Mode is the least affected
  path, but the `<Routes>`/`<Route>` element API should be re-confirmed against
  the v8 release notes rather than assumed.
- v8 is only *required* by GHSA-qwww-vcr4-c8h2, which is not applicable, so the
  upgrade should be scheduled on its own merits, not as a security fix.

## 5. What was verified unchanged

Public URLs, redirects, guards and the TV fallback are all untouched; the
27 new tests exist to prove it. Covered: root, protected routes, route
parameters, query-string preservation, the `returnTo` round trip, `/login`,
public `/tv`, `/party/:token`, `/party/:token/upload`,
`/beauty-lab-upload/:token`, protected `/tv/pair`, the legacy
`/gallery` → `/media` redirects, and the catch-all.

Two behaviours are documented as they actually are, rather than asserted as if
they were something else:

- **There is no standalone 404 page.** The catch-all is
  `<Navigate to="/" replace />`, so an unknown URL goes to `/` (and, for an
  anonymous visitor, on to `/login`).
- **`/admin` has no client-side role guard.** It sits behind the same
  `ProtectedRoute` as every other authenticated page; the page and the backend
  both gate non-admins with 403. Adding a client-side admin guard would be a
  product change and is out of scope here.

## 6. Follow-ups

1. **React Router 8** — optional, not security-driven. Schedule on its own
   merits; prerequisites are already met.
2. ~~**Frontend build image `node:20-alpine` → `node:22-alpine`**~~ — **done
   2026-07-29**, in its own slice. It was unrelated to the router (7.18.2 needs
   only Node 20), and the emitted bundles turned out SHA-256 identical on both
   Node majors. See [`development-environment.md`](development-environment.md).
