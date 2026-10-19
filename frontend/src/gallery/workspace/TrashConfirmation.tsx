import { useEffect, useRef, type ReactNode } from 'react';
import { useI18n } from '../../i18n';

// Confirmation dialog for the destructive bulk move-to-Trash. Focus is trapped
// on the two actions; Escape cancels; focus returns to the trigger via the
// selection bar. Never hard-deletes — the copy makes restoration explicit.
interface Props {
  count: number;
  busy: boolean;
  onConfirm(): void;
  onCancel(): void;
  // What the action reaches, when it starts from the account selection.
  scope?: ReactNode;
}

export function TrashConfirmation({ count, busy, onConfirm, onCancel, scope }: Props) {
  const { t, tn } = useI18n();
  const confirmRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    confirmRef.current?.focus();
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape' && !busy) onCancel();
    }
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [onCancel, busy]);

  return (
    <div className="ws-confirm-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget && !busy) onCancel(); }}>
      <div
        className="ws-confirm"
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="ws-trash-title"
        data-testid="ws-trash-confirm"
      >
        <h2 id="ws-trash-title" className="ws-confirm-title">
          {tn(count, 'gallery.ws.trash.title')}
        </h2>
        <p className="ws-confirm-body">{t('gallery.ws.trash.body')}</p>
        {scope}
        <div className="ws-confirm-actions">
          <button type="button" className="row-action" data-testid="ws-trash-cancel" disabled={busy} onClick={onCancel}>
            {t('common.cancel')}
          </button>
          <button
            ref={confirmRef}
            type="button"
            className="row-action-destructive"
            data-testid="ws-trash-confirm-btn"
            disabled={busy}
            onClick={onConfirm}
          >
            {busy ? t('gallery.ws.trash.busy') : t('gallery.ws.trash.confirm')}
          </button>
        </div>
      </div>
    </div>
  );
}
