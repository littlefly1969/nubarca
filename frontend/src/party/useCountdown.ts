import { useEffect, useState } from 'react';

/**
 * Whole seconds left until a server-supplied deadline, or null when there is
 * none.
 *
 * The deadline is the server's; only the counting is local. A television that
 * lost the network keeps counting down the last deadline it was told about,
 * which is exactly right — the activity is still running in the room — and the
 * next snapshot corrects it.
 *
 * Pure and exported so the arithmetic is testable without a clock.
 */
export function secondsRemaining(endsAt: string | null | undefined, now: number): number | null {
  if (!endsAt) return null;
  const deadline = Date.parse(endsAt);
  if (Number.isNaN(deadline)) return null;
  return Math.max(0, Math.ceil((deadline - now) / 1000));
}

/** mm:ss, zero-padded. Numeric, so it needs no translation. */
export function formatCountdown(seconds: number): string {
  const whole = Math.max(0, Math.floor(seconds));
  return `${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, '0')}`;
}

export function useCountdown(endsAt: string | null | undefined): number | null {
  const [seconds, setSeconds] = useState(() => secondsRemaining(endsAt, Date.now()));

  useEffect(() => {
    setSeconds(secondsRemaining(endsAt, Date.now()));
    if (!endsAt) return;
    const timer = setInterval(() => setSeconds(secondsRemaining(endsAt, Date.now())), 500);
    return () => clearInterval(timer);
  }, [endsAt]);

  return seconds;
}
