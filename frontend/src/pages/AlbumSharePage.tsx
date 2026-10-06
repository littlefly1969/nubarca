import { useCallback, useEffect, useRef, useState } from 'react';
import { useParams } from 'react-router';
import {
  ALBUM_SHARE_ERRORS,
  ApiError,
  challengeAlbumShare,
  getAlbumShare,
  getAlbumShareItems,
  uploadToAlbumShareWithProgress,
  verifyAlbumShare,
  type AlbumShareItem,
  type AlbumSharePublic,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { PRODUCT_NAME } from '../brand/brand';
import { LanguageSwitcher } from '../components/LanguageSwitcher';
import { Icon } from '../components/icons/Icon';
import { HomeScreenButton } from '../homeScreen/HomeScreenButton';
import { PublicGallery } from '../publicMedia/PublicGallery';
import { PublicMediaViewer } from '../publicMedia/PublicMediaViewer';
import {
  restartAlbumShareApp, useHomeScreenAppMismatch, useHomeScreenTitle,
} from '../homeScreen/homeScreen';
import { useUploadWakeLock } from '../uploads/useUploadWakeLock';
import { fileKey, loadDone, markDone, partition } from '../uploads/uploadQueueStore';
import './PartyContribution.css';
import './AlbumShare.css';

// AN ALBUM, SHARED BY LINK. The public page anybody holding the address opens.
//
// It borrows the party contribution page's shell deliberately — the same dark
// surface, the same brand bar, the same one-column rhythm on a phone — because
// a visitor arriving from a message should not be able to tell which of the two
// kinds of link they were sent. What it does NOT borrow is the party itself:
// no phases, no television, no guests who arrive, no moderation. An album has
// none of those, and this page never mentions one.
//
// Three things happen here and nothing else: look, take, add. There is no way
// to remove a photograph, because the route does not exist on the server.
//
// The same page is the album's home-screen app (src/homeScreen): opened by its
// link it moves under /album/app/<key>/open/<token>, the album's own scope, and
// offers "Installa album". The app opens the link and nothing more — a revoked
// or rotated link closes it, and a protected album still asks for its code.

const WORDMARK = {
  src: '/brand/nubarca-wordmark-on-dark-480w.png',
  width: 480,
  height: 135,
} as const;

type Status =
  | { kind: 'loading' }
  | { kind: 'locked' }
  | { kind: 'ready'; album: AlbumSharePublic; items: AlbumShareItem[] }
  | { kind: 'error' }
  | { kind: 'gone' };

export function AlbumSharePage() {
  const { token = '' } = useParams<{ token: string }>();
  const { t } = useI18n();
  // An album's app whose key is not this token's album: a made-up address,
  // shown as unavailable rather than as another album inside this one's app.
  const mismatch = useHomeScreenAppMismatch();
  const [status, setStatus] = useState<Status>(mismatch ? { kind: 'gone' } : { kind: 'loading' });
  const [refreshFailed, setRefreshFailed] = useState(false);
  useHomeScreenTitle(status.kind === 'ready' ? status.album.albumName : null);

  const load = useCallback(async (signal?: AbortSignal, refresh = false) => {
    try {
      const album = await getAlbumShare(token, signal);
      const items = await getAlbumShareItems(token, signal);
      if (!signal?.aborted) {
        setStatus({ kind: 'ready', album, items: items.items });
        setRefreshFailed(false);
      }
    } catch (error) {
      if (signal?.aborted) return;
      // A refresh must not unmount the upload's confirmation or retry queue.
      if (refresh) { setRefreshFailed(true); return; }
      // 401 is the ONE answer that is not "nothing here": the link is live and
      // the visitor is expected — they simply have not proved who they are.
      setStatus(error instanceof ApiError && error.status === 401
        ? { kind: 'locked' }
        : error instanceof ApiError && error.status === 404
          ? { kind: 'gone' } : { kind: 'error' });
    }
  }, [token]);

  useEffect(() => {
    if (mismatch) return undefined;
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load, mismatch]);

  return (
    <main className="party-contribution album-share">
      <div className="party-contribution-shell">
        <div className="party-contribution-topbar">
          <img
            className="party-contribution-logo"
            src={WORDMARK.src}
            alt={PRODUCT_NAME}
            width={WORDMARK.width}
            height={WORDMARK.height}
          />
          <div className="album-share-topbar-actions">
            {status.kind === 'ready' && <HomeScreenButton subject="album" />}
            <LanguageSwitcher className="language-switcher language-switcher-public" compact />
          </div>
        </div>

        {status.kind === 'loading' && (
          <div className="party-contribution-state"><p>{t('common.loading')}</p></div>
        )}

        {status.kind === 'gone' && (
          <div className="party-contribution-state" data-testid="album-share-gone">
            <h1>{t('albumLink.goneTitle')}</h1>
            <p>{t('albumLink.goneBody')}</p>
          </div>
        )}

        {status.kind === 'error' && (
          <div className="party-contribution-state" role="alert">
            <p>{t('albumLink.loadFailed')}</p>
            <button className="party-contribution-secondary" onClick={() => void load()}>
              {t('common.retry')}
            </button>
          </div>
        )}

        {status.kind === 'locked' && (
          // Verified: an album page starts again WITH the grant, so it moves
          // under its app (or its app checks its key) — elsewhere, it reloads.
          <SecondFactorGate token={token} onVerified={() => { if (!restartAlbumShareApp()) void load(); }} />
        )}

        {status.kind === 'ready' && (
          <AlbumShareBody
            key={token}
            token={token}
            album={status.album}
            items={status.items}
            onChanged={() => void load(undefined, true)}
          />
        )}
        {refreshFailed && (
          <div className="album-share-refresh" role="alert">
            <p>{t('albumLink.refreshFailed')}</p>
            <button className="party-contribution-secondary" onClick={() => void load(undefined, true)}>
              {t('common.retry')}
            </button>
          </div>
        )}
      </div>
    </main>
  );
}

// ── The album ─────────────────────────────────────────────────────────────

function AlbumShareBody({
  token, album, items, onChanged,
}: {
  token: string;
  album: AlbumSharePublic;
  items: AlbumShareItem[];
  onChanged(): void;
}) {
  const { t, tn } = useI18n();
  const [coverFailed, setCoverFailed] = useState(false);
  // The open picture, by position, so ‹ › and a swipe walk the album; and the
  // tile it was opened from, so focus goes back there.
  const [openIndex, setOpenIndex] = useState<number | null>(null);
  const openerRef = useRef<HTMLElement | null>(null);
  const close = useCallback(() => {
    setOpenIndex(null);
    openerRef.current?.focus?.();
    openerRef.current = null;
  }, []);
  // Clamped in the update itself: several key presses can land before a render.
  const move = useCallback((delta: number) => setOpenIndex((i) => (
    i === null ? i : Math.max(0, Math.min(items.length - 1, i + delta)))), [items.length]);
  const shown = openIndex === null ? null : items[openIndex] ?? null;

  return (
    <>
      <header className="album-share-head">
        {album.coverUrl && !coverFailed && (
          <img className="album-share-cover" src={album.coverUrl} alt="" onError={() => setCoverFailed(true)} />
        )}
        <div className="album-share-heading">
          <p className="album-share-eyebrow">{t('albumLink.sharedAlbum')}</p>
          <h1 className="album-share-title">{album.albumName}</h1>
          <p className="album-share-count">
            {tn(album.itemCount, 'albumLink.itemCount')}
          </p>
        </div>
      </header>

      <AlbumShareUpload token={token} album={album} onDone={onChanged} />

      {items.length === 0 ? (
        <p className="party-contribution-intro" data-testid="album-share-empty">
          {t('albumLink.empty')}
        </p>
      ) : (
        // The party's own mosaic: an album shared by link and a party's
        // photographs are the same thing to look at.
        <div className="album-share-gallery">
          <PublicGallery
            testId="album-share-grid"
            itemTestId={(item) => `album-share-item-${item.id}`}
            items={items}
            onOpen={(index, tile) => { openerRef.current = tile; setOpenIndex(index); }}
          />
        </div>
      )}

      {shown && openIndex !== null && (
        <PublicMediaViewer
          item={{
            id: shown.id,
            kind: shown.isVideo ? 'video' : 'image',
            // MEDIUM in the viewer, again as the rest of the product does; a
            // video is PLAYED from its ladder, its poster until it starts.
            previewUrl: shown.previewUrl,
            playbackUrl: shown.playbackUrl,
            // The original leaves only through this, and only when the owner allowed it.
            downloadUrl: shown.downloadUrl,
          }}
          label={shown.isVideo ? t('party.videoViewer') : t('party.photoViewer')}
          downloadLabel={t(album.canDownloadOriginal ? 'albumLink.downloadOriginal' : 'albumLink.download')}
          // Said, rather than left as a button that would answer 404.
          note={shown.isVideo && !shown.downloadUrl ? t('albumLink.videoNeedsOriginals') : null}
          onClose={close}
          onPrevious={openIndex > 0 ? () => move(-1) : undefined}
          onNext={openIndex < items.length - 1 ? () => move(1) : undefined}
        />
      )}
    </>
  );
}

// ── Adding to it ──────────────────────────────────────────────────────────

type Run = {
  total: number;
  sent: number;
  failed: number;
  skipped: number;
  stopped: string | null;
  uncertain: boolean;
  current: number;
  name: string;
  fraction: number;
};

function AlbumShareUpload({
  token, album, onDone,
}: {
  token: string;
  album: AlbumSharePublic;
  onDone(): void;
}) {
  const { t, tn } = useI18n();
  const inputRef = useRef<HTMLInputElement | null>(null);
  const activeRef = useRef<AbortController | null>(null);
  const rememberedRef = useRef(new Set<string>());
  const [run, setRun] = useState<Run | null>(null);
  const [busy, setBusy] = useState(false);
  const [retryFiles, setRetryFiles] = useState<File[]>([]);
  const [alreadySent, setAlreadySent] = useState(0);
  const wakeLock = useUploadWakeLock();

  useEffect(() => {
    let live = true;
    void loadDone(token).catch(() => rememberedRef.current).then((done) => {
      if (live) {
        for (const key of done) rememberedRef.current.add(key);
        setAlreadySent(rememberedRef.current.size);
      }
    });
    return () => { live = false; activeRef.current?.abort(); };
  }, [token]);

  useEffect(() => {
    if (!busy) return;
    const beforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', beforeUnload);
    return () => window.removeEventListener('beforeunload', beforeUnload);
  }, [busy]);

  async function send(files: readonly File[]) {
    if (files.length === 0 || activeRef.current || !album.canUpload) return;
    const controller = new AbortController();
    activeRef.current = controller;
    setBusy(true);
    setRetryFiles([]);
    setRun(null);
    wakeLock.start();
    try {
      const stored = await loadDone(token).catch(() => rememberedRef.current);
      if (controller.signal.aborted) return;
      const done = new Set([...stored, ...rememberedRef.current]);
      const { pending, skipped } = partition(files, done);
      // A duplicate in the same selection must not consume a second slot.
      const seen = new Set<string>();
      const unique = pending.filter((file) => {
        const key = fileKey(file);
        if (seen.has(key)) { skipped.push(file); return false; }
        seen.add(key); return true;
      });
      const progress: Run = {
        total: unique.length, sent: 0, failed: 0, skipped: skipped.length,
        stopped: null, uncertain: false, current: 0, name: '', fraction: 0,
      };
      const retry: File[] = [];
      setRun({ ...progress });

      for (const [index, file] of unique.entries()) {
        if (controller.signal.aborted) return;
        progress.current = index + 1;
        progress.name = file.name;
        progress.fraction = 0;
        setRun({ ...progress });
        try {
          const report = await uploadToAlbumShareWithProgress(token, file, (fraction) => {
            if (controller.signal.aborted) return;
            progress.fraction = fraction;
            setRun({ ...progress });
          }, controller.signal);
          if (controller.signal.aborted) return;
          if (report.accepted > 0) {
            progress.sent += 1;
            rememberedRef.current.add(fileKey(file));
            // Storage is best effort; an accepted file stays accepted if it fails.
            await markDone(token, fileKey(file)).catch(() => {});
          } else if (report.rejected > 0) {
            progress.failed += 1;
            retry.push(file);
          }
          if (report.stopped) {
            progress.stopped = report.stopped;
            retry.push(...unique.slice(index + (report.accepted + report.rejected > 0 ? 1 : 0)));
            break;
          }
        } catch (error) {
          if (controller.signal.aborted) return;
          const { code, status } = error as { code?: string; status?: number };
          if (code === ALBUM_SHARE_ERRORS.uploadLimitReached
            || code === ALBUM_SHARE_ERRORS.uploadsDisabled) {
            progress.stopped = code;
            retry.push(...unique.slice(index));
            break;
          }
          progress.failed += 1;
          retry.push(file);
          // A broken connection, lost grant or throttled server will not improve
          // by immediately sending every remaining file into the same failure.
          if (!status || status >= 500 || status === 429 || status === 401 || status === 404) {
            progress.uncertain = !status || status >= 500;
            progress.stopped = status === 429 ? 'rate_limited'
              : status === 401 || status === 404 ? 'access' : 'connection';
            retry.push(...unique.slice(index + 1));
            break;
          }
        }
        setRun({ ...progress });
      }
      if (controller.signal.aborted) return;
      setRun({ ...progress });
      setRetryFiles(retry);
      setAlreadySent(rememberedRef.current.size);
      onDone();
    } finally {
      if (activeRef.current === controller) {
        activeRef.current = null;
        wakeLock.stop();
        if (!controller.signal.aborted) setBusy(false);
        if (inputRef.current) inputRef.current.value = '';
      }
    }
  }

  if (!album.canUpload && !run) return null;
  const complete = run !== null && !busy && run.failed === 0 && !run.stopped;
  const waiting = run ? Math.max(0, run.total - run.sent - run.failed) : 0;
  const saving = busy && run !== null && run.fraction === 1;

  return (
    <section className="album-share-upload" data-testid="album-share-upload" aria-label={t('albumLink.addTitle')}>
      <div className="album-share-upload-top">
        <div className="album-share-upload-copy">
          <h2 className="album-share-subtitle">{t('albumLink.addTitle')}</h2>
          {album.uploadsRemaining !== null && (
            <p className="album-share-meta" data-testid="album-share-remaining">
              {tn(album.uploadsRemaining, 'albumLink.remaining')}
            </p>
          )}
        </div>
        <input
          ref={inputRef}
          type="file"
          multiple
          accept="image/*,video/*"
          className="party-contribution-file-input"
          aria-label={t('albumLink.choose')}
          tabIndex={-1}
          data-testid="album-share-input"
          disabled={busy || !album.canUpload}
          onChange={(e) => void send(Array.from(e.target.files ?? []))}
        />
        <button
          type="button"
          className="album-share-picker"
          disabled={busy || !album.canUpload}
          onClick={() => inputRef.current?.click()}
        >
          <Icon name={busy ? 'upload' : 'plus'} size={24} />
          {t(busy ? 'albumLink.uploading' : 'albumLink.choose')}
        </button>
      </div>

      {alreadySent > 0 && !run && !busy && (
        <p className="album-share-meta" data-testid="album-share-resume">
          {tn(alreadySent, 'albumLink.alreadySent')}
        </p>
      )}

      {(run || busy) && (
        <div className={`album-share-progress${complete ? ' album-share-progress-complete' : ''}`}
          role="status" aria-live="polite" data-testid="album-share-progress">
          <p className="album-share-progress-title">
            {busy ? <span className="album-share-activity" aria-hidden="true" />
              : <Icon name={complete ? 'check' : 'info'} size={24} />}
            {t(busy ? saving ? 'albumLink.saving' : 'albumLink.uploading'
              : complete ? run?.total === 0 ? 'albumLink.allSkipped' : 'albumLink.complete'
                : 'albumLink.incomplete')}
          </p>
          {run && <p>{t('albumLink.progress', { sent: run.sent, total: run.total })}</p>}
          {busy && run && run.current > 0 && (
            <>
              <p className="album-share-current" title={run.name}>{run.name}</p>
              <div className="party-contribution-progressbar" role="progressbar"
                aria-label={t('albumLink.fileProgress')}
                aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.floor(run.fraction * 100)}>
                <div className="party-contribution-progressbar-fill" style={{ width: `${run.fraction * 100}%` }} />
              </div>
              <p className="album-share-meta">
                {t(saving ? 'albumLink.fileSaving' : 'albumLink.fileSending', {
                  current: run.current, total: run.total, percent: Math.floor(run.fraction * 100),
                })}
              </p>
            </>
          )}
          {busy && <p className="album-share-wait">{t('albumLink.wait')}</p>}
          {run && !busy && (
            <>
              {run.skipped > 0 && <p className="album-share-meta">{tn(run.skipped, 'albumLink.skipped')}</p>}
              {run.failed > 0 && <p className="album-share-stopped">{tn(run.failed, 'albumLink.failed')}</p>}
              {waiting > 0 && <p className="album-share-meta">{tn(waiting, 'albumLink.waiting')}</p>}
              {run.stopped === ALBUM_SHARE_ERRORS.uploadLimitReached && (
                <p className="album-share-stopped" data-testid="album-share-limit">{t('albumLink.limitReached')}</p>
              )}
              {run.stopped === ALBUM_SHARE_ERRORS.uploadsDisabled && (
                <p className="album-share-stopped">{t('albumLink.uploadsClosed')}</p>
              )}
              {run.stopped === 'rate_limited' && <p>{t('albumLink.rateLimited')}</p>}
              {run.stopped === 'access' && <p>{t('albumLink.accessStopped')}</p>}
              {run.stopped === 'connection' && <p>{t('albumLink.connectionStopped')}</p>}
              {run.uncertain && <p className="album-share-meta">{t('albumLink.checkBeforeRetry')}</p>}
            </>
          )}
        </div>
      )}
      {!busy && retryFiles.length > 0 && album.canUpload && run?.stopped !== 'access' && (
        <button className="party-contribution-secondary album-share-retry" onClick={() => void send(retryFiles)}>
          <Icon name="restore" size={24} />{t('albumLink.retryMissing')}
        </button>
      )}
      {(!run || busy) && <p className="album-share-meta album-share-caveat">{t('albumLink.keepOpen')}</p>}
    </section>
  );
}

