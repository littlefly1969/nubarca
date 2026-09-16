import { useState } from 'react';
import {
  ApiError,
  setAlbumPartyMode,
  transitionParty,
  type AlbumPartyStatus,
  type Party,
} from '@nubarca/api-client';
import { useAuth } from '../../auth/useAuth';
import { useI18n, type MessageKey } from '../../i18n';
import { mainMediaSource } from '../partyModel';
import { PartyShareCard } from './PartyShareCard';
import {
  attentionBodyKey,
  attentionBodyPluralKey,
  attentionTitleKey,
  factsFailed,
  loadedValue,
  primaryIntent,
  statusStoryKey,
  stepActionKey,
  stepNoteKey,
  stepTitleKey,
  workspaceAttention,
  workspaceSteps,
  type WorkspaceFacts,
  type WorkspaceSection,
} from './partyWorkspaceModel';
import { Button, EmptyState, Notice, Panel, SectionHead, Step } from './ui';

// "RIEPILOGO" — the page that answers one question: what do I do now?
//
// It is deliberately NOT a dashboard. There is no metric here that the host
// cannot act on, no chart, and no card that exists to fill a column. What there
// is, in the order a host reads it:
//
//   1. WHERE THE PARTY IS, in a sentence, and the ONE action that moves it on.
//   2. WHAT IS WRONG, if anything — a guest link that expired mid-party, a
//      photograph deleted out from under a poster, a queue nobody has read.
//   3. WHAT IS LEFT TO DO, as work rather than as numbers, each step naming the
//      section that does it.
//   4. THE LINK, once there is one to give away.
//
// During the party this page steps back: the console is the product then, and
// the summary's whole job is to point at it.

export function PartySummarySection({
  facts, onNavigate, onPartyUpdated, onAlbumPartyUpdated, onPartyReload, onRetry,
}: {
  facts: WorkspaceFacts;
  onNavigate(section: WorkspaceSection): void;
  onPartyUpdated(next: Party): void;
  onAlbumPartyUpdated(next: AlbumPartyStatus): void;
  /** Read the party again: opening it to guests publishes it server-side. */
  onPartyReload(): Promise<void>;
  onRetry(): void;
}) {
  const { t, tn, formatDate } = useI18n();
  const { party } = facts;
  const albumParty = loadedValue(facts.albumParty);
  const steps = workspaceSteps(facts);
  const attention = workspaceAttention(facts);
  const album = mainMediaSource(party);
  const remaining = steps.filter((step) => !step.done && step.id !== 'start');

  return (
    <>
      <SectionHead title={t('party.section.summary')} lede={t('party.summary.lede')} />

      <NextMove
        facts={facts}
        onNavigate={onNavigate}
        onPartyUpdated={onPartyUpdated}
        onAlbumPartyUpdated={onAlbumPartyUpdated}
        onPartyReload={onPartyReload}
        onRetry={onRetry}
      />

      {/* Something was asked for and did not come. Said once, here, rather than
          letting each panel below quietly report an absence as a fact: a
          checklist missing its steps is confusing, a checklist LYING about them
          is worse. */}
      {factsFailed(facts) && (
        <Notice
          tone="warn"
          testId="party-facts-error"
          title={t('party.summary.partialTitle')}
          actions={<Button onClick={onRetry} data-testid="party-facts-retry">{t('common.retry')}</Button>}
        >
          <p>{t('party.summary.partialBody')}</p>
        </Notice>
      )}

      {attention.length > 0 && (
        <Panel
          title={t('party.summary.attention')}
          note={t('party.summary.attentionNote')}
          testId="party-attention"
        >
          {attention.map((item) => (
            <Notice
              key={item.id}
              tone={item.tone === 'warn' ? 'warn' : 'info'}
              title={t(attentionTitleKey(item.id))}
              testId={`party-attention-${item.id}`}
              actions={(
                <Button onClick={() => onNavigate(item.section)}>
                  {t(`party.section.${item.section}` as MessageKey)}
                </Button>
              )}
            >
              {/* A counted message agrees with its number; an uncounted one is
                  one sentence. Which applies is decided by the item itself. */}
              <p>
                {item.count === undefined
                  ? t(attentionBodyKey(item.id))
                  : tn(item.count, attentionBodyPluralKey(item.id))}
              </p>
            </Notice>
          ))}
        </Panel>
      )}

      {steps.length > 0 && (
        <Panel
          title={t('party.summary.steps')}
          note={remaining.length === 0
            ? t('party.summary.stepsAllDone')
            : tn(remaining.length, 'party.summary.stepsNote')}
          testId="party-steps"
        >
          <ol className="pw-steps">
            {steps.filter((step) => step.id !== 'start').map((step) => (
              <Step
                key={step.id}
                done={step.done}
                testId={`party-step-${step.id}`}
                title={t(stepTitleKey(step.id))}
                note={step.done
                  ? undefined
                  : `${t(stepNoteKey(step.id))}${step.optional ? ` · ${t('party.step.optional')}` : ''}`}
                action={step.done ? undefined : (
                  <Button onClick={() => onNavigate(step.section)} data-testid={`party-step-go-${step.id}`}>
                    {t(stepActionKey(step.id))}
                  </Button>
                )}
              />
            ))}
          </ol>
        </Panel>
      )}

      <Panel title={t('party.summary.essentials')} testId="party-essentials">
        <div className="pw-rows">
          <div className="pw-row">
            <span className="pw-row-text">
              <span className="pw-row-label">{t('party.overview.dateLabel')}</span>
              <span className="pw-row-note">
                {party.eventStartsAt ? formatDate(party.eventStartsAt) : t('party.overview.noDate')}
              </span>
            </span>
            <span className="pw-row-control">
              <Button tone="quiet" onClick={() => onNavigate('settings')}>{t('party.summary.change')}</Button>
            </span>
          </div>
          <div className="pw-row">
            <span className="pw-row-text">
              <span className="pw-row-label">{t('party.summary.albumRow')}</span>
              <span className="pw-row-note">{album ? album.albumName : t('party.summary.noAlbum')}</span>
            </span>
            <span className="pw-row-control">
              <Button tone="quiet" onClick={() => onNavigate('photos')}>
                {album ? t('party.summary.change') : t('party.summary.choose')}
              </Button>
            </span>
          </div>
          <GuestRow facts={facts} onNavigate={onNavigate} />
        </div>
      </Panel>

      {albumParty?.partyMode && albumParty.partyUrl
        ? <PartyShareCard partyUrl={albumParty.partyUrl} />
        : null}
    </>
  );
}

