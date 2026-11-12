import { useEffect, useState } from 'react';
import {
  ApiError,
  getAlbumPrintLogo,
  setAlbumPrintLogo,
  type AlbumPrintLogo,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { PartyImageUploadButton, partyImageUploadErrorKey, type PartyImageUploadError } from '../party/PartyImageField';

// THE ALBUM'S OWN LOGO for printed sheets.
//
// It lives on the album because every print has one: a party prints from its
// main album, a share prints its album, the owner prints from an album. The
// party workspace shows this very control for its main album, so there is one
// logo to keep, not two that could disagree.
//
// Whoever prints then chooses: the NubArca mark, this logo, or — only where
// it is allowed — none. At a party "none" is the host's decision (the switch
// below, off by default); the owner's own print and a share's could always
// leave the mark out.
//
// The logo is an upload into the owner's library — a reference to an ordinary
// file, filed in no album, exactly as a party slot's photograph is.

type Status = 'idle' | 'saving' | 'saved' | 'invalid' | 'failed';

export function AlbumPrintLogoSettings({ albumId }: { albumId: string }) {
  const { t } = useI18n();
  const [settings, setSettings] = useState<AlbumPrintLogo | null>(null);
  const [status, setStatus] = useState<Status>('idle');
  const [uploadError, setUploadError] = useState<PartyImageUploadError | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    getAlbumPrintLogo(albumId, controller.signal)
      .then(setSettings)
      // Not the caller's album (an editor), or not reachable: nothing to show.
      .catch(() => { if (!controller.signal.aborted) setSettings(null); });
    return () => controller.abort();
  }, [albumId]);

  if (!settings) return null;

  async function save(next: { fileItemId: string | null; optional: boolean }) {
    setStatus('saving');
    try {
      setSettings(await setAlbumPrintLogo(albumId, next));
      setStatus('saved');
    } catch (err) {
      const code = err instanceof ApiError ? (err.body as { error?: string } | null)?.error : undefined;
      setStatus(code === 'invalid_media' ? 'invalid' : 'failed');
    }
  }

  const busy = status === 'saving';
  return (
    <div className="album-print-logo" data-testid="album-print-logo">
      <h4 className="album-print-logo-heading">{t('albumPrintLogo.heading')}</h4>
      <p className="muted">{t('albumPrintLogo.help')}</p>

      {settings.fileItemId === null ? (
        <p className="muted" data-testid="album-print-logo-none">{t('albumPrintLogo.none')}</p>
      ) : settings.previewUrl ? (
        <img
          className="album-print-logo-preview"
          src={settings.previewUrl}
          alt={t('albumPrintLogo.previewAlt')}
          data-testid="album-print-logo-preview"
        />
      ) : (
        <p className="inline-error" data-testid="album-print-logo-unavailable">{t('albumPrintLogo.unavailable')}</p>
      )}

      <div className="album-print-logo-actions">
        <PartyImageUploadButton
          disabled={busy}
          testId="album-print-logo-upload"
          onUploaded={(choice) => {
            setUploadError(null);
            void save({ fileItemId: choice.fileItemId, optional: settings.optional });
          }}
          onError={setUploadError}
        />
        {settings.fileItemId !== null && (
          <button
            type="button" disabled={busy} data-testid="album-print-logo-remove"
            onClick={() => void save({ fileItemId: null, optional: settings.optional })}
          >
            {t('albumPrintLogo.remove')}
          </button>
        )}
      </div>

      <label className="album-print-logo-optional">
        <input
          type="checkbox"
          data-testid="album-print-logo-optional"
          checked={settings.optional}
          disabled={busy}
          onChange={(e) => void save({ fileItemId: settings.fileItemId, optional: e.target.checked })}
        />
        <span>{t('albumPrintLogo.optional')}</span>
      </label>
      <p className="muted">{t('albumPrintLogo.optionalHelp')}</p>

      <span aria-live="polite">
        {status === 'saved' && <span className="muted" role="status">{t('albumPrintLogo.saved')}</span>}
      </span>
      {status === 'invalid' && <p className="inline-error" role="alert">{t('albumPrintLogo.invalid')}</p>}
      {status === 'failed' && <p className="inline-error" role="alert">{t('albumPrintLogo.failed')}</p>}
      {uploadError && <p className="inline-error" role="alert">{t(partyImageUploadErrorKey(uploadError))}</p>}
    </div>
  );
}
