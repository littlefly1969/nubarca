import { spawn } from 'node:child_process';

/** Launch one browser on its persistent profile, with an OS-assigned CDP port. */
export async function launchChromium(executable, profile, { timeoutMs = 45_000, spawnProcess = spawn } = {}) {
  const proc = spawnProcess(executable, [
    '--headless=new', '--disable-gpu', '--no-sandbox', '--no-first-run',
    '--autoplay-policy=no-user-gesture-required',
    `--user-data-dir=${profile}`, '--remote-debugging-port=0',
    '--window-size=1280,720', 'about:blank',
  ], { stdio: ['ignore', 'ignore', 'pipe'] });

  const exited = new Promise((resolve) => {
    proc.once('exit', (code, signal) => resolve({ code, signal }));
    proc.once('error', (error) => resolve({ error }));
  });

  try {
    const url = await new Promise((resolve, reject) => {
      let stderr = '';
      const finish = (error, endpoint) => {
        clearTimeout(timer);
        proc.stderr.removeListener('data', onData);
        proc.removeListener('error', onError);
        proc.removeListener('exit', onExit);
        if (error) reject(new Error(`${error}\n${stderr || '(no Chromium stderr)'}`));
        else resolve(endpoint);
      };
      const onData = (chunk) => {
        stderr = (stderr + chunk.toString()).slice(-8192);
        const endpoint = stderr.match(/DevTools listening on (ws:\/\/[^\s]+)\r?\n/)?.[1];
        // A pipe chunk can end halfway through the URL. Wait for its newline.
        if (endpoint) finish(null, endpoint);
      };
      const onError = (error) => finish(`Chromium could not start: ${error.message}`);
      const onExit = (code, signal) => finish(`Chromium exited before readiness (code=${code}, signal=${signal})`);
      const timer = setTimeout(() => finish(`Chromium did not become ready within ${timeoutMs} ms`), timeoutMs);
      proc.stderr.on('data', onData);
      proc.once('error', onError);
      proc.once('exit', onExit);
    });
    return { proc, exited, url };
  } catch (error) {
    if (proc.exitCode === null && proc.signalCode === null) proc.kill('SIGKILL');
    throw error;
  }
}
