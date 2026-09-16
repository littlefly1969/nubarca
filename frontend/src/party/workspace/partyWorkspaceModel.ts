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

/**
 * ONE READ, IN ONE OF THREE STATES — and they are three, not two.
 *
 * `null` used to mean both "has not arrived" and "the request failed", and the
 * surfaces above could not tell them apart: a failed album-settings read left
 * the summary showing a placeholder for ever, and a failed guest-content read
 * made the checklist say the invitation had not been written. Both are the same
 * mistake — UNKNOWN IS NOT ZERO, and it is not "not configured" either.
 *
 * `ready` may legitimately carry `null`: a party with no album has no album
 * settings, and that is a fact rather than a missing answer.
 */
export type Loaded<T> =
  | { status: 'loading' }
  | { status: 'ready'; value: T }
  | { status: 'error' };

export const loadedValue = <T>(loaded: Loaded<T>): T | null =>
  (loaded.status === 'ready' ? loaded.value : null);

/** Everything the summary reasons over, gathered once. */
export interface WorkspaceFacts {
  party: Party;
  /** `ready` with `null` when the party has no album to have settings for. */
  albumParty: Loaded<AlbumPartyStatus | null>;
  slots: Loaded<readonly PartyGuestContentSlot[]>;
  guests: Loaded<GuestDirectorySummary | null>;
  /**
   * The two queues, SEPARATELY. One list failing must not report the other's
   * count as the whole truth, and must never report its own as zero.
   */
  moderation: { uploads: Loaded<number>; messages: Loaded<number> };
}

