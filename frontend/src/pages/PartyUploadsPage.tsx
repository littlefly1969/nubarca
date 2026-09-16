import { useCallback, useEffect, useState, type ReactNode } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
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
import { Badge, Button, EmptyState, Notice, Panel, PanelSkeleton, SwitchRow } from '../party/workspace/ui';
import '../party/workspace/PartyWorkspace.css';

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
  // The queue is reached FROM a party, and its own back link returns there.
  // Falling back to the album keeps an old bookmark working.
  const [searchParams] = useSearchParams();
  const partyId = searchParams.get('party');
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

  const back = partyId
    ? { to: `/parties/${partyId}?section=photos`, label: t('partyUploads.backToParty') }
    : { to: `/albums/${albumId}`, label: t('partyUploads.backToAlbum') };

  if (status.kind === 'loading') {
    return (
      <main className="pw" data-testid="party-uploads-page">
        <header className="pw-head">
          <div className="pw-skeleton pw-skeleton--title" aria-hidden />
        </header>
        <PanelSkeleton rows={2} />
        <p role="status" className="visually-hidden">{t('common.loading')}</p>
      </main>
    );
  }
  if (status.kind === 'error') {
    return (
      <main className="pw" data-testid="party-uploads-page">
        <header className="pw-head">
          <Link to={back.to} className="pw-back">← {back.label}</Link>
        </header>
        <Notice
          tone="error"
          testId="party-uploads-error"
          title={status.message}
          actions={<Button onClick={load}>{t('common.retry')}</Button>}
        >
          <p>{t('party.loadErrorBody')}</p>
        </Notice>
      </main>
    );
  }

  const { list } = status;
  const pending = list.items.filter((i) => i.status === 'pending');
  const visible = list.items.filter((i) => i.status === 'approved');
  const removed = list.items.filter((i) =>
    i.status === 'hidden' || i.status === 'rejected' || i.status === 'removed_from_album');

  return (
    <main className="pw" data-testid="party-uploads-page">
      <header className="pw-head">
        <Link to={back.to} className="pw-back">← {back.label}</Link>
        <div className="pw-head-main">
          <div className="pw-identity">
            <h1 className="pw-title">{t('partyUploads.title')}</h1>
            <p className="pw-section-lede">{t('partyUploads.intro')}</p>
          </div>
        </div>
      </header>

      <div className="pw-panels">
        {/* The rule lives WITH the queue it explains: this is the answer to
            "why are these waiting", so it is not a second copy of a switch
            kept somewhere else. */}
        <Panel
          title={t('partyUploads.rule')}
          testId="approval-toggle"
          headingLevel={2}
        >
          <div className="pw-rows">
            <SwitchRow
              testId="party-uploads-approval"
              label={t('partyUploads.requireApproval')}
              note={t('partyUploads.requireApprovalHelp')}
              checked={list.requireUploadApproval}
              disabled={busy}
              onChange={(next) => void handleToggleApproval(next)}
            />
          </div>
        </Panel>

        {list.items.length === 0 && (
          <EmptyState
            testId="party-uploads-empty"
            title={t('partyUploads.emptyTitle')}
            body={t('partyUploads.empty')}
          />
        )}

        {pending.length > 0 && (
          <Panel
            title={t('partyUploads.sectionPending')}
            note={t('partyUploads.sectionPendingNote')}
            testId="party-uploads-pending"
            headingLevel={2}
            aside={<Badge kind="warn">{t('party.photos.pending', { count: pending.length })}</Badge>}
          >
            <ul className="pw-mod-list">
              {pending.map((item) => (
                <PartyUploadRow key={item.fileItemId} item={item} busy={busy}>
                  <Button
                    tone="primary"
                    disabled={busy}
                    aria-label={t('partyUploads.approveLabel', { name: item.name })}
                    onClick={() => void handleModerate(item, 'approve')}
                  >
                    {t('partyUploads.approve')}
                  </Button>
                  <Button
                    tone="danger"
                    disabled={busy}
                    aria-label={t('partyUploads.rejectLabel', { name: item.name })}
                    onClick={() => void handleModerate(item, 'reject')}
                  >
                    {t('partyUploads.reject')}
                  </Button>
                </PartyUploadRow>
              ))}
            </ul>
          </Panel>
        )}

        {visible.length > 0 && (
          <Panel
            title={t('partyUploads.sectionVisible')}
            note={t('partyUploads.sectionVisibleNote')}
            testId="party-uploads-visible"
            headingLevel={2}
          >
            <ul className="pw-mod-list">
              {visible.map((item) => (
                <PartyUploadRow key={item.fileItemId} item={item} busy={busy}>
                  <Button
                    tone="danger"
                    disabled={busy}
                    aria-label={t('partyUploads.hideLabel', { name: item.name })}
                    onClick={() => void handleModerate(item, 'hide')}
                  >
                    {t('partyUploads.hide')}
                  </Button>
                </PartyUploadRow>
              ))}
            </ul>
          </Panel>
        )}

        {removed.length > 0 && (
          <Panel
            title={t('partyUploads.sectionRemoved')}
            note={t('partyUploads.sectionRemovedNote')}
            testId="party-uploads-removed"
            headingLevel={2}
          >
            <ul className="pw-mod-list">
              {removed.map((item) => (
                <PartyUploadRow key={item.fileItemId} item={item} busy={busy}>
                  <Button
                    disabled={busy}
                    aria-label={t('partyUploads.restoreLabel', { name: item.name })}
                    onClick={() => void handleModerate(item, 'restore')}
                  >
                    {t('partyUploads.restore')}
                  </Button>
                </PartyUploadRow>
              ))}
            </ul>
          </Panel>
        )}
      </div>
    </main>
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
    <li className="pw-mod-row" data-testid="party-upload-row">
      <img
        src={item.thumbnailUrl}
        alt=""
        className="pw-mod-thumb"
        loading="lazy"
        onError={(e) => { (e.target as HTMLImageElement).style.display = 'none'; }}
      />
      <span className="pw-mod-text">
        <span className="pw-mod-name">{item.name}</span>
        {/* The state in words beside the picture, never by colour alone —
            and in its own element, so it can be read as one thing. */}
        <span className="pw-mod-meta">
          <span className="pw-mod-state" data-status={item.status}>
            {t(STATUS_LABEL_KEY[item.status])}
          </span>
          <span aria-hidden> · </span>
          <span>{formatDate(item.uploadedAt)}</span>
        </span>
      </span>
      <span className="pw-mod-actions">{children}</span>
    </li>
  );
}
