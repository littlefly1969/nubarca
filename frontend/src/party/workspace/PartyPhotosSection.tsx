import { useState } from 'react';
import { Link } from 'react-router';
import {
  ApiError,
  setAlbumPartyMode,
  type AlbumPartyStatus,
  type Party,
} from '@nubarca/api-client';
import { useAuth } from '../../auth/useAuth';
import { usePermissions } from '../../auth/usePermissions';
import { PERMISSIONS } from '../../auth/permissions';
import { useI18n } from '../../i18n';
import { PartyAlbumSection } from '../PartyAlbumSection';
import { PartySlideshowSettings } from '../PartyAdvancedSettings';
import { mainMediaSource } from '../partyModel';
import { Button, Disclosure, LinkRow, Notice, Panel, SectionHead, SwitchRow } from './ui';
import { QueueBadge } from './PartyLiveSection';
import type { Loaded, WorkspaceSection } from './partyWorkspaceModel';

// "FOTO" — the photographs, and who may see or add them.
//
// A host asks four questions about a party's pictures, and this section answers
// them in that order:
//
//   Where do they live?          → the album, named and one tap away.
//   May the guests add theirs?   → one switch, and what it costs them.
//   What have they added?        → the queue, with what is waiting in it.
//   What is public?              → said in words beside each decision.
//
// Nothing here is a second photo browser. The album page and the media library
// are where photographs are looked at, and this points at them.

export function PartyPhotosSection({
  party, albumParty, albumPartyFailed, moderation,
  onPartyUpdated, onAlbumPartyUpdated, onNavigate, onRetry,
}: {
  party: Party;
  albumParty: AlbumPartyStatus | null;
  /** The settings were asked for and did not come — not the same as absent. */
  albumPartyFailed: boolean;
  moderation: { uploads: Loaded<number>; messages: Loaded<number> };
  onPartyUpdated(next: Party): void;
  onAlbumPartyUpdated(next: AlbumPartyStatus): void;
  onNavigate(section: WorkspaceSection): void;
  onRetry(): void;
}) {
  const { t, formatDate } = useI18n();
  const { invalidateAuth } = useAuth();
  const perms = usePermissions();
  const canContributions = perms.hasAll([PERMISSIONS.partyAccess, PERMISSIONS.partyContributions]);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);

  const album = mainMediaSource(party);
  const albumId = album?.albumId ?? null;
  const open = albumParty?.partyMode === true;

  async function toggleUpload(next: boolean) {
    if (!albumId) return;
    setBusy(true); setFailed(false);
    try {
      onAlbumPartyUpdated(await setAlbumPartyMode(albumId, true, next));
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setFailed(true);
    } finally { setBusy(false); }
  }

  return (
    <>
      <SectionHead title={t('party.section.photos')} lede={t('party.photos.lede')} />

      <PartyAlbumSection party={party} onPartyUpdated={onPartyUpdated} />

      {albumId !== null && albumPartyFailed && (
        <Notice
          tone="error"
          testId="party-photos-settings-error"
          title={t('party.settingsUnreadable')}
          actions={<Button onClick={onRetry}>{t('common.retry')}</Button>}
        >
          <p>{t('party.settingsUnreadableBody')}</p>
        </Notice>
      )}

      {albumId === null || albumPartyFailed ? null : (
        <>
          <Panel
            title={t('party.photos.contributions')}
            note={t('party.photos.contributionsNote')}
            testId="party-contributions"
          >
            {!open ? (
              <Notice tone="info" testId="party-photos-needs-access">
                <p>{t('party.photos.needsAccess')}</p>
              </Notice>
            ) : canContributions ? (
              <div className="pw-rows">
                <SwitchRow
                  testId="party-photos-uploads"
                  label={t('party.photos.uploadsLabel')}
                  note={t('party.photos.uploadsNote')}
                  checked={albumParty?.uploadEnabled ?? false}
                  disabled={busy}
                  onChange={(next) => void toggleUpload(next)}
                />
              </div>
            ) : (
              <p className="pw-small pw-muted">
                {albumParty?.uploadEnabled
                  ? t('party.photos.uploadsOnReadOnly')
                  : t('party.photos.uploadsOffReadOnly')}
              </p>
            )}
            {failed && (
              <Notice tone="error"><p>{t('party.overview.saveFailed')}</p></Notice>
            )}
          </Panel>

          <Panel
            title={t('party.photos.queueHeading')}
            note={t('party.photos.queueNote')}
            testId="party-moderation"
          >
            {/* Reachable on `party.access` alone: closing the channel must never
                lock the host out of the queue it filled. */}
            <LinkRow
              to={`/albums/${albumId}/party-uploads?party=${party.id}`}
              testId="party-photos-queue"
              title={t('partyUploads.title')}
              note={albumParty?.requireUploadApproval
                ? t('party.photos.approvalOn')
                : t('party.photos.approvalOff')}
              after={<QueueBadge queue={moderation.uploads} />}
            />
          </Panel>

          <Panel
            title={t('party.photos.guestsSee')}
            note={t('party.photos.guestsSeeNote')}
            testId="party-photos-visibility"
          >
            <ul className="pw-facts">
              <li>
                <span className="pw-fact-label">{t('party.photos.factBefore')}</span>
                <span className="pw-fact-value">{t('party.photos.factBeforeValue')}</span>
              </li>
              <li>
                <span className="pw-fact-label">{t('party.photos.factDuring')}</span>
                <span className="pw-fact-value">
                  {albumParty?.uploadEnabled
                    ? t('party.photos.factDuringOpen')
                    : t('party.photos.factDuringClosed')}
                </span>
              </li>
              <li>
                <span className="pw-fact-label">{t('party.photos.factAfter')}</span>
                <span className="pw-fact-value">
                  {party.libraryAccessExpiresAt
                    ? t('party.photos.factAfterUntil', { date: formatDate(party.libraryAccessExpiresAt) })
                    : t('party.photos.factAfterFollowsGuest')}
                </span>
              </li>
            </ul>
            <div className="pw-panel-actions">
              <Button tone="quiet" onClick={() => onNavigate('settings')} data-testid="party-photos-windows">
                {t('party.photos.changeWindows')}
              </Button>
            </div>
          </Panel>

          {albumParty && canContributions && (
            <Disclosure
              summary={t('party.photos.limits')}
              note={t('party.photos.limitsNote')}
              testId="party-photos-limits"
            >
              <PartySlideshowSettings
                albumId={albumId} party={albumParty} onUpdated={onAlbumPartyUpdated}
              />
            </Disclosure>
          )}
        </>
      )}

      {party.status === 'ended' && albumId && (
        <Panel title={t('party.photos.memories')} note={t('party.photos.memoriesNote')}>
          <div className="pw-panel-actions">
            <Link to={`/albums/${albumId}`} className="pw-btn pw-btn--primary">
              {t('party.photos.openMemories')}
            </Link>
          </div>
        </Panel>
      )}
    </>
  );
}
