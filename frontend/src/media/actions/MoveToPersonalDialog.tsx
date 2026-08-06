import { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { ApiError, getVaultStatus, type VaultMoveResult } from '@nubarca/api-client';
import { useAuth } from '../../auth/useAuth';
import { useI18n } from '../../i18n';
import { PrivateVaultAccessForm } from '../../vault/PrivateVaultAccessForm';

// Bulk "Move to Personal" dialog shared by the photo and video galleries.
//
// Token handling contract: the raw unlock token lives ONLY in `tokenRef`
// below, for the brief window between PrivateVaultAccessForm resolving it and
// the move-in call settling. It is never passed to `onClose`/`execute`
// callers, never written to storage/URL/console, and is dropped (ref cleared)
// the instant the move settles — success, failure, or unmount. Nothing here
// ever renders the token.
//
// Lock is deliberately NOT called after the move: the backend's lock revokes
// ALL of the owner's live tokens, which could kill a Personal session open in
// another tab. The token above simply expires server-side on its own (see
// PrivateVaultService.TokenLifetime); we just stop holding on to it.
export interface MoveToPersonalDialogProps {
  fileIds: string[];
  onClose(): void;
  execute(token: string): Promise<VaultMoveResult>;
}

type Phase = 'access' | 'moving';

export function MoveToPersonalDialog({ fileIds, onClose, execute }: MoveToPersonalDialogProps) {
  const { t, tn } = useI18n();
  const { invalidateAuth } = useAuth();
  const [phase, setPhase] = useState<Phase>('access');
  const [error, setError] = useState<string | null>(null);
  const tokenRef = useRef<string | null>(null);
  const dialogRef = useRef<HTMLDivElement>(null);
  const previouslyFocusedRef = useRef<HTMLElement | null>(null);

  useEffect(() => {
    previouslyFocusedRef.current = document.activeElement as HTMLElement | null;
    dialogRef.current?.focus();
    return () => {
      tokenRef.current = null;
      previouslyFocusedRef.current?.focus?.();
    };
  }, []);

  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape' && phase !== 'moving') onClose();
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose, phase]);

  async function handleUnlocked(token: string) {
    tokenRef.current = token;
    setPhase('moving');
    setError(null);
    try {
      await execute(token);
      tokenRef.current = null;
      onClose();
    } catch (err) {
      tokenRef.current = null;
      if (err instanceof ApiError && err.status === 401) {
        // Distinguish an expired Personal token from a dead NubArca session
        // — only the latter should sign the user out of the whole app.
        try {
          await getVaultStatus();
          setError(t('moveToPersonal.sessionExpired'));
        } catch (statusErr) {
          if (statusErr instanceof ApiError && statusErr.status === 401) {
            invalidateAuth();
            return;
          }
          setError(t('moveToPersonal.moveError'));
        }
      } else {
        setError(t('moveToPersonal.moveError'));
      }
      setPhase('access');
    }
  }

  return createPortal(
    <div
      className="ws-confirm-backdrop"
      onMouseDown={(e) => {
        if (e.target === e.currentTarget && phase !== 'moving') onClose();
      }}
    >
      <div
        className="ws-confirm move-to-personal-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="move-to-personal-title"
        aria-describedby="move-to-personal-desc"
        ref={dialogRef}
        tabIndex={-1}
        data-testid="move-to-personal-dialog"
      >
        <h2 id="move-to-personal-title" className="ws-confirm-title">
          {tn(fileIds.length, 'moveToPersonal.title')}
        </h2>
        <div id="move-to-personal-desc">
          <p className="ws-confirm-body">{t('moveToPersonal.explainHidden')}</p>
          <p className="ws-confirm-body">{t('moveToPersonal.explainAccess')}</p>
          <p className="ws-confirm-body">{t('moveToPersonal.explainKept')}</p>
        </div>

        {phase === 'moving' ? (
          <p className="muted" role="status" data-testid="move-to-personal-busy">
            {t('moveToPersonal.moving')}
          </p>
        ) : (
          <>
            {error && (
              <p className="folder-error" role="alert" data-testid="move-to-personal-error">
                {error}
              </p>
            )}
            <PrivateVaultAccessForm
              onUnlocked={(token) => void handleUnlocked(token)}
              createLabel={t('moveToPersonal.createAndMove')}
              unlockLabel={t('moveToPersonal.unlockAndMove')}
            />
          </>
        )}

        <div className="ws-confirm-actions">
          <button
            type="button"
            className="row-action"
            onClick={onClose}
            disabled={phase === 'moving'}
            data-testid="move-to-personal-cancel"
          >
            {t('common.cancel')}
          </button>
        </div>
      </div>
    </div>,
    document.body,
  );
}
