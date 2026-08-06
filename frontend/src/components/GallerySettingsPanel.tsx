import { useEffect, useState } from 'react';
import {
  deleteMediaLibraryRule,
  getMediaLibraryEffective,
  putMediaLibraryRule,
  type MediaLibraryEffective,
  type MediaLibraryEffectiveKind,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';
import type { I18nContextValue } from '../i18n';

type TranslateFn = I18nContextValue['t'];

// Slice 94 — per-folder gallery (media library) settings. Inline panel in the
// folder browser, modelled on MovePicker. Excluding a folder only changes
// MEDIA views (gallery, future map, batch media jobs): the folder and its
// files stay fully visible in the browser, downloadable, and shareable.

type Mode = 'inherit' | 'include' | 'exclude';

interface GallerySettingsPanelProps {
  folderId: string;
  folderName: string;
  onSaved(message: string): void;
  onCancel(): void;
}

function describeKind(t: TranslateFn, label: string, kind: MediaLibraryEffectiveKind): string {
  const state = (excluded: boolean) => (excluded
    ? t('gallerySettings.excludedWord')
    : t('gallerySettings.includedWord'));
  if (kind.source === 'rule') {
    return t('gallerySettings.explicit', { label, state: state(kind.excluded) });
  }
  if (kind.source === 'inherited') {
    return t('gallerySettings.inherited', {
      label, state: state(kind.excluded), source: kind.sourceFolderName ?? '…',
    });
  }
  return t('gallerySettings.defaultIncluded', { label });
}

export function GallerySettingsPanel({
  folderId, folderName, onSaved, onCancel,
}: GallerySettingsPanelProps) {
  const { t } = useI18n();
  const [effective, setEffective] = useState<MediaLibraryEffective | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const [mode, setMode] = useState<Mode>('inherit');
  const [photos, setPhotos] = useState(true);
  const [videos, setVideos] = useState(true);
  const [children, setChildren] = useState(true);

  useEffect(() => {
    const controller = new AbortController();
    void (async () => {
      try {
        const data = await getMediaLibraryEffective(folderId, controller.signal);
        setEffective(data);
        if (data.rule) {
          setMode(data.rule.ruleType === 'exclude' ? 'exclude' : 'include');
          setPhotos(data.rule.appliesToPhotos);
          setVideos(data.rule.appliesToVideos);
          setChildren(data.rule.appliesToChildren);
        }
      } catch (err) {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        setError(t('gallerySettings.loadError'));
      }
    })();
    return () => controller.abort();
  }, [folderId, t]);

  async function onSave() {
    setBusy(true);
    setError(null);
    try {
      if (mode === 'inherit') {
        if (effective?.rule) {
          await deleteMediaLibraryRule(effective.rule.id);
        }
        onSaved(t('gallerySettings.followsParent', { name: folderName }));
      } else {
        if (!photos && !videos) {
          setError(t('gallerySettings.selectKind'));
          setBusy(false);
          return;
        }
        await putMediaLibraryRule({
          folderId,
          ruleType: mode,
          appliesToPhotos: photos,
          appliesToVideos: videos,
          appliesToChildren: children,
        });
        onSaved(mode === 'exclude'
          ? t('gallerySettings.nowExcluded', { name: folderName })
          : t('gallerySettings.nowIncluded', { name: folderName }));
      }
    } catch {
      setError(t('gallerySettings.saveError'));
      setBusy(false);
    }
  }

  return (
    <div className="gallery-settings" role="region" aria-label={t('gallerySettings.regionAria', { name: folderName })}>
      <p className="muted">{t('gallerySettings.intro')}</p>

      {error && <p className="row-inline-error" role="alert">{error}</p>}
      {!effective && !error && <p role="status">{t('common.loading')}</p>}

      {effective && (
        <>
          <p className="gallery-settings-state" data-testid="gallery-effective">
            {describeKind(t, t('gallerySettings.photosLabel'), effective.photos)}
            <br />
            {describeKind(t, t('gallerySettings.videosLabel'), effective.videos)}
          </p>

          <fieldset className="gallery-settings-mode" disabled={busy}>
            <legend className="muted">{t('gallerySettings.thisFolder')}</legend>
            <label>
              <input
                type="radio"
                name={`gallery-mode-${folderId}`}
                checked={mode === 'inherit'}
                onChange={() => setMode('inherit')}
              />{' '}
              {t('gallerySettings.followParentOption')}
            </label>
            <label>
              <input
                type="radio"
                name={`gallery-mode-${folderId}`}
                checked={mode === 'exclude'}
                onChange={() => setMode('exclude')}
              />{' '}
              {t('gallerySettings.excludeOption')}
            </label>
            <label>
              <input
                type="radio"
                name={`gallery-mode-${folderId}`}
                checked={mode === 'include'}
                onChange={() => setMode('include')}
              />{' '}
              {t('gallerySettings.includeOption')}
            </label>
          </fieldset>

          {mode !== 'inherit' && (
            <fieldset className="gallery-settings-kinds" disabled={busy}>
              <legend className="muted">{t('gallerySettings.appliesTo')}</legend>
              <label>
                <input
                  type="checkbox"
                  checked={photos}
                  onChange={(e) => setPhotos(e.target.checked)}
                />{' '}
                {t('gallerySettings.photosLabel')}
              </label>
              <label>
                <input
                  type="checkbox"
                  checked={videos}
                  onChange={(e) => setVideos(e.target.checked)}
                />{' '}
                {t('gallerySettings.videosLabel')}
              </label>
              <label>
                <input
                  type="checkbox"
                  checked={children}
                  onChange={(e) => setChildren(e.target.checked)}
                />{' '}
                {t('gallerySettings.subfoldersToo')}
              </label>
            </fieldset>
          )}

          <div className="row-actions">
            <button
              type="button"
              className="row-action-primary"
              onClick={() => void onSave()}
              disabled={busy}
            >
              {busy ? t('common.saving') : t('common.save')}
            </button>
            <button type="button" className="row-action" onClick={onCancel} disabled={busy}>
              {t('common.cancel')}
            </button>
          </div>
        </>
      )}
    </div>
  );
}
