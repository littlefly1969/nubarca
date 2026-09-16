import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import {
  ApiError,
  listPartyMessages,
  moderatePartyMessage,
  partyMessageActions,
  DESTRUCTIVE_PARTY_MESSAGE_ACTIONS,
  setAlbumPartyMode,
  type PartyMessage,
  type PartyMessageAction,
  type PartyMessageList,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n, type MessageKey } from '../i18n';
import { Badge, Button, EmptyState, Notice, Panel, PanelSkeleton, SwitchRow } from '../party/workspace/ui';
import '../party/workspace/PartyWorkspace.css';

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
  // The queue is reached FROM a party, and its own back link returns there.
  // Falling back to the album keeps an old bookmark working.
  const [searchParams] = useSearchParams();
  const partyId = searchParams.get('party');
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

  const back = partyId
    ? { to: `/parties/${partyId}?section=activities`, label: t('partyUploads.backToParty') }
    : { to: `/albums/${albumId}`, label: t('partyUploads.backToAlbum') };

  if (status.kind === 'loading') {
    return (
      <main className="pw" data-testid="party-messages-page">
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
      <main className="pw" data-testid="party-messages-page">
        <header className="pw-head">
          <Link to={back.to} className="pw-back">← {back.label}</Link>
        </header>
        <Notice
          tone="error"
          testId="party-messages-error"
          title={status.message}
          actions={<Button onClick={load}>{t('common.retry')}</Button>}
        >
          <p>{t('party.loadErrorBody')}</p>
        </Notice>
      </main>
    );
  }

  const pending = status.list.items.filter((m) => m.status === 'pending').length;

  return (
    <main className="pw" data-testid="party-messages-page">
      <header className="pw-head">
        <Link to={back.to} className="pw-back">← {back.label}</Link>
        <div className="pw-head-main">
          <div className="pw-identity">
            <h1 className="pw-title">{t('partyMessages.title')}</h1>
            <p className="pw-section-lede">{t('partyMessages.intro')}</p>
          </div>
        </div>
      </header>

      <div className="pw-panels">
        {!status.list.isOwner && (
          <Notice tone="info" testId="party-messages-delegate-notice">
            <p>{t('partyMessages.delegateNotice')}</p>
          </Notice>
        )}

        {/* The approval switch is a party SETTING: a delegate moderates the
            messages without ever changing what the party requires. */}
        {status.list.isOwner && status.list.partyActive && (
          <Panel title={t('partyMessages.rule')} testId="message-approval-toggle" headingLevel={2}>
            <div className="pw-rows">
              <SwitchRow
                testId="party-messages-approval"
                label={t('partyMessages.requireApproval')}
                note={t('partyMessages.requireApprovalHelp')}
                checked={status.list.requireMessageApproval}
                disabled={busy}
                onChange={(next) => void toggleApproval(next)}
              />
            </div>
          </Panel>
        )}

        {!status.list.partyActive && (
          <EmptyState
            testId="party-messages-no-party"
            title={t('partyMessages.noPartyTitle')}
            body={t('partyMessages.noParty')}
          />
        )}

        {status.list.partyActive && status.list.items.length === 0 && (
          <EmptyState
            testId="party-messages-empty"
            title={t('partyMessages.emptyTitle')}
            body={t('partyMessages.empty')}
          />
        )}

        {status.list.items.length > 0 && (
          <Panel
            title={t('partyMessages.queue')}
            testId="party-messages-queue"
            headingLevel={2}
            aside={pending > 0
              ? <Badge kind="warn">{t('party.photos.pending', { count: pending })}</Badge>
              : undefined}
          >
            <div className="pw-chips" role="tablist" aria-label={t('partyMessages.filters')}>
              {(['all', 'pending', 'visible', 'hidden', 'hero'] as const).map((key) => (
                <button
                  key={key}
                  type="button"
                  role="tab"
                  aria-selected={filter === key}
                  className="pw-chip"
                  data-testid={`party-messages-filter-${key}`}
                  onClick={() => setFilter(key)}
                >
                  {t(FILTER_LABEL_KEY[key])}
                </button>
              ))}
            </div>

            {shown.length === 0 ? (
              <p className="pw-small pw-muted" data-testid="party-messages-empty-filtered">
                {t('partyMessages.emptyFiltered')}
              </p>
            ) : (
              <ul className="pw-mod-list">
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
          </Panel>
        )}
      </div>
    </main>
  );
}

// One label per action, so the rendered set follows the shared matrix rather
// than a second list of conditions kept in step by hand.
const PARTY_MESSAGE_ACTION_LABELS = {
  approve: 'partyMessages.approve',
  reject: 'partyMessages.reject',
  hide: 'partyMessages.hide',
  restore: 'partyMessages.restore',
  'promote-hero': 'partyMessages.promoteHero',
  'demote-hero': 'partyMessages.demoteHero',
} as const;

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
    <li className="pw-mod-row pw-mod-row--message" data-testid="party-message-row">
      <span className="pw-mod-text">
        <span className="pw-mod-name">
          {message.displayName ?? t('partyMessages.anonymous')}
          {message.isHero && (
            <span className="pw-badge pw-badge--published pw-badge--plain">
              {t('partyMessages.heroBadge')}
            </span>
          )}
        </span>
        {/* Rendered as TEXT. Never dangerouslySetInnerHTML, never a Markdown
            renderer: the body is whatever a stranger typed. */}
        <span className="pw-message-body">{message.text}</span>
        <span className="pw-mod-meta">
          <span className="pw-mod-state" data-status={message.status}>
            {t(STATUS_LABEL_KEY[message.status])}
          </span>
          <span aria-hidden> · </span>
          <span>{formatDate(message.createdAt)}</span>
        </span>
      </span>
      <span className="pw-mod-actions">
        {/* WHICH actions this message admits comes from the shared transition
            matrix (@nubarca/contracts), not from conditions written here. The
            rules used to live in this markup, where a second client could not
            read them — and the phone now offers exactly the same set. */}
        {partyMessageActions(message).map((action) => (
          <Button
            key={action}
            tone={DESTRUCTIVE_PARTY_MESSAGE_ACTIONS.includes(action) ? 'danger' : 'secondary'}
            disabled={busy}
            onClick={() => onAct(action)}
          >
            {t(PARTY_MESSAGE_ACTION_LABELS[action])}
          </Button>
        ))}
      </span>
    </li>
  );
}
