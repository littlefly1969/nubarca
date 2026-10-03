import { useState } from 'react';
import {
  ApiError,
  type AlbumPartyStatus,
  type Party,
} from '@nubarca/api-client';
import { useAuth } from '../../auth/useAuth';
import { usePermissions } from '../../auth/usePermissions';
import { PERMISSIONS } from '../../auth/permissions';
import { useI18n } from '../../i18n';
import { PartyPrintSettings } from '../../albums/PartyPrintSettings';
import { mainMediaSource } from '../partyModel';
import { absoluteGuestUrl } from './PartyShareCard';
import { usePartyApi } from './partyApi';
import { Button, EmptyState, LinkRow, Notice, Panel, SectionHead, SwitchRow } from './ui';
import { PartyTvTargets } from './PartyTvTargets';
import { PartyQrCardPrintDialog } from './PartyQrCardPrintDialog';

// "SCHERMI E STAMPA" — the party as it appears on something other than a phone.
//
// Grouped by the OBJECT in the room rather than by the subsystem behind it: the
// television in the corner, the screen somebody paired, the printer by the
// door. A host setting up a venue walks around it and asks "what goes on that?"
// — not "where are the slideshow settings".
//
// Every one of these is optional and each empty state says so. A party with no
// screen and no printer is a complete party.

export function PartyScreensSection({
  party, albumParty, albumPartyFailed, onAlbumPartyUpdated, onRetry,
}: {
  party: Party;
  albumParty: AlbumPartyStatus | null;
  albumPartyFailed: boolean;
  onAlbumPartyUpdated(next: AlbumPartyStatus): void;
  onRetry(): void;
}) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const api = usePartyApi();
  const perms = usePermissions();
  const canPrint = perms.hasAll([PERMISSIONS.partyAccess, PERMISSIONS.partyPrint]);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);
  const [printingQr, setPrintingQr] = useState(false);

  const albumId = mainMediaSource(party)?.albumId ?? null;
  const partyUrl = albumParty?.partyMode ? albumParty.partyUrl : null;
  const tvUrl = partyUrl ? absoluteGuestUrl(`${partyUrl}/tv`) : null;

  async function toggleTv(next: boolean) {
    if (!albumId || !albumParty) return;
    setBusy(true); setFailed(false);
    try {
      await api.setAlbumTvVisibility(albumId, next);
      // The TV flag belongs to the ALBUM and the party's own settings only
      // report it, so the page adopts the value it just set rather than asking
      // a second endpoint for an answer it already knows.
      onAlbumPartyUpdated({ ...albumParty, showOnTv: next });
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setFailed(true);
    } finally { setBusy(false); }
  }

  if (albumId === null) {
    return (
      <>
        <SectionHead title={t('party.section.screens')} lede={t('party.screens.lede')} />
        <EmptyState
          testId="party-screens-needs-album"
          title={t('party.screens.needsAlbumTitle')}
          body={t('party.screens.needsAlbumBody')}
        />
      </>
    );
  }

  if (albumPartyFailed) {
    return (
      <>
        <SectionHead title={t('party.section.screens')} lede={t('party.screens.lede')} />
        <Notice
          tone="error"
          testId="party-screens-settings-error"
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
      <SectionHead title={t('party.section.screens')} lede={t('party.screens.lede')} />

      <Panel
        title={t('party.screens.tv')}
        note={t('party.screens.tvNote')}
        testId="party-screens-tv"
      >
        <div className="pw-rows">
          <SwitchRow
            testId="party-screens-show-on-tv"
            label={t('albumDetail.showOnTv')}
            note={t('party.screens.tvSwitchNote')}
            checked={albumParty?.showOnTv ?? false}
            disabled={busy || albumParty === null}
            onChange={(next) => void toggleTv(next)}
          />
        </div>
        {failed && <Notice tone="error"><p>{t('party.overview.saveFailed')}</p></Notice>}
        {tvUrl ? (
          <LinkRow
            href={tvUrl}
            testId="party-screens-tv-link"
            title={t('party.screens.openStage')}
            note={t('party.screens.openStageNote')}
          />
        ) : (
          <p className="pw-small pw-muted" data-testid="party-screens-tv-locked">
            {t('party.screens.stageLocked')}
          </p>
        )}
      </Panel>

      {/* Paired televisions are INSTALLATION hardware — they outlive this party
          and belong to whoever runs the server. A collaborator points a screen
          at this evening; they do not pair or unpair devices, so only an owner
          sees this at all. */}
      {api.isOwner && <PartyTvTargets albumId={albumId} />}

      {canPrint ? (
        <Panel
          title={t('party.screens.print')}
          note={t('party.screens.printNote')}
          testId="party-print"
        >
          <PartyPrintSettings albumId={albumId} />
          {/* The host's own sheet for the tables, on the printer chosen
              above. An owner's: a collaborator runs the evening's printing,
              not the host's photographs on paper. */}
          {api.isOwner && (
            <div className="pw-rows" data-testid="party-qr-card-row">
              <h4>{t('party.qrCard.heading')}</h4>
              <p className="pw-small pw-muted">{t('party.qrCard.note')}</p>
              {partyUrl ? (
                <div className="pw-panel-actions">
                  <Button data-testid="party-qr-card-open" onClick={() => setPrintingQr(true)}>
                    {t('party.qrCard.open')}
                  </Button>
                </div>
              ) : (
                <p className="pw-small pw-muted" data-testid="party-qr-card-closed">{t('party.qrCard.closed')}</p>
              )}
            </div>
          )}
          {printingQr && partyUrl && (
            <PartyQrCardPrintDialog
              albumId={albumId}
              partyName={mainMediaSource(party)?.albumName ?? party.title}
              partyUrl={partyUrl}
              coverFileItemId={party.liveCoverFileItemId ?? party.invitationCoverFileItemId ?? null}
              onClose={() => setPrintingQr(false)}
            />
          )}
        </Panel>
      ) : (
        <Panel title={t('party.screens.print')} testId="party-print-locked">
          <p className="pw-small pw-muted">{t('party.screens.printNoPermission')}</p>
        </Panel>
      )}
    </>
  );
}
