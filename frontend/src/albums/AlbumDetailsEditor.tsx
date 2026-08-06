import { useEffect, useRef, useState } from 'react';
import { ApiError, editSharedAlbumDetails } from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';

// SHARE-ALBUM-03: title and description, editable by the Owner AND an Editor
// through the same endpoint and the same concurrency model.
//
// On 409 the form does NOT resubmit. It reports what happened, hands the caller
// the album's current values, and keeps the user's typed text so a rename is not
// silently lost — but it never re-sends it, because that would re-apply an
// intent formed against a state that no longer exists.

interface Props {
  albumId: string;
  version: number;
  name: string;
  description: string | null;
  onSaved(next: { version: number; name: string; description: string | null }): void;
  onClose(): void;
  returnFocusRef?: React.RefObject<HTMLButtonElement | null>;
}

export function AlbumDetailsEditor({
  albumId, version, name, description, onSaved, onClose, returnFocusRef,
}: Props) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const [draftName, setDraftName] = useState(name);
  const [draftDescription, setDraftDescription] = useState(description ?? '');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Shown after a conflict so the user can see what the album says NOW while
  // their own text is still in the fields.
  const [current, setCurrent] = useState<{ name: string; description: string | null } | null>(null);
  const dialogRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    dialogRef.current?.querySelector<HTMLInputElement>('input')?.focus();
    return () => returnFocusRef?.current?.focus();
  }, [returnFocusRef]);

  async function save() {
    const trimmed = draftName.trim();
    if (trimmed.length === 0) { setError(t('albumEdit.nameRequired')); return; }
    setSaving(true);
    setError(null);
    setCurrent(null);
    try {
      const result = await editSharedAlbumDetails(albumId, version, {
        name: trimmed,
        description: draftDescription.trim(),
      });
      onSaved({
        version: result.version, name: result.name, description: result.description,
      });
      onClose();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      if (err instanceof ApiError && err.status === 409) {
        // NO automatic retry. Surface the collision and the current values; the
        // user's own text stays in the fields for them to decide about.
        const body = err.body as { name?: string; description?: string | null } | null;
        setCurrent({ name: body?.name ?? '', description: body?.description ?? null });
        setError(t('albumEdit.conflict'));
        return;
      }
      if (err instanceof ApiError && (err.status === 403 || err.status === 404)) {
        // Demoted or revoked while the form was open — the control they are
        // looking at no longer exists.
        setError(t('albumEdit.noLongerAllowed'));
        onClose();
        return;
      }
      if (err instanceof ApiError && err.status === 400) {
        setError((err.body as { error?: string } | null)?.error ?? t('albumEdit.saveError'));
        return;
      }
      setError(t('albumEdit.saveError'));
    } finally {
      setSaving(false);
    }
  }

  return (
    <div
      className="ws-sheet-backdrop"
      data-testid="album-edit-backdrop"
      onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}
    >
      <div
        ref={dialogRef}
        className="ws-sheet album-edit-panel"
        role="dialog"
        aria-modal="true"
        aria-label={t('albumEdit.title')}
        data-testid="album-edit-panel"
        onKeyDown={(e) => { if (e.key === 'Escape') { e.stopPropagation(); onClose(); } }}
      >
        <header className="ws-sheet-head">
          <h2 className="ws-sheet-title">{t('albumEdit.title')}</h2>
          <button
            type="button" className="ws-icon-button"
            aria-label={t('common.close')}
            data-testid="album-edit-close"
            onClick={onClose}
          >✕</button>
        </header>

        <div className="ws-sheet-body">
          <label>
            {t('albumEdit.name')}
            <input
              type="text"
              data-testid="album-edit-name"
              value={draftName}
              maxLength={255}
              disabled={saving}
              onChange={(e) => setDraftName(e.target.value)}
            />
          </label>
          <label>
            {t('albumEdit.description')}
            <input
              type="text"
              data-testid="album-edit-description"
              value={draftDescription}
              maxLength={1000}
              disabled={saving}
              onChange={(e) => setDraftDescription(e.target.value)}
            />
          </label>

          {error && <p className="inline-error" role="alert" data-testid="album-edit-error">{error}</p>}
          {current && (
            <p className="muted" data-testid="album-edit-current">
              {current.name}
              {current.description ? ` — ${current.description}` : ''}
            </p>
          )}
        </div>

        <footer className="ws-sheet-foot">
          <div className="ws-sheet-foot-right">
            <button type="button" className="row-action" onClick={onClose}>
              {t('common.cancel')}
            </button>
            <button
              type="button"
              className="row-action-primary"
              data-testid="album-edit-save"
              disabled={saving}
              onClick={() => void save()}
            >
              {saving ? t('albumEdit.saving') : t('albumEdit.save')}
            </button>
          </div>
        </footer>
      </div>
    </div>
  );
}
