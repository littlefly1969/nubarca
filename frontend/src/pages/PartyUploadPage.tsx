import { useCallback, useEffect, useRef, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router';
import {
  ApiError,
  classifyPartyFile,
  getPartyGuestContext,
  PARTY_VIDEO_TYPES,
  startPartyUploadSession,
  uploadToPartyWithProgress,
  type PartyUploadSession,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { PRODUCT_NAME } from '../brand/brand';
import { LanguageSwitcher } from '../components/LanguageSwitcher';
import { PartyGuestMessageForm } from '../components/PartyGuestMessageForm';
import { PartyGuestbookPanel } from '../components/PartyGuestbookPanel';
import {
  CONTRIBUTION_MODE_PARAM, contributionModeFrom, type ContributionMode,
} from './partyContributionMode';
import { useUploadWakeLock } from '../uploads/useUploadWakeLock';
import './PartyContribution.css';

// PUBLIC, unauthenticated party UPLOAD landing. Reached by scanning the "Upload
// photos" QR on a paired TV. The :token here is the SEPARATE upload token. It
// lets a guest add image(s) to the album without signing in — no login, no
// owner identity, no library browsing. When party/upload is disabled or revoked
// the API returns 404 and we show a friendly "unavailable".
// The same wordmark and switcher the guest hub uses, so arriving here reads as
// the same party rather than a different site. Byte-exact approved on-dark
// asset: this surface is a fixed dark one, whatever theme the visitor resolved.
const CONTRIBUTION_WORDMARK = {
  src: '/brand/nubarca-wordmark-on-dark-480w.png',
  width: 480,
  height: 135,
} as const;

function ContributionBrandBar() {
  return (
    <div className="party-contribution-topbar">
      <img
        className="party-contribution-logo"
        src={CONTRIBUTION_WORDMARK.src}
        alt={PRODUCT_NAME}
        width={CONTRIBUTION_WORDMARK.width}
        height={CONTRIBUTION_WORDMARK.height}
      />
      <LanguageSwitcher className="language-switcher language-switcher-public" compact />
    </div>
  );
}

function PhotoVideoIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <rect x="3.5" y="5.5" width="13" height="13" rx="2.5" />
      <path d="m5.6 15.4 3.2-3.2a1.4 1.4 0 0 1 2 0l3.5 3.5" />
      <circle cx="12.6" cy="9.6" r="1.3" />
      <path d="m16.5 10.4 4-2.2v7.6l-4-2.2" />
    </svg>
  );
}

function HeartIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d="M12 19.6C7.9 16.9 4.5 14.2 4.5 10.6A3.9 3.9 0 0 1 12 8.6a3.9 3.9 0 0 1 7.5 2c0 3.6-3.4 6.3-7.5 9Z" />
    </svg>
  );
}

function BookIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d="M4 5.2A1.7 1.7 0 0 1 5.7 3.5H19v14.3H5.7A1.7 1.7 0 0 0 4 19.5Z" />
      <path d="M4 19.5a1.7 1.7 0 0 0 1.7 1.7H19" />
      <path d="M8.2 8.1h6.6M8.2 11.4h4.4" />
    </svg>
  );
}

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
  // The mode lives in the URL, so the guest hub can link straight to the
  // composer and a reload keeps the guest where they were. Changing it REPLACES
  // the entry: flipping between the two halves of one page is not a journey,
  // and Back should leave the page rather than walk the tabs.
  const [searchParams, setSearchParams] = useSearchParams();
  // WHETHER THE WRITTEN HALF EXISTS AT ALL, from the server. A party may take
  // photographs and no greetings, and then this page is a page about
  // photographs: no switch between two halves, no composer, and a `?mode=message`
  // that somebody kept in their history resolves to media rather than to a form
  // the server would refuse. `undefined` is a backend that predates the switch,
  // where every party took greetings.
  const messagesEnabled = session?.slideshowMessagesEnabled !== false;
  const photosEnabled = session?.uploadEnabled !== false;
  const guestbookEnabled = session?.guestbookEnabled === true;
  const requestedMode = contributionModeFrom(searchParams.get(CONTRIBUTION_MODE_PARAM));
  // A mode this party does not take resolves to one it does, so a link kept in
  // somebody's history opens a page that works instead of a form the server
  // would refuse. Photographs first when they are open, because that is what a
  // party is mostly; otherwise whichever written channel exists.
  const open = (mode: ContributionMode): boolean =>
    mode === 'media' ? photosEnabled
      : mode === 'message' ? messagesEnabled
        : guestbookEnabled;
  const fallback: ContributionMode =
    photosEnabled ? 'media' : messagesEnabled ? 'message' : 'guestbook';
  const contribution: ContributionMode = open(requestedMode) ? requestedMode : fallback;
  // How many choices there really are. One is not a choice, so the group is
  // absent rather than a single pressed button with nothing to switch to.
  const offered = (['media', 'message', 'guestbook'] as const).filter(open);
  const partyUrl = session?.partyUrl ?? null;
  const setContribution = useCallback((mode: ContributionMode) => {
    setSearchParams((current) => {
      const next = new URLSearchParams(current);
      next.set(CONTRIBUTION_MODE_PARAM, mode);
      return next;
    }, { replace: true });
  }, [setSearchParams]);
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
    getPartyGuestContext(token, signal)
      // The party names itself now; the album is named only where an album
      // means something. Either is a fine heading for the upload page.
      .then((ctx) => setPhase({ kind: 'ready', albumName: ctx.albumName ?? ctx.title }))
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
    return (
      <main className="party-contribution" aria-busy="true">
        <div className="party-contribution-shell">
          <ContributionBrandBar />
          <div className="party-contribution-state"><p>{t('common.loading')}</p></div>
        </div>
      </main>
    );
  }
  if (phase.kind === 'unavailable') {
    return (
      <main className="party-contribution">
        <div className="party-contribution-shell">
          <ContributionBrandBar />
          <div className="party-contribution-state">
            <h1>{t('partyUpload.unavailableTitle')}</h1>
            <p role="alert">{t('partyUpload.unavailableBody')}</p>
          </div>
        </div>
      </main>
    );
  }
  if (phase.kind === 'error') {
    return (
      <main className="party-contribution">
        <div className="party-contribution-shell">
          <ContributionBrandBar />
          <div className="party-contribution-state">
            <h1>{t('partyUpload.errorTitle')}</h1>
            <p role="alert">{t('partyUpload.errorBody')}</p>
            <button
              type="button"
              className="party-contribution-secondary"
              onClick={() => probe()}
            >
              {t('common.tryAgain')}
            </button>
          </div>
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
    <main className="party-contribution">
      <div className="party-contribution-shell">
        <ContributionBrandBar />
        <header className="party-contribution-head">
          <h1 className="party-contribution-title">
            {phase.albumName
              ? t('partyUpload.titleTo', { album: phase.albumName })
              : t('partyUpload.titleGeneric')}
          </h1>
          <p className="party-contribution-help">{t('partyUpload.subtitle')}</p>
        </header>

        {/* THE WAY BACK. This page is reached from the party and had no way
            home: a guest who finished contributing was left with the browser's
            Back button, or nothing at all if they arrived from a QR code. */}
        {partyUrl && (
          <Link className="party-contribution-back" to={partyUrl} data-testid="party-upload-back">
            ← {t('partyUpload.backToParty')}
          </Link>
        )}

        {/* Two buttons in a named group rather than a half-built tablist: the
            previous markup announced tabs without aria-controls, panels or the
            arrow-key behaviour a tablist promises.

            The group is ABSENT, not disabled, when the party takes no
            greetings. A choice between one thing is not a choice, and a
            disabled half would be the product advertising something this party
            does not have. */}
        {/* One button per channel this party actually takes, in a named group
            rather than a half-built tablist. The group is ABSENT when there is
            only one: a choice between one thing is not a choice, and a lone
            pressed button is the product advertising a decision nobody has.

            Switching is locked while media is in flight — moving away would
            leave the queue running behind a progress bar nobody can see. */}
        {offered.length > 1 && (
        <div className="party-contribution-modes" role="group" aria-label={t('partyUpload.modeLabel')}>
          {offered.includes('media') && (
          <button
            type="button"
            className="party-contribution-mode"
            data-testid="party-mode-media"
            aria-pressed={contribution === 'media'}
            onClick={() => setContribution('media')}
            disabled={busy}
          >
            <PhotoVideoIcon />
            {t('partyMessage.tabMedia')}
          </button>
          )}
          {offered.includes('message') && (
          <button
            type="button"
            className="party-contribution-mode"
            data-testid="party-mode-message"
            aria-pressed={contribution === 'message'}
            onClick={() => setContribution('message')}
            disabled={busy}
          >
            <HeartIcon />
            {t('partyMessage.tabMessage')}
          </button>
          )}
          {offered.includes('guestbook') && (
          <button
            type="button"
            className="party-contribution-mode"
            data-testid="party-mode-guestbook"
            aria-pressed={contribution === 'guestbook'}
            onClick={() => setContribution('guestbook')}
            disabled={busy}
          >
            <BookIcon />
            {t('partyMessage.tabGuestbook')}
          </button>
          )}
        </div>
        )}

        {contribution === 'message' && token && (
          <PartyGuestMessageForm
            uploadToken={token}
            remainingSends={session?.remainingMessages ?? null}
            onShareMedia={() => setContribution('media')}
          />
        )}

        {contribution === 'guestbook' && token && (
          <PartyGuestbookPanel
            token={token}
            remaining={session?.remainingGuestbookEntries ?? null}
          />
        )}

        {contribution === 'media' && photosEnabled && (<>
        <p className="party-contribution-intro">{t('partyUpload.intro')}</p>

        {session && (
          <ul className="party-contribution-quota" data-testid="upload-quota">
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

        {/* Visually hidden but still a real, focusable input, so the picker is a
            thumb-sized surface for touch AND reachable from the keyboard. The
            aria-label stays on the input: it is the control, and the label
            beside it must not become a second accessible name for it. */}
        <input
          ref={inputRef}
          id="party-media-input"
          className="party-contribution-file-input"
          type="file"
          // Photos AND the video containers the party pipeline accepts. The
          // server re-checks every byte; this only shapes the picker.
          accept={['image/*', ...PARTY_VIDEO_TYPES].join(',')}
          multiple
          aria-label={t('partyUpload.chooseMedia')}
          onChange={(e) => onSelect(e.target.files)}
          disabled={busy}
        />
        <label className="party-contribution-picker" htmlFor="party-media-input">
          <span className="party-contribution-picker-icon" aria-hidden="true">
            <PhotoVideoIcon />
          </span>
          <span className="party-contribution-picker-text">
            <strong>{t('partyUpload.pickerTitle')}</strong>
            <span>
              {files.length > 0
                ? tn(files.length, 'partyUpload.selected')
                : t('partyUpload.pickerHelp')}
            </span>
          </span>
        </label>

        {!busy && (overPhotoQuota || overVideoQuota || unsupported > 0) && (
          <div className="party-contribution-notice" role="status">
            {overPhotoQuota && <p>{t('partyUpload.overPhotoQuota')}</p>}
            {overVideoQuota && <p>{t('partyUpload.overVideoQuota')}</p>}
            {unsupported > 0 && <p>{t('partyUpload.unsupportedSelected')}</p>}
          </div>
        )}

        <div className="party-contribution-actions">
          <button
            type="button"
            className="party-contribution-primary"
            onClick={() => void handleUpload()}
            disabled={busy || files.length === 0}
          >
            {uploadLabel}
          </button>
        </div>

        {busy && (
          <div className="party-contribution-progress" data-testid="upload-progress">
            <div
              className="party-contribution-progressbar"
              role="progressbar"
              aria-valuemin={0}
              aria-valuemax={100}
              aria-valuenow={Math.round(progress * 100)}
            >
              <div
                className="party-contribution-progressbar-fill"
                style={{ width: `${Math.round(progress * 100)}%` }}
              />
            </div>
            <p className="party-contribution-progress-label" aria-live="polite">
              {progress >= 1
                ? t('partyUpload.processing')
                : t('partyUpload.uploadingPercent', { percent: Math.round(progress * 100) })}
            </p>
            <p className="party-contribution-progress-note" role="alert">{t('partyUpload.doNotClose')}</p>
          </div>
        )}

        <div className="party-contribution-status" aria-live="polite">
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
