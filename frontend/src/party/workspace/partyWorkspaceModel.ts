import type {
  AlbumPartyStatus,
  GuestDirectorySummary,
  Party,
  PartyGuestContentKind,
  PartyGuestContentSlot,
  PartyStatus,
} from '@nubarca/api-client';
import type { MessageKey, PluralKey } from '../../i18n';
import { mainMediaSource } from '../partyModel';

// The Party workspace's information architecture, as data.
//
// Which sections exist, which one a host lands on, what the party is waiting
// for and what is wrong with it are all decided HERE — pure functions over the
// party, its album settings, its guest content and its guest counts — so the
// answers can be tested without rendering a page, and so two surfaces can never
// disagree about whether the invitation has been written.
//
// The MODEL IS STABLE ACROSS THE LIFECYCLE. A host learns one set of sections
// and keeps it from the first draft to the last photograph; what changes is
// which section they land on, what each one leads with, and which steps are
// still open. Live adds one section and takes none away.

/** The four states an evening passes through, in order. */
export const PARTY_STATUSES: readonly PartyStatus[] = ['draft', 'published', 'live', 'ended'];

/* ── Sections ─────────────────────────────────────────────────────────────── */

export const WORKSPACE_SECTIONS = [
  'summary', 'live', 'experience', 'guests', 'photos', 'activities', 'screens', 'settings',
] as const;

export type WorkspaceSection = (typeof WORKSPACE_SECTIONS)[number];

export function isWorkspaceSection(value: unknown): value is WorkspaceSection {
  return typeof value === 'string' && (WORKSPACE_SECTIONS as readonly string[]).includes(value);
}

/**
 * The sections a party OFFERS, in the order the navigation shows them.
 *
 * `live` is the only one that comes and goes, and only because a console for an
 * evening that is not happening would be a room with the lights off. Everything
 * else is present in every phase: a host who learned where the guest list is
 * during the draft finds it in the same place at midnight.
 */
export function workspaceSections(status: PartyStatus): WorkspaceSection[] {
  return WORKSPACE_SECTIONS.filter((section) => section !== 'live' || status === 'live');
}

/** Where a host lands when they open the party without asking for a section. */
export function defaultWorkspaceSection(status: PartyStatus): WorkspaceSection {
  // While it is happening, the door is the page. Every other phase opens on the
  // summary, which is the page that says what to do next.
  return status === 'live' ? 'live' : 'summary';
}

export function sectionLabelKey(section: WorkspaceSection): MessageKey {
  return `party.section.${section}` as MessageKey;
}

/* ── What the party knows about itself ────────────────────────────────────── */

/** Everything the summary reasons over, gathered once. */
export interface WorkspaceFacts {
  party: Party;
  albumParty: AlbumPartyStatus | null;
  slots: readonly PartyGuestContentSlot[];
  /** Null while the guest counts have not arrived — never zero standing in for unknown. */
  guests: GuestDirectorySummary | null;
  /** Contributions waiting for the host, or null when they were not asked for. */
  moderation: { uploads: number; messages: number } | null;
}

/** A slot SAYS something: it is on, and it has words or a photograph in it. */
export function slotHasContent(slot: PartyGuestContentSlot): boolean {
  if (!slot.enabled) return false;
  if (slot.mediaFileItemId) return true;
  return Object.values(slot.content ?? {}).some(
    (value) => typeof value === 'string' && value.trim() !== '',
  );
}

export function findSlot(
  slots: readonly PartyGuestContentSlot[],
  kind: PartyGuestContentKind,
): PartyGuestContentSlot | undefined {
  return slots.find((slot) => slot.kind === kind);
}

/** A slot whose photograph was deleted: the reference survived, the picture did not. */
export function slotsWithLostMedia(
  slots: readonly PartyGuestContentSlot[],
): PartyGuestContentSlot[] {
  return slots.filter((slot) => slot.mediaFileItemId !== null && slot.mediaUrl === null);
}

export function guestAccessExpired(party: Party, now: Date = new Date()): boolean {
  return party.guestAccessExpiresAt !== null && new Date(party.guestAccessExpiresAt) <= now;
}

