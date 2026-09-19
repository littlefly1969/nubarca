import { useState } from 'react';
import { Link } from 'react-router';
import {
  ApiError,
  contributionSettingsFromStatus,
  type AlbumPartyStatus,
  type Party,
  type PartyContributionsPatch,
} from '@nubarca/api-client';
import { useAuth } from '../../auth/useAuth';
import { usePermissions } from '../../auth/usePermissions';
import { PERMISSIONS } from '../../auth/permissions';
import { useI18n } from '../../i18n';
import { PartyAlbumSection } from '../PartyAlbumSection';
import { PartySlideshowSettings } from '../PartyAdvancedSettings';
import { mainMediaSource } from '../partyModel';
import { CREW_CAPABILITIES } from '../crew/crewModel';
import { partyDeepLink, usePartyApi } from './partyApi';
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
  const api = usePartyApi();
  const perms = usePermissions();
  // TWO GATES, and both must hold. The host's own permission decides whether
  // this installation's party may take contributions at all; the crew
  // capability decides whether THIS person configures them. A Regista moderates
  // what guests left and does not choose whether they may leave it — so the
  // switch is absent for them rather than present and refused.
  const canContributions = perms.hasAll([PERMISSIONS.partyAccess, PERMISSIONS.partyContributions])
    && api.can(CREW_CAPABILITIES.contributionsConfigure);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);

  const album = mainMediaSource(party);
  const albumId = album?.albumId ?? null;
  const open = albumParty?.partyMode === true;

  const contributions = albumParty ? contributionSettingsFromStatus(albumParty) : null;

  /**
   * ONE switch, saved on its own.
   *
   * The three contributions are independent product decisions, so only the one
   * that moved travels: sending the whole current state back would work until
   * two people configured one party at once, at which point the second save
   * would quietly restore what the first had just changed.
   */
  async function toggle(changes: PartyContributionsPatch) {
    if (!albumId) return;
    setBusy(true); setFailed(false);
    try {
      onAlbumPartyUpdated(await api.setPartyContributionSettings(albumId, changes));
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
          {/* THE THREE CONTRIBUTIONS, on one card, in the order a host thinks
              about them: what people bring, what goes on the screen, what stays
              in the book.

              They are three switches and not one, because they are three
              decisions. A shared album with nothing written on the television
              is a real party; so is a guest book with no photographs. And the
              two written ones are deliberately named for what they DO — a
              message is read out during the evening, a dedication is kept —
              because "messages" said twice would be the one mistake this card
              exists to avoid. */}
          <Panel
            title={t('party.photos.contributions')}
            note={t('party.photos.contributionsNote')}
            testId="party-contributions"
          >
            {!open || !contributions ? (
              <Notice tone="info" testId="party-photos-needs-access">
                <p>{t('party.photos.needsAccess')}</p>
              </Notice>
            ) : canContributions ? (
              <div className="pw-rows">
                <SwitchRow
                  testId="party-photos-uploads"
                  label={t('party.photos.uploadsLabel')}
                  note={t('party.photos.uploadsNote')}
                  checked={contributions.uploadEnabled}
                  disabled={busy}
                  onChange={(next) => void toggle({ uploadEnabled: next })}
                />
                <SwitchRow
                  testId="party-contributions-messages"
                  label={t('party.contributions.messagesLabel')}
                  note={t('party.contributions.messagesNote')}
                  checked={contributions.slideshowMessagesEnabled}
                  disabled={busy}
                  onChange={(next) => void toggle({ slideshowMessagesEnabled: next })}
                />
                <SwitchRow
                  testId="party-contributions-guestbook"
                  label={t('party.contributions.guestbookLabel')}
                  note={t('party.contributions.guestbookNote')}
                  checked={contributions.guestbookEnabled}
                  disabled={busy}
                  onChange={(next) => void toggle({ guestbookEnabled: next })}
                />
              </div>
            ) : (
              /* A REGISTA moderates what guests left and does not decide
                 whether they may leave it. They are told what is true rather
                 than shown a switch the server would refuse. */
              <ul className="pw-facts" data-testid="party-contributions-readonly">
                <li>
                  <span className="pw-fact-label">{t('party.photos.uploadsLabel')}</span>
                  <span className="pw-fact-value">
                    {contributions.uploadEnabled
                      ? t('party.contributions.on')
                      : t('party.contributions.off')}
                  </span>
                </li>
                <li>
                  <span className="pw-fact-label">{t('party.contributions.messagesLabel')}</span>
                  <span className="pw-fact-value">
                    {contributions.slideshowMessagesEnabled
                      ? t('party.contributions.on')
                      : t('party.contributions.off')}
                  </span>
                </li>
                <li>
                  <span className="pw-fact-label">{t('party.contributions.guestbookLabel')}</span>
                  <span className="pw-fact-value">
                    {contributions.guestbookEnabled
                      ? t('party.contributions.on')
                      : t('party.contributions.off')}
                  </span>
                </li>
              </ul>
            )}
            {failed && (
              <Notice tone="error"><p>{t('party.overview.saveFailed')}</p></Notice>
            )}
          </Panel>

          {/* The book's own queue, beside the greetings' — reachable on
              `party.access` alone, because closing a channel must never lock
              anybody out of the queue it filled. It is NOT inside the card
              above: configuring a contribution and reading what it collected
              are two different jobs, and the product already separates them. */}
          {contributions?.guestbookEnabled && (
            <Panel
              title={t('party.guestbook.queueHeading')}
              note={t('party.guestbook.queueNote')}
              testId="party-guestbook-queue-panel"
            >
              <LinkRow
                to={partyDeepLink(api, 'guestbook', party.id, albumId)}
                testId="party-guestbook-queue"
                title={t('party.guestbook.openQueue')}
                note={contributions.requireGuestbookApproval
                  ? t('party.guestbook.approvalOn')
                  : t('party.guestbook.approvalOff')}
              />
              {canContributions && (
                <div className="pw-rows">
                  <SwitchRow
                    testId="party-guestbook-approval"
                    label={t('party.guestbook.approvalLabel')}
                    note={t('party.guestbook.approvalNote')}
                    checked={contributions.requireGuestbookApproval}
                    disabled={busy}
                    onChange={(next) => void toggle({ requireGuestbookApproval: next })}
                  />
                </div>
              )}
            </Panel>
          )}

          <Panel
            title={t('party.photos.queueHeading')}
            note={t('party.photos.queueNote')}
            testId="party-moderation"
          >
            {/* Reachable on `party.access` alone: closing the channel must never
                lock the host out of the queue it filled. */}
            <LinkRow
              to={partyDeepLink(api, 'photos', party.id, albumId)}
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

      {/* The album itself is the host's library, not the party. A collaborator
          moderated the evening's photographs; they do not get the shelf they
          were filed on afterwards. */}
      {party.status === 'ended' && albumId && api.isOwner && (
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
