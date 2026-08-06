import { useEffect, useRef, useState } from 'react';
import { ApiError, getVaultStatus, setupVault, unlockVault } from '@nubarca/api-client';
import { useI18n } from '../i18n';

// Shared Private Vault setup/unlock form. Used by the standalone Personal page
// AND by the "Move to Personal" dialog triggered from the galleries — the
// password lifecycle must never be implemented twice.
//
// This component knows NOTHING about what the caller intends to do with the
// resulting token (browse, or move a selection in): it only resolves a raw
// unlock token and hands it to `onUnlocked`, once, in memory. It never persists
// the token itself and never renders it.
export type PrivateVaultAccessMode = 'loading-status' | 'setup' | 'unlock';

interface Props {
  onUnlocked(token: string): void;
  // Optional idle-state submit label overrides — callers that chain a further
  // action onto the unlock (e.g. "Unlock and move") can rephrase the button
  // without forking the form. Busy-state wording stays generic.
  createLabel?: string;
  unlockLabel?: string;
}

export function PrivateVaultAccessForm({ onUnlocked, createLabel, unlockLabel }: Props) {
  const { t } = useI18n();
  const [mode, setMode] = useState<PrivateVaultAccessMode>('loading-status');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const passwordRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    const controller = new AbortController();
    getVaultStatus(controller.signal)
      .then((s) => setMode(s.configured ? 'unlock' : 'setup'))
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        // Treat an errored status as "unlock" mode; unlock will surface issues.
        setMode('unlock');
      });
    return () => controller.abort();
  }, []);

  useEffect(() => {
    if (mode === 'setup' || mode === 'unlock') passwordRef.current?.focus();
  }, [mode]);

  function describeError(err: unknown, fallback: string, unauthorized: string): string {
    if (err instanceof ApiError && err.status === 429) return t('vault.rateLimited');
    if (err instanceof ApiError && err.status === 401) return unauthorized;
    return fallback;
  }

  async function doUnlock() {
    setBusy(true);
    setError(null);
    try {
      const result = await unlockVault(password);
      setPassword('');
      setConfirm('');
      onUnlocked(result.token);
    } catch (err: unknown) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      setError(describeError(err, t('vault.genericError'), t('vault.unlockFail')));
      setBusy(false);
    }
  }

  async function doCreate() {
    setBusy(true);
    setError(null);
    if (password.length < 8) {
      setError(t('vault.passwordTooShort'));
      setBusy(false);
      return;
    }
    if (password !== confirm) {
      setError(t('vault.passwordMismatch'));
      setBusy(false);
      return;
    }
    try {
      await setupVault(password);
      const result = await unlockVault(password);
      setPassword('');
      setConfirm('');
      onUnlocked(result.token);
    } catch (err: unknown) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      setError(describeError(err, t('vault.setupError'), t('vault.setupError')));
      setBusy(false);
    }
  }

  if (mode === 'loading-status') {
    return <p className="muted">{t('common.loading')}</p>;
  }

  return (
    <div className="vault-locked">
      <p className="muted vault-locked-intro">
        {mode === 'unlock' ? t('vault.unlockIntro') : t('vault.createIntro')}
      </p>
      <form
        className="vault-locked-form"
        onSubmit={(e) => {
          e.preventDefault();
          void (mode === 'unlock' ? doUnlock() : doCreate());
        }}
      >
        <input
          ref={passwordRef}
          type="password"
          className="vault-password-input"
          placeholder={t('vault.passwordPlaceholder')}
          value={password}
          autoComplete="off"
          disabled={busy}
          onChange={(e) => setPassword(e.target.value)}
          data-testid="vault-password"
        />
        {mode === 'setup' && (
          <input
            type="password"
            className="vault-password-input"
            placeholder={t('vault.confirmPlaceholder')}
            value={confirm}
            autoComplete="off"
            disabled={busy}
            onChange={(e) => setConfirm(e.target.value)}
            data-testid="vault-password-confirm"
          />
        )}
        {error && (
          <p className="folder-error" role="alert">
            {error}
          </p>
        )}
        <button
          type="submit"
          className="row-action-primary"
          disabled={busy}
          data-testid="vault-submit"
        >
          {mode === 'unlock'
            ? (busy ? t('vault.unlocking') : (unlockLabel ?? t('vault.unlock')))
            : (busy ? t('vault.creating') : (createLabel ?? t('vault.createArea')))}
        </button>
      </form>
    </div>
  );
}
