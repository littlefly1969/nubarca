import { useEffect, useState } from 'react';
import { Link } from 'react-router';
import {
  ApiError,
  getVaultStatus,
  lockVault,
  unlockVault,
  vaultMoveIn,
} from '@nubarca/api-client';

interface MoveToVaultModalProps {
  fileIds: string[];
  folderIds: string[];
  onCancel(): void;
  onDone(message: { tone: 'info' | 'error'; text: string }): void;
}

// Moving content into the Private Vault requires proving vault access. The user
// enters the vault password here; we unlock (short-lived token, kept only in
// this component's memory), move the selection in, then immediately lock. The
// password is never stored anywhere. If no vault is configured yet, we point the
// user at the Private tab to set one up (no move is attempted).
export function MoveToVaultModal({ fileIds, folderIds, onCancel, onDone }: MoveToVaultModalProps) {
  const [phase, setPhase] = useState<'checking' | 'need-setup' | 'ready' | 'error-check'>('checking');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const count = fileIds.length + folderIds.length;

  useEffect(() => {
    const controller = new AbortController();
    getVaultStatus(controller.signal)
      .then((s) => setPhase(s.configured ? 'ready' : 'need-setup'))
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        setPhase('error-check');
      });
    return () => controller.abort();
  }, []);

  async function submit() {
    if (busy) return;
    setError(null);
    if (password.length === 0) {
      setError('Enter your vault password.');
      return;
    }
    setBusy(true);
    let token: string | null = null;
    try {
      const unlocked = await unlockVault(password);
      token = unlocked.token;
      const result = await vaultMoveIn(token, { fileIds, folderIds });
      onDone({
        tone: 'info',
        text: `Moved ${result.movedFiles + result.movedFolders} item${
          result.movedFiles + result.movedFolders !== 1 ? 's' : ''
        } to the Private Vault.`,
      });
    } catch (err: unknown) {
      // Generic failure — never distinguishes wrong-password from missing vault.
      if (err instanceof ApiError && err.status === 401) {
        setError('Unable to unlock the private area. Check your password.');
      } else {
        setError('Something went wrong. Please try again.');
      }
      setBusy(false);
      return;
    } finally {
      // Best-effort lock so the token never lingers server-side.
      if (token) void lockVault(token).catch(() => {});
    }
  }

  return (
    <div className="files-modal" role="dialog" aria-modal="true" aria-label="Move to Private Vault">
      <div className="files-modal-backdrop" onClick={() => !busy && onCancel()} />
      <div className="files-modal-content">
        <div className="vault-move-modal">
          <h3>Move to Private Vault</h3>
          {phase === 'checking' && <p className="muted">Checking…</p>}

          {phase === 'error-check' && (
            <>
              <p className="folder-error">Could not reach the Private Vault. Please try again.</p>
              <div className="vault-modal-actions">
                <button type="button" className="row-action" onClick={onCancel}>
                  Close
                </button>
              </div>
            </>
          )}

          {phase === 'need-setup' && (
            <>
              <p className="muted">
                You haven’t set up a Private Vault yet. Create one in the Private tab, then
                move items in.
              </p>
              <div className="vault-modal-actions">
                <Link className="row-action-primary" to="/private" onClick={onCancel}>
                  Open Private
                </Link>
                <button type="button" className="row-action" onClick={onCancel}>
                  Cancel
                </button>
              </div>
            </>
          )}

          {phase === 'ready' && (
            <>
              <p className="muted">
                {count} item{count !== 1 ? 's' : ''} will be moved into your Private Vault and
                removed from normal Files, Gallery, search, and export. Enter your vault password
                to confirm.
              </p>
              <form
                onSubmit={(e) => {
                  e.preventDefault();
                  void submit();
                }}
              >
                <input
                  type="password"
                  className="vault-password-input"
                  placeholder="Vault password"
                  value={password}
                  autoFocus
                  autoComplete="off"
                  disabled={busy}
                  onChange={(e) => setPassword(e.target.value)}
                  data-testid="vault-move-password"
                />
                {error && (
                  <p className="folder-error" role="alert">
                    {error}
                  </p>
                )}
                <div className="vault-modal-actions">
                  <button
                    type="submit"
                    className="row-action-primary"
                    disabled={busy}
                    data-testid="vault-move-confirm"
                  >
                    {busy ? 'Moving…' : 'Move to Private Vault'}
                  </button>
                  <button type="button" className="row-action" onClick={onCancel} disabled={busy}>
                    Cancel
                  </button>
                </div>
              </form>
            </>
          )}
        </div>
      </div>
    </div>
  );
}
