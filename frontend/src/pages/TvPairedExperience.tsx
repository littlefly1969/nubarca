import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  getTvPersonalHome,
  getTvPersonalStatus,
  TV_CODE_LENGTH,
  unlockTvPersonal,
} from '@nubarca/api-client';
import { TvPersonalGallery } from './TvPersonalGallery';
import { useI18n } from '../i18n';

// The paired /tv screens that belong to a GENERAL display: the mode selector,
// the Personal Area code gate and the Personal Area itself.
//
// WHICH of them is on screen is no longer decided here. It is the display's
// state machine (tv/semantics/flow.ts, the port of the app's own), driven by
// TvDisplay — which is what lets an owner's assignment take the screen from any
// of them, a Personal Area included, and lock it on the way.
//
// The unlock grant still lives ONLY in application memory (TvDisplay's state):
// a reload, a tab close or leaving the paired state loses it, and it is never
// written to localStorage, sessionStorage, a cookie or a URL.

export type ModeNotice = 'pinChanged' | null;

// While inside the Personal Area, re-validate the grant on this cadence so a
// code change (or server-side revocation) evicts the TV promptly, not merely on
// the next user action.
const PERSONAL_REVALIDATE_MS = 15_000;

/** Mode selection: shown on every start of a GENERAL display; never auto-reopens a mode. */
export function TvModeSelect({
  notice, onChooseParty, onChoosePersonal, onChooseBeautyLab,
}: {
  notice: ModeNotice;
  onChooseParty: () => void;
  onChoosePersonal: () => void;
  onChooseBeautyLab: () => void;
}) {
  const { t } = useI18n();
  return (
    <div
      className="tv-mode-select"
      onKeyDown={(e) => {
        // BACK on the mode selector must never enter a mode.
        if (e.key === 'Backspace' || e.key === 'Escape') e.preventDefault();
      }}
    >
      <h2 className="tv-mode-title">{t('tv.modeTitle')}</h2>
      {notice === 'pinChanged' && (
        <p role="status" data-testid="tv-pin-changed-notice">{t('tv.pinChangedNotice')}</p>
      )}
      <div className="tv-mode-options">
        <button
          type="button" className="tv-mode-option" autoFocus data-testid="tv-mode-party"
          onClick={onChooseParty}
        >
          {t('tv.modeParty')}
        </button>
        <button
          type="button" className="tv-mode-option" data-testid="tv-mode-personal"
          onClick={onChoosePersonal}
        >
          {t('tv.modePersonal')} <span aria-hidden="true">🔒</span>
        </button>
        <button
          type="button" className="tv-mode-option" data-testid="tv-mode-beauty-lab"
          onClick={onChooseBeautyLab}
        >
          {t('tv.modeBeautyLab')} <span aria-hidden="true">🔒</span>
        </button>
      </div>
    </div>
  );
}

/**
 * The shared unlock gate for the Personal Area and the Beauty Lab. One code,
 * one in-memory grant, whichever of the two asked for it.
 *
 * On entry it asks the server what credential the owner holds: none at all is
 * an incomplete association (legacy data the atomic pairing cannot produce);
 * the retired numeric PIN gets a notice instead of a code field that could only
 * ever fail.
 */
export function TvCodeGate({
  onBack, onUnlocked, onSessionInvalid, onAssociationIncomplete,
}: {
  onBack: () => void;
  onUnlocked: (grant: string, displayName: string, galleryAvailable: boolean) => void;
  onSessionInvalid: () => void;
  onAssociationIncomplete: () => void;
}) {
  const { t } = useI18n();
  const [legacyCredential, setLegacyCredential] = useState(false);
  useEffect(() => {
    const controller = new AbortController();
    getTvPersonalStatus(controller.signal)
      .then((status) => {
        if (!status.pinConfigured) { onAssociationIncomplete(); return; }
        setLegacyCredential(status.scheme === 'pin-v1');
      })
      .catch((err: unknown) => {
        if (err instanceof ApiError && err.status === 401) onSessionInvalid();
        // Transient: entry still works — unlock re-checks server-side.
      });
    return () => controller.abort();
  }, [onAssociationIncomplete, onSessionInvalid]);

  if (legacyCredential) {
    return (
      <div className="tv-pin-entry" data-testid="tv-code-upgrade-required">
        <h2>{t('tv.codeTitle')}</h2>
        <p role="status">{t('tv.codeUpgradeRequired')}</p>
        <button type="button" onClick={onBack}>{t('tv.personalBack')}</button>
      </div>
    );
  }
  return <TvCodeEntry onBack={onBack} onUnlocked={onUnlocked} onSessionInvalid={onSessionInvalid} />;
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
export function TvPersonalArea({
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
