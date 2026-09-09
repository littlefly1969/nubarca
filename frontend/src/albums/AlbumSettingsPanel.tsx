import { useEffect, useRef, useState, type KeyboardEvent } from 'react';
import {
  ApiError,
  deleteAlbum,
  getAlbumPartySettings,
  setAlbumTvVisibility,
  updateAlbum,
  type AlbumDetail,
  type AlbumPartyStatus,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { usePermissions } from '../auth/usePermissions';
import { PERMISSIONS } from '../auth/permissions';
import { useI18n } from '../i18n';
import { AlbumPartyBridge } from './AlbumPartyBridge';

// The album's own settings: rename, description, Show-on-TV, delete.
//
// Party used to live here in full — guest access, upload switches, moderation
// links, slideshow numbers, the game and its deck, printing — because there was
// nowhere else for it to be. There is now: Party is a destination of its own,
// and what remains here is a BRIDGE, one sentence and one door. Two complete
// interfaces configuring one party would be two places to change it and two
// places for them to disagree.

interface Props {
  albumId: string;
  album: AlbumDetail;
  party: AlbumPartyStatus | null;
  onAlbumUpdated(album: AlbumDetail): void;
  onPartyUpdated(party: AlbumPartyStatus): void;
  onDeleted(): void;
  onClose(): void;
  returnFocusRef?: React.RefObject<HTMLButtonElement | null>;
}

export function AlbumSettingsPanel({
  albumId, album, party, onAlbumUpdated, onPartyUpdated, onDeleted, onClose, returnFocusRef,
}: Props) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  // A capability the caller does not hold is ABSENT from this panel, never a
  // control that answers 403 when pressed. The server enforces each of these
  // independently; what happens here is only that a door nobody may open is
  // not drawn. Each feature asks for the product permission as well, because
  // that is the rule the server applies.
  const canParty = usePermissions().has(PERMISSIONS.partyAccess);
  const dialogRef = useRef<HTMLDivElement>(null);
  const [name, setName] = useState(album.name);
  const [desc, setDesc] = useState(album.description ?? '');
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [tvSaving, setTvSaving] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);

  useEffect(() => {
    dialogRef.current?.querySelector<HTMLElement>('input, button')?.focus();
    return () => returnFocusRef?.current?.focus();
  }, [returnFocusRef]);

  const dirty = name.trim() !== album.name || desc.trim() !== (album.description ?? '');

  function onKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    if (e.key === 'Escape') { e.stopPropagation(); onClose(); return; }
    if (e.key !== 'Tab') return;
    const list = Array.from(dialogRef.current?.querySelectorAll<HTMLElement>(
      'input, textarea, button, a[href], [tabindex]:not([tabindex="-1"])',
    ) ?? []);
    if (list.length === 0) return;
    const first = list[0], last = list[list.length - 1];
    if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
  }

  async function save() {
    setSaving(true); setSaveError(null);
    try {
      const updated = await updateAlbum(albumId, name.trim(), desc.trim() || null);
      onAlbumUpdated(updated);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      if (err instanceof ApiError && err.status === 409) setSaveError(t('albumDetail.exists'));
      else if (err instanceof ApiError && err.status === 400) setSaveError(t('albumDetail.invalidInput'));
      else setSaveError(t('albumDetail.saveError'));
    } finally { setSaving(false); }
  }

  async function toggleTv(next: boolean) {
    setTvSaving(true);
    try {
      const updated = await setAlbumTvVisibility(albumId, next);
      onAlbumUpdated(updated);
      if (!next) onPartyUpdated(await getAlbumPartySettings(albumId));
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) invalidateAuth();
    } finally { setTvSaving(false); }
  }

  async function doDelete() {
    if (!window.confirm(t('albumSettings.deleteConfirm'))) return;
    setDeleting(true); setDeleteError(null);
    try {
      await deleteAlbum(albumId);
      onDeleted();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setDeleteError(t('albumSettings.deleteError'));
    } finally { setDeleting(false); }
  }

  return (
    <div
      className="ws-sheet-backdrop"
      data-testid="album-settings-backdrop"
      onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}
    >
      <div
        ref={dialogRef}
        className="ws-sheet album-settings-panel"
        role="dialog"
        aria-modal="true"
        aria-label={t('albumSettings.title')}
        data-testid="album-settings-panel"
        onKeyDown={onKeyDown}
      >
        <header className="ws-sheet-head">
          <h2 className="ws-sheet-title">{t('albumSettings.title')}</h2>
          <button type="button" className="ws-icon-button" aria-label={t('albumSettings.close')} data-testid="album-settings-close" onClick={onClose}>✕</button>
        </header>

        <div className="ws-sheet-body">
        <fieldset className="ws-filter-section">
          <label>
            {t('albumDetail.nameAria')}
            <input type="text" data-testid="album-name" value={name} onChange={(e) => setName(e.target.value)} />
          </label>
          <label>
            {t('albumDetail.descAria')}
            <input type="text" data-testid="album-desc" value={desc} placeholder={t('albumDetail.descPlaceholder')} onChange={(e) => setDesc(e.target.value)} />
          </label>
          {saveError && <p className="inline-error" role="alert">{saveError}</p>}
          <button type="button" className="row-action-primary" data-testid="album-save" disabled={!dirty || saving} onClick={() => void save()}>
            {saving ? t('common.saving') : t('common.save')}
          </button>
        </fieldset>

        <fieldset className="ws-filter-section">
          <label className="album-tv-label">
            <input type="checkbox" data-testid="album-tv-toggle" checked={album.showOnTv} disabled={tvSaving} onChange={(e) => void toggleTv(e.target.checked)} />
            <span>{t('albumDetail.showOnTv')}</span>
          </label>
          <p className="muted">{t('albumDetail.showOnTvHelp')}</p>

          {/* Party's own destination owns the rest. This is a bridge: which
              party this album belongs to, and the way in. */}
          {canParty && <AlbumPartyBridge albumId={albumId} partyMode={party?.partyMode ?? false} />}
        </fieldset>

        <fieldset className="ws-filter-section">
          {deleteError && <p className="inline-error" role="alert">{deleteError}</p>}
          <button type="button" className="btn-danger" data-testid="album-delete" disabled={deleting} onClick={() => void doDelete()}>
            {t('albumSettings.deleteAlbum')}
          </button>
        </fieldset>
        </div>
      </div>
    </div>
  );
}
