import { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { ApiError, type MediaLibraryBulkResult } from '@nubarca/api-client';
import { useAuth } from '../../auth/useAuth';
import { useI18n } from '../../i18n';

// Slice 3: lightweight "Move to Excluded" confirmation shared by the photo and
// video galleries. No password, no token — excluding only flips a per-file flag
// (the files stay in their folders). Explains the effect, captures the id
// snapshot from the caller, runs the exclude, and reconciles via `execute`.
export interface MoveToExcludedDialogProps {
  count: number;
  onClose(): void;
  execute(): Promise<MediaLibraryBulkResult>;
}

export function MoveToExcludedDialog({ count, onClose, execute }: MoveToExcludedDialogProps) {
  const { t, tn } = useI18n();
  const { invalidateAuth } = useAuth();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const dialogRef = useRef<HTMLDivElement>(null);
  const confirmRef = useRef<HTMLButtonElement>(null);
  const previouslyFocusedRef = useRef<HTMLElement | null>(null);

  useEffect(() => {
    previouslyFocusedRef.current = document.activeElement as HTMLElement | null;
    confirmRef.current?.focus();
    return () => previouslyFocusedRef.current?.focus?.();
  }, []);

  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape' && !busy) onClose();
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose, busy]);

  async function onConfirm() {
    if (busy) return; // guard against double submit
    setBusy(true);
    setError(null);
    try {
      await execute();
      onClose();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        invalidateAuth();
        return;
      }
      setError(t('moveToExcluded.error'));
      setBusy(false);
    }
  }

  return createPortal(
    <div
      className="ws-confirm-backdrop"
      onMouseDown={(e) => { if (e.target === e.currentTarget && !busy) onClose(); }}
    >
      <div
        className="ws-confirm move-to-excluded-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="move-to-excluded-title"
        aria-describedby="move-to-excluded-desc"
        ref={dialogRef}
        tabIndex={-1}
        data-testid="move-to-excluded-dialog"
      >
        <h2 id="move-to-excluded-title" className="ws-confirm-title">
          {tn(count, 'moveToExcluded.title')}
        </h2>
        <div id="move-to-excluded-desc">
          <p className="ws-confirm-body">{t('moveToExcluded.explainKept')}</p>
          <p className="ws-confirm-body">{t('moveToExcluded.explainHidden')}</p>
        </div>

        {error && (
          <p className="folder-error" role="alert" data-testid="move-to-excluded-error">
            {error}
          </p>
        )}

        <div className="ws-confirm-actions">
          <button
            type="button"
            className="row-action"
            onClick={onClose}
            disabled={busy}
            data-testid="move-to-excluded-cancel"
          >
            {t('common.cancel')}
          </button>
          <button
            ref={confirmRef}
            type="button"
            className="row-action-primary"
            onClick={() => void onConfirm()}
            disabled={busy}
            data-testid="move-to-excluded-confirm"
          >
            {busy ? t('moveToExcluded.moving') : t('moveToExcluded.confirm')}
          </button>
        </div>
      </div>
    </div>,
    document.body,
  );
}
