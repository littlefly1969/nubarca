import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  getTvPersonalHome,
  getTvPersonalStatus,
  lockTvPersonal,
  TV_CODE_LENGTH,
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

// While inside the Personal Area, re-validate the grant on this cadence so a
// code change (or server-side revocation) evicts the TV promptly, not merely on
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
  // The owner still holds the retired numeric PIN. Resolved when entering the
  // unlock gate, and reset there — never remembered across visits, so
  // configuring the new code from another device takes effect on the next try.
  const [legacyCredential, setLegacyCredential] = useState(false);

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
    setLegacyCredential(false);
    try {
      const status = await getTvPersonalStatus();
      if (!status.pinConfigured) { associationIncomplete(); return; }
      // Configured, but with the retired numeric PIN: this television has no
      // numeric entry surface any more and must not pretend otherwise. Show the
      // "configure the new code from your account" notice rather than a code
      // field that can only ever fail. Deliberately NOT an incomplete
      // association — the pairing is fine and Party keeps working.
      setLegacyCredential(status.scheme === 'pin-v1');
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) sessionInvalid();
      // Transient error: entry still works — unlock re-checks server-side.
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
    if (legacyCredential) {
      return (
        <div className="tv-pin-entry" data-testid="tv-code-upgrade-required">
          <h2>{t('tv.codeTitle')}</h2>
          <p role="status">{t('tv.codeUpgradeRequired')}</p>
          <button type="button" onClick={() => setMode({ kind: 'modeSelect', notice: null })}>
            {t('tv.personalBack')}
          </button>
        </div>
      );
    }
    return (
      <TvCodeEntry
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

// BLIND directional-code entry, on the same security model as the native TV
// app: this surface is rendered ON a television, so nothing on screen may
// identify a symbol the user is entering. There is no keypad, no symbol
// glyph, no "last direction" echo and no per-press highlight — only neutral
// progress dots that say HOW MANY moves have been entered, never which ones.
//
// Arrow keys and Enter/Space append; Backspace removes one move; Backspace on
// an empty code (or Escape) returns to mode selection. Auto-submits at exactly
// TV_CODE_LENGTH moves. The entered code lives only in component state and is
// cleared on failure and unmount; it is never logged, persisted, or put in a
// URL.
const CODE_KEYS: Record<string, string> = {
  ArrowUp: 'U',
  ArrowDown: 'D',
  ArrowLeft: 'L',
  ArrowRight: 'R',
  Enter: 'S',
  ' ': 'S',
};

function TvCodeEntry({
  onBack,
  onUnlocked,
  onSessionInvalid,
}: {
  onBack: () => void;
  onUnlocked: (grant: string, displayName: string, galleryAvailable: boolean) => void;
  onSessionInvalid: () => void;
}) {
  const { t } = useI18n();
  const [code, setCode] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const surfaceRef = useRef<HTMLDivElement>(null);

  const submit = useCallback(async (value: string) => {
    setBusy(true);
    try {
      const grant = await unlockTvPersonal(value);
      const home = await getTvPersonalHome(grant.unlockToken);
      onUnlocked(grant.unlockToken, home.displayName, home.galleryAvailable);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onSessionInvalid();
        return;
      }
      setCode('');
      setError(err instanceof ApiError && err.status === 429
        ? t('tv.pinThrottled')
        : t('tv.pinError'));
      setBusy(false);
      surfaceRef.current?.focus();
    }
  }, [onUnlocked, onSessionInvalid, t]);

  const append = useCallback((symbol: string) => {
    if (busy) return;
    setError(null);
    setCode((cur) => {
      const next = (cur + symbol).slice(0, TV_CODE_LENGTH);
      if (next.length === TV_CODE_LENGTH) void submit(next);
      return next;
    });
  }, [busy, submit]);

  const onKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
    const symbol = CODE_KEYS[e.key];
    if (symbol !== undefined) {
      e.preventDefault();
      append(symbol);
      return;
    }
    if (e.key === 'Backspace') {
      e.preventDefault();
      if (busy) return;
      if (code.length > 0) setCode((cur) => cur.slice(0, -1));
      else onBack();
    } else if (e.key === 'Escape') {
      e.preventDefault();
      onBack();
    }
  };

  useEffect(() => {
    surfaceRef.current?.focus();
  }, []);

  return (
    <div
      className="tv-pin-entry"
      ref={surfaceRef}
      tabIndex={-1}
      onKeyDown={onKeyDown}
      data-testid="tv-code-entry"
    >
      <h2>{t('tv.codeTitle')}</h2>
      <p className="tv-pin-hint">{t('tv.codePrompt')}</p>
      {/* Count only. aria-live announces the COUNT, never a symbol — a screen
          reader in the room must not read the secret out loud either. */}
      <div
        className="tv-pin-dots"
        aria-label={t('tv.codeProgress', {
          count: String(code.length),
          total: String(TV_CODE_LENGTH),
        })}
        aria-live="polite"
      >
        {Array.from({ length: TV_CODE_LENGTH }, (_, i) => (
          <span key={i} className={`tv-pin-dot${i < code.length ? ' tv-pin-dot-filled' : ''}`}>
            {i < code.length ? '●' : '○'}
          </span>
        ))}
      </div>
      {/* Purely instructional remote diagram. It is STATIC: no element of it
          ever reacts to a press, because a reactive arrow would leak exactly
          what the missing keypad was removed to hide. */}
      <div className="tv-code-ring" aria-hidden="true">
        <span className="tv-code-ring__up">↑</span>
        <span className="tv-code-ring__left">←</span>
        <span className="tv-code-ring__center">●</span>
        <span className="tv-code-ring__right">→</span>
        <span className="tv-code-ring__down">↓</span>
      </div>
      <p className="tv-pin-hint">{t('tv.codeHint')}</p>
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