/**
 * The guest row, which says what is TRUE of this party rather than what the
 * product would like it to have.
 *
 * A party with no guest list is not an incomplete party: a lot of evenings have
 * an open door and nothing to RSVP to. So a list that does not exist is
 * described as optional, and the numbers offered are the ones that mean
 * something — arrivals recorded when the party is running, replies when there
 * is a list to reply to, and never "expected 0 · missing 0".
 */
function GuestRow({
  facts, onNavigate,
}: {
  facts: WorkspaceFacts;
  onNavigate(section: WorkspaceSection): void;
}) {
  const { t } = useI18n();
  const { party } = facts;
  const guests = loadedValue(facts.guests);
  const listed = (guests?.groups ?? 0) > 0;
  const running = party.status === 'live' || party.status === 'ended';

  const note = facts.guests.status === 'error'
    ? t('party.summary.guestsUnknown')
    : guests === null
      ? t('common.loading')
      : running
        ? t('party.summary.guestsArrived', { count: guests.attendance.totalArrivals })
        : listed
          ? t('party.summary.guestsReplies', {
            attending: guests.rsvp.attending, pending: guests.rsvp.missingResponses,
          })
          : t('party.summary.guestsOptional');

  return (
    <div className="pw-row" data-testid="party-essentials-guests">
      <span className="pw-row-text">
        <span className="pw-row-label">{t('party.section.guests')}</span>
        <span className="pw-row-note">{note}</span>
      </span>
      <span className="pw-row-control">
        <Button tone="quiet" onClick={() => onNavigate('guests')}>{t('party.summary.open')}</Button>
      </span>
    </div>
  );
}

/**
 * The next move, and only one.
 *
 * `primaryIntent` decides which — it knows that a draft without an album is not
 * one button away from a party — and this renders it plus the state it applies
 * to. The state and the action are separate lines on purpose: "Pubblicata" and
 * "Avvia la festa" are two different facts, and a host must never have to read
 * a button to find out where their evening is.
 */
