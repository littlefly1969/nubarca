import { useCallback, useEffect, useState } from 'react';
import {
  ApiError,
  addAlbumShareGuest,
  createAlbumShareLink,
  getAlbumShareLink,
  removeAlbumShareGuest,
  revokeAlbumShareLink,
  rotateAlbumShareLink,
  updateAlbumShareLink,
  type AlbumShareLink,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { useAuth } from '../auth/useAuth';
import { copyWhenReady } from '../party/guestShare';
import { absoluteGuestUrl, useQrSvg } from '../party/workspace/PartyShareCard';
import { Button, Notice, Panel, SwitchRow } from '../party/workspace/ui';

// SHARING AN ALBUM BY LINK, from the owner's side.
//
// One album has ONE link, not a list of them: an owner who has to pick which of
// four addresses to revoke has already lost track of who holds what. Changing
// the address is a separate, named act — rotating — because creating must never
// silently invalidate something already sent to thirty people.
//
// The switches are independent and save one at a time, so two surfaces
// configuring one link cannot undo each other.

export function AlbumShareLinkPanel({ albumId }: { albumId: string }) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const [link, setLink] = useState<AlbumShareLink | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);
  const [copied, setCopied] = useState<'idle' | 'ok' | 'failed'>('idle');
  const [confirmRotate, setConfirmRotate] = useState(false);

  const absolute = link?.url ? absoluteGuestUrl(link.url) : null;
  const qr = useQrSvg(absolute, 176);

  const load = useCallback(async (signal?: AbortSignal) => {
    setLoading(true);
    try {
      setLink(await getAlbumShareLink(albumId, signal));
    } catch (error) {
      if (signal?.aborted) return;
      if (error instanceof ApiError && error.status === 401) { invalidateAuth(); return; }
      // 404 here means "no link yet", which is the ordinary starting state and
      // not a failure worth a notice.
      setLink(null);
    } finally {
      if (!signal?.aborted) setLoading(false);
    }
  }, [albumId, invalidateAuth]);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  useEffect(() => {
    if (copied === 'idle') return;
    const timer = setTimeout(() => setCopied('idle'), 4000);
    return () => clearTimeout(timer);
  }, [copied]);

  async function run<T>(action: () => Promise<T>) {
    setBusy(true); setFailed(false);
    try {
      return await action();
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) { invalidateAuth(); return null; }
      setFailed(true);
      return null;
    } finally { setBusy(false); }
  }

  if (loading) return null;

  if (!link) {
    return (
      <Panel
        title={t('albumLinkOwner.heading')}
        note={t('albumLinkOwner.note')}
        testId="album-share-link"
      >
        <div className="pw-panel-actions">
          <Button
            tone="primary"
            disabled={busy}
            data-testid="album-share-link-create"
            onClick={() => void run(async () => setLink(await createAlbumShareLink(albumId)))}
          >
            {t('albumLinkOwner.create')}
          </Button>
        </div>
        {failed && <Notice tone="error"><p>{t('albumLinkOwner.saveFailed')}</p></Notice>}
      </Panel>
    );
  }

  return (
    <Panel
      title={t('albumLinkOwner.heading')}
      note={t('albumLinkOwner.note')}
      testId="album-share-link"
    >
      <div className="pw-share">
        <code className="pw-share-url" data-testid="album-share-link-url">{absolute}</code>
        <div className="pw-panel-actions">
          <Button
            tone="primary"
            data-testid="album-share-link-copy"
            onClick={() => {
              void copyWhenReady(Promise.resolve(absolute ?? ''))
                .then((ok) => setCopied(ok ? 'ok' : 'failed'));
            }}
          >
            {t('albumLinkOwner.copy')}
          </Button>
          {/* The share sheet, where the phone has one. It is what actually
              gets the address into a chat, and it is not a substitute for the
              copy button: a desktop has no sheet. */}
          {typeof navigator !== 'undefined' && 'share' in navigator && (
            <Button
              data-testid="album-share-link-send"
              onClick={() => {
                void navigator.share({ url: absolute ?? '' }).catch(() => { /* dismissed */ });
              }}
            >
              {t('albumLinkOwner.send')}
            </Button>
          )}
        </div>
        <div aria-live="polite">
          {copied === 'ok' && (
            <p className="pw-small pw-muted" role="status" data-testid="album-share-link-copied">
              {t('albumLinkOwner.copied')}
            </p>
          )}
          {copied === 'failed' && (
            <p className="pw-small pw-muted" role="status">{t('albumLinkOwner.copyFailed')}</p>
          )}
        </div>
        {qr && (
          <figure className="pw-qr" data-testid="album-share-link-qr">
            <div
              className="pw-qr-frame"
              // The QR is built in the browser from the address above: there is
              // no second source of truth for where a visitor lands.
              dangerouslySetInnerHTML={{ __html: qr }}
            />
          </figure>
        )}
      </div>

      <div className="pw-rows">
        <SwitchRow
          testId="album-share-link-uploads"
          label={t('albumLinkOwner.uploadsLabel')}
          note={t('albumLinkOwner.uploadsNote')}
          checked={link.uploadEnabled}
          disabled={busy}
          onChange={(next) => void run(async () =>
            setLink(await updateAlbumShareLink(albumId, { uploadEnabled: next })))}
        />
        <SwitchRow
          testId="album-share-link-originals"
          label={t('albumLinkOwner.originalsLabel')}
          note={t('albumLinkOwner.originalsNote')}
          checked={link.allowOriginalDownload}
          disabled={busy}
          onChange={(next) => void run(async () =>
            setLink(await updateAlbumShareLink(albumId, { allowOriginalDownload: next })))}
        />
        <SwitchRow
          testId="album-share-link-second-factor"
          label={t('albumLinkOwner.secondFactorLabel')}
          note={t('albumLinkOwner.secondFactorNote')}
          checked={link.requireSecondFactor}
          disabled={busy}
          onChange={(next) => void run(async () =>
            setLink(await updateAlbumShareLink(albumId, { requireSecondFactor: next })))}
        />
      </div>

      {/* The list is what makes the second factor authorization rather than
          theatre, so it appears WITH the switch and not in a settings drawer
          somewhere else. */}
      {link.requireSecondFactor && (
        <GuestList
          albumId={albumId}
          link={link}
          busy={busy}
          onChanged={() => void load()}
        />
      )}

      <UploadCeiling
        albumId={albumId}
        link={link}
        busy={busy}
        onSaved={(next) => setLink(next)}
        onFailed={() => setFailed(true)}
      />

      <div className="pw-panel-actions">
        {/* ROTATING kills an address people are holding, so it asks first. */}
        {confirmRotate ? (
          <>
            <Button
              tone="danger"
              disabled={busy}
              data-testid="album-share-link-rotate-confirm"
              onClick={() => void run(async () => {
                setLink(await rotateAlbumShareLink(albumId));
                setConfirmRotate(false);
              })}
            >
              {t('albumLinkOwner.rotateConfirm')}
            </Button>
            <Button tone="quiet" onClick={() => setConfirmRotate(false)}>
              {t('common.cancel')}
            </Button>
          </>
        ) : (
          <Button
            tone="quiet"
            disabled={busy}
            data-testid="album-share-link-rotate"
            onClick={() => setConfirmRotate(true)}
          >
            {t('albumLinkOwner.rotate')}
          </Button>
        )}
        <Button
          tone="danger"
          disabled={busy}
          data-testid="album-share-link-revoke"
          onClick={() => void run(async () => {
            await revokeAlbumShareLink(albumId);
            setLink(null);
          })}
        >
          {t('albumLinkOwner.revoke')}
        </Button>
      </div>

      {failed && <Notice tone="error"><p>{t('albumLinkOwner.saveFailed')}</p></Notice>}
    </Panel>
  );
}

