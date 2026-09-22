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

const WORDMARK = {
  src: '/brand/nubarca-wordmark-on-dark-480w.png',
  width: 480,
  height: 135,
} as const;

type Status =
  | { kind: 'loading' }
  | { kind: 'locked' }
  | { kind: 'ready'; album: AlbumSharePublic; items: AlbumShareItem[] }
  | { kind: 'gone' };

export function AlbumSharePage() {
  const { token = '' } = useParams<{ token: string }>();
  const { t } = useI18n();
  const [status, setStatus] = useState<Status>({ kind: 'loading' });

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      const album = await getAlbumShare(token, signal);
      const items = await getAlbumShareItems(token, signal);
      if (!signal?.aborted) setStatus({ kind: 'ready', album, items: items.items });
    } catch (error) {
      if (signal?.aborted) return;
      // 401 is the ONE answer that is not "nothing here": the link is live and
      // the visitor is expected — they simply have not proved who they are.
      setStatus(error instanceof ApiError && error.status === 401
        ? { kind: 'locked' }
        : { kind: 'gone' });
    }
  }, [token]);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

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
          <LanguageSwitcher className="language-switcher language-switcher-public" compact />
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

        {status.kind === 'locked' && (
          <SecondFactorGate token={token} onVerified={() => void load()} />
        )}

        {status.kind === 'ready' && (
          <AlbumShareBody
            token={token}
            album={status.album}
            items={status.items}
            onChanged={() => void load()}
          />
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
  const [open, setOpen] = useState<AlbumShareItem | null>(null);

  return (
    <>
      <header className="album-share-head">
        <h1 className="album-share-title">{album.albumName}</h1>
        <p className="album-share-count">
          {tn(album.itemCount, 'albumLink.itemCount')}
        </p>
      </header>

      {album.canUpload && (
        <AlbumShareUpload token={token} album={album} onDone={onChanged} />
      )}

      {items.length === 0 ? (
        <p className="party-contribution-intro" data-testid="album-share-empty">
          {t('albumLink.empty')}
        </p>
      ) : (
        <ul className="album-share-grid" data-testid="album-share-grid">
          {items.map((item) => (
            <li key={item.id}>
              <button
                type="button"
                className="album-share-tile"
                data-testid={`album-share-item-${item.id}`}
                onClick={() => setOpen(item)}
              >
                {/* SMALL in the grid, as every grid in this product uses —
                    a medium in forty tiles is forty times the bytes for a
                    picture nobody is looking at yet. */}
                <img src={item.thumbnailUrl} alt="" loading="lazy" />
                {item.isVideo && <span className="album-share-video" aria-hidden="true" />}
              </button>
            </li>
          ))}
        </ul>
      )}

      {open && (
        <Lightbox
          item={open}
          canDownloadOriginal={album.canDownloadOriginal}
          onClose={() => setOpen(null)}
        />
      )}
    </>
  );
}

function Lightbox({
  item, canDownloadOriginal, onClose,
}: {
  item: AlbumShareItem;
  canDownloadOriginal: boolean;
  onClose(): void;
}) {
  const { t } = useI18n();

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <div className="album-share-lightbox" role="dialog" aria-modal="true" data-testid="album-share-lightbox">
      <button
        type="button"
        className="album-share-close"
        aria-label={t('common.close')}
        onClick={onClose}
      >×</button>
      {/* MEDIUM in the viewer, again as the rest of the product does. The
          original leaves only through the download below, and only when the
          owner allowed it. */}
      <img src={item.previewUrl} alt="" />
      <a
        className="party-contribution-primary album-share-download"
        href={item.downloadUrl}
        download
        data-testid="album-share-download"
      >
        {t(canDownloadOriginal ? 'albumLink.downloadOriginal' : 'albumLink.download')}
      </a>
    </div>
  );
}

// ── Adding to it ──────────────────────────────────────────────────────────

