import type {
  AlbumPartyStatus,
  Party,
  PartyGuestContentKind,
  PartyGuestContentSlot,
} from '@nubarca/api-client';
import { useI18n, type MessageKey } from '../../i18n';
import { PartyContentCard } from '../PartyContentEditors';
import { PartyCoverCard } from '../PartyCoverCard';
import { mainMediaSource } from '../partyModel';
import { absoluteGuestUrl } from './PartyShareCard';
import { Badge, ButtonLink, EmptyState, Notice, Panel, SectionHead } from './ui';
import { slotsWithLostMedia } from './partyWorkspaceModel';

// "ESPERIENZA" — everything the guest will see, in the order they will see it.
//
// This is the party's content, and it is deliberately not a CMS: there is no
// block palette, no page tree and no slug. A party has a handful of things to
// say — who is invited, where it is, what to wear, what there is to eat,
// anything else, and thank you afterwards — and each of them is one card with
// the form that shape needs.
//
// The organising idea is TIME, not data type. A host does not think "I will
// edit the location entity"; they think "what do people see when the invitation
// arrives, what do they see at the party, and what do they see afterwards". So
// the cards are grouped by the moment they belong to, each group says what it is
// for, and the preview at the top opens the real guest page rather than a
// mock-up of it.

const INVITATION: readonly PartyGuestContentKind[] = ['invitation'];
const PRACTICAL: readonly PartyGuestContentKind[] = ['location', 'dress-code', 'menu', 'info'];
const AFTERWARDS: readonly PartyGuestContentKind[] = ['thank-you'];

export function PartyExperienceSection({
  party, albumParty, slots, onPartyUpdated, onSlotSaved,
}: {
  party: Party;
  albumParty: AlbumPartyStatus | null;
  slots: readonly PartyGuestContentSlot[];
  onPartyUpdated(next: Party): void;
  onSlotSaved(next: PartyGuestContentSlot): void;
}) {
  const { t, tn } = useI18n();
  const albumId = mainMediaSource(party)?.albumId ?? null;
  const guestUrl = albumParty?.partyMode && albumParty.partyUrl
    ? absoluteGuestUrl(albumParty.partyUrl)
    : null;
  const lost = slotsWithLostMedia(slots);

  return (
    <>
      <SectionHead
        title={t('party.section.experience')}
        lede={t('party.experience.lede')}
        actions={guestUrl
          ? (
            <ButtonLink href={guestUrl} tone="primary" data-testid="party-experience-preview">
              {t('party.experience.preview')}
            </ButtonLink>
          )
          : undefined}
      />

      {!guestUrl && (
        <Notice tone="info" testId="party-experience-no-preview">
          <p>{t('party.experience.previewLocked')}</p>
        </Notice>
      )}

      {lost.length > 0 && (
        <Notice tone="warn" title={t('party.attention.lost-media.title')} testId="party-experience-lost">
          <p>{tn(lost.length, 'party.attention.lost-media.body')}</p>
        </Notice>
      )}

      <Group
        title={t('party.experience.group.invitation')}
        lede={t('party.experience.group.invitationLede')}
        kinds={INVITATION}
        phases={['before', 'live']}
        slots={slots}
        partyId={party.id}
        albumId={albumId}
        onSlotSaved={onSlotSaved}
        before={(
          <PartyCoverCard
            party={party} which="invitation" albumId={albumId} onPartyUpdated={onPartyUpdated}
          />
        )}
      />

      <Group
        title={t('party.experience.group.practical')}
        lede={t('party.experience.group.practicalLede')}
        kinds={PRACTICAL}
        phases={['before', 'live', 'after']}
        slots={slots}
        partyId={party.id}
        albumId={albumId}
        onSlotSaved={onSlotSaved}
      />

      <Group
        title={t('party.experience.group.during')}
        lede={t('party.experience.group.duringLede')}
        kinds={[]}
        phases={[]}
        slots={slots}
        partyId={party.id}
        albumId={albumId}
        onSlotSaved={onSlotSaved}
        before={(
          <PartyCoverCard
            party={party} which="live" albumId={albumId} onPartyUpdated={onPartyUpdated}
          />
        )}
      />

      <Group
        title={t('party.experience.group.after')}
        lede={t('party.experience.group.afterLede')}
        kinds={AFTERWARDS}
        phases={['after']}
        slots={slots}
        partyId={party.id}
        albumId={albumId}
        onSlotSaved={onSlotSaved}
      />
    </>
  );
}

/**
 * One moment of the evening, and the cards that belong to it.
 *
 * The heading counts what the guests will actually see, so a host scanning the
 * page can tell an empty group from a full one without opening any of them.
 */
function Group({
  title, lede, kinds, phases, slots, partyId, albumId, onSlotSaved, before,
}: {
  title: string;
  lede: string;
  kinds: readonly PartyGuestContentKind[];
  phases: readonly ('before' | 'live' | 'after')[];
  slots: readonly PartyGuestContentSlot[];
  partyId: string;
  albumId: string | null;
  onSlotSaved(next: PartyGuestContentSlot): void;
  before?: React.ReactNode;
}) {
  const { t } = useI18n();
  const mine = slots.filter((slot) => kinds.includes(slot.kind));
  const on = mine.filter((slot) => slot.enabled).length;

  return (
    <div className="pw-group" data-testid={`party-experience-${kinds[0] ?? 'during'}`}>
      <div className="pw-group-head">
        <h3 className="pw-group-title">{title}</h3>
        {mine.length > 0 && (
          <Badge kind={on > 0 ? 'ok' : 'plain'}>
            {on > 0 ? t('party.experience.countOn', { on, all: mine.length }) : t('party.experience.countNone')}
          </Badge>
        )}
      </div>
      <p className="pw-group-lede">{lede}</p>
      {before}
      {mine.map((slot) => (
        <PartyContentCard
          key={slot.kind}
          slot={slot}
          partyId={partyId}
          albumId={albumId}
          phases={phases}
          onSaved={onSlotSaved}
        />
      ))}
    </div>
  );
}

/** Content that has not arrived yet — the section is a shape, not an error. */
export function PartyExperienceEmpty() {
  const { t } = useI18n();
  return (
    <Panel>
      <EmptyState
        title={t('party.experience.emptyTitle')}
        body={t('party.experience.emptyBody')}
        testId="party-experience-empty"
      />
    </Panel>
  );
}

/** The kinds' localized names, shared with anything that lists them. */
export function contentKindLabelKey(kind: PartyGuestContentKind): MessageKey {
  return `partyContent.kind.${kind}` as MessageKey;
}
