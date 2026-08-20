# NubArca test strategy

NubArca keeps two backend test lanes:

- `scripts/test-backend-fast.sh` runs deterministic tests that require only the
  local .NET runtime and SQLite. This is the normal development and pre-commit
  lane.
- `scripts/test-backend-full.sh` runs the fast lane first, then the tests marked
  `Category=External` in a separate process. The external lane includes
  PostgreSQL/Testcontainers, pgvector, real FFmpeg and the opt-in live
  HumanAesExpert sidecar. Both lanes are attempted even if the fast lane fails,
  and the command returns a failure status if either lane fails.

Separating the lanes does not remove coverage. It prevents external processes
and containers from competing with the broad SQLite suite, while the full
command remains the release/CI contract. External tests may still report
`Skipped` when their documented dependency or opt-in environment variable is
unavailable.

## GitHub Actions

The public repository uses standard GitHub-hosted Ubuntu runners only, so the
automated verification does not require paid runner capacity or repository
secrets.

`.github/workflows/ci.yml` runs on every pull request, every push to `main`, and
manual dispatch. Its independent jobs run in parallel and verify:

- the NubArca product identity contract;
- a Release build and the deterministic backend lane;
- frontend typechecking, tests, and production build;
- TV typechecking and tests;
- mobile typechecking.

Superseded CI runs on the same branch are cancelled so an obsolete backend run
does not consume capacity while a newer commit is waiting.

`.github/workflows/backend-full.yml` runs every night at 02:17 UTC and can also
be started manually. It executes `scripts/test-backend-full.sh`, including the
external lane backed by the Docker, PostgreSQL/Testcontainers and FFmpeg
capabilities available on the standard Ubuntu runner. Test result artifacts are
uploaded even when a test command fails; fast results are retained for 7 days
and full-suite results for 14 days.

Neither workflow deploys, publishes a release, writes to the repository, or
receives production secrets. Production deployment remains a separate,
operator-controlled operation governed by `deploy/FAST_DEPLOY.md`.

Additional arguments are passed through to `dotnet test`, for example:

```bash
scripts/test-backend-fast.sh --no-restore --no-build
scripts/test-backend-full.sh --logger "trx;LogFileName=backend.trx"
```

## Test-host isolation and reuse

`SqliteWebApplicationFactory` preserves one database and storage root per test
case. Default-config hosts are pooled within a test process; on return to the
pool the factory:

1. disposes every client created by that test;
2. deletes all SQLite rows with foreign keys temporarily disabled;
3. clears the storage root and mutable singleton test state;
4. returns only a successfully reset host to the pool.

A reset failure discards the host instead of exposing it to another test.
Factories with custom settings or a custom clock are not pooled because their
configuration often points at test-specific directories.

The SQLite schema itself is built once per test process and copied into new
isolated in-memory databases through SQLite's backup API. Endpoint flows use a
fast test-only password hasher; focused `AuthServiceTests` continue to use ASP.NET
Core's real `PasswordHasher<User>` and therefore retain production-hasher
coverage.

## Performance baseline

On the development host on 2026-07-28:

- `AuthEndpointTests` (11 tests): 17 s before, 3 s after;
- cross-cutting authentication/authorization sample (157 tests): 2 min 28 s
  before, 21 s after;
- TV personal gallery/area/interpretation sample (111 tests): 11 s after moving
  rate-limit-neutral tests onto the standard pooled factory;
- complete fast lane (2,497 tests): approximately 2 hours historically,
  15 min 26 s after the pooling and fixture improvements.

Wall-clock time also includes test-runner startup and is slightly higher.