/** True once the host has built a guest list. An empty one is a valid party. */
export function hasGuestList(guests: GuestDirectorySummary | null): boolean {
  return (guests?.groups ?? 0) > 0;
}

/* ── The one thing to do now ──────────────────────────────────────────────── */

export type PrimaryIntent =
  /** Nothing is stopping publication: open the party to its guests. */
  | { kind: 'open-to-guests' }
  /** It is published and the evening has not started. */
  | { kind: 'start-live' }
  /** It is happening: go and run it. */
  | { kind: 'go-live-console' }
  /** It is happening: stop it. */
  | { kind: 'end-live' }
  /** It is over: the photographs are the product now. */
  | { kind: 'open-photos' }
  /** Something must be configured before any of the above means anything. */
  | { kind: 'link-album' }
  /**
   * The album's settings have not arrived, so whether the guests can already
   * reach this party is genuinely not known yet. Saying "publish it" here would
   * be a guess, and a host who pressed it would be acting on one.
   */
  | { kind: 'unknown' };

/**
 * THE action of the moment — one, contextual, and never a lie.
 *
 * It is deliberately not the same thing as the lifecycle transition: a draft
 * with no album is not one button away from a party, so the summary offers the
 * step that actually unblocks it instead of a "publish" that would fail.
 */
export function primaryIntent(facts: WorkspaceFacts): PrimaryIntent {
  const { party, albumParty } = facts;
  const album = mainMediaSource(party);
  switch (party.status) {
    case 'draft':
      if (!album) return { kind: 'link-album' };
      if (albumParty === null) return { kind: 'unknown' };
      return { kind: 'open-to-guests' };
    case 'published':
      if (!album) return { kind: 'link-album' };
      if (albumParty === null) return { kind: 'unknown' };
      if (!albumParty.partyMode) return { kind: 'open-to-guests' };
      return { kind: 'start-live' };
    case 'live':
      return { kind: 'go-live-console' };
    case 'ended':
      return { kind: 'open-photos' };
  }
}

/* ── Next steps ───────────────────────────────────────────────────────────── */

export type StepId =
  | 'date'
  | 'album'
  | 'invitation'
  | 'details'
  | 'guest-access'
  | 'guest-list'
  | 'invitations-sent'
  | 'contributions'
  | 'screens'
  | 'start'
  | 'thank-you'
  | 'memories';

export interface WorkspaceStep {
  id: StepId;
  done: boolean;
  /** Where pressing the step's action takes the host. */
  section: WorkspaceSection;
  /** Genuinely optional: a party is complete without it, and the copy says so. */
  optional: boolean;
}

/**
 * What is left to do, in the order it is worth doing — and what is already
 * done, because a checklist that hides its completed items cannot be used to
 * confirm that the evening is ready.
 *
 * The list changes with the phase, but only by what a host is actually working
 * on: the same steps, re-prioritised, never a different product.
 */
export function workspaceSteps(facts: WorkspaceFacts): WorkspaceStep[] {
  const { party, albumParty, slots, guests } = facts;
  const album = mainMediaSource(party) !== null;
  const invitation = findSlot(slots, 'invitation');
  const thankYou = findSlot(slots, 'thank-you');
  const accessOpen = albumParty?.partyMode === true;
  const listed = hasGuestList(guests);

  const step = (
    id: StepId, done: boolean, section: WorkspaceSection, optional = false,
  ): WorkspaceStep => ({ id, done, section, optional });

  switch (party.status) {
    case 'draft':
      return [
        step('date', party.eventStartsAt !== null, 'settings'),
        step('album', album, 'photos'),
        step('invitation', invitation ? slotHasContent(invitation) : false, 'experience'),
        step('details', slots.some((s) => s.kind !== 'invitation' && s.kind !== 'thank-you' && slotHasContent(s)), 'experience', true),
        step('guest-list', listed, 'guests', true),
        step('guest-access', accessOpen, 'summary'),
      ];
    case 'published':
      return [
        step('guest-access', accessOpen, 'summary'),
        step('invitations-sent', listed && (guests?.rsvp.invited ?? 0) > 0, 'guests', !listed),
        step('invitation', invitation ? slotHasContent(invitation) : false, 'experience'),
        step('contributions', albumParty?.uploadEnabled === true, 'photos', true),
        step('screens', albumParty?.showOnTv === true, 'screens', true),
        step('start', false, 'summary'),
      ];
    case 'live':
      // Nothing is a "step" during the party. The console is the work, and the
      // summary points at it rather than handing the host a checklist while a
      // room full of people waits.
      return [];
    case 'ended':
      return [
        step('thank-you', thankYou ? slotHasContent(thankYou) : false, 'experience', true),
        step('memories', party.libraryAccessExpiresAt !== null, 'settings', true),
      ];
  }
}

