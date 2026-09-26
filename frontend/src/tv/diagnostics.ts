// What happened to this display, in a form somebody can read the morning after
// a mini-PC went black in the middle of a party.
//
// Deliberately small and deliberately LEAK-FREE: a closed vocabulary of events,
// each with at most a few primitive details that describe the DISPLAY — a
// presentation word, a failure class, a count — never a token, a signed URL, a
// Personal Area code, a guest's greeting or a file name. Nothing is sent
// anywhere. Events go to the console (a kiosk browser started with
// `--enable-logging` keeps them in its log file) and to a short in-memory ring
// that can be read from the devtools console with `__nubarcaTvDiagnostics()`.

export type TvDiagnosticEvent =
  | 'tv.boot'
  | 'tv.session.restored'
  | 'tv.session.revoked'
  | 'tv.pairing.started'
  | 'tv.pairing.completed'
  | 'tv.assignment.changed'
  | 'tv.presentation.changed'
  | 'tv.network.offline'
  | 'tv.network.restored'
  | 'tv.resume'
  | 'tv.wakelock.acquired'
  | 'tv.wakelock.released'
  | 'tv.wakelock.failed'
  | 'tv.fullscreen.failed'
  | 'tv.video.error'
  | 'tv.video.skipped'
  | 'tv.party.gone'
  | 'tv.game.grant.failed'
  | 'tv.personal.preempted';

export type TvDiagnosticDetail = Record<string, string | number | boolean | null>;

export interface TvDiagnosticEntry {
  at: string;
  event: TvDiagnosticEvent;
  detail?: TvDiagnosticDetail;
}

const RING_SIZE = 200;
const ring: TvDiagnosticEntry[] = [];

export function tvLog(event: TvDiagnosticEvent, detail?: TvDiagnosticDetail): void {
  const entry: TvDiagnosticEntry = { at: new Date().toISOString(), event, ...(detail ? { detail } : {}) };
  ring.push(entry);
  if (ring.length > RING_SIZE) ring.splice(0, ring.length - RING_SIZE);
  try {
    // eslint-disable-next-line no-console
    console.info('[nubarca-tv]', event, detail ?? '');
  } catch {
    /* a console that throws is not a reason to stop a display */
  }
}

/** A copy of the recent events, oldest first. */
export function readTvDiagnostics(): TvDiagnosticEntry[] {
  return ring.slice();
}

/** For tests: forget everything. */
export function clearTvDiagnostics(): void {
  ring.length = 0;
}

if (typeof window !== 'undefined') {
  (window as unknown as { __nubarcaTvDiagnostics?: () => TvDiagnosticEntry[] })
    .__nubarcaTvDiagnostics = readTvDiagnostics;
}
