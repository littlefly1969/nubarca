import { useCallback, useEffect, useReducer, useRef, useState, type ReactNode } from 'react';
import {
  ApiError,
  getTvPersonalStatus,
  getTvSession,
  heartbeatTvSession,
  lockTvPersonal,
  type TvSessionStatus,
} from '@nubarca/api-client';
import { toLanguage, useI18n } from '../i18n';
import { TvBrowser } from '../pages/TvBrowser';
import { TvBeautyLab } from '../pages/TvBeautyLab';
import { TvCodeGate, TvModeSelect, TvPersonalArea } from '../pages/TvPairedExperience';
import {
  CONTROL_POLL_MS, backoffMs, shouldHeartbeat, toAssignmentView,
} from './semantics/assignmentView';
import {
  admissionEvents, flowEffects, initialFlowState, isAssignedPartyState, isPersonalState,
  tvFlowReducer, type TvFlowEvent, type TvFlowState,
} from './semantics/flow';
import { useDisplayPlatform } from './platform/displayPlatform';
import {
  useFullscreen, useIdle, useLatest, usePageVisible, useResume, useWakeLock,
} from './platform/hooks';
import { usePoll } from './platform/usePoll';
import { tvLog } from './diagnostics';
import { TvPairingScreen, type PairingNotice } from './TvPairingScreen';
import { TvAssignedSlideshow } from './party/TvAssignedSlideshow';
import { TvAssignedGame } from './party/TvAssignedGame';
import { TvPartySurface } from './party/TvPartyOverlays';
import './tvDisplay.css';

// A BROWSER RUNNING /tv IS A NUBARCA DISPLAY.
//
// Not a preview and not a fallback: once paired it is a television like any
// other, listed in the owner's TV Devices, assigned, revoked and taken over
// exactly as a Fire TV is — because it reads the same control plane with the
// same rules. The state machine is the app's (semantics/flow.ts, held to the
// native one by nativeParity.test.ts); what this component adds is only what a
// browser has to do for itself:
//
//   BOOT — `GET /api/tv/session` decides. A 401 is the only answer that sends
//     the display to pairing. No network, a timeout, a server restarting: the
//     display keeps its pairing and keeps asking on a capped backoff. The first
//     answer already carries the assignment, so a display assigned to a party
//     starts IN the party — never on the mode selector first.
//
//   CONTROL PLANE — while visible, one read every five seconds, single-flight,
//     a heartbeat once a minute. Every read is the assignment and the session
//     check at once: the owner's decision takes the screen from whatever is on
//     it (locking a Personal Area on the way), and a revoked session tears
//     everything down.
//
//   RESUME — sleep, a switched-off screen, a hidden tab, a frozen page: when
//     the page runs again the wake lock is asked for again and the control
//     plane and the surface on screen are re-read at once, not at the next
//     interval, so nothing shows a pre-standby party for minutes.
//
//   APPLIANCE — the screen is held awake where the browser allows it, the
//     cursor hides, fullscreen is one click away and never a condition for the
//     display to work.

/** No pointer or key for this long hides the cursor and the display's own buttons. */
export const CURSOR_IDLE_MS = 3_000;
/** A boot read that has not answered in this long is a failure to retry, not a wait. */
const BOOT_TIMEOUT_MS = 12_000;
/** How often the mode selector re-checks that the association is complete. */
const ASSOCIATION_CHECK_MS = 60_000;

function statusOf(error: unknown): number | null {
  return error instanceof ApiError ? error.status : null;
}

function timeoutSignal(ms: number): { signal: AbortSignal; done: () => void } {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), ms);
  return { signal: controller.signal, done: () => clearTimeout(timer) };
}