/* ── What needs attention ─────────────────────────────────────────────────── */

export type AttentionId =
  | 'no-album'
  | 'access-closed'
  | 'access-expired'
  | 'lost-media'
  | 'pending-uploads'
  | 'pending-messages';

export interface WorkspaceAttention {
  id: AttentionId;
  tone: 'warn' | 'info';
  section: WorkspaceSection;
  /** Filled into the message, when it has a number to state. */
  count?: number;
}

/**
 * Problems, as distinct from steps: a step is work not yet done, this is
 * something that is WRONG — a guest link that has expired while the party is
 * on, a photograph that was deleted out from under a poster, a queue nobody
 * has looked at. Each is stated in words, each names where to fix it, and none
 * of them is inferred from the absence of an optional feature.
 */
export function workspaceAttention(facts: WorkspaceFacts, now: Date = new Date()): WorkspaceAttention[] {
  const { party, albumParty, slots, moderation } = facts;
  const out: WorkspaceAttention[] = [];
  const running = party.status === 'live';

  if (party.status !== 'draft' && mainMediaSource(party) === null) {
    out.push({ id: 'no-album', tone: 'warn', section: 'photos' });
  }
  if (running && albumParty && !albumParty.partyMode) {
    out.push({ id: 'access-closed', tone: 'warn', section: 'summary' });
  }
  if ((running || party.status === 'published') && guestAccessExpired(party, now)) {
    out.push({ id: 'access-expired', tone: 'warn', section: 'settings' });
  }
  const lost = slotsWithLostMedia(slots);
  if (lost.length > 0) {
    out.push({ id: 'lost-media', tone: 'warn', section: 'experience', count: lost.length });
  }
  if (moderation && moderation.uploads > 0) {
    out.push({ id: 'pending-uploads', tone: 'info', section: 'photos', count: moderation.uploads });
  }
  if (moderation && moderation.messages > 0) {
    out.push({ id: 'pending-messages', tone: 'info', section: 'activities', count: moderation.messages });
  }
  return out;
}

/* ── Words ────────────────────────────────────────────────────────────────── */

/**
 * The sentence under the party's name: where the evening is, in the host's
 * language, never a raw status. It is the STATE — the action beside it is a
 * separate thing, and the two are allowed to say different things.
 */
export function statusStoryKey(status: PartyStatus): MessageKey {
  return `party.story.${status}` as MessageKey;
}

export function stepTitleKey(id: StepId): MessageKey {
  return `party.step.${id}.title` as MessageKey;
}

export function stepNoteKey(id: StepId): MessageKey {
  return `party.step.${id}.note` as MessageKey;
}

export function stepActionKey(id: StepId): MessageKey {
  return `party.step.${id}.action` as MessageKey;
}

export function attentionTitleKey(id: AttentionId): MessageKey {
  return `party.attention.${id}.title` as MessageKey;
}

export function attentionBodyKey(id: AttentionId): MessageKey {
  return `party.attention.${id}.body` as MessageKey;
}

/**
 * The same body for the items that carry a NUMBER.
 *
 * Italian agrees with the count — "una sezione" against "3 sezioni" — so a
 * counted message cannot be one string with a placeholder in it. These ids
 * define `_one`/`_other` instead and are read with `tn`; the uncounted ones
 * define a plain `.body` and are read with `t`. Which of the two applies is
 * decided by whether the attention item has a `count`, so there is one rule
 * rather than a list to keep in step.
 */
export function attentionBodyPluralKey(id: AttentionId): PluralKey {
  return `party.attention.${id}.body` as PluralKey;
}
