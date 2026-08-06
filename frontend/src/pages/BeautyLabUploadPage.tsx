import { useCallback, useEffect, useRef, useState } from 'react';
import { useParams } from 'react-router';
import {
  ApiError,
  getBeautyLabUploadState,
  uploadBeautyLabFiles,
  type BeautyLabUploadFileResult,
  type BeautyLabUploadStatus,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { LanguageSwitcher } from '../components/LanguageSwitcher';

// PUBLIC, unauthenticated TV "Beauty Lab" (Laboratorio bellezza) mobile upload
// landing. Reached by scanning the QR shown on a paired TV. The :token is a
// short-lived capability that ONLY uploads images into the owner's Aesthetics
// Lab — no login, no owner identity, no library/lab browsing. Fire TV can't pick
// local files, so this phone page is the file chooser: a camera capture input +
// a gallery (multiple) input. When the session is unknown/expired/revoked/full
// the page shows a clear message and disables uploading.
type Phase =
  | { kind: 'loading' }
  | { kind: 'ready'; status: BeautyLabUploadStatus }
  | { kind: 'unavailable' };

export function BeautyLabUploadPage() {
  const { token } = useParams<{ token: string }>();
  const { t, tn } = useI18n();
  const [phase, setPhase] = useState<Phase>({ kind: 'loading' });
  const [busy, setBusy] = useState(false);
  const [progress, setProgress] = useState<{ done: number; total: number } | null>(null);
  const [result, setResult] = useState<
    { accepted: number; rejected: number; files: BeautyLabUploadFileResult[] } | null
  >(null);
  const [uploadError, setUploadError] = useState<string | null>(null);
  const cameraRef = useRef<HTMLInputElement>(null);
  const galleryRef = useRef<HTMLInputElement>(null);

  const refreshState = useCallback((signal?: AbortSignal) => {
    if (!token) { setPhase({ kind: 'unavailable' }); return; }
    getBeautyLabUploadState(token, signal)
      .then((s) => setPhase({ kind: 'ready', status: s.status }))
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        // 404 (unknown token) or any error → generic unavailable.
        setPhase({ kind: 'unavailable' });
      });
  }, [token]);

  useEffect(() => {
    const ctrl = new AbortController();
    refreshState(ctrl.signal);
    return () => ctrl.abort();
  }, [refreshState]);

  const handleFiles = async (list: FileList | null, input: HTMLInputElement | null) => {
    if (!token || !list || list.length === 0) return;
    const files = Array.from(list);
    setBusy(true);
    setUploadError(null);
    setResult(null);
    setProgress({ done: 0, total: files.length });
    try {
      const r = await uploadBeautyLabFiles(token, files);
      setResult({ accepted: r.accepted, rejected: r.rejected, files: r.files });
      // Adopt the post-upload lifecycle so the page flips to full/expired when
      // the session can no longer accept photos.
      setPhase({ kind: 'ready', status: r.status });
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) {
        setPhase({ kind: 'unavailable' });
        return;
      }
      setUploadError(t('beautyLabUpload.failed'));
    } finally {
      setBusy(false);
      setProgress(null);
      if (input) input.value = '';
    }
  };

  if (phase.kind === 'loading') {
    return <main className="party-upload-page"><p>{t('common.loading')}</p></main>;
  }

  if (phase.kind === 'unavailable') {
    return (
      <StateCard
        title={t('beautyLabUpload.unavailableTitle')}
        body={t('beautyLabUpload.unavailableBody')}
      />
    );
  }

  const { status } = phase;
  if (status === 'expired') {
    return <StateCard title={t('beautyLabUpload.expiredTitle')} body={t('beautyLabUpload.expiredBody')} />;
  }
  if (status === 'revoked') {
    return <StateCard title={t('beautyLabUpload.revokedTitle')} body={t('beautyLabUpload.revokedBody')} />;
  }

  const full = status === 'full';

  return (
    <main className="party-upload-page beauty-lab-upload-page">
      <div className="party-upload-card">
        <div className="party-upload-top">
          <h1>{t('beautyLabUpload.title')}</h1>
          <LanguageSwitcher className="language-switcher language-switcher-public" />
        </div>
        <p className="muted">{t('beautyLabUpload.intro')}</p>

        {full ? (
          <p role="alert" className="inline-error">{t('beautyLabUpload.fullBody')}</p>
        ) : (
          <div className="beauty-lab-upload-actions">
            {/* Camera capture. Not every browser honours `capture`; the gallery
                input below is the reliable fallback. */}
            <label className="beauty-lab-upload-button">
              {t('beautyLabUpload.takePhoto')}
              <input
                ref={cameraRef}
                type="file"
                accept="image/*"
                capture="environment"
                aria-label={t('beautyLabUpload.takePhoto')}
                onChange={(e) => void handleFiles(e.target.files, cameraRef.current)}
                disabled={busy}
              />
            </label>

            <label className="beauty-lab-upload-button">
              {t('beautyLabUpload.chooseGallery')}
              <input
                ref={galleryRef}
                type="file"
                accept="image/*"
                multiple
                aria-label={t('beautyLabUpload.chooseGallery')}
                onChange={(e) => void handleFiles(e.target.files, galleryRef.current)}
                disabled={busy}
              />
            </label>
          </div>
        )}

        <div className="party-upload-status" aria-live="polite">
          {busy && progress && (
            <p>{t('beautyLabUpload.progress', { done: String(progress.done), total: String(progress.total) })}</p>
          )}
          {busy && !progress && <p>{t('beautyLabUpload.uploading')}</p>}

          {result && (
            <>
              <p data-testid="upload-result">
                {result.accepted > 0
                  ? tn(result.accepted, 'beautyLabUpload.resultUploaded')
                  : t('beautyLabUpload.resultNone')}
                {result.rejected > 0 && tn(result.rejected, 'beautyLabUpload.rejectedSuffix')}
              </p>
              {result.files.length > 0 && (
                <ul className="beauty-lab-upload-files">
                  {result.files.map((f, i) => (
                    <li key={`${f.name}-${i}`} className={f.ok ? 'ok' : 'failed'}>
                      <span className="name">{f.name}</span>
                      <span className="mark">
                        {f.ok ? t('beautyLabUpload.fileOk') : t('beautyLabUpload.fileFailed')}
                      </span>
                    </li>
                  ))}
                </ul>
              )}
            </>
          )}
          {uploadError && <p role="alert" className="inline-error">{uploadError}</p>}
        </div>
      </div>
    </main>
  );
}

function StateCard({ title, body }: { title: string; body: string }) {
  return (
    <main className="party-upload-page beauty-lab-upload-page">
      <div className="party-upload-card">
        <h1>{title}</h1>
        <p role="alert">{body}</p>
      </div>
    </main>
  );
}
