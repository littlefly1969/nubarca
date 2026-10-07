# Album-share upload idempotency

This is a separate, unimplemented slice. A server may accept an upload before
the browser loses the response; an explicit retry can currently create another
album item. The public uploader correctly stops and asks the visitor to check
the album first. Browser resume metadata cannot establish server idempotency.

## Proposed contract

- Generate one opaque upload ID per selected file and persist it before the
  first POST. Reuse it for every retry of that upload, including a response lost
  after acceptance. Do not identify an upload by name, size or modification time.
- Store the accepted result under a unique `(share ID, upload ID)` constraint.
  A repeated authorized POST returns that original result and consumes no
  additional quota or album membership. Share token rotation does not change
  the share's identity.
- Coordinate concurrent first requests atomically with quota reservation and
  file acceptance. Define recovery for pending/abandoned uploads and reject
  reusing an ID for a different payload.
- Every replay still passes the existing share lifecycle, access and grant
  checks. Idempotency must not grant access to a revoked or protected share.

## Acceptance

Test two simultaneous identical requests on PostgreSQL: one accepted upload,
one quota slot and the same result in both responses. Also cover response loss
followed by retry, rejected uploads, pending-request recovery, payload mismatch,
share isolation, rotation, revocation and second-factor access. Client tests must
show that retries reuse the original ID and distinct files get distinct IDs.