/** Anything the workspace asked for and did not get. */
export function factsFailed(facts: WorkspaceFacts): boolean {
  return facts.albumParty.status === 'error'
    || facts.slots.status === 'error'
    || facts.guests.status === 'error';
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
   * The album's settings have not arrived YET, so whether the guests can
   * already reach this party is genuinely not known. Saying "publish it" here
   * would be a guess, and a host who pressed it would be acting on one.
   */
  | { kind: 'unknown' }
  /**
   * They were asked for and did not come. Distinct from `unknown`, because a
   * placeholder that waits for ever is how a failed read used to look: this
   * one says so and offers the read again.
   */
  | { kind: 'unavailable' };

/**
 * THE action of the moment — one, contextual, and never a lie.
 *
 * It is deliberately not the same thing as the lifecycle transition: a draft
 * with no album is not one button away from a party, so the summary offers the
 * step that actually unblocks it instead of a "publish" that would fail.
 */
export function primaryIntent(facts: WorkspaceFacts): PrimaryIntent {
  const { party } = facts;
  const album = mainMediaSource(party);
  const settings = facts.albumParty;
  switch (party.status) {
    case 'draft':
    case 'published': {
      if (!album) return { kind: 'link-album' };
      if (settings.status === 'loading') return { kind: 'unknown' };
      if (settings.status === 'error') return { kind: 'unavailable' };
      if (!settings.value?.partyMode) return { kind: 'open-to-guests' };
      return party.status === 'published' ? { kind: 'start-live' } : { kind: 'open-to-guests' };
    }
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
  | 'invitations'
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
  const { party } = facts;
  const album = mainMediaSource(party) !== null;
  // A step is only offered when the evidence for it ARRIVED. A read that failed
  // must never produce "not done": "you have not written the invitation" and
  // "we could not read the invitation" are different sentences, and only one of
  // them is true. The summary says which, above the list.
  const slots = loadedValue(facts.slots);
  const settings = loadedValue(facts.albumParty);
  const guests = loadedValue(facts.guests);
  const invitation = slots ? findSlot(slots, 'invitation') : undefined;
  const thankYou = slots ? findSlot(slots, 'thank-you') : undefined;
  const accessOpen = settings?.partyMode === true;
  const listed = hasGuestList(guests);

  const step = (
    id: StepId, done: boolean, section: WorkspaceSection, optional = false,
  ): WorkspaceStep => ({ id, done, section, optional });
  const known = (source: Loaded<unknown>, made: WorkspaceStep): WorkspaceStep | null =>
    (source.status === 'ready' ? made : null);
  const kept = (steps: (WorkspaceStep | null)[]): WorkspaceStep[] =>
    steps.filter((one): one is WorkspaceStep => one !== null);

  switch (party.status) {
    case 'draft':
      return kept([
        step('date', party.eventStartsAt !== null, 'settings'),
        step('album', album, 'photos'),
        known(facts.slots, step('invitation', invitation ? slotHasContent(invitation) : false, 'experience')),
        known(facts.slots, step('details', (slots ?? []).some(
          (one) => one.kind !== 'invitation' && one.kind !== 'thank-you' && slotHasContent(one),
        ), 'experience', true)),
        known(facts.guests, step('guest-list', listed, 'guests', true)),
        known(facts.albumParty, step('guest-access', accessOpen, 'summary')),
      ]);
    case 'published':
      return kept([
        known(facts.albumParty, step('guest-access', accessOpen, 'summary')),
        // NEVER MARKED DONE, because nothing here knows whether an invitation
        // was actually handed to anybody. `rsvp.invited` counts NAMED GUESTS —
        // it is the size of the list, not a delivery receipt — so a list
        // created a minute ago used to tick this box while no personal link had
        // left the building. There is no delivery aggregate on the counts-only
        // query, and inventing one is not this change's job; so the entry is an
        // open door rather than a claim, and its copy says what to do rather
        // than what has happened.
        known(facts.guests, step('invitations', false, 'guests', true)),
        known(facts.slots, step('invitation', invitation ? slotHasContent(invitation) : false, 'experience')),
        known(facts.albumParty, step('contributions', settings?.uploadEnabled === true, 'photos', true)),
        known(facts.albumParty, step('screens', settings?.showOnTv === true, 'screens', true)),
        step('start', false, 'summary'),
      ]);
    case 'live':
      // Nothing is a "step" during the party. The console is the work, and the
      // summary points at it rather than handing the host a checklist while a
      // room full of people waits.
      return [];
    case 'ended':
      return kept([
        known(facts.slots, step('thank-you', thankYou ? slotHasContent(thankYou) : false, 'experience', true)),
        step('memories', party.libraryAccessExpiresAt !== null, 'settings', true),
      ]);
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
  const { party, moderation } = facts;
  const out: WorkspaceAttention[] = [];
  const running = party.status === 'live';
  // Every problem below is claimed only from evidence that ARRIVED. A read that
  // failed raises nothing: a warning invented from a missing answer is worse
  // than no warning, because the host would act on it.
  const settings = loadedValue(facts.albumParty);
  const slots = loadedValue(facts.slots);

  if (party.status !== 'draft' && mainMediaSource(party) === null) {
    out.push({ id: 'no-album', tone: 'warn', section: 'photos' });
  }
  if (running && settings && !settings.partyMode) {
    out.push({ id: 'access-closed', tone: 'warn', section: 'summary' });
  }
  if ((running || party.status === 'published') && guestAccessExpired(party, now)) {
    out.push({ id: 'access-expired', tone: 'warn', section: 'settings' });
  }
  const lost = slots ? slotsWithLostMedia(slots) : [];
  if (lost.length > 0) {
    out.push({ id: 'lost-media', tone: 'warn', section: 'experience', count: lost.length });
  }
  // Each queue answers for itself: one failing must not be reported as zero,
  // and must not suppress the other's real count either.
  if (moderation.uploads.status === 'ready' && moderation.uploads.value > 0) {
    out.push({ id: 'pending-uploads', tone: 'info', section: 'photos', count: moderation.uploads.value });
  }
  if (moderation.messages.status === 'ready' && moderation.messages.value > 0) {
    out.push({ id: 'pending-messages', tone: 'info', section: 'activities', count: moderation.messages.value });
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
