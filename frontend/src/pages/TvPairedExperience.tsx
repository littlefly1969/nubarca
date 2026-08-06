import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  getTvPersonalHome,
  getTvPersonalStatus,
  lockTvPersonal,
  unlockTvPersonal,
} from '@nubarca/api-client';
import { TvBrowser } from './TvBrowser';
import { TvPersonalGallery } from './TvPersonalGallery';
import { TvBeautyLab } from './TvBeautyLab';
import { useI18n } from '../i18n';

// Paired /tv experience: the explicit mode state machine.
//
//   modeSelect → party            (no PIN — the existing TV experience)
//   modeSelect → pin → personalHome → galleryShell
//   BACK: galleryShell → personalHome → (LOCK) → modeSelect; pin → modeSelect;
//         party root → modeSelect (no PIN to come back).
//
// The unlock grant lives ONLY in component state (application memory): a page
// refresh, tab close, or leaving the paired state unmounts it — every start
// is locked. It is never written to localStorage/sessionStorage/cookies/URLs.
// Any 401 (pairing revoked/expired) clears local state and bubbles to the
// parent, which returns to the pairing screen.
//
// PIN change while unlocked: the server answers 403 {error:"pin_changed"} for
// the stale grant — the client locks immediately and shows the "PIN was
// changed" notice on the mode selector (the pairing itself stays valid).
//
// Invariant recovery: a paired session whose owner has NO PIN can no longer be
// produced by the atomic pairing flow — encountering pinConfigured=false means
// legacy/corrupted state. The client reports it up (association incomplete)
// instead of showing a PIN pad that can never succeed or quietly running Party.

type ModeNotice = 'pinChanged' | null;

// The PIN screen is a shared gate: after a successful unlock it navigates to the
// ORIGINAL requested target (Personal Area or Beauty Lab). Both reuse the SAME
// PIN + in-memory grant — Beauty Lab never mints a second PIN or grant type.
type UnlockTarget = 'personal' | 'beautyLab';

type Mode =
  | { kind: 'modeSelect'; notice: ModeNotice }
  | { kind: 'party' }
  | { kind: 'pin'; target: UnlockTarget }
  | { kind: 'personalHome'; grant: string; displayName: string; galleryAvailable: boolean }
  | { kind: 'galleryShell'; grant: string; displayName: string; galleryAvailable: boolean }
  | { kind: 'beautyLab'; grant: string; displayName: string };

const PIN_LENGTH = 6;
// While inside the Personal Area, re-validate the grant on this cadence so a
// PIN change (or server-side revocation) evicts the TV promptly, not merely on
// the next user action.
const PERSONAL_REVALIDATE_MS = 15_000;

interface TvPairedExperienceProps {
  onSessionInvalid: () => void;
  // Paired session whose owner has no Personal Area PIN (legacy/corrupted
  // state): the parent clears local TV state and returns to pairing with the
  // "pairing is incomplete" message.
  onAssociationIncomplete: () => void;
}