// ── The second factor ─────────────────────────────────────────────────────

function SecondFactorGate({
  token, onVerified,
}: {
  token: string;
  onVerified(): void;
}) {
  const { t } = useI18n();
  const [email, setEmail] = useState('');
  const [code, setCode] = useState('');
  const [sent, setSent] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function ask(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      await challengeAlbumShare(token, email.trim());
      // The answer is the same for a listed and an unlisted address, so this
      // screen says the same thing either way. Telling somebody their address
      // is not on the list would be telling them whose is.
      setSent(true);
    } catch {
      // The server answers 202 for a listed address, an unlisted one and a
      // cooldown alike, so anything that lands here is a real failure to reach
      // it — never a verdict about the address.
      setError(t('albumLink.codeFailed'));
    } finally { setBusy(false); }
  }

  async function verify(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      await verifyAlbumShare(token, email.trim(), code.trim());
      onVerified();
    } catch {
      // ONE MESSAGE, because the server gives one refusal. A "too many
      // attempts" of its own would say "this address is on the list" to
      // anybody willing to guess six times — the oracle the server closed, put
      // back by the client.
      setError(t('albumLink.wrongCode'));
    } finally { setBusy(false); }
  }

  return (
    <section className="album-share-gate" data-testid="album-share-gate">
      <h1 className="album-share-title">{t('albumLink.gateTitle')}</h1>
      <p className="party-contribution-intro">{t('albumLink.gateBody')}</p>

      {!sent ? (
        <form onSubmit={(e) => void ask(e)} className="album-share-form">
          <label className="album-share-field">
            <span>{t('albumLink.emailLabel')}</span>
            <input
              type="email"
              inputMode="email"
              autoComplete="email"
              required
              value={email}
              data-testid="album-share-email"
              onChange={(e) => setEmail(e.target.value)}
            />
          </label>
          <button
            type="submit"
            className="party-contribution-primary"
            disabled={busy || email.trim() === ''}
            data-testid="album-share-ask"
          >
            {t('albumLink.sendCode')}
          </button>
        </form>
      ) : (
        <form onSubmit={(e) => void verify(e)} className="album-share-form">
          <p className="party-contribution-hint" data-testid="album-share-sent">
            {t('albumLink.codeSent')}
          </p>
          <label className="album-share-field">
            <span>{t('albumLink.codeLabel')}</span>
            <input
              inputMode="numeric"
              autoComplete="one-time-code"
              pattern="[0-9]{6}"
              maxLength={6}
              required
              value={code}
              data-testid="album-share-code"
              onChange={(e) => setCode(e.target.value.replace(/\D/g, ''))}
            />
          </label>
          <button
            type="submit"
            className="party-contribution-primary"
            disabled={busy || code.length !== 6}
            data-testid="album-share-verify"
          >
            {t('albumLink.enter')}
          </button>
        </form>
      )}

      {error && (
        <p className="inline-error" role="alert" data-testid="album-share-error">{error}</p>
      )}
    </section>
  );
}