type Run = {
  total: number;
  sent: number;
  failed: number;
  skipped: number;
  /** A stable code when the link's ceiling stopped the run. */
  stopped: string | null;
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
  const [run, setRun] = useState<Run | null>(null);
  const [busy, setBusy] = useState(false);
  const [alreadySent, setAlreadySent] = useState(0);
  const wakeLock = useUploadWakeLock();

  // What this token has already accepted from this phone. Read once, so the
  // page can say "7 già caricate" before anybody picks anything.
  useEffect(() => {
    let live = true;
    void loadDone(token).then((done) => { if (live) setAlreadySent(done.size); });
    return () => { live = false; };
  }, [token]);

  async function send(files: FileList | null) {
    if (!files || files.length === 0) return;
    setBusy(true);
    wakeLock.start();
    try {
      const done = await loadDone(token);
      // THE RESUME. Anything this phone already sent under this link is
      // recognised and skipped, so an interrupted run of forty does not start
      // again at one.
      const { pending, skipped } = partition(Array.from(files), done);
      const progress: Run = {
        total: pending.length, sent: 0, failed: 0, skipped: skipped.length, stopped: null,
      };
      setRun({ ...progress });

      for (const file of pending) {
        try {
          const report = await uploadToAlbumShareWithProgress(token, file);
          if (report.accepted > 0) {
            progress.sent += 1;
            await markDone(token, fileKey(file));
          } else {
            progress.failed += 1;
          }
          if (report.stopped) {
            // The link's ceiling. Stopping here rather than sending the rest
            // into a wall is the difference between one clear message and
            // thirty identical failures.
            progress.stopped = report.stopped;
            setRun({ ...progress });
            break;
          }
        } catch (error) {
          progress.failed += 1;
          const code = (error as { code?: string }).code ?? null;
          if (code === ALBUM_SHARE_ERRORS.uploadLimitReached
            || code === ALBUM_SHARE_ERRORS.uploadsDisabled) {
            progress.stopped = code;
            setRun({ ...progress });
            break;
          }
        }
        setRun({ ...progress });
      }
      setAlreadySent((await loadDone(token)).size);
      onDone();
    } finally {
      wakeLock.stop();
      setBusy(false);
      if (inputRef.current) inputRef.current.value = '';
    }
  }

  return (
    <section className="album-share-upload" data-testid="album-share-upload">
      <h2 className="album-share-subtitle">{t('albumLink.addTitle')}</h2>

      {album.uploadsRemaining !== null && (
        <p className="party-contribution-intro" data-testid="album-share-remaining">
          {tn(album.uploadsRemaining, 'albumLink.remaining')}
        </p>
      )}

      <input
        ref={inputRef}
        type="file"
        multiple
        accept="image/*,video/*"
        className="album-share-input"
        data-testid="album-share-input"
        disabled={busy}
        onChange={(e) => void send(e.target.files)}
      />

      {/* The text says what the control DOES. This one opens the gallery, so
          it does not offer to take a photograph. */}
      <p className="party-contribution-hint">{t('albumLink.pickHint')}</p>

      {alreadySent > 0 && !run && (
        <p className="party-contribution-hint" data-testid="album-share-resume">
          {tn(alreadySent, 'albumLink.alreadySent')}
        </p>
      )}

      {run && (
        <div className="album-share-progress" role="status" data-testid="album-share-progress">
          <p>{t('albumLink.progress', { sent: run.sent, total: run.total })}</p>
          {run.skipped > 0 && <p>{tn(run.skipped, 'albumLink.skipped')}</p>}
          {run.failed > 0 && <p>{tn(run.failed, 'albumLink.failed')}</p>}
          {run.stopped === ALBUM_SHARE_ERRORS.uploadLimitReached && (
            <p className="album-share-stopped" data-testid="album-share-limit">
              {t('albumLink.limitReached')}
            </p>
          )}
          {run.stopped === ALBUM_SHARE_ERRORS.uploadsDisabled && (
            <p className="album-share-stopped">{t('albumLink.uploadsClosed')}</p>
          )}
        </div>
      )}

      {/* SAID OUT LOUD, because the browser cannot do what people assume. A
          visitor who walks away from a half-finished upload believing it will
          continue is a visitor who loses photographs. */}
      <p className="party-contribution-hint album-share-caveat">{t('albumLink.keepOpen')}</p>
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
    } catch (err) {
      setError(err instanceof ApiError && err.status === 429
        ? t('albumLink.codeTooSoon')
        : t('albumLink.codeFailed'));
    } finally { setBusy(false); }
  }

  async function verify(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      await verifyAlbumShare(token, email.trim(), code.trim());
      onVerified();
    } catch (err) {
      const body = err instanceof ApiError ? (err.body as { error?: string } | null) : null;
      setError(body?.error === ALBUM_SHARE_ERRORS.tooManyAttempts
        ? t('albumLink.tooManyAttempts')
        : t('albumLink.wrongCode'));
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
