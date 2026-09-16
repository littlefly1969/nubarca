import { useState } from 'react';
import {
  ApiError,
  GUEST_CONSOLE_PARAMS,
  transitionParty,
  unexpectedArrivals,
  type GuestDirectoryState,
  type Party,
  type AlbumPartyStatus,
} from '@nubarca/api-client';
import { useAuth } from '../../auth/useAuth';
import { usePermissions } from '../../auth/usePermissions';
import { PERMISSIONS } from '../../auth/permissions';
import { useI18n, type MessageKey } from '../../i18n';
import { mainMediaSource } from '../partyModel';
import { absoluteGuestUrl } from './PartyShareCard';
import {
  attentionBodyKey,
  attentionBodyPluralKey,
  attentionTitleKey,
  workspaceAttention,
  type WorkspaceFacts,
  type WorkspaceSection,
} from './partyWorkspaceModel';
import { Badge, Button, LinkRow, Notice, Panel, SectionHead, Stats } from './ui';

// "LIVE" — the party while it is happening.
//
// This is the only surface in the product designed to be used standing up, in a
// room with music, on a phone held in one hand, by somebody who is being spoken
// to at the same time. Everything about it follows from that:
//
//   THE NUMBERS FIRST, large, because "how many are here" is the question the
//   host is asked every ten minutes and the one they cannot answer from memory.
//
//   ONE TAP TO THE DOOR. Recording arrivals is the guest console's job and it
//   is not rebuilt here — but the console's own filters are reachable straight
//   from these numbers, so "who is missing" is a tap rather than a search.
//
//   EVERYTHING ELSE AS A LIST OF PLACES, each saying what is waiting there. No
//   configuration, no dials, no charts: an evening is not the moment to decide
//   how many seconds a photograph holds the television.
//
// It is also not a separate product. The sections around it do not disappear,
// the vocabulary does not change, and when the party ends this one simply goes
// away again.

export function PartyLiveSection({
  facts, onNavigate, onOpenGuests, onPartyUpdated, onRefresh,
}: {
  facts: WorkspaceFacts;
  onNavigate(section: WorkspaceSection): void;
  /** Open the guest console already filtered — the door, pre-sorted. */
  onOpenGuests(filter: GuestDirectoryState | null): void;
  onPartyUpdated(next: Party): void;
  onRefresh(): void;
}) {
  const { t, tn } = useI18n();
  const { party, albumParty, guests, moderation } = facts;
  const attention = workspaceAttention(facts);

  return (
    <>
      <SectionHead
        title={t('party.section.live')}
        lede={t('party.live.lede')}
        actions={(
          <Button tone="quiet" onClick={onRefresh} data-testid="party-live-refresh">
            {t('party.console.refresh')}
          </Button>
        )}
      />

      <Arrivals facts={facts} onOpenGuests={onOpenGuests} />

      {attention.length > 0 && attention.map((item) => (
        <Notice
          key={item.id}
          tone={item.tone === 'warn' ? 'warn' : 'info'}
          title={t(attentionTitleKey(item.id))}
          testId={`party-live-attention-${item.id}`}
          actions={(
            <Button onClick={() => onNavigate(item.section)}>
              {t(`party.section.${item.section}` as MessageKey)}
            </Button>
          )}
        >
          <p>
            {item.count === undefined
              ? t(attentionBodyKey(item.id))
              : tn(item.count, attentionBodyPluralKey(item.id))}
          </p>
        </Notice>
      ))}

      <RightNow party={party} albumParty={albumParty} moderation={moderation} />

      <EndTheParty party={party} onPartyUpdated={onPartyUpdated} />

      {guests === null && (
        <p className="visually-hidden" role="status">{t('common.loading')}</p>
      )}
    </>
  );
}

/**
 * How many people are here — and, when there is a guest list, how many are not.
 *
 * A party with NO guest list is not missing anything: an open door records
 * arrivals and nothing is "expected", so the panel says "presenze registrate"
 * and offers no "missing" that would always read zero. That is the difference
 * between a product that supports open parties and one that tolerates them.
 */
function Arrivals({
  facts, onOpenGuests,
}: {
  facts: WorkspaceFacts;
  onOpenGuests(filter: GuestDirectoryState | null): void;
}) {
  const { t } = useI18n();
  const { guests } = facts;
  const listed = (guests?.groups ?? 0) > 0;
  const attendance = guests?.attendance;

  return (
    <Panel tone="feature" testId="party-live-arrivals" title={t('party.live.arrivals')}>
      {guests === null ? (
        <div className="pw-skeleton pw-skeleton--panel" aria-hidden />
      ) : listed && attendance ? (
        <Stats
          testId="party-live-metrics"
          label={t('party.console.metrics')}
          items={[
            { key: 'arrived', label: t('party.console.metric.arrived'), value: attendance.totalArrivals, tone: 'accent' },
            { key: 'expected', label: t('party.console.metric.expected'), value: attendance.expectedPeople },
            { key: 'missing', label: t('party.console.metric.missing'), value: attendance.expectedMissing, tone: attendance.expectedMissing > 0 ? 'warn' : undefined },
            { key: 'others', label: t('party.console.metric.others'), value: unexpectedArrivals(attendance) },
          ]}
        />
      ) : (
        <Stats
          testId="party-live-metrics"
          label={t('party.console.metrics')}
          items={[{
            key: 'recorded',
            label: t('party.console.metric.recorded'),
            value: attendance?.totalArrivals ?? 0,
            tone: 'accent',
          }]}
        />
      )}

      <p className="pw-panel-note">
        {listed ? t('party.live.arrivalsNote') : t('party.live.arrivalsOpenNote')}
      </p>

      <div className="pw-panel-actions">
        <Button tone="primary" size="lg" data-testid="party-live-door" onClick={() => onOpenGuests(null)}>
          {t('party.live.openDoor')}
        </Button>
      </div>

      {/* The console's own filters, one tap away. They are shortcuts INTO the
          list, not a second list. */}
      {listed && (
        <div className="pw-chips" role="group" aria-label={t('party.live.shortcuts')}>
          {(['to_arrive', 'arrived', 'unexpected'] as const).map((filter) => (
            <button
              key={filter} type="button" className="pw-chip"
              data-testid={`party-live-filter-${filter}`}
              onClick={() => onOpenGuests(filter)}
            >
              {t(`party.console.filter.${filter}` as MessageKey)}
            </button>
          ))}
        </div>
      )}

      {/* Cooperative attendance, said once: the host is not the only way a name
          gets ticked off, and a room of two hundred should not queue at a
          phone. */}
      {listed && (
        <p className="pw-small pw-muted" data-testid="party-live-cooperative">
          {t('party.console.selfCheckInNote')}
        </p>
      )}
    </Panel>
  );
}

