import type { AlbumPartyStatus, Party } from '@nubarca/api-client';
import { usePermissions } from '../../auth/usePermissions';
import { PERMISSIONS } from '../../auth/permissions';
import { useI18n } from '../../i18n';
import { PartyGameSettings } from '../PartyAdvancedSettings';
import { mainMediaSource } from '../partyModel';
import { CREW_CAPABILITIES } from '../crew/crewModel';
import { partyDeepLink, usePartyApi } from './partyApi';
import { Badge, Button, EmptyState, LinkRow, Notice, Panel, SectionHead } from './ui';


// "ATTIVITÀ" — what there is to DO at the party, as opposed to what there is
// to look at.
//
// Two things live here, and they are presented as two things a guest does
// rather than as two subsystems: the game the room plays together, and the
// greetings people leave. Neither is required — an evening with no game and no
// messages is a complete party — so both empty states say so instead of
// reading as unfinished configuration.
//
// Preparing and conducting stay separate: the deck is written here, and the
// evening is run from the control room, which is a different job done standing
// up in front of people.

export function PartyActivitiesSection({
  party, albumParty, albumPartyFailed, onAlbumPartyUpdated, onRetry,
}: {
  party: Party;
  albumParty: AlbumPartyStatus | null;
  albumPartyFailed: boolean;
  onAlbumPartyUpdated(next: AlbumPartyStatus): void;
  onRetry(): void;
}) {
  const { t } = useI18n();
  const api = usePartyApi();
  const perms = usePermissions();
  const canGames = perms.hasAll([PERMISSIONS.partyAccess, PERMISSIONS.partyGames]);
  const albumId = mainMediaSource(party)?.albumId ?? null;
  const open = albumParty?.partyMode === true;

  if (albumId === null) {
    return (
      <>
        <SectionHead title={t('party.section.activities')} lede={t('party.activities.lede')} />
        <EmptyState
          testId="party-activities-needs-album"
          title={t('party.activities.needsAlbumTitle')}
          body={t('party.activities.needsAlbumBody')}
        />
      </>
    );
  }

  if (albumPartyFailed) {
    return (
      <>
        <SectionHead title={t('party.section.activities')} lede={t('party.activities.lede')} />
        <Notice
          tone="error"
          testId="party-activities-settings-error"
          title={t('party.settingsUnreadable')}
          actions={<Button onClick={onRetry}>{t('common.retry')}</Button>}
        >
          <p>{t('party.settingsUnreadableBody')}</p>
        </Notice>
      </>
    );
  }

  return (
    <>
      <SectionHead title={t('party.section.activities')} lede={t('party.activities.lede')} />

      {!open && (
        <Notice tone="info" testId="party-activities-needs-access">
          <p>{t('party.activities.needsAccess')}</p>
        </Notice>
      )}

      {canGames ? (
        <Panel
          title={t('party.activities.game')}
          note={t('party.activities.gameNote')}
          testId="party-activities-game"
          aside={albumParty?.gameEnabled
            ? <Badge kind="ok">{t('party.activities.gameOn')}</Badge>
            : <Badge kind="plain">{t('party.activities.gameOff')}</Badge>}
        >
          {albumParty ? (
            <>
              {/* The RULES are `activities.manage`; running the game is
                  `activities.control`. A role with only the latter presses
                  start and next all evening and never rewrites the game. */}
              {api.can(CREW_CAPABILITIES.activitiesManage) && (
                <PartyGameSettings
                  albumId={albumId} party={albumParty} onUpdated={onAlbumPartyUpdated}
                />
              )}
              {albumParty.gameEnabled && (
                <LinkRow
                  to={partyDeepLink(api, 'game', party.id, albumId)}
                  testId="party-activities-control-room"
                  title={t('partyGame.controlRoom')}
                  note={t('party.activities.controlRoomNote')}
                />
              )}
            </>
          ) : (
            <p className="pw-small pw-muted">{t('common.loading')}</p>
          )}
        </Panel>
      ) : (
        <Panel title={t('party.activities.game')} testId="party-activities-game-locked">
          <p className="pw-small pw-muted">{t('party.activities.gameNoPermission')}</p>
        </Panel>
      )}
    </>
  );
}
