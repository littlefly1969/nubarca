import { useCallback, useEffect, useRef, useState } from 'react';
import { useParams } from 'react-router';
import {
  ApiError,
  getPartyAlbum,
  uploadToPartyWithProgress,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { LanguageSwitcher } from '../components/LanguageSwitcher';

// PUBLIC, unauthenticated party UPLOAD landing. Reached by scanning the "Upload
// photos" QR on a paired TV. The :token here is the SEPARATE upload token. It
// lets a guest add image(s) to the album without signing in — no login, no
// owner identity, no library browsing. When party/upload is disabled or revoked
// the API returns 404 and we show a friendly "unavailable".
type Phase =
  | { kind: 'loading' }
  | { kind: 'ready'; albumName: string }
  | { kind: 'unavailable' }
  | { kind: 'error' };

export function PartyUploadPage() {
  const { token } = useParams<{ token: string }>();
  const { t, tn } = useI18n();
  const [phase, setPhase] = useState<Phase>({ kind: 'loading' });
  const [files, setFiles] = useState<File[]>([]);
  const [busy, setBusy] = useState(false);
  // Fraction (0..1) of bytes sent for the current upload; reaches 1 while the
  // server still processes, so we show a "processing" state after that.
  const [progress, setProgress] = useState(0);
  const [result, setResult] = useState<{ accepted: number; rejected: number } | null>(null);
  const [uploadError, setUploadError] = useState<string | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  // While an upload is in flight, warn the guest before they close/reload the
  // tab — a single request, so leaving aborts the whole upload and loses it.
  useEffect(() => {
    if (!busy) return;
    const onBeforeUnload = (e: BeforeUnloadEvent) => {
      e.preventDefault();
      e.returnValue = '';
    };
    window.addEventListener('beforeunload', onBeforeUnload);
    return () => window.removeEventListener('beforeunload', onBeforeUnload);
  }, [busy]);

  // Probe the album header via the upload token. The public GET /api/party
  // endpoints use the VIEW token, so we cannot fetch items here — but the same
  // upload token is accepted by a HEAD-like read? No: instead we just try to
  // resolve the album name defensively; a 404 means the link is down.
  const probe = useCallback((signal?: AbortSignal) => {
    if (!token) { setPhase({ kind: 'unavailable' }); return; }
    setPhase({ kind: 'loading' });
    // The upload token is NOT a view token, so the album-name probe may 404 even
    // when uploads are allowed. Treat a 404 here as "show the generic upload
    // page" rather than unavailable — the authoritative check is the POST.
    getPartyAlbum(token, signal)
      .then((a) => setPhase({ kind: 'ready', albumName: a.albumName }))
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 404) {
          // Expected: upload token can't read the album. Render a generic page.
          setPhase({ kind: 'ready', albumName: '' });
          return;
        }
        setPhase({ kind: 'error' });
      });
  }, [token]);

  useEffect(() => {
    const ctrl = new AbortController();
    probe(ctrl.signal);
    return () => ctrl.abort();
  }, [probe]);

  const onSelect = (list: FileList | null) => {
    setResult(null);
    setUploadError(null);
    setFiles(list ? Array.from(list) : []);
  };

  const handleUpload = async () => {
    if (!token || files.length === 0) return;
    setBusy(true);
    setUploadError(null);
    setResult(null);
    setProgress(0);
    try {
      const r = await uploadToPartyWithProgress(token, files, setProgress);
      setResult(r);
      setFiles([]);
      if (inputRef.current) inputRef.current.value = '';
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) {
        setPhase({ kind: 'unavailable' });
        return;
      }
      setUploadError(t('partyUpload.failed'));
    } finally {
      setBusy(false);
      setProgress(0);
    }
  };

  if (phase.kind === 'loading') {
    return <main className="party-upload-page"><p>{t('common.loading')}</p></main>;
  }
  if (phase.kind === 'unavailable') {
    return (
      <main className="party-upload-page">
        <div className="party-upload-card">
          <h1>{t('partyUpload.unavailableTitle')}</h1>
          <p role="alert">{t('partyUpload.unavailableBody')}</p>
        </div>
      </main>
    );
  }
  if (phase.kind === 'error') {
    return (
      <main className="party-upload-page">
        <div className="party-upload-card">
          <h1>{t('partyUpload.errorTitle')}</h1>
          <p role="alert">{t('partyUpload.errorBody')}</p>
          <button type="button" onClick={() => probe()}>{t('common.tryAgain')}</button>
        </div>
      </main>
    );
  }

  const uploadLabel = busy
    ? t('partyUpload.uploading')
    : files.length === 0
      ? t('partyUpload.uploadPhotos')
      : tn(files.length, 'partyUpload.uploadCount');

  return (
    <main className="party-upload-page">
      <div className="party-upload-card">
        <div className="party-upload-top">
          <h1>
            {phase.albumName
              ? t('partyUpload.titleTo', { album: phase.albumName })
              : t('partyUpload.titleGeneric')}
          </h1>
          <LanguageSwitcher className="language-switcher language-switcher-public" />
        </div>
        <p className="muted">{t('partyUpload.intro')}</p>

        <input
          ref={inputRef}
          type="file"
          accept="image/*"
          multiple
          aria-label={t('partyUpload.choosePhotos')}
          onChange={(e) => onSelect(e.target.files)}
          disabled={busy}
        />

        <div className="party-upload-actions">
          <button
            type="button"
            onClick={() => void handleUpload()}
            disabled={busy || files.length === 0}
          >
            {uploadLabel}
          </button>
          {files.length > 0 && !busy && (
            <span className="muted">{tn(files.length, 'partyUpload.selected')}</span>
          )}
        </div>

        {busy && (
          <div className="party-upload-progress" data-testid="upload-progress">
            <div
              className="party-upload-progressbar"
              role="progressbar"
              aria-valuemin={0}
              aria-valuemax={100}
              aria-valuenow={Math.round(progress * 100)}
            >
              <div
                className="party-upload-progressbar-fill"
                style={{ width: `${Math.round(progress * 100)}%` }}
              />
            </div>
            <p className="party-upload-progress-label" aria-live="polite">
              {progress >= 1
                ? t('partyUpload.processing')
                : t('partyUpload.uploadingPercent', { percent: Math.round(progress * 100) })}
            </p>
            <p className="party-upload-warning" role="alert">{t('partyUpload.doNotClose')}</p>
          </div>
        )}

        <div className="party-upload-status" aria-live="polite">
          {result && (
            <p data-testid="upload-result">
              {result.accepted > 0
                ? tn(result.accepted, 'partyUpload.resultUploaded')
                : t('partyUpload.resultNone')}
              {result.rejected > 0 && tn(result.rejected, 'partyUpload.rejectedSuffix')}
            </p>
          )}
          {uploadError && <p role="alert" className="inline-error">{uploadError}</p>}
        </div>
      </div>
    </main>
  );
}
