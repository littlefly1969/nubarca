import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import {
  ApiError,
  DESTRUCTIVE_PARTY_MESSAGE_ACTIONS,
  partyMessageActions,
  type PartyGuestbookManagedEntry,
  type PartyGuestbookManagerList,
  type PartyMessageAction,
} from '@nubarca/api-client';
import { usePartyApi } from '../party/workspace/partyApi';
import { useAuth } from '../auth/useAuth';
import { useI18n, type MessageKey } from '../i18n';
import { Badge, Button, EmptyState, Notice, Panel, PanelSkeleton } from '../party/workspace/ui';
import '../party/workspace/PartyWorkspace.css';

// THE GUEST BOOK's moderation queue — the third contribution's queue, beside
// the photographs' and the greetings'.
//
// It is PARTY-scoped, which is the one way it differs from the greetings page
// beside it, and it differs because the resource does: a book survives a QR
// rotation, an album change and a party that has no album yet. So the route
// names the party and there is no album id anywhere on this page.
//
// There is deliberately no Hero here, and no "show on the slideshow": a
// dedication is written to be kept, not projected, and the absence of the
// action is the invariant.

type Status =
  | { kind: 'loading' }
  | { kind: 'ready'; list: PartyGuestbookManagerList }
  | { kind: 'error'; message: string };

type Filter = 'all' | 'pending' | 'visible' | 'hidden';

const STATUS_LABEL_KEY: Record<PartyGuestbookManagedEntry['status'], MessageKey> = {
  pending: 'partyMessages.statusPending',
  visible: 'partyGuestbook.statusInBook',
  hidden: 'partyMessages.statusHidden',
  rejected: 'partyMessages.statusRejected',
};

const FILTER_LABEL_KEY: Record<Filter, MessageKey> = {
  all: 'partyMessages.filterAll',
  pending: 'partyMessages.filterPending',
  visible: 'partyGuestbook.filterInBook',
  hidden: 'partyMessages.filterHidden',
};

/**
 * `partyId` and `back` are handed in by the Party Crew surface, which reaches
 * this page through its own family of routes; the host's route supplies them
 * from the URL. Every crew call accepts the id and resolves the party from the
 * device cookie regardless.
 */