export function TvPairedExperience({
  onSessionInvalid,
  onAssociationIncomplete,
}: TvPairedExperienceProps) {
  const { t } = useI18n();
  // Every mount (page load, refresh, re-pair) starts on mode selection, locked.
  const [mode, setMode] = useState<Mode>({ kind: 'modeSelect', notice: null });

  // Pairing revoked/expired: drop every bit of personal state BEFORE bubbling
  // up, so no personal UI can survive under a dead session.
  const sessionInvalid = useCallback(() => {
    setMode({ kind: 'modeSelect', notice: null });
    onSessionInvalid();
  }, [onSessionInvalid]);

  const associationIncomplete = useCallback(() => {
    setMode({ kind: 'modeSelect', notice: null });
    onAssociationIncomplete();
  }, [onAssociationIncomplete]);

  // Validate the association once per mount: atomic pairing guarantees a PIN,
  // so pinConfigured=false is the legacy/corrupted state → recovery, before
  // either mode can be used under an invalid association.
  useEffect(() => {
    const ctrl = new AbortController();
    getTvPersonalStatus(ctrl.signal)
      .then((status) => {
        if (!status.pinConfigured) associationIncomplete();
      })
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 401) sessionInvalid();
        // Transient error: the next interaction re-checks server-side anyway.
      });
    return () => ctrl.abort();
  }, [associationIncomplete, sessionInvalid]);

  const openMode = useCallback(async (target: UnlockTarget) => {
    setMode({ kind: 'pin', target });
    try {
      const status = await getTvPersonalStatus();
      if (!status.pinConfigured) associationIncomplete();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) sessionInvalid();
      // Transient error: the keypad still works — unlock re-checks server-side.
    }
  }, [associationIncomplete, sessionInvalid]);

  // After a successful unlock, navigate to the ORIGINAL requested target.
  const unlocked = useCallback(
    (target: UnlockTarget, grant: string, displayName: string, galleryAvailable: boolean) => {
      setMode(
        target === 'beautyLab'
          ? { kind: 'beautyLab', grant, displayName }
          : { kind: 'personalHome', grant, displayName, galleryAvailable },
      );
    },
    [],
  );

  // Leaving the Personal Area locks IMMEDIATELY: the grant is dropped from
  // memory first, then revoked server-side (best-effort — the server's bounded
  // grant lifetime covers a lost lock call). Idempotent by design.
  const lock = useCallback((notice: ModeNotice = null) => {
    setMode({ kind: 'modeSelect', notice });
    void lockTvPersonal().catch(() => { /* grant already dropped locally */ });
  }, []);

  // Shared 403 handling for personal calls: a stale-generation grant means the
  // owner changed the PIN → lock with the notice; any other 403 → plain lock.
  const personalForbidden = useCallback((err: ApiError) => {
    const body = err.body as { error?: string } | null;
    lock(body?.error === 'pin_changed' ? 'pinChanged' : null);
  }, [lock]);

  if (mode.kind === 'party') {
    return (
      <TvBrowser
        onSessionInvalid={sessionInvalid}
        onExitRoot={() => setMode({ kind: 'modeSelect', notice: null })}
      />
    );
  }

  if (mode.kind === 'pin') {
    const target = mode.target;
    return (
      <TvPinEntry
        onBack={() => setMode({ kind: 'modeSelect', notice: null })}
        onUnlocked={(grant, displayName, galleryAvailable) =>
          unlocked(target, grant, displayName, galleryAvailable)}
        onSessionInvalid={sessionInvalid}
      />
    );
  }

  if (mode.kind === 'beautyLab') {
    return (
      <TvBeautyLab
        grant={mode.grant}
        // BACK from the Beauty Lab root LOCKS and returns to mode selection —
        // exactly the Personal Area security behaviour.
        onBack={() => lock()}
        onPersonalError={(err) => {
          if (err instanceof ApiError && err.status === 401) { sessionInvalid(); return true; }
          if (err instanceof ApiError && err.status === 403) { personalForbidden(err); return true; }
          return false;
        }}
      />
    );
  }

  if (mode.kind === 'personalHome' || mode.kind === 'galleryShell') {
    return (
      <TvPersonalArea
        mode={mode}
        onOpenGallery={() => setMode({ ...mode, kind: 'galleryShell' })}
        onGalleryBack={() => setMode({ ...mode, kind: 'personalHome' })}
        onLock={() => lock()}
        onForbidden={personalForbidden}
        onSessionInvalid={sessionInvalid}
      />
    );
  }

  // Mode selection: shown on EVERY start; never auto-reopens the last mode.
  return (
    <div
      className="tv-mode-select"
      onKeyDown={(e) => {
        // BACK on the mode selector must never enter a mode.
        if (e.key === 'Backspace' || e.key === 'Escape') e.preventDefault();
      }}
    >
      <h2 className="tv-mode-title">{t('tv.modeTitle')}</h2>
      {mode.notice === 'pinChanged' && (
        <p role="status" data-testid="tv-pin-changed-notice">{t('tv.pinChangedNotice')}</p>
      )}
      <div className="tv-mode-options">
        <button
          type="button"
          className="tv-mode-option"
          autoFocus
          data-testid="tv-mode-party"
          onClick={() => setMode({ kind: 'party' })}
        >
          {t('tv.modeParty')}
        </button>
        <button
          type="button"
          className="tv-mode-option"
          data-testid="tv-mode-personal"
          onClick={() => void openMode('personal')}
        >
          {t('tv.modePersonal')} <span aria-hidden="true">🔒</span>
        </button>
        <button
          type="button"
          className="tv-mode-option"
          data-testid="tv-mode-beauty-lab"
          onClick={() => void openMode('beautyLab')}
        >
          {t('tv.modeBeautyLab')} <span aria-hidden="true">🔒</span>
        </button>
      </div>
    </div>
  );
}

