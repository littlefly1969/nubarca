import { useEffect, useRef, useState } from 'react';
import { ApiError } from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { getMailRun } from './mailApi';
import { isRunActive, type MailRun } from './types';

export const RUN_POLL_MS = 2000;
const MAX_BACKOFF_MS = 30000;

// Follows one import run while it is queued or running.
//
// One timer at a time, re-armed only after the previous request settled, so a
// slow server never accumulates overlapping requests. Stops for good at a
// terminal status and on unmount. A failed poll doubles the delay (2 s, 4 s,
// 8 s … 30 s) rather than hammering a struggling server; the next success
// resets it. A 401 hands the session back to the auth layer and stops.
export function useRunPolling(
  initial: MailRun,
  onTerminal?: (run: MailRun) => void,
): { run: MailRun; pollError: boolean; replace: (run: MailRun) => void } {
  const { invalidateAuth } = useAuth();
  const [run, setRun] = useState<MailRun>(initial);
  const [pollError, setPollError] = useState(false);
  const onTerminalRef = useRef(onTerminal);
  onTerminalRef.current = onTerminal;

  // Whatever the caller hands in (a cancel/resume response) becomes the new
  // truth and restarts the loop if it is active again.
  const [generation, setGeneration] = useState(0);
  const replace = (next: MailRun) => {
    setRun(next);
    setGeneration((g) => g + 1);
  };

  const runId = run.id;
  const active = isRunActive(run);

  useEffect(() => {
    if (!active) return;
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let controller: AbortController | null = null;
    let delay = RUN_POLL_MS;

    const tick = async () => {
      controller = new AbortController();
      try {
        const next = await getMailRun(runId, controller.signal);
        if (stopped) return;
        setRun(next);
        setPollError(false);
        delay = RUN_POLL_MS;
        if (!isRunActive(next)) {
          onTerminalRef.current?.(next);
          return;
        }
      } catch (err) {
        if (stopped) return;
        if (err instanceof ApiError && err.status === 401) {
          invalidateAuth();
          return;
        }
        setPollError(true);
        delay = Math.min(delay * 2, MAX_BACKOFF_MS);
      }
      if (!stopped) timer = setTimeout(() => void tick(), delay);
    };

    timer = setTimeout(() => void tick(), delay);
    return () => {
      stopped = true;
      if (timer !== undefined) clearTimeout(timer);
      controller?.abort();
    };
  }, [runId, active, generation, invalidateAuth]);

  return { run, pollError, replace };
}
