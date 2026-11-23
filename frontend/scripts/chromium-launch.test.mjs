import { strict as assert } from 'node:assert';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import { test } from 'node:test';
import { launchChromium } from './chromium-launch.mjs';

function fakeProcess() {
  const proc = new EventEmitter();
  proc.stderr = new PassThrough();
  proc.exitCode = null;
  proc.signalCode = null;
  proc.kill = (signal) => {
    proc.signalCode = signal;
    queueMicrotask(() => proc.emit('exit', null, signal));
  };
  return proc;
}

test('uses the announced CDP endpoint, even when the pipe splits the URL', async () => {
  const proc = fakeProcess();
  const ready = launchChromium('chrome', '/persistent-display', { spawnProcess: (executable, args) => {
    assert.equal(executable, 'chrome');
    assert.ok(args.includes('--remote-debugging-port=0'));
    assert.ok(args.includes('--user-data-dir=/persistent-display'));
    return proc;
  } });
  const endpoint = 'ws://127.0.0.1:42791/devtools/browser/display-id';
  let resolved = false;
  ready.then(() => { resolved = true; });
  proc.stderr.write(`diagnostic\nDevTools listening on ${endpoint.slice(0, -5)}`);
  await Promise.resolve();
  assert.equal(resolved, false);
  proc.stderr.write(`${endpoint.slice(-5)}\n`);
  const browser = await ready;
  assert.equal(browser.url, endpoint);
  proc.kill('SIGTERM');
  assert.deepEqual(await browser.exited, { code: null, signal: 'SIGTERM' });
});

test('restarting preserves the display profile and obtains a fresh endpoint', async () => {
  for (const port of [42791, 42792]) {
    const proc = fakeProcess();
    const ready = launchChromium('chrome', '/same-display', { spawnProcess: (_exe, args) => {
      assert.ok(args.includes('--user-data-dir=/same-display'));
      return proc;
    } });
    proc.stderr.write(`DevTools listening on ws://127.0.0.1:${port}/devtools/browser/session\n`);
    assert.match((await ready).url, new RegExp(`:${port}/`));
    proc.kill('SIGTERM');
  }
});

test('reports early browser exit with the actual diagnostic', async () => {
  const proc = fakeProcess();
  const ready = launchChromium('chrome', '/display', { spawnProcess: () => proc });
  proc.stderr.write('profile could not be opened\n');
  proc.exitCode = 1;
  proc.emit('exit', 1, null);
  await assert.rejects(ready, /exited before readiness.*code=1[\s\S]*profile could not be opened/);
});

test('reports spawn errors without waiting for the readiness timeout', async () => {
  const proc = fakeProcess();
  const ready = launchChromium('missing-chrome', '/display', { spawnProcess: () => proc });
  proc.emit('error', new Error('ENOENT'));
  await assert.rejects(ready, /could not start: ENOENT/);
});

test('a readiness timeout terminates the browser and retains bounded diagnostics', async () => {
  const proc = fakeProcess();
  const ready = launchChromium('chrome', '/display', { timeoutMs: 20, spawnProcess: () => proc });
  proc.stderr.write('x'.repeat(20_000) + '\nlast diagnostic\n');
  await assert.rejects(ready, (error) => {
    assert.match(error.message, /did not become ready within 20 ms/);
    assert.match(error.message, /last diagnostic/);
    assert.ok(error.message.length < 8400);
    return true;
  });
  assert.equal(proc.signalCode, 'SIGKILL');
});
