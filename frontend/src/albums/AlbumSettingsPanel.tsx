import { useEffect, useRef, useState, type KeyboardEvent } from 'react';
import { Link } from 'react-router';
import {
  ApiError,
  deleteAlbum,
  getAlbumPartySettings,
  setAlbumPartyMode,
  setAlbumTvVisibility,
  updateAlbum,
  type AlbumDetail,
  type AlbumPartyStatus,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';

// Slice 5: the album's rename / description / Show-on-TV / Party (view link,
// guest upload, upload-management) / delete controls, moved out of the content
// area into a modal panel so the grid is not buried under a stack of checkboxes.
// The TV/Party BACKEND semantics are unchanged — this only relocates the UI.

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
  const dialogRef = useRef<HTMLDivElement>(null);
  const [name, setName] = useState(album.name);
  const [desc, setDesc] = useState(album.description ?? '');
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [tvSaving, setTvSaving] = useState(false);
  const [partySaving, setPartySaving] = useState(false);
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

  async function toggleParty(next: boolean) {
    if (next && !window.confirm(t('albumDetail.confirmPartyEnable'))) return;
    setPartySaving(true);
    try {
      const updated = await setAlbumPartyMode(albumId, next);
      onPartyUpdated(updated);
      if (next) onAlbumUpdated({ ...album, showOnTv: updated.showOnTv });
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) invalidateAuth();
    } finally { setPartySaving(false); }
  }

  async function toggleUpload(next: boolean) {
    if (next && !window.confirm(t('albumDetail.confirmUploadEnable'))) return;
    setPartySaving(true);
    try {
      onPartyUpdated(await setAlbumPartyMode(albumId, true, next));
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) invalidateAuth();
    } finally { setPartySaving(false); }
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

          <label className="album-tv-label">
            <input
              type="checkbox"
              checked={party?.partyMode ?? false}
              disabled={partySaving || party === null}
              aria-label={t('albumDetail.partyMode')}
              onChange={(e) => void toggleParty(e.target.checked)}
            />
            <span>{t('albumDetail.partyMode')}</span>
          </label>
          <p className="muted">{t('albumDetail.partyModeHelp')}</p>

          {party?.partyMode && party.partyUrl && (
            <p className="album-party-url" data-testid="party-url">
              {t('albumDetail.publicLink')}{' '}
              <a href={party.partyUrl} target="_blank" rel="noopener noreferrer">{window.location.origin}{party.partyUrl}</a>
            </p>
          )}
          {party?.partyMode && (
            <div className="album-party-upload" data-testid="album-party-upload">
              <label className="album-tv-label">
                <input type="checkbox" checked={party.uploadEnabled} disabled={partySaving} aria-label={t('albumDetail.allowGuestUploads')} onChange={(e) => void toggleUpload(e.target.checked)} />
                <span>{t('albumDetail.allowGuestUploads')}</span>
              </label>
              <p className="muted">{t('albumDetail.guestUploadsHelp')}</p>
              {party.uploadEnabled && party.uploadUrl && (
                <p className="album-party-url" data-testid="party-upload-url">
                  {t('albumDetail.uploadLink')}{' '}
                  <a href={party.uploadUrl} target="_blank" rel="noopener noreferrer">{window.location.origin}{party.uploadUrl}</a>
                </p>
              )}
              <p className="album-party-manage" data-testid="party-uploads-link">
                <Link to={`/albums/${albumId}/party-uploads`}>
                  {t('albumDetail.manageGuestUploads')}
                  {party.requireUploadApproval ? ` (${t('albumDetail.approvalRequired')})` : ''}
                </Link>
              </p>
            </div>
          )}
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