/** Everything else that might need the host in the next ten minutes. */
function RightNow({
  party, albumParty, moderation,
}: {
  party: Party;
  albumParty: AlbumPartyStatus | null;
  moderation: { uploads: number; messages: number } | null;
}) {
  const { t } = useI18n();
  const perms = usePermissions();
  const albumId = mainMediaSource(party)?.albumId ?? null;
  const partyUrl = albumParty?.partyMode ? albumParty.partyUrl : null;

  if (!albumId) return null;

  return (
    <Panel title={t('party.live.rightNow')} note={t('party.live.rightNowNote')} testId="party-live-now">
      <LinkRow
        to={`/albums/${albumId}/party-uploads?party=${party.id}`}
        testId="party-live-uploads"
        title={t('partyUploads.title')}
        note={albumParty?.uploadEnabled ? t('party.live.uploadsOpen') : t('party.live.uploadsClosed')}
        after={moderation && moderation.uploads > 0
          ? <Badge kind="warn">{t('party.photos.pending', { count: moderation.uploads })}</Badge>
          : undefined}
      />
      <LinkRow
        to={`/albums/${albumId}/party-messages?party=${party.id}`}
        testId="party-live-messages"
        title={t('partyMessages.title')}
        note={albumParty?.requireMessageApproval
          ? t('party.activities.approvalOn')
          : t('party.activities.approvalOff')}
        after={moderation && moderation.messages > 0
          ? <Badge kind="warn">{t('party.photos.pending', { count: moderation.messages })}</Badge>
          : undefined}
      />
      {albumParty?.gameEnabled && perms.hasAll([PERMISSIONS.partyAccess, PERMISSIONS.partyGames]) && (
        <LinkRow
          to={`/albums/${albumId}/party-game`}
          testId="party-live-control-room"
          title={t('partyGame.controlRoom')}
          note={t('party.live.controlRoomNote')}
        />
      )}
      {partyUrl && (
        <LinkRow
          href={absoluteGuestUrl(`${partyUrl}/tv`)}
          testId="party-live-stage"
          title={t('party.screens.openStage')}
          note={t('party.live.stageNote')}
        />
      )}
      {partyUrl && (
        <LinkRow
          href={absoluteGuestUrl(partyUrl)}
          testId="party-live-guest-view"
          title={t('party.live.guestView')}
          note={t('party.live.guestViewNote')}
        />
      )}
    </Panel>
  );
}

/** The one irreversible thing the console can do, kept at the bottom. */
function EndTheParty({
  party, onPartyUpdated,
}: {
  party: Party;
  onPartyUpdated(next: Party): void;
}) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const [asking, setAsking] = useState(false);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);

  async function run() {
    setBusy(true); setFailed(false);
    try {
      onPartyUpdated(await transitionParty(party.id, 'end-live', party.version));
      setAsking(false);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      const body = (err as ApiError).body as { party?: Party } | undefined;
      if (body?.party) onPartyUpdated(body.party);
      setFailed(true);
    } finally { setBusy(false); }
  }

  return (
    <Panel title={t('party.live.ending')} note={t('party.live.endingNote')} testId="party-live-end">
      {!asking ? (
        <div className="pw-panel-actions">
          <Button data-testid="party-end-live" onClick={() => { setFailed(false); setAsking(true); }}>
            {t('party.action.endLive')}
          </Button>
        </div>
      ) : (
        <Notice
          tone="warn"
          title={t('party.action.confirmEndTitle')}
          testId="party-end-live-confirm"
          actions={(
            <>
              <Button tone="danger" busy={busy} data-testid="party-end-live-yes" onClick={() => void run()}>
                {t('party.action.endLive')}
              </Button>
              <Button disabled={busy} onClick={() => setAsking(false)}>{t('party.create.cancel')}</Button>
            </>
          )}
        >
          <p>{t('party.action.confirmEnd')}</p>
        </Notice>
      )}
      {failed && <Notice tone="error"><p>{t('party.action.failed')}</p></Notice>}
    </Panel>
  );
}

/** The console's URL for a filtered guest list — the console's own keys. */
export function guestConsoleSearch(
  current: URLSearchParams, filter: GuestDirectoryState | null,
): URLSearchParams {
  const next = new URLSearchParams(current);
  next.set('section', 'guests');
  if (filter === null || filter === 'all') next.delete(GUEST_CONSOLE_PARAMS.state);
  else next.set(GUEST_CONSOLE_PARAMS.state, filter);
  next.delete(GUEST_CONSOLE_PARAMS.group);
  return next;
}
