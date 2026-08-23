import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import test from 'node:test';

const script = readFileSync(
  resolve(import.meta.dirname, '../../deploy/update-production.sh'),
  'utf8',
);

test('production update has a read-only review command and SHA-confirmed apply', () => {
  assert.match(script, /update-production\.sh check --env-file/);
  assert.match(script, /update-production\.sh apply --env-file.*--confirm/);
  assert.match(script, /CONFIRM_SHA.*CANDIDATE_SHA/);
  assert.match(script, /origin\/main moved/);
});

test('production update consumes only immutable CI images and never builds or prunes', () => {
  assert.match(script, /docker manifest inspect --verbose/);
  assert.match(script, /NEW_API_IMAGE="\$API_REPO@\$API_DIGEST"/);
  assert.match(script, /NEW_FRONTEND_IMAGE="\$FRONTEND_REPO@\$FRONTEND_DIGEST"/);
  assert.doesNotMatch(script, /docker (?:compose )?build\b/);
  assert.doesNotMatch(script, /\bprune\b/);
  assert.match(script, /up -d --no-build --no-deps/);
});

test('gates images and Compose before replacing release pins', () => {
  const backendVerify = script.indexOf('scripts/verify-production-image.sh');
  const frontendVerify = script.indexOf('scripts/verify-production-frontend-image.sh');
  const composeGate = script.indexOf('COMPOSE_CANDIDATE');
  const pinReplace = script.indexOf('mv -f docker-compose.release.local.yml.tmp');
  assert.ok(backendVerify >= 0 && backendVerify < composeGate);
  assert.ok(frontendVerify >= 0 && frontendVerify < composeGate);
  assert.ok(composeGate < pinReplace);
  assert.match(script, /restoring the previous image pins/);
});

test('migrations stop the simple path before production changes', () => {
  assert.match(script, /src\/NubArca\.Api\/Data\/Migrations/);
  assert.match(script, /candidate contains database migrations; use deploy\/FAST_DEPLOY\.md manually/);
  assert.doesNotMatch(script, /\bdb migrate\b/);
});
