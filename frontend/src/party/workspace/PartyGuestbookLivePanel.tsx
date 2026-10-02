import type {
  PartyGuestbookLiveControl,
  PartyGuestbookTvUnavailableReason,
  PartyTvPresentation,
} from '@nubarca/api-client';
import { useI18n, type MessageKey } from '../../i18n';
import { usePartyGuestbookLive } from '../usePartyGuestbookLive';
import { Badge, Button, Notice, Panel, SwitchRow } from './ui';

// THE GUEST BOOK, LIVE — two decisions, kept visibly apart.
//
//   The ROOM: whether guests may open the book and read it on their phones.
//   It never decides whether they may WRITE in it — that is the contribution
//   setting, and the copy says so.
//
//   The TELEVISION: whether the book is on the party's screen. The line here
//   is what the server says a paired television is told to show, not the
//   switch somebody pressed: the game may hold the screen, an emptied book
//   yields to the slideshow, and the regia must see that, not its own wish.
//
// Every control is one the SERVER offered this caller (`availableCommands`):
// the game holding the screen is said in words, not by greying a button out.

const PRESENTATION_LABEL: Record<PartyTvPresentation, MessageKey> = {
  slideshow: 'party.guestbookLive.presentation.slideshow',
  game: 'party.guestbookLive.presentation.game',
  guestbook: 'party.guestbookLive.presentation.guestbook',
  unavailable: 'party.guestbookLive.presentation.unavailable',
};

const UNAVAILABLE_LABEL: Record<PartyGuestbookTvUnavailableReason, MessageKey> = {
  game_active: 'party.guestbookLive.gameOnTv',
  guestbook_empty: 'party.guestbookLive.empty',
  guestbook_not_available: 'party.guestbookLive.notAvailable',
  party_not_live: 'party.guestbookLive.notLive',
};

const REFUSAL_LABEL: Record<string, MessageKey> = {
  version_conflict: 'party.guestbookLive.refused.stale',
  game_active: 'party.guestbookLive.gameOnTv',
  guestbook_empty: 'party.guestbookLive.empty',
  guestbook_not_available: 'party.guestbookLive.notAvailable',
  party_not_live: 'party.guestbookLive.notLive',
  forbidden: 'party.guestbookLive.refused.forbidden',
};

export function PartyGuestbookLivePanel({ albumId }: { albumId: string }) {
  const { t } = useI18n();
  const live = usePartyGuestbookLive(albumId);
  const { control } = live;

  // No live party behind the album, or a crew role with neither half of
  // these controls: the card is simply not part of this regia.
  if (live.connection === 'unavailable') return null;
  // A party that keeps no book has nothing to put anywhere.
  if (control && !control.guestbookEnabled && !control.viewingEnabled && !control.tvActive) return null;

  return (
    <Panel
      title={t('party.guestbookLive.title')}
      testId="party-guestbook-live"
      aside={control ? <TvBadge control={control} /> : undefined}
    >
      {live.connection === 'loading' && !control && (
        <div className="pw-skeleton pw-skeleton--panel" aria-hidden />
      )}
      {live.connection === 'error' && !control && (
        <Notice
          tone="warn"
          testId="party-guestbook-live-error"
          actions={<Button onClick={live.refresh}>{t('common.retry')}</Button>}
        >
          <p>{t('party.guestbookLive.offline')}</p>
        </Notice>
      )}
      {control && (
        <>
          <Viewing control={control} pending={live.pending !== null} onRun={live.run} />
          <Television control={control} pending={live.pending} onRun={live.run} />
          {live.refusal && (
            <Notice tone="warn" testId="party-guestbook-live-refusal">
              <p>{t(REFUSAL_LABEL[live.refusal] ?? 'party.guestbookLive.refused.generic')}</p>
            </Notice>
          )}
          {live.stale && (
            <p className="pw-small pw-muted" role="status">{t('party.guestbookLive.reconnecting')}</p>
          )}
        </>
      )}
    </Panel>
  );
}

function TvBadge({ control }: { control: PartyGuestbookLiveControl }) {
  const { t } = useI18n();
  if (control.tvPresentation !== 'guestbook') return null;
  return <Badge kind="live" testId="party-guestbook-live-on-tv">{t('party.guestbookLive.onTv')}</Badge>;
}

/** The room's half: read the whole book, or only what each guest wrote. */
function Viewing({
  control, pending, onRun,
}: {
  control: PartyGuestbookLiveControl;
  pending: boolean;
  onRun(command: 'enable_viewing' | 'disable_viewing'): void;
}) {
  const { t } = useI18n();
  const command = control.viewingEnabled ? 'disable_viewing' : 'enable_viewing';
  const offered = control.availableCommands.includes(command);
  return (
    <div className="pw-rows">
      <SwitchRow
        testId="party-guestbook-live-viewing"
        label={t('party.guestbookLive.viewing')}
        note={t('party.guestbookLive.viewingNote')}
        checked={control.viewingEnabled}
        disabled={pending || !offered}
        onChange={() => onRun(command)}
      />
      <p className="pw-small pw-muted" data-testid="party-guestbook-live-viewing-state">
        {t(control.viewingEnabled ? 'party.guestbookLive.viewingOn' : 'party.guestbookLive.viewingOff')}
      </p>
    </div>
  );
}

/** The television's half, as the server sees the screen. */
function Television({
  control, pending, onRun,
}: {
  control: PartyGuestbookLiveControl;
  pending: string | null;
  onRun(command: 'show_on_tv' | 'return_to_slideshow'): void;
}) {
  const { t } = useI18n();
  const canShow = control.availableCommands.includes('show_on_tv');
  const canReturn = control.availableCommands.includes('return_to_slideshow');
  const onTv = control.tvPresentation === 'guestbook';

  return (
    <div className="pw-guestbook-live-tv" data-testid="party-guestbook-live-tv"
      data-presentation={control.tvPresentation}>
      <p className="pw-row-label">
        {onTv
          ? <strong data-testid="party-guestbook-live-tv-state">{t('party.guestbookLive.onTv')}</strong>
          : <span data-testid="party-guestbook-live-tv-state">{t(PRESENTATION_LABEL[control.tvPresentation])}</span>}
      </p>
      <p className="pw-small pw-muted" data-testid="party-guestbook-live-counts">
        {t('party.guestbookLive.counts', { visible: control.visibleEntries, pending: control.pendingEntries })}
      </p>

      {/* Why the book cannot go up now, in words — the game holding the
          screen, or nothing yet to show. Never while it is already there. */}
      {!onTv && !canShow && control.tvUnavailableReason && (
        <Notice tone="info" testId={`party-guestbook-live-unavailable-${control.tvUnavailableReason}`}>
          <p>{t(UNAVAILABLE_LABEL[control.tvUnavailableReason])}</p>
        </Notice>
      )}

      {(canShow || canReturn) && (
        <div className="pw-panel-actions">
          {canReturn && (
            <Button
              tone="primary"
              size="lg"
              busy={pending === 'return_to_slideshow'}
              disabled={pending !== null}
              data-testid="party-guestbook-live-return"
              onClick={() => onRun('return_to_slideshow')}
            >
              {t('party.guestbookLive.returnToSlideshow')}
            </Button>
          )}
          {canShow && (
            <Button
              tone="primary"
              size="lg"
              busy={pending === 'show_on_tv'}
              disabled={pending !== null}
              data-testid="party-guestbook-live-show"
              onClick={() => onRun('show_on_tv')}
            >
              {t('party.guestbookLive.showOnTv')}
            </Button>
          )}
        </div>
      )}
    </div>
  );
}