export function PartyGuestbookPage({
  partyId: partyIdProp, back: backProp,
}: {
  partyId?: string;
  back?: { to: string; label: string };
} = {}) {
  const { partyId: routePartyId } = useParams<{ partyId: string }>();
  const partyId = partyIdProp ?? routePartyId;
  const navigate = useNavigate();
  const { invalidateAuth } = useAuth();
  const { t } = useI18n();
  const api = usePartyApi();
  const [status, setStatus] = useState<Status>({ kind: 'loading' });
  const [filter, setFilter] = useState<Filter>('all');
  const [busy, setBusy] = useState(false);

  const back = backProp ?? {
    to: `/parties/${partyId}?section=photos`,
    label: t('partyUploads.backToParty'),
  };
  const backTo = back.to;

  const load = useCallback(() => {
    if (!partyId) return;
    api.listPartyGuestbook(partyId)
      .then((list) => setStatus({ kind: 'ready', list }))
      .catch((err) => {
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        // 404 covers "no such party" and "not yours to moderate" alike — the
        // server deliberately does not distinguish them, and neither does this.
        if (err instanceof ApiError && err.status === 404) { void navigate(backTo); return; }
        setStatus({ kind: 'error', message: t('partyGuestbook.loadError') });
      });
  }, [partyId, api, invalidateAuth, navigate, backTo, t]);

  useEffect(() => { load(); }, [load]);

  const list = status.kind === 'ready' ? status.list : null;

  const shown = useMemo(() => {
    if (!list) return [];
    switch (filter) {
      case 'pending': return list.entries.filter((e) => e.status === 'pending');
      case 'visible': return list.entries.filter((e) => e.status === 'visible');
      // One bucket for everything a manager took down, whether it was declined
      // before going in or removed after: the recovery action is the same.
      case 'hidden':
        return list.entries.filter((e) => e.status === 'hidden' || e.status === 'rejected');
      default: return list.entries;
    }
  }, [list, filter]);

  const act = async (entry: PartyGuestbookManagedEntry, action: PartyMessageAction) => {
    if (!partyId) return;
    setBusy(true);
    try {
      await api.moderatePartyGuestbookEntry(partyId, entry.id, action);
      load();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setStatus({ kind: 'error', message: t('partyGuestbook.updateError') });
    } finally {
      setBusy(false);
    }
  };

  if (status.kind === 'loading') {
    return (
      <main className="pw" data-testid="party-guestbook-page">
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
      <main className="pw" data-testid="party-guestbook-page">
        <header className="pw-head">
          <Link to={back.to} className="pw-back">← {back.label}</Link>
        </header>
        <Notice
          tone="error"
          testId="party-guestbook-error"
          title={status.message}
          actions={<Button onClick={load}>{t('common.retry')}</Button>}
        >
          <p>{t('party.loadErrorBody')}</p>
        </Notice>
      </main>
    );
  }

  const pending = status.list.entries.filter((e) => e.status === 'pending').length;

  return (
    <main className="pw" data-testid="party-guestbook-page">
      <header className="pw-head">
        <Link to={back.to} className="pw-back">← {back.label}</Link>
        <div className="pw-head-main">
          <div className="pw-identity">
            <h1 className="pw-title">{t('partyGuestbook.title')}</h1>
            <p className="pw-section-lede">{t('partyGuestbook.intro')}</p>
          </div>
        </div>
      </header>

      <div className="pw-panels">
        {!status.list.isOwner && (
          <Notice tone="info" testId="party-guestbook-delegate-notice">
            <p>{t('partyGuestbook.delegateNotice')}</p>
          </Notice>
        )}

        {/* THE BOOK IS CLOSED, and what is in it is still here. Said plainly,
            because the alternative is a manager reading a full queue and
            wondering why no guest can see any of it. */}
        {!status.list.guestbookEnabled && (
          <Notice tone="warn" testId="party-guestbook-disabled">
            <p>{t('partyGuestbook.disabledNotice')}</p>
          </Notice>
        )}

        {/* WHAT THIS QUEUE IS FOR, said once: whether dedications wait for a
            decision is configured on the party's contribution card, beside the
            other two channels, and not duplicated here. A switch on the queue
            it governs would be a second place for the same setting. */}
        <p className="pw-small pw-muted" data-testid="party-guestbook-approval-state">
          {status.list.requireGuestbookApproval
            ? t('party.guestbook.approvalOn')
            : t('party.guestbook.approvalOff')}
        </p>

        {status.list.entries.length === 0 && (
          <EmptyState
            testId="party-guestbook-empty"
            title={t('partyGuestbook.emptyTitle')}
            body={t('partyGuestbook.empty')}
          />
        )}

        {status.list.entries.length > 0 && (
          <Panel
            title={t('partyGuestbook.queue')}
            testId="party-guestbook-queue"
            headingLevel={2}
            aside={pending > 0
              ? <Badge kind="warn">{t('party.photos.pending', { count: pending })}</Badge>
              : undefined}
          >
            <div className="pw-chips" role="tablist" aria-label={t('partyMessages.filters')}>
              {(['all', 'pending', 'visible', 'hidden'] as const).map((key) => (
                <button
                  key={key}
                  type="button"
                  role="tab"
                  aria-selected={filter === key}
                  className="pw-chip"
                  data-testid={`party-guestbook-filter-${key}`}
                  onClick={() => setFilter(key)}
                >
                  {t(FILTER_LABEL_KEY[key])}
                </button>
              ))}
            </div>

            {shown.length === 0 ? (
              <p className="pw-small pw-muted" data-testid="party-guestbook-empty-filtered">
                {t('partyMessages.emptyFiltered')}
              </p>
            ) : (
              <ul className="pw-mod-list">
                {shown.map((entry) => (
                  <GuestbookRow
                    key={entry.id}
                    entry={entry}
                    busy={busy}
                    onAct={(action) => void act(entry, action)}
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

const GUESTBOOK_ACTION_LABELS = {
  approve: 'partyGuestbook.approve',
  reject: 'partyMessages.reject',
  hide: 'partyGuestbook.hide',
  restore: 'partyMessages.restore',
} as const;

function GuestbookRow({
  entry, busy, onAct,
}: {
  entry: PartyGuestbookManagedEntry;
  busy: boolean;
  onAct: (action: PartyMessageAction) => void;
}) {
  const { t, formatDate } = useI18n();
  // The SHARED transition matrix decides which actions exist, minus the two
  // that only a greeting has. A dedication is never a Hero, so the surface does
  // not offer it — and the server has no route for it either.
  const actions = partyMessageActions({ status: entry.status, isHero: false })
    .filter((action): action is keyof typeof GUESTBOOK_ACTION_LABELS =>
      action in GUESTBOOK_ACTION_LABELS);

  return (
    <li className="pw-mod-row pw-mod-row--message" data-testid="party-guestbook-row">
      <span className="pw-mod-text">
        <span className="pw-mod-name">
          {entry.authorDisplayName ?? t('partyMessages.anonymous')}
        </span>
        {/* Rendered as TEXT. Never dangerouslySetInnerHTML, never a Markdown
            renderer: the body is whatever a stranger typed. */}
        <span className="pw-message-body">{entry.body}</span>
        <span className="pw-mod-meta">
          <span className="pw-mod-state" data-status={entry.status}>
            {t(STATUS_LABEL_KEY[entry.status])}
          </span>
          <span aria-hidden> · </span>
          <span>{formatDate(entry.createdAt)}</span>
        </span>
      </span>
      <span className="pw-mod-actions">
        {actions.map((action) => (
          <Button
            key={action}
            tone={DESTRUCTIVE_PARTY_MESSAGE_ACTIONS.includes(action) ? 'danger' : 'secondary'}
            disabled={busy}
            onClick={() => onAct(action)}
          >
            {t(GUESTBOOK_ACTION_LABELS[action])}
          </Button>
        ))}
      </span>
    </li>
  );
}