function GuestList({
  albumId, link, busy, onChanged,
}: {
  albumId: string;
  link: AlbumShareLink;
  busy: boolean;
  onChanged(): void;
}) {
  const { t } = useI18n();
  const [email, setEmail] = useState('');
  const [adding, setAdding] = useState(false);
  const [rejected, setRejected] = useState(false);

  async function add() {
    setAdding(true); setRejected(false);
    try {
      await addAlbumShareGuest(albumId, email.trim());
      setEmail('');
      onChanged();
    } catch {
      setRejected(true);
    } finally { setAdding(false); }
  }

  return (
    <div className="pw-rows" data-testid="album-share-link-guests">
      <p className="pw-small pw-muted">{t('albumLinkOwner.guestsNote')}</p>
      <ul className="pw-list">
        {link.guests.map((guest) => (
          <li key={guest.id} className="pw-row">
            <span className="pw-row-text">
              <span className="pw-row-label">{guest.email}</span>
            </span>
            <span className="pw-row-control">
              <Button
                tone="quiet"
                disabled={busy}
                data-testid={`album-share-link-guest-remove-${guest.id}`}
                onClick={() => void removeAlbumShareGuest(albumId, guest.id).then(onChanged)}
              >
                {t('common.remove')}
              </Button>
            </span>
          </li>
        ))}
      </ul>
      <div className="pw-panel-actions">
        <input
          type="email"
          inputMode="email"
          placeholder={t('albumLinkOwner.guestPlaceholder')}
          aria-label={t('albumLinkOwner.guestPlaceholder')}
          value={email}
          data-testid="album-share-link-guest-email"
          onChange={(e) => setEmail(e.target.value)}
        />
        <Button
          disabled={busy || adding || email.trim() === ''}
          data-testid="album-share-link-guest-add"
          onClick={() => void add()}
        >
          {t('albumLinkOwner.guestAdd')}
        </Button>
      </div>
      {rejected && (
        <p className="pw-small pw-muted" role="alert" data-testid="album-share-link-guest-rejected">
          {t('albumLinkOwner.guestRejected')}
        </p>
      )}
    </div>
  );
}

