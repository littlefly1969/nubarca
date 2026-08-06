import { useCallback, useEffect, useState, type ReactNode } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import {
  getAlbumPartySettings,
  listPartyUploads,
  moderatePartyUpload,
  setAlbumPartyMode,
  type PartyUploadItem,
  type PartyUploadList,
} from '@nubarca/api-client';
import { ApiError } from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n, type MessageKey } from '../i18n';

type Status =
  | { kind: 'loading' }
  | { kind: 'ready'; list: PartyUploadList }
  | { kind: 'error'; message: string };

const STATUS_LABEL_KEY: Record<PartyUploadItem['status'], MessageKey> = {
  approved: 'partyUploads.statusApproved',
  pending: 'partyUploads.statusPending',
  hidden: 'partyUploads.statusHidden',
  rejected: 'partyUploads.statusRejected',
  removed_from_album: 'partyUploads.statusRemovedFromAlbum',
};

// Owner-private moderation of anonymous party uploads for one album. Lets the
// owner hide/remove guest content quickly, and (optionally) require approval
// before new uploads appear. Approval defaults OFF — uploads stay immediately
// visible. No storage/blob/token/face internals are ever shown.
export function PartyUploadsPage() {
  const { albumId } = useParams<{ albumId: string }>();
  const navigate = useNavigate();
  const { invalidateAuth } = useAuth();
  const { t } = useI18n();
  const [status, setStatus] = useState<Status>({ kind: 'loading' });
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => {
    if (!albumId) return;
    setStatus({ kind: 'loading' });
    listPartyUploads(albumId)
      .then((list) => setStatus({ kind: 'ready', list }))
      .catch((err) => {
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        if (err instanceof ApiError && err.status === 404) { void navigate('/albums'); return; }
        setStatus({ kind: 'error', message: t('partyUploads.loadError') });
      });
  }, [albumId, invalidateAuth, navigate, t]);

  useEffect(() => { load(); }, [load]);

  const handleModerate = async (
    item: PartyUploadItem,
    action: 'hide' | 'approve' | 'reject' | 'restore',
  ) => {
    if (!albumId) return;
    if (action === 'hide' && !window.confirm(
      t('partyUploads.confirmHide', { name: item.name }),
    )) return;
    if (action === 'reject' && !window.confirm(
      t('partyUploads.confirmReject', { name: item.name }),
    )) return;
    if (action === 'restore' && !window.confirm(
      t('partyUploads.confirmRestore', { name: item.name }),
    )) return;
    setBusy(true);
    try {
      await moderatePartyUpload(albumId, item.fileItemId, action);
      load();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setStatus({ kind: 'error', message: t('partyUploads.updateError') });
    } finally {
      setBusy(false);
    }
  };

  const handleToggleApproval = async (next: boolean) => {
    if (!albumId) return;
    if (next && !window.confirm(t('partyUploads.confirmEnableApproval'))) return;
    setBusy(true);
    try {
      // Party stays on; only the approval sub-switch changes (tokens unaffected).
      await setAlbumPartyMode(albumId, true, undefined, next);
      // Re-check settings, then reload the list to reflect the new mode.
      await getAlbumPartySettings(albumId);
      load();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setStatus({ kind: 'error', message: t('partyUploads.approvalError') });
    } finally {
      setBusy(false);
    }
  };

  if (status.kind === 'loading') {
    return <div className="page"><p>{t('common.loading')}</p></div>;
  }
  if (status.kind === 'error') {
    return (
      <div className="page">
        <p className="error-message" data-testid="party-uploads-error">{status.message}</p>
        <button type="button" onClick={load}>{t('common.retry')}</button>
      </div>
    );
  }

  const { list } = status;
  const pending = list.items.filter((i) => i.status === 'pending');
  const visible = list.items.filter((i) => i.status === 'approved');
  const removed = list.items.filter((i) =>
    i.status === 'hidden' || i.status === 'rejected' || i.status === 'removed_from_album');

  return (
    <div className="page party-uploads-page">
      <p>
        <Link to={`/albums/${albumId}`}>{t('partyUploads.backToAlbum')}</Link>
      </p>
      <h1>{t('partyUploads.title')}</h1>
      <p className="muted">{t('partyUploads.intro')}</p>

      <div className="album-party-approval" data-testid="approval-toggle">
        <label className="album-tv-label">
          <input
            type="checkbox"
            checked={list.requireUploadApproval}
            disabled={busy}
            onChange={(e) => void handleToggleApproval(e.target.checked)}
            aria-label={t('partyUploads.requireApproval')}
          />
          <span>{t('partyUploads.requireApproval')}</span>
        </label>
        <p className="muted">{t('partyUploads.requireApprovalHelp')}</p>
      </div>

      {list.items.length === 0 && (
        <p className="empty-state" data-testid="party-uploads-empty">
          {t('partyUploads.empty')}
        </p>
      )}

      {pending.length > 0 && (
        <section data-testid="party-uploads-pending">
          <h2>{t('partyUploads.sectionPending')}</h2>
          <ul className="party-uploads-list">
            {pending.map((item) => (
              <PartyUploadRow key={item.fileItemId} item={item} busy={busy}>
                <button
                  type="button"
                  onClick={() => void handleModerate(item, 'approve')}
                  disabled={busy}
                  aria-label={t('partyUploads.approveLabel', { name: item.name })}
                >
                  {t('partyUploads.approve')}
                </button>
                <button
                  type="button"
                  className="btn-danger"
                  onClick={() => void handleModerate(item, 'reject')}
                  disabled={busy}
                  aria-label={t('partyUploads.rejectLabel', { name: item.name })}
                >
                  {t('partyUploads.reject')}
                </button>
              </PartyUploadRow>
            ))}
          </ul>
        </section>
      )}

      {visible.length > 0 && (
        <section data-testid="party-uploads-visible">
          <h2>{t('partyUploads.sectionVisible')}</h2>
          <ul className="party-uploads-list">
            {visible.map((item) => (
              <PartyUploadRow key={item.fileItemId} item={item} busy={busy}>
                <button
                  type="button"
                  className="btn-danger"
                  onClick={() => void handleModerate(item, 'hide')}
                  disabled={busy}
                  aria-label={t('partyUploads.hideLabel', { name: item.name })}
                >
                  {t('partyUploads.hide')}
                </button>
              </PartyUploadRow>
            ))}
          </ul>
        </section>
      )}

      {removed.length > 0 && (
        <section data-testid="party-uploads-removed">
          <h2>{t('partyUploads.sectionRemoved')}</h2>
          <ul className="party-uploads-list">
            {removed.map((item) => (
              <PartyUploadRow key={item.fileItemId} item={item} busy={busy}>
                <button
                  type="button"
                  onClick={() => void handleModerate(item, 'restore')}
                  disabled={busy}
                  aria-label={t('partyUploads.restoreLabel', { name: item.name })}
                >
                  {t('partyUploads.restore')}
                </button>
              </PartyUploadRow>
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}

function PartyUploadRow({
  item,
  children,
}: {
  item: PartyUploadItem;
  busy: boolean;
  children: ReactNode;
}) {
  const { t, formatDate } = useI18n();
  return (
    <li className="party-mod-row" data-testid="party-upload-row">
      <img
        src={item.thumbnailUrl}
        alt=""
        className="party-mod-thumb"
        loading="lazy"
        onError={(e) => { (e.target as HTMLImageElement).style.display = 'none'; }}
      />
      <span className="party-mod-name">{item.name}</span>
      <span className={`party-mod-status status-${item.status}`}>
        {t(STATUS_LABEL_KEY[item.status])}
      </span>
      <span className="party-mod-meta">{formatDate(item.uploadedAt)}</span>
      <span className="party-mod-actions">{children}</span>
    </li>
  );
}
