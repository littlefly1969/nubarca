import { useState } from 'react';
import { Link } from 'react-router';
import { ApiError, setAlbumPartyMode, type AlbumPartyStatus } from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { usePermissions } from '../auth/usePermissions';
import { PERMISSIONS } from '../auth/permissions';
import { useI18n } from '../i18n';
import { PartyPrintSettings } from '../albums/PartyPrintSettings';
import { PartyGameSettings, PartySlideshowSettings } from './PartyAdvancedSettings';

// The evening's controls, ORCHESTRATED rather than rebuilt.
//
// Every one of these already worked: guest contributions, the moderation
// queues, slideshow timing, the game and its deck, the control room, printing.
// What changes is where they are mounted — the party rather than the album's
// settings modal — and what decides whether each appears.
//
// PERMISSION VISIBILITY, and the one rule that is easy to get wrong:
// `party.contributions` governs OPENING the channel, not tidying up what came
// through it. A host whose role loses that key must still be able to moderate
// the photographs and greetings their guests already sent, which is exactly
// what the backend policy allows — so the queues below sit under
// `party.access`, and only the switch sits under `party.contributions`.

export function PartyLiveTab({
  albumId, albumParty, onAlbumPartyUpdated,
}: {
  albumId: string | null;
  albumParty: AlbumPartyStatus | null;
  onAlbumPartyUpdated(next: AlbumPartyStatus): void;
}) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const perms = usePermissions();
  const canContributions = perms.hasAll([PERMISSIONS.partyAccess, PERMISSIONS.partyContributions]);
  const canGames = perms.hasAll([PERMISSIONS.partyAccess, PERMISSIONS.partyGames]);
  const canPrint = perms.hasAll([PERMISSIONS.partyAccess, PERMISSIONS.partyPrint]);
  const [busy, setBusy] = useState(false);

  // No album means nothing album-scoped can be asked for yet — so nothing
  // album-scoped is rendered, and no endpoint is called with an id that does
  // not exist. It is an unfinished configuration, said plainly.
  if (albumId === null) {
    return (
      <section className="party-card party-card--empty" data-testid="party-live-needs-album">
        <p className="muted">{t('party.live.needsAlbum')}</p>
      </section>
    );
  }

  if (!albumParty?.partyMode) {
    return (
      <section className="party-card party-card--empty" data-testid="party-live-needs-guests">
        <p className="muted">{t('party.live.needsGuests')}</p>
      </section>
    );
  }

  async function toggleUpload(next: boolean) {
    setBusy(true);
    try {
      onAlbumPartyUpdated(await setAlbumPartyMode(albumId!, true, next));
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) invalidateAuth();
    } finally { setBusy(false); }
  }

  return (
    <div className="party-live">
      {canContributions && (
        <section className="party-card" data-testid="party-contributions">
          <h3>{t('party.live.contributions')}</h3>
          <label className="party-toggle">
            <input
              type="checkbox" checked={albumParty.uploadEnabled} disabled={busy}
              aria-label={t('albumDetail.allowGuestUploads')}
              onChange={(e) => void toggleUpload(e.target.checked)}
            />
            <span>{t('albumDetail.allowGuestUploads')}</span>
          </label>
          <p className="muted">{t('albumDetail.guestUploadsHelp')}</p>
        </section>
      )}

      {/* Reachable on `party.access` alone: closing the channel must never lock
          the host out of the queue it filled. */}
      <section className="party-card" data-testid="party-moderation">
        <h3>{t('party.live.moderation')}</h3>
        <p className="party-card-actions">
          <Link to={`/albums/${albumId}/party-uploads`}>
            {t('albumDetail.manageGuestUploads')}
            {albumParty.requireUploadApproval ? ` (${t('albumDetail.approvalRequired')})` : ''}
          </Link>
        </p>
        <p className="party-card-actions">
          <Link to={`/albums/${albumId}/party-messages`}>
            {t('partyMessages.title')}
            {albumParty.requireMessageApproval ? ` (${t('albumDetail.approvalRequired')})` : ''}
          </Link>
        </p>
      </section>

      {canGames && (
        <section className="party-card" data-testid="party-games">
          <PartyGameSettings
            albumId={albumId} party={albumParty} onUpdated={onAlbumPartyUpdated}
          />
          {albumParty.gameEnabled && (
            <p className="party-card-actions">
              <Link to={`/albums/${albumId}/party-game`}>{t('partyGame.controlRoom')}</Link>
            </p>
          )}
        </section>
      )}

      {canPrint && (
        <section className="party-card" data-testid="party-print">
          <PartyPrintSettings albumId={albumId} />
        </section>
      )}

      {/* The numbers last, and folded away: a host configuring an evening should
          meet its decisions before its dials. */}
      <details className="party-card party-advanced">
        <summary>{t('party.live.advanced')}</summary>
        <PartySlideshowSettings
          albumId={albumId} party={albumParty} onUpdated={onAlbumPartyUpdated}
        />
      </details>
    </div>
  );
}
