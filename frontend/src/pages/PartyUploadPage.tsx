import { useCallback, useEffect, useRef, useState } from 'react';
import { useParams } from 'react-router';
import {
  ApiError,
  classifyPartyFile,
  getPartyAlbum,
  PARTY_VIDEO_TYPES,
  startPartyUploadSession,
  uploadToPartyWithProgress,
  type PartyUploadSession,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { LanguageSwitcher } from '../components/LanguageSwitcher';
import { PartyGuestMessageForm } from '../components/PartyGuestMessageForm';

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

// Aggregate outcome of one upload RUN (which is many requests — see the queue
// below), so the guest reads one sentence rather than a line per file.
interface RunResult {
  acceptedPhotos: number;
  acceptedVideos: number;
  rejected: number;
  quotaRejectedPhotos: number;
  quotaRejectedVideos: number;
}

const EMPTY_RUN: RunResult = {
  acceptedPhotos: 0, acceptedVideos: 0, rejected: 0,
  quotaRejectedPhotos: 0, quotaRejectedVideos: 0,
};

// Videos are large. One multipart request carrying every selected clip would
// mean a single failure loses the whole batch, a progress bar that says nothing
// useful, and a request big enough for a proxy to refuse outright. The queue
// sends ONE media per request to the SAME endpoint — no new upload protocol,
// no chunking — so each file settles independently and the quota reported by
// each response steers the rest of the run.
const QUEUE_CONCURRENCY = 1;

// Best-effort Screen Wake Lock for one foreground upload. The initial request
// is made directly from the upload click (important on WebKit), then reacquired
// only when an in-flight upload returns to a visible tab. Unsupported/denied
// locks never affect the upload itself.
function useUploadWakeLock() {
  const wantedRef = useRef(false);
  const lockRef = useRef<WakeLockSentinel | null>(null);
  const pendingRef = useRef<Promise<void> | null>(null);

  const releaseCurrent = useCallback(() => {
    const lock = lockRef.current;
    lockRef.current = null;
    if (lock && !lock.released) void lock.release().catch(() => { /* best effort */ });
  }, []);

  const request = useCallback(() => {
    if (!wantedRef.current || document.visibilityState !== 'visible'
      || lockRef.current !== null || pendingRef.current !== null
      || !('wakeLock' in navigator)) return;

    try {
      let pending: Promise<void>;
      pending = navigator.wakeLock.request('screen')
        .then(async (lock) => {
          if (!wantedRef.current || document.visibilityState !== 'visible') {
            await lock.release().catch(() => { /* upload already stopped/hidden */ });
            return;
          }
          lockRef.current = lock;
          lock.addEventListener('release', () => {
            if (lockRef.current === lock) lockRef.current = null;
          }, { once: true });
        })
        .catch(() => { /* unsupported by policy/system: keep uploading */ })
        .finally(() => {
          if (pendingRef.current === pending) pendingRef.current = null;
        });
      pendingRef.current = pending;
    } catch {
      // A synchronous browser rejection is also non-fatal to the upload.
    }
  }, []);

  const start = useCallback(() => {
    wantedRef.current = true;
    request();
  }, [request]);

  const stop = useCallback(() => {
    wantedRef.current = false;
    releaseCurrent();
  }, [releaseCurrent]);

  useEffect(() => {
    const onVisibilityChange = () => {
      if (document.visibilityState === 'visible') {
        // If the hidden document still has a request settling, retry exactly
        // after it clears its pending guard. A successful old request makes the
        // retry a no-op; a rejected one no longer loses this visible transition.
        const pending = pendingRef.current;
        if (pending) void pending.then(request);
        else request();
      } else releaseCurrent();
    };
    document.addEventListener('visibilitychange', onVisibilityChange);
    return () => {
      document.removeEventListener('visibilitychange', onVisibilityChange);
      wantedRef.current = false;
      releaseCurrent();
    };
  }, [releaseCurrent, request]);

  return { start, stop };
}

export function PartyUploadPage() {
  const { token } = useParams<{ token: string }>();
  const { t, tn } = useI18n();
  const [phase, setPhase] = useState<Phase>({ kind: 'loading' });
  const [files, setFiles] = useState<File[]>([]);
  const [busy, setBusy] = useState(false);
  // Fraction (0..1) of bytes sent for the current upload; reaches 1 while the
  // server still processes, so we show a "processing" state after that.
  const [progress, setProgress] = useState(0);
  const [result, setResult] = useState<RunResult | null>(null);
  const [uploadError, setUploadError] = useState<string | null>(null);
  const [session, setSession] = useState<PartyUploadSession | null>(null);
  // Which of the two contributions the guest is making. Media is the default:
  // a party is still mostly photographs, and the written channel is an addition
  // to that rather than a competitor for the same screen.
  const [contribution, setContribution] = useState<'media' | 'message'>('media');
  const inputRef = useRef<HTMLInputElement>(null);
  const uploadWakeLock = useUploadWakeLock();

  // Resolve (or create) this guest's upload session so the quota can be shown
  // before anything is picked. Failure is NOT fatal: the upload endpoint
  // resolves the session itself, so a guest can still upload without this.
  useEffect(() => {
    if (!token) return;
    const ctrl = new AbortController();
    startPartyUploadSession(token, ctrl.signal)
      .then(setSession)
      .catch(() => { /* quota display is best-effort; the server stays authoritative */ });
    return () => ctrl.abort();
  }, [token]);

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
    // Keep this call before the first await: the click's user activation gives
    // WebKit the best chance to grant the initial lock. It is held for the WHOLE
    // queue, not per request, so the screen cannot sleep between two videos.
    uploadWakeLock.start();
    setBusy(true);
    setUploadError(null);
    setResult(null);
    setProgress(0);

    const run: RunResult = { ...EMPTY_RUN };
    // Once the SERVER says a kind is full, stop sending more of that kind — but
    // keep sending the other, because the two quotas are independent. This is
    // the race the client cannot pre-empt: another of the guest's own tabs, or
    // a lowered quota, can exhaust a kind mid-run.
    let photosFull = false;
    let videosFull = false;
    let failed = false;

    try {
      for (let i = 0; i < files.length; i += QUEUE_CONCURRENCY) {
        const file = files[i];
        const kind = classifyPartyFile(file);
        if ((kind === 'photo' && photosFull) || (kind === 'video' && videosFull)) {
          // Not sent at all: the server already told us there is no room.
          if (kind === 'photo') run.quotaRejectedPhotos += 1;
          else run.quotaRejectedVideos += 1;
          run.rejected += 1;
          setProgress((i + 1) / files.length);
          continue;
        }

        const response = await uploadToPartyWithProgress(
          token, [file],
          // Whole-run progress: this file's own fraction, scaled into its slot.
          (fraction) => setProgress((i + fraction) / files.length),
        );

        run.acceptedPhotos += response.acceptedPhotos ?? 0;
        run.acceptedVideos += response.acceptedVideos ?? 0;
        run.quotaRejectedPhotos += response.quotaRejectedPhotos ?? 0;
        run.quotaRejectedVideos += response.quotaRejectedVideos ?? 0;
        run.rejected += response.rejected ?? 0;
        if (response.remainingPhotos === 0) photosFull = true;
        if (response.remainingVideos === 0) videosFull = true;
        // Every response carries the fresh quota, so the header stays truthful
        // during a long run without a second round-trip.
        setSession((current) => (current === null ? current : {
          ...current,
          usedPhotos: current.usedPhotos + (response.acceptedPhotos ?? 0),
          usedVideos: current.usedVideos + (response.acceptedVideos ?? 0),
          remainingPhotos: response.remainingPhotos ?? current.remainingPhotos,
          remainingVideos: response.remainingVideos ?? current.remainingVideos,
        }));
        setProgress((i + 1) / files.length);
      }
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) {
        setPhase({ kind: 'unavailable' });
        uploadWakeLock.stop();
        setBusy(false);
        setProgress(0);
        return;
      }
      failed = true;
      setUploadError(t('partyUpload.failed'));
    } finally {
      uploadWakeLock.stop();
      setBusy(false);
      setProgress(0);
    }

    setResult(run);
    if (!failed) {
      setFiles([]);
      if (inputRef.current) inputRef.current.value = '';
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
      ? t('partyUpload.uploadMedia')
      : tn(files.length, 'partyUpload.uploadCount');

  // Client-side selection check. UX ONLY — the backend decides. It exists so a
  // guest is told before a long video upload that there is no room for it,
  // rather than after.
  const selectedPhotos = files.filter((f) => classifyPartyFile(f) === 'photo').length;
  const selectedVideos = files.filter((f) => classifyPartyFile(f) === 'video').length;
  const unsupported = files.filter((f) => classifyPartyFile(f) === 'unsupported').length;
  const overPhotoQuota = session?.remainingPhotos != null && selectedPhotos > session.remainingPhotos;
  const overVideoQuota = session?.remainingVideos != null && selectedVideos > session.remainingVideos;

  const quotaLine = (
    remaining: number | null,
    max: number | null,
    unlimitedKey: 'partyUpload.photosUnlimited' | 'partyUpload.videosUnlimited',
    countKey: 'partyUpload.photosRemaining' | 'partyUpload.videosRemaining',
  ) => (remaining == null || max == null
    ? t(unlimitedKey)
    : t(countKey, { remaining: String(remaining), max: String(max) }));

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
        <div className="party-contribution-tabs" role="tablist">
          <button
            type="button"
            role="tab"
            aria-selected={contribution === 'media'}
            className={contribution === 'media' ? 'active' : undefined}
            onClick={() => setContribution('media')}
            disabled={busy}
          >
            {t('partyMessage.tabMedia')}
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={contribution === 'message'}
            className={contribution === 'message' ? 'active' : undefined}
            onClick={() => setContribution('message')}
            // Switching away mid-upload would leave the queue running behind a
            // hidden progress bar, so the choice is locked while media is
            // in flight.
            disabled={busy}
          >
            {t('partyMessage.tabMessage')}
          </button>
        </div>

        {contribution === 'message' && token && (
          <PartyGuestMessageForm uploadToken={token} />
        )}

        {contribution === 'media' && (<>
        <p className="muted">{t('partyUpload.intro')}</p>

        {session && (
          <ul className="party-upload-quota" data-testid="upload-quota">
            <li>{quotaLine(
              session.remainingPhotos, session.maxPhotos,
              'partyUpload.photosUnlimited', 'partyUpload.photosRemaining')}
            </li>
            <li>{quotaLine(
              session.remainingVideos, session.maxVideos,
              'partyUpload.videosUnlimited', 'partyUpload.videosRemaining')}
            </li>
          </ul>
        )}

        <input
          ref={inputRef}
          type="file"
          // Photos AND the video containers the party pipeline accepts. The
          // server re-checks every byte; this only shapes the picker.
          accept={['image/*', ...PARTY_VIDEO_TYPES].join(',')}
          multiple
          aria-label={t('partyUpload.chooseMedia')}
          onChange={(e) => onSelect(e.target.files)}
          disabled={busy}
        />

        {!busy && (overPhotoQuota || overVideoQuota || unsupported > 0) && (
          <div className="party-upload-selection-warning" role="status">
            {overPhotoQuota && <p>{t('partyUpload.overPhotoQuota')}</p>}
            {overVideoQuota && <p>{t('partyUpload.overVideoQuota')}</p>}
            {unsupported > 0 && <p>{t('partyUpload.unsupportedSelected')}</p>}
          </div>
        )}

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
            <div data-testid="upload-result">
              <p>
                {result.acceptedPhotos + result.acceptedVideos > 0
                  ? t('partyUpload.resultMixed', {
                    photos: String(result.acceptedPhotos),
                    videos: String(result.acceptedVideos),
                  })
                  : t('partyUpload.resultNone')}
                {result.rejected > 0 && tn(result.rejected, 'partyUpload.rejectedSuffix')}
              </p>
              {result.quotaRejectedPhotos > 0 && (
                <p className="muted">{t('partyUpload.photoQuotaReached')}</p>
              )}
              {result.quotaRejectedVideos > 0 && (
                <p className="muted">{t('partyUpload.videoQuotaReached')}</p>
              )}
            </div>
          )}
          {uploadError && <p role="alert" className="inline-error">{uploadError}</p>}
        </div>
        </>)}
      </div>
    </main>
  );
}