// 6-digit PIN entry. Keypad buttons for D-pad/arrow navigation plus direct
// digit keys; Backspace deletes the last digit, and with no digits returns to
// mode selection. Auto-submits at 6 digits. The entered digits live only in
// component state and are cleared on failure and unmount; they are never
// logged, persisted, or put in a URL.
function TvPinEntry({
  onBack,
  onUnlocked,
  onSessionInvalid,
}: {
  onBack: () => void;
  onUnlocked: (grant: string, displayName: string, galleryAvailable: boolean) => void;
  onSessionInvalid: () => void;
}) {
  const { t } = useI18n();
  const [digits, setDigits] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const firstKeyRef = useRef<HTMLButtonElement>(null);

  const submit = useCallback(async (pin: string) => {
    setBusy(true);
    try {
      const grant = await unlockTvPersonal(pin);
      const home = await getTvPersonalHome(grant.unlockToken);
      onUnlocked(grant.unlockToken, home.displayName, home.galleryAvailable);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onSessionInvalid();
        return;
      }
      setDigits('');
      setError(err instanceof ApiError && err.status === 429
        ? t('tv.pinThrottled')
        : t('tv.pinError'));
      setBusy(false);
      // Predictable focus after a failure: back to the first keypad key.
      firstKeyRef.current?.focus();
    }
  }, [onUnlocked, onSessionInvalid, t]);

  const addDigit = useCallback((d: string) => {
    if (busy) return;
    setError(null);
    setDigits((cur) => {
      const next = (cur + d).slice(0, PIN_LENGTH);
      if (next.length === PIN_LENGTH) void submit(next);
      return next;
    });
  }, [busy, submit]);

  const deleteDigit = useCallback(() => {
    if (busy) return;
    setDigits((cur) => cur.slice(0, -1));
  }, [busy]);

  const onKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
    if (/^[0-9]$/.test(e.key)) {
      e.preventDefault();
      addDigit(e.key);
    } else if (e.key === 'Backspace') {
      e.preventDefault();
      if (digits.length > 0) deleteDigit();
      else onBack();
    } else if (e.key === 'Escape') {
      e.preventDefault();
      onBack();
    }
  };

  useEffect(() => {
    firstKeyRef.current?.focus();
  }, []);

  return (
    <div className="tv-pin-entry" onKeyDown={onKeyDown} data-testid="tv-pin-entry">
      <h2>{t('tv.pinTitle')}</h2>
      <div className="tv-pin-dots" aria-label={t('tv.pinTitle')} aria-live="polite">
        {Array.from({ length: PIN_LENGTH }, (_, i) => (
          <span key={i} className={`tv-pin-dot${i < digits.length ? ' tv-pin-dot-filled' : ''}`}>
            {i < digits.length ? '●' : '○'}
          </span>
        ))}
      </div>
      <div className="tv-pin-keypad">
        {['1', '2', '3', '4', '5', '6', '7', '8', '9'].map((d, i) => (
          <button
            key={d}
            type="button"
            ref={i === 0 ? firstKeyRef : undefined}
            disabled={busy}
            onClick={() => addDigit(d)}
          >
            {d}
          </button>
        ))}
        <button type="button" disabled={busy} onClick={deleteDigit} aria-label={t('tv.pinDelete')}>
          ⌫
        </button>
        <button type="button" disabled={busy} onClick={() => addDigit('0')}>0</button>
        <button type="button" disabled={busy} onClick={onBack} aria-label={t('tv.personalBack')}>
          ←
        </button>
      </div>
      <p className="tv-pin-hint">{t('tv.pinHint')}</p>
      {error && <p role="alert" data-testid="tv-pin-error">{error}</p>}
    </div>
  );
}

// Personal Area (home + gallery shell). One component so the grant
// re-validation poll spans both screens: every PERSONAL_REVALIDATE_MS the
// grant is checked server-side — a PIN change or revocation evicts the TV
// promptly (403 → lock, with the pin_changed notice when reported).
function TvPersonalArea({
  mode,
  onOpenGallery,
  onGalleryBack,
  onLock,
  onForbidden,
  onSessionInvalid,
}: {
  mode: { kind: 'personalHome' | 'galleryShell'; grant: string; displayName: string; galleryAvailable: boolean };
  onOpenGallery: () => void;
  onGalleryBack: () => void;
  onLock: () => void;
  onForbidden: (err: ApiError) => void;
  onSessionInvalid: () => void;
}) {
  const { t } = useI18n();

  const handlePersonalError = useCallback((err: unknown): boolean => {
    if (err instanceof ApiError && err.status === 401) {
      onSessionInvalid();
      return true;
    }
    if (err instanceof ApiError && err.status === 403) {
      onForbidden(err);
      return true;
    }
    return false;
  }, [onForbidden, onSessionInvalid]);

  // Grant re-validation poll — the ONE Personal Area-level lifecycle, spanning
  // the home AND the gallery (the gallery adds no timers of its own; its API
  // calls also re-validate the grant server-side on every request).
  useEffect(() => {
    const timer = window.setInterval(() => {
      getTvPersonalHome(mode.grant).catch((err: unknown) => {
        handlePersonalError(err);
        // Transient errors: keep the current screen; the next poll retries.
      });
    }, PERSONAL_REVALIDATE_MS);
    return () => window.clearInterval(timer);
  }, [mode.grant, handlePersonalError]);

  if (mode.kind === 'galleryShell') {
    return (
      <TvPersonalGallery
        grant={mode.grant}
        onBack={onGalleryBack}
        onPersonalError={handlePersonalError}
      />
    );
  }

  return (
    <div
      className="tv-personal-home"
      data-testid="tv-personal-home"
      onKeyDown={(e) => {
        // BACK from the Personal Area root LOCKS and returns to mode selection.
        if (e.key === 'Backspace' || e.key === 'Escape') {
          e.preventDefault();
          onLock();
        }
      }}
    >
      <h2>{t('tv.personalTitle')}</h2>
      <p className="tv-personal-owner">{mode.displayName}</p>
      <div className="tv-mode-options">
        {mode.galleryAvailable && (
          <button type="button" className="tv-mode-option" autoFocus onClick={onOpenGallery}>
            {t('tv.personalGallery')}
          </button>
        )}
        <button type="button" className="tv-mode-option" onClick={onLock} data-testid="tv-personal-lock">
          {t('tv.personalLock')}
        </button>
      </div>
    </div>
  );
}