function UploadCeiling({
  albumId, link, busy, onSaved, onFailed,
}: {
  albumId: string;
  link: AlbumShareLink;
  busy: boolean;
  onSaved(next: AlbumShareLink): void;
  onFailed(): void;
}) {
  const { t } = useI18n();
  const [value, setValue] = useState(String(link.maxUploads));

  useEffect(() => { setValue(String(link.maxUploads)); }, [link.maxUploads]);

  const reached = link.maxUploads > 0 && link.uploadCount >= link.maxUploads;

  return (
    <div className="pw-rows" data-testid="album-share-link-ceiling">
      <label className="pw-row">
        <span className="pw-row-text">
          <span className="pw-row-label">{t('albumLinkOwner.ceilingLabel')}</span>
          <span className="pw-row-note">{t('albumLinkOwner.ceilingNote')}</span>
        </span>
        <span className="pw-row-control">
          <input
            type="number"
            min={0}
            max={100000}
            value={value}
            disabled={busy}
            data-testid="album-share-link-ceiling-input"
            aria-label={t('albumLinkOwner.ceilingLabel')}
            onChange={(e) => setValue(e.target.value)}
            onBlur={() => {
              const next = Number.parseInt(value, 10);
              if (Number.isNaN(next) || next === link.maxUploads) return;
              void updateAlbumShareLink(albumId, { maxUploads: next })
                .then(onSaved)
                .catch(onFailed);
            }}
          />
        </span>
      </label>
      <p className="pw-small pw-muted" data-testid="album-share-link-used">
        {t('albumLinkOwner.used', { count: link.uploadCount })}
      </p>
      {/* The ceiling refuses uploads and leaves reading open, so the owner has
          to be TOLD rather than discover it from somebody's complaint. */}
      {reached && (
        <Notice tone="warn" testId="album-share-link-full">
          <p>{t('albumLinkOwner.full')}</p>
        </Notice>
      )}
    </div>
  );
}
