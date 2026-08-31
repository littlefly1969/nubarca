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
  assert.match(script, /--confirm-migrations/);
  assert.match(script, /CONFIRM_SHA.*CANDIDATE_SHA/);
  assert.match(script, /origin\/main moved/);
  assert.match(script, /another production update is already running/);
});

test('production update consumes only immutable CI images and never builds or prunes', () => {
  assert.match(script, /docker manifest inspect --verbose/);
  assert.match(script, /NEW_API_IMAGE="\$API_REPO@\$API_DIGEST"/);
  assert.match(script, /NEW_FRONTEND_IMAGE="\$FRONTEND_REPO@\$FRONTEND_DIGEST"/);
  assert.doesNotMatch(script, /docker (?:compose )?build\b/);
  assert.doesNotMatch(script, /\bprune\b/);
  assert.match(script, /up -d --no-build --no-deps/);
  assert.match(script, /TV_OTA_REPO="ghcr\.io\/\$GHCR_OWNER\/nubarca-tv-ota"/);
  assert.match(script, /pull-publish-tv-ota-image\.sh.*TV_OTA_REPO@\$TV_OTA_DIGEST/);
});

test('TV changes require a CI-built native or OTA artifact', () => {
  assert.match(script, /TV_CHANGED.*TV_DIGEST.*TV_OTA_DIGEST/s);
  assert.match(script, /no CI-built TV native or OTA artifact exists/);
  assert.match(script, /\.nubarca-tv-ota\.source/);
});

test('gates images, Compose, backup, and migrations before replacing release pins', () => {
  const backendVerify = script.indexOf('scripts/verify-production-image.sh');
  const frontendVerify = script.indexOf('scripts/verify-production-frontend-image.sh');
  const composeGate = script.indexOf('COMPOSE_CANDIDATE');
  const backup = script.lastIndexOf('    create_migration_backup');
  const migration = script.lastIndexOf('    run_candidate_migrations');
  const pinReplace = script.indexOf('mv -f docker-compose.release.local.yml.tmp');
  assert.ok(backendVerify >= 0 && backendVerify < composeGate);
  assert.ok(frontendVerify >= 0 && frontendVerify < composeGate);
  assert.ok(composeGate < backup);
  assert.ok(backup < migration);
  assert.ok(migration < pinReplace);
  assert.match(script, /restoring the previous image pins/);
});

test('approved migrations require explicit confirmation and a verified backup', () => {
  assert.match(script, /production-migration-plan\.py/);
  assert.match(script, /BACKUP_DIR must be an absolute non-root path/);
  assert.match(script, /pg_dump/);
  assert.match(script, /gzip -t/);
  assert.match(script, /verify-production-db-backup\.py/);
  assert.match(script, /__EFMigrationsHistory/);
  assert.match(script, /sha256sum/);
  assert.match(script, /"\$NEW_API_IMAGE" db migrate/);
  assert.match(script, /run check and repeat its --confirm-migrations command/);
  assert.doesNotMatch(script, /(?:^|[;&|]\s*|\s)source\s+["']?\$ENV_FILE/m);
});
