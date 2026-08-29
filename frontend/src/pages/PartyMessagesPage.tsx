import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import {
  ApiError,
  listPartyMessages,
  moderatePartyMessage,
  setAlbumPartyMode,
  type PartyMessage,
  type PartyMessageAction,
  type PartyMessageList,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n, type MessageKey } from '../i18n';

type Status =
  | { kind: 'loading' }
  | { kind: 'ready'; list: PartyMessageList }
  | { kind: 'error'; message: string };

type Filter = 'all' | 'pending' | 'visible' | 'hidden' | 'hero';

const STATUS_LABEL_KEY: Record<PartyMessage['status'], MessageKey> = {
  pending: 'partyMessages.statusPending',
  visible: 'partyMessages.statusVisible',
  hidden: 'partyMessages.statusHidden',
  rejected: 'partyMessages.statusRejected',
};

const FILTER_LABEL_KEY: Record<Filter, MessageKey> = {
  all: 'partyMessages.filterAll',
  pending: 'partyMessages.filterPending',
  visible: 'partyMessages.filterVisible',
  hidden: 'partyMessages.filterHidden',
  hero: 'partyMessages.filterHero',
};

// Moderation of the guest MESSAGE feed for one album's current party. Reachable
// by the album owner and by a member the owner has given the narrow
// `canManagePartyMessages` delegation — the SERVER decides which, and answers a
// generic 404 to everybody else, so this page renders whatever the API let it
// have rather than checking a role of its own.
//
// The one thing the page does branch on is `isOwner`: the approval switch is a
// party SETTING, and a delegate moderates messages without ever changing what
// the party requires.
export function PartyMessagesPage() {
  const { albumId } = useParams<{ albumId: string }>();
  const navigate = useNavigate();
  const { invalidateAuth } = useAuth();
  const { t } = useI18n();
  const [status, setStatus] = useState<Status>({ kind: 'loading' });
  const [filter, setFilter] = useState<Filter>('all');
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => {
    if (!albumId) return;
    listPartyMessages(albumId)
      .then((list) => setStatus({ kind: 'ready', list }))
      .catch((err) => {
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        // 404 covers "no such album" and "not yours to manage" alike — the
        // server deliberately does not distinguish them, and neither do we.
        if (err instanceof ApiError && err.status === 404) { void navigate('/albums'); return; }
        setStatus({ kind: 'error', message: t('partyMessages.loadError') });
      });
  }, [albumId, invalidateAuth, navigate, t]);

  useEffect(() => { load(); }, [load]);

  const list = status.kind === 'ready' ? status.list : null;

  const shown = useMemo(() => {
    if (!list) return [];
    switch (filter) {
      case 'pending': return list.items.filter((m) => m.status === 'pending');
      case 'visible': return list.items.filter((m) => m.status === 'visible');
      // One bucket for everything a manager took down, whether it was declined
      // before going up or removed after: the recovery action is the same.
      case 'hidden': return list.items.filter((m) => m.status === 'hidden' || m.status === 'rejected');
      case 'hero': return list.items.filter((m) => m.isHero);
      default: return list.items;
    }
  }, [list, filter]);

  const act = async (message: PartyMessage, action: PartyMessageAction) => {
    if (!albumId) return;
    setBusy(true);
    try {
      await moderatePartyMessage(albumId, message.id, action);
      load();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setStatus({ kind: 'error', message: t('partyMessages.updateError') });
    } finally {
      setBusy(false);
    }
  };

  const toggleApproval = async (next: boolean) => {
    if (!albumId) return;
    setBusy(true);
    try {
      // Party stays on and the tokens are untouched: only the message-approval
      // sub-switch moves.
      await setAlbumPartyMode(albumId, true, undefined, undefined, undefined, next);
      load();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setStatus({ kind: 'error', message: t('partyMessages.approvalError') });
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
        <p className="error-message" data-testid="party-messages-error">{status.message}</p>
        <button type="button" onClick={load}>{t('common.retry')}</button>
      </div>
    );
  }

  return (
    <div className="page party-messages-page">
      <p>
        <Link to={`/albums/${albumId}`}>{t('partyUploads.backToAlbum')}</Link>
      </p>
      <h1>{t('partyMessages.title')}</h1>
      <p className="muted">{t('partyMessages.intro')}</p>

      {!status.list.isOwner && (
        <p className="muted" data-testid="party-messages-delegate-notice">
          {t('partyMessages.delegateNotice')}
        </p>
      )}

      {status.list.isOwner && status.list.partyActive && (
        <div className="album-party-approval" data-testid="message-approval-toggle">
          <label className="album-tv-label">
            <input
              type="checkbox"
              checked={status.list.requireMessageApproval}
              disabled={busy}
              onChange={(e) => void toggleApproval(e.target.checked)}
              aria-label={t('partyMessages.requireApproval')}
            />
            <span>{t('partyMessages.requireApproval')}</span>
          </label>
          <p className="muted">{t('partyMessages.requireApprovalHelp')}</p>
        </div>
      )}

      {!status.list.partyActive && (
        <p className="empty-state" data-testid="party-messages-no-party">
          {t('partyMessages.noParty')}
        </p>
      )}

      {status.list.partyActive && status.list.items.length === 0 && (
        <p className="empty-state" data-testid="party-messages-empty">
          {t('partyMessages.empty')}
        </p>
      )}

      {status.list.items.length > 0 && (
        <>
          <div className="party-messages-filters" role="tablist">
            {(['all', 'pending', 'visible', 'hidden', 'hero'] as const).map((key) => (
              <button
                key={key}
                type="button"
                role="tab"
                aria-selected={filter === key}
                className={filter === key ? 'active' : undefined}
                onClick={() => setFilter(key)}
              >
                {t(FILTER_LABEL_KEY[key])}
              </button>
            ))}
          </div>

          {shown.length === 0 ? (
            <p className="empty-state" data-testid="party-messages-empty-filtered">
              {t('partyMessages.emptyFiltered')}
            </p>
          ) : (
            <ul className="party-messages-list">
              {shown.map((message) => (
                <PartyMessageRow
                  key={message.id}
                  message={message}
                  busy={busy}
                  onAct={(action) => void act(message, action)}
                />
              ))}
            </ul>
          )}
        </>
      )}
    </div>
  );
}

