import assert from 'node:assert/strict';
import test from 'node:test';
import { KeepAwakeController, type KeepAwakeDriver } from './keepAwake.ts';

// Records every activate/deactivate the controller issues so we can assert on
// both the count (no duplicate activation, no leaked/spurious deactivation) and
// the ordering.
function recordingDriver(): { driver: KeepAwakeDriver; log: string[] } {
  const log: string[] = [];
  const driver: KeepAwakeDriver = {
    activate: (tag) => log.push(`activate:${tag}`),
    deactivate: (tag) => log.push(`deactivate:${tag}`),
  };
  return { driver, log };
}

test('an active viewer enables keep-awake exactly once', () => {
  const { driver, log } = recordingDriver();
  const c = new KeepAwakeController(driver, 'viewer');
  c.sync(true);
  assert.deepEqual(log, ['activate:viewer']);
  assert.equal(c.isHeld, true);
});

test('leaving the viewer disables keep-awake', () => {
  const { driver, log } = recordingDriver();
  const c = new KeepAwakeController(driver, 'viewer');
  c.sync(true);
  c.sync(false);
  assert.deepEqual(log, ['activate:viewer', 'deactivate:viewer']);
  assert.equal(c.isHeld, false);
});

test('the grid (never active) never enables keep-awake', () => {
  const { driver, log } = recordingDriver();
  const c = new KeepAwakeController(driver, 'grid');
  // A grid mounts without ever going active.
  c.sync(false);
  c.release();
  assert.deepEqual(log, []);
  assert.equal(c.isHeld, false);
});

test('staying active never re-activates (no duplicate tags)', () => {
  const { driver, log } = recordingDriver();
  const c = new KeepAwakeController(driver, 'viewer');
  c.sync(true);
  c.sync(true);
  c.sync(true);
  assert.deepEqual(log, ['activate:viewer']);
});

test('lock / revocation releases a held lock via release()', () => {
  const { driver, log } = recordingDriver();
  const c = new KeepAwakeController(driver, 'viewer');
  c.sync(true);
  // Personal Area lock / pairing revocation unmounts the viewer → release().
  c.release();
  assert.deepEqual(log, ['activate:viewer', 'deactivate:viewer']);
  assert.equal(c.isHeld, false);
});

test('release() on a never-active controller emits no spurious deactivate', () => {
  const { driver, log } = recordingDriver();
  const c = new KeepAwakeController(driver, 'viewer');
  c.release();
  c.release();
  assert.deepEqual(log, []);
});

test('release() is idempotent — a released lock is never double-deactivated', () => {
  const { driver, log } = recordingDriver();
  const c = new KeepAwakeController(driver, 'viewer');
  c.sync(true);
  c.release();
  c.release();
  assert.deepEqual(log, ['activate:viewer', 'deactivate:viewer']);
});

test('toggling active off then on re-activates (details sheet close → viewer)', () => {
  const { driver, log } = recordingDriver();
  const c = new KeepAwakeController(driver, 'viewer');
  c.sync(true);   // slideshow visible
  c.sync(false);  // details sheet opened
  c.sync(true);   // details sheet closed → slideshow visible again
  assert.deepEqual(log, ['activate:viewer', 'deactivate:viewer', 'activate:viewer']);
  assert.equal(c.isHeld, true);
});

test('Party and Personal viewers behave identically for the same lifecycle', () => {
  const runLifecycle = (tag: string): string[] => {
    const { driver, log } = recordingDriver();
    const c = new KeepAwakeController(driver, tag);
    c.sync(true);   // viewer opens
    c.sync(true);   // navigation within the viewer (still active)
    c.release();    // viewer exits
    return log.map((entry) => entry.replace(`:${tag}`, ''));
  };
  const party = runLifecycle('party-viewer');
  const personal = runLifecycle('personal-viewer');
  assert.deepEqual(party, ['activate', 'deactivate']);
  assert.deepEqual(personal, party);
});

test('distinct tags never cross-release each other (two concurrent controllers)', () => {
  const { driver, log } = recordingDriver();
  const a = new KeepAwakeController(driver, 'a');
  const b = new KeepAwakeController(driver, 'b');
  a.sync(true);
  b.sync(true);
  a.release();
  // b must still hold its own lock — a's release only touched tag 'a'.
  assert.equal(b.isHeld, true);
  assert.deepEqual(log, ['activate:a', 'activate:b', 'deactivate:a']);
});