export function TvDisplay() {
  const { t, setLanguage } = useI18n();
  const platform = useDisplayPlatform();
  const [flow, rawDispatch] = useReducer(tvFlowReducer, initialFlowState);
  const flowRef = useRef<TvFlowState>(flow);
  // The Personal Area unlock grant: in memory, here, and nowhere else.
  const [grant, setGrant] = useState<string | null>(null);
  const [pairingNotice, setPairingNotice] = useState<PairingNotice>(null);
  const [bootFailures, setBootFailures] = useState(0);
  const [online, setOnline] = useState(true);
  const [resumeEpoch, setResumeEpoch] = useState(0);
  const visible = usePageVisible();

  // Every event that can take a screen away from somebody goes through here,
  // so the teardown it requires is decided by the pure flowEffects and cannot
  // be forgotten at a call site.
  const dispatch = useCallback((event: TvFlowEvent) => {
    const from = flowRef.current;
    const next = tvFlowReducer(from, event);
    const effects = flowEffects(from, event);
    if (effects.revokeGrant) {
      if (isPersonalState(from) && isAssignedPartyState(next)) tvLog('tv.personal.preempted');
      setGrant(null);
      void lockTvPersonal().catch(() => { /* the grant is already gone from memory */ });
    }
    if (effects.dropGrant) setGrant(null);
    if (next !== from) {
      if (next.name === 'pairing') {
        if (next.incomplete) setPairingNotice('incomplete');
        else if (from.name !== 'loading') {
          tvLog('tv.session.revoked');
          setPairingNotice('revoked');
        } else setPairingNotice(null);
      }
      if (isAssignedPartyState(next) || isAssignedPartyState(from)) {
        const fromKey = isAssignedPartyState(from) ? from.party.key : null;
        const nextKey = isAssignedPartyState(next) ? next.party.key : null;
        if (fromKey !== nextKey) tvLog('tv.assignment.changed', { to: nextKey ? 'party' : 'general' });
        tvLog('tv.presentation.changed', { from: from.name, to: next.name });
      }
    }
    flowRef.current = next;
    rawDispatch(event);
  }, []);

  const adoptLanguage = useCallback((language: string) => {
    const lang = toLanguage(language);
    if (lang) setLanguage(lang, { persistLocal: false });
  }, [setLanguage]);

  // ADMISSION: the one door from "this session is valid" to the first screen,
  // for a reload and a completed pairing alike. The incomplete-association
  // check runs BEFORE the first screen is chosen, so such a session never
  // passes through a party on its way to the recovery.
  const admissionRef = useRef(0);
  const admissionTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const admit = useCallback((session: TvSessionStatus) => {
    const admission = ++admissionRef.current;
    let attempt = 0;
    const check = () => {
      const { signal, done } = timeoutSignal(BOOT_TIMEOUT_MS);
      getTvPersonalStatus(signal)
        .then((status) => {
          if (admissionRef.current !== admission) return;
          adoptLanguage(session.language);
          const view = toAssignmentView(session.assignment);
          tvLog('tv.session.restored', { presentation: view.presentation });
          setBootFailures(0);
          for (const event of admissionEvents(view, status.pinConfigured)) dispatch(event);
        })
        .catch((error: unknown) => {
          if (admissionRef.current !== admission) return;
          if (statusOf(error) === 401) {
            dispatch({ type: 'SESSION_INVALID' });
            return;
          }
          setBootFailures((n) => n + 1);
          admissionTimer.current = setTimeout(check, backoffMs(attempt++));
        })
        .finally(done);
    };
    check();
  }, [adoptLanguage, dispatch]);

  // BOOT: validate the session this browser holds. Only a 401 unpairs. Runs
  // once per mount: adopting the owner's language re-renders the provider, and
  // that must never re-run the boot.
  const bootRetry = useRef<() => void>(() => {});
  const admitRef = useLatest(admit);
  useEffect(() => {
    tvLog('tv.boot');
    let cancelled = false;
    let inFlight = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let attempt = 0;
    const validate = () => {
      if (cancelled || inFlight) return;
      if (timer) clearTimeout(timer);
      inFlight = true;
      const { signal, done } = timeoutSignal(BOOT_TIMEOUT_MS);
      getTvSession(signal)
        .then((session) => {
          if (!cancelled) admitRef.current(session);
          bootRetry.current = () => {};
        })
        .catch((error: unknown) => {
          if (cancelled) return;
          if (statusOf(error) === 401) {
            bootRetry.current = () => {};
            dispatch({ type: 'SESSION_INVALID' });
            return;
          }
          setBootFailures((n) => n + 1);
          timer = setTimeout(validate, backoffMs(attempt++));
        })
        .finally(() => {
          done();
          inFlight = false;
        });
    };
    bootRetry.current = validate;
    validate();
    return () => {
      cancelled = true;
      if (timer) clearTimeout(timer);
      if (admissionTimer.current) clearTimeout(admissionTimer.current);
      admissionRef.current += 1;
      bootRetry.current = () => {};
    };
  }, [admitRef, dispatch]);

  const onPaired = useCallback((session: TvSessionStatus) => admit(session), [admit]);
  const onSessionInvalid = useCallback(() => dispatch({ type: 'SESSION_INVALID' }), [dispatch]);
  const onAssociationIncomplete = useCallback(() => dispatch({ type: 'ASSOCIATION_INCOMPLETE' }), [dispatch]);

  // THE CONTROL PLANE.
  const sessionLive = flow.name !== 'loading' && flow.name !== 'pairing';
  const lastHeartbeat = useRef<number | null>(null);
  const failures = useRef(0);
  const refreshControlPlane = usePoll<TvSessionStatus>({
    enabled: sessionLive && visible,
    intervalMs: CONTROL_POLL_MS,
    refreshKey: resumeEpoch,
    read: (signal) => {
      const now = platform.now();
      const beat = shouldHeartbeat(lastHeartbeat.current, now);
      return (beat ? heartbeatTvSession(signal) : getTvSession(signal)).then((session) => {
        if (beat) lastHeartbeat.current = now;
        return session;
      });
    },
    onValue: (session) => {
      if (failures.current > 0) tvLog('tv.network.restored', { failures: failures.current });
      failures.current = 0;
      setOnline(true);
      dispatch({ type: 'ASSIGNMENT', view: toAssignmentView(session.assignment) });
    },
    onError: (error, consecutive) => {
      if (statusOf(error) === 401) {
        dispatch({ type: 'SESSION_INVALID' });
        return 'stop';
      }
      failures.current = consecutive;
      if (consecutive === 1) tvLog('tv.network.offline', { status: statusOf(error) });
      setOnline(false);
      return undefined;
    },
    retryDelayMs: (consecutive) => Math.max(CONTROL_POLL_MS, backoffMs(consecutive - 1)),
  });

  // RESUME: everything on screen asks again, now.
  const flowNameRef = useLatest(flow.name);
  useResume(() => {
    if (flowNameRef.current === 'loading') bootRetry.current();
    setResumeEpoch((n) => n + 1);
  });

  // The mode selector makes no calls of its own, so an incomplete association
  // would sit there looking usable: check on entry and every minute.
  useEffect(() => {
    if (flow.name !== 'mode') return;
    let cancelled = false;
    const check = () => {
      getTvPersonalStatus()
        .then((status) => { if (!cancelled && !status.pinConfigured) onAssociationIncomplete(); })
        .catch((error: unknown) => { if (!cancelled && statusOf(error) === 401) onSessionInvalid(); });
    };
    check();
    const timer = setInterval(check, ASSOCIATION_CHECK_MS);
    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, [flow.name, onAssociationIncomplete, onSessionInvalid]);

  // THE APPLIANCE.
  useWakeLock(sessionLive);
  const idle = useIdle(CURSOR_IDLE_MS);
  const fullscreen = useFullscreen();
  const assigned = isAssignedPartyState(flow);

  // BACK on an assigned party never leaves it — the owner decides that. The
  // most it may do is leave fullscreen, and the browser already does that
  // for Escape.
  const assignedBack = useCallback(() => {
    if (platform.isFullscreen()) void platform.exitFullscreen();
  }, [platform]);
  useEffect(() => {
    if (flow.name !== 'partyGame' && flow.name !== 'partyUnavailable') return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (platform.mapKey(event) !== 'back') return;
      event.preventDefault();
      assignedBack();
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [flow.name, platform, assignedBack]);

  const onPartyGone = useCallback(() => {
    dispatch({ type: 'PARTY_CONTENT_GONE' });
    refreshControlPlane();
  }, [dispatch, refreshControlPlane]);

  const personalForbidden = useCallback((error: ApiError) => {
    const body = error.body as { error?: string } | null;
    dispatch({ type: 'LOCK', reason: body?.error === 'pin_changed' ? 'pinChanged' : undefined });
  }, [dispatch]);

  const personalError = useCallback((error: unknown): boolean => {
    if (error instanceof ApiError && error.status === 401) { onSessionInvalid(); return true; }
    if (error instanceof ApiError && error.status === 403) { personalForbidden(error); return true; }
    return false;
  }, [onSessionInvalid, personalForbidden]);

  const chrome = sessionLive && (
    <div className={`tvd-chrome${idle ? ' tvd-chrome--idle' : ''}`}>
      {!online && <span className="tvd-offline" role="status" data-testid="tv-reconnecting">{t('tv.reconnecting')}</span>}
      {fullscreen.supported && !fullscreen.active && (
        <button type="button" className="tvd-fullscreen" data-testid="tv-fullscreen" onClick={fullscreen.enter}>
          ⛶ {t('tv.fullscreen')}
        </button>
      )}
    </div>
  );

  let surface: ReactNode;
  switch (flow.name) {
    case 'loading':
      surface = (
        <main className="tv-page">
          <div className="tv-card" data-testid="tv-connecting">
            <h1>{t('tv.title')}</h1>
            <p role="status">{bootFailures > 0 ? t('tv.reconnecting') : t('tv.connecting')}</p>
          </div>
        </main>
      );
      break;
    case 'pairing':
      surface = <TvPairingScreen notice={pairingNotice} onPaired={onPaired} />;
      break;
    case 'partySlideshow':
      surface = flow.party.albumId === null ? (
        <TvPartySurface albumName={flow.party.albumName} message={t('tv.partyUnavailable')} testId="tv-party-unavailable" />
      ) : (
        <TvAssignedSlideshow
          key={flow.party.key}
          albumId={flow.party.albumId}
          albumName={flow.party.albumName}
          onBack={assignedBack}
          onGone={onPartyGone}
          onSessionInvalid={onSessionInvalid}
          refreshKey={resumeEpoch}
        />
      );
      break;
    case 'partyGame':
      surface = (
        <TvAssignedGame
          key={flow.party.key}
          albumName={flow.party.albumName}
          onSessionInvalid={onSessionInvalid}
          onRequestAssignment={refreshControlPlane}
          refreshKey={resumeEpoch}
        />
      );
      break;
    case 'partyUnavailable':
      // Fail CLOSED: no other party, no general experience, no stale frame.
      surface = (
        <TvPartySurface
          key={flow.party.key}
          albumName={flow.party.albumName}
          message={t('tv.partyUnavailable')}
          testId="tv-party-unavailable"
        />
      );
      break;
    default:
      surface = (
        <main className="tv-page tv-page-browse">
          <h1 className="tv-browse-title">{t('tv.title')}</h1>
          {renderGeneral()}
        </main>
      );
  }

  function renderGeneral(): ReactNode {
    switch (flow.name) {
      case 'mode':
        return (
          <TvModeSelect
            notice={flow.notice}
            onChooseParty={() => dispatch({ type: 'CHOOSE_PARTY' })}
            onChoosePersonal={() => dispatch({ type: 'CHOOSE_PERSONAL' })}
            onChooseBeautyLab={() => dispatch({ type: 'CHOOSE_BEAUTY_LAB' })}
          />
        );
      case 'party':
        return (
          <TvBrowser
            onSessionInvalid={onSessionInvalid}
            onExitRoot={() => dispatch({ type: 'PARTY_EXIT' })}
            refreshKey={resumeEpoch}
          />
        );
      case 'pin':
        return (
          <TvCodeGate
            onBack={() => dispatch({ type: 'PIN_CANCELLED' })}
            onUnlocked={(token, displayName, galleryAvailable) => {
              setGrant(token);
              dispatch({ type: 'UNLOCKED', home: { displayName, galleryAvailable } });
            }}
            onSessionInvalid={onSessionInvalid}
            onAssociationIncomplete={onAssociationIncomplete}
          />
        );
      case 'personalHome':
      case 'personalLibrary':
        if (grant === null) return null;
        return (
          <TvPersonalArea
            mode={{
              kind: flow.name === 'personalLibrary' ? 'galleryShell' : 'personalHome',
              grant,
              displayName: flow.home.displayName,
              galleryAvailable: flow.home.galleryAvailable,
            }}
            onOpenGallery={() => dispatch({ type: 'OPEN_LIBRARY' })}
            onGalleryBack={() => dispatch({ type: 'LIBRARY_BACK' })}
            onLock={() => dispatch({ type: 'LOCK' })}
            onForbidden={personalForbidden}
            onSessionInvalid={onSessionInvalid}
          />
        );
      case 'beautyLab':
        if (grant === null) return null;
        return (
          <TvBeautyLab
            grant={grant}
            onBack={() => dispatch({ type: 'LOCK' })}
            onPersonalError={personalError}
          />
        );
      default:
        return null;
    }
  }

  return (
    <div
      className={`tvd-root${assigned ? ' tvd-root--assigned' : ''}${idle && sessionLive ? ' tvd-root--idle' : ''}`}
      data-testid="tv-display"
      data-flow={flow.name}
    >
      {surface}
      {chrome}
    </div>
  );
}
