# @nubarca/contracts

The **one** client-side definition of NubArca's domain vocabulary, consumed by
all three clients (MOBILE-FIRST-CLASS-PARITY-CONTRACTS-01 §1-2, §41-43).

> One domain. One contract. Multiple first-class experiences.

## What belongs here

DTOs, enums, roles, capability types, query types, **query serialization**,
mutation payload builders, validation ranges and pure domain helpers — anything
whose meaning must be identical on web, phone and television.

## What must never be here

React, React Native, the DOM, `window`, Expo, UI components, SecureStore,
cookie handling, TV session handling, navigation — and **no transport**. There
is no `fetch` in this package. Each client keeps its own authenticated
transport, which is duplication the architecture accepts on purpose: transports
legitimately differ, meanings do not.

`contractPurity.test.ts` enforces both lists, so this stays true by test rather
than by good intentions.

## How each client resolves it

| client | mechanism |
|---|---|
| frontend | `tsconfig` path + Vite/Vitest alias, like `@nubarca/api-client` |
| mobile | `file:` dependency (npm symlinks it) + Metro `watchFolders` |
| tv | the same |

The `file:` symlink is what lets `node --test` load the TypeScript directly:
Node refuses to strip types inside `node_modules`, but it resolves the symlink
to its real path first, and that path is outside `node_modules`.

## The authority is the server

When the clients disagree, neither wins by seniority — the backend DTO decides.
Converging `MediaItem` found web and mobile drifted in *both* directions
against `NubArca.Api.Files.MediaItem`: mobile was missing `favorite`, `rating`
and `hasGps`, and had invented `audioCodec` and `frameRate`, which live on
`VideoItem` (`/api/videos`) and are never sent on `/api/media`.