function PartyMessageRow({
  message,
  busy,
  onAct,
}: {
  message: PartyMessage;
  busy: boolean;
  onAct: (action: PartyMessageAction) => void;
}) {
  const { t, formatDate } = useI18n();
  return (
    <li className="party-mod-row party-message-row" data-testid="party-message-row">
      <span className="party-message-author">
        {message.displayName ?? t('partyMessages.anonymous')}
      </span>
      {/* Rendered as TEXT. Never dangerouslySetInnerHTML, never a Markdown
          renderer: the body is whatever a stranger typed. */}
      <span className="party-message-body">{message.text}</span>
      <span className={`party-mod-status status-${message.status}`}>
        {t(STATUS_LABEL_KEY[message.status])}
      </span>
      {message.isHero && (
        <span className="party-message-hero-badge">{t('partyMessages.heroBadge')}</span>
      )}
      <span className="party-mod-meta">{formatDate(message.createdAt)}</span>
      <span className="party-mod-actions">
        {message.status === 'pending' && (
          <button type="button" disabled={busy} onClick={() => onAct('approve')}>
            {t('partyMessages.approve')}
          </button>
        )}
        {message.status === 'pending' && (
          <button type="button" className="btn-danger" disabled={busy} onClick={() => onAct('reject')}>
            {t('partyMessages.reject')}
          </button>
        )}
        {message.status === 'visible' && (
          <button type="button" className="btn-danger" disabled={busy} onClick={() => onAct('hide')}>
            {t('partyMessages.hide')}
          </button>
        )}
        {(message.status === 'hidden' || message.status === 'rejected') && (
          <button type="button" disabled={busy} onClick={() => onAct('restore')}>
            {t('partyMessages.restore')}
          </button>
        )}
        {/* Hero is offered only on a LIVE message, matching the server, which
            refuses to promote anything that is not currently visible. */}
        {message.status === 'visible' && !message.isHero && (
          <button type="button" disabled={busy} onClick={() => onAct('promote-hero')}>
            {t('partyMessages.promoteHero')}
          </button>
        )}
        {message.isHero && (
          <button type="button" disabled={busy} onClick={() => onAct('demote-hero')}>
            {t('partyMessages.demoteHero')}
          </button>
        )}
      </span>
    </li>
  );
}