function NextMove({
  facts, onNavigate, onPartyUpdated, onAlbumPartyUpdated, onPartyReload, onRetry,
}: {
  facts: WorkspaceFacts;
  onNavigate(section: WorkspaceSection): void;
  onPartyUpdated(next: Party): void;
  onAlbumPartyUpdated(next: AlbumPartyStatus): void;
  onPartyReload(): Promise<void>;
  onRetry(): void;
}) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const { party } = facts;
  const album = mainMediaSource(party);
  const intent = primaryIntent(facts);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState<MessageKey | null>(null);
  // Ending the evening is irreversible — there is no re-open transition — so it
  // is confirmed INLINE, where the sentence that matters fits, rather than in a
  // browser dialog that can only ask "are you sure?".
  const [endingAsked, setEndingAsked] = useState(false);

  async function guarded(work: () => Promise<void>) {
    setBusy(true); setFailed(null);
    try {
      await work();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      // A refusal carries the party as it actually is, so the page adopts the
      // server's answer instead of insisting on the state it acted from.
      const body = (err as ApiError).body as { party?: Party } | undefined;
      if (body?.party) onPartyUpdated(body.party);
      setFailed('party.action.failed');
    } finally { setBusy(false); }
  }

  const openToGuests = () => guarded(async () => {
    // Opening the party to its guests is what publishes it: the capability's
    // own transition, not a second "publish" button that would race it.
    //
    // AND THE PARTY MOVED WITH IT. The capability's answer describes the
    // album's settings and says nothing about the party's lifecycle, so
    // adopting it alone left the badge reading "Bozza" and this very panel
    // still offering to publish something the server had already published —
    // until the host reloaded the page by hand. The party is read again, in
    // the same action, so there is one source of truth for what happened.
    onAlbumPartyUpdated(await setAlbumPartyMode(album!.albumId, true));
    await onPartyReload();
  });

  const transition = (action: 'start-live' | 'end-live') => guarded(async () => {
    onPartyUpdated(await transitionParty(party.id, action, party.version));
  });

  const story = t(statusStoryKey(party.status));

  let action = null;
  switch (intent.kind) {
    case 'unavailable':
      // Asked for and not answered. A placeholder here would wait for ever.
      action = (
        <Button tone="primary" size="lg" data-testid="party-next-retry" onClick={onRetry}>
          {t('common.retry')}
        </Button>
      );
      break;
    case 'unknown':
      // A placeholder the size of the button that is coming, so the panel does
      // not jump under a thumb already reaching for it.
      action = <div className="pw-skeleton pw-btn pw-btn--lg" aria-hidden style={{ width: '12rem' }} />;
      break;
    case 'link-album':
      action = (
        <Button tone="primary" size="lg" data-testid="party-next-move" onClick={() => onNavigate('photos')}>
          {t('party.next.linkAlbum')}
        </Button>
      );
      break;
    case 'open-to-guests':
      action = (
        <Button
          tone="primary" size="lg" busy={busy} data-testid="party-next-move"
          onClick={() => void openToGuests()}
        >
          {t('party.next.openToGuests')}
        </Button>
      );
      break;
    case 'start-live':
      action = (
        <Button
          tone="primary" size="lg" busy={busy} data-testid="party-next-move"
          onClick={() => void transition('start-live')}
        >
          {t('party.action.startLive')}
        </Button>
      );
      break;
    case 'go-live-console':
      action = (
        <Button tone="primary" size="lg" data-testid="party-next-move" onClick={() => onNavigate('live')}>
          {t('party.next.openConsole')}
        </Button>
      );
      break;
    case 'open-photos':
      action = (
        <Button tone="primary" size="lg" data-testid="party-next-move" onClick={() => onNavigate('photos')}>
          {t('party.next.openPhotos')}
        </Button>
      );
      break;
  }

  return (
    <Panel
      tone="feature"
      testId="party-next"
      title={t('party.next.heading')}
      note={story}
      headingLevel={3}
    >
      {intent.kind === 'unavailable' ? (
        <p className="pw-panel-note" role="alert" data-testid="party-next-unavailable">
          {t('party.next.unavailable.why')}
        </p>
      ) : intent.kind !== 'unknown' && (
        <p className="pw-panel-note">{t(`party.next.${intent.kind}.why` as MessageKey)}</p>
      )}
      <div className="pw-panel-actions">
        {action}
        {party.status === 'live' && !endingAsked && (
          <Button onClick={() => { setFailed(null); setEndingAsked(true); }} data-testid="party-end-live">
            {t('party.action.endLive')}
          </Button>
        )}
      </div>
      {party.status === 'live' && endingAsked && (
        <Notice
          tone="warn"
          title={t('party.action.confirmEndTitle')}
          testId="party-end-live-confirm"
          actions={(
            <>
              <Button
                tone="danger" busy={busy} data-testid="party-end-live-yes"
                onClick={() => void transition('end-live').then(() => setEndingAsked(false))}
              >
                {t('party.action.endLive')}
              </Button>
              <Button disabled={busy} onClick={() => setEndingAsked(false)}>
                {t('party.create.cancel')}
              </Button>
            </>
          )}
        >
          <p>{t('party.action.confirmEnd')}</p>
        </Notice>
      )}
      {failed && <Notice tone="error" testId="party-next-failed"><p>{t(failed)}</p></Notice>}
    </Panel>
  );
}

/** What the summary shows while the party itself is still arriving. */
export function PartySummarySkeleton() {
  const { t } = useI18n();
  return (
    <EmptyState
      title={t('common.loading')}
      body={t('party.summary.loading')}
      testId="party-summary-loading"
    />
  );
}
