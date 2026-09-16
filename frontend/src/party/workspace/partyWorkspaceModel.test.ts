import { describe, expect, it } from 'vitest';
import type {
  AlbumPartyStatus,
  GuestDirectorySummary,
  Party,
  PartyGuestContentSlot,
  PartyStatus,
} from '@nubarca/api-client';
import {
  PARTY_STATUSES,
  defaultWorkspaceSection,
  guestAccessExpired,
  hasGuestList,
  primaryIntent,
  slotHasContent,
  slotsWithLostMedia,
  workspaceAttention,
  factsFailed,
  workspaceSections,
  workspaceSteps,
  type Loaded,
  type WorkspaceFacts,
} from './partyWorkspaceModel';

// The Party workspace's information architecture, tested without rendering a
// pixel. What a host is offered, what they land on, what the party is waiting
// for and what is wrong with it are decisions — and a decision that can only be
// checked by clicking through a browser is a decision nobody re-checks.

const party = (over: Partial<Party> = {}): Party => ({
  id: 'p1', title: 'Festa di Marta', description: null, status: 'draft',
  eventStartsAt: null, liveStartedAt: null, liveEndedAt: null,
  guestAccessExpiresAt: null, libraryAccessExpiresAt: null,
  version: 1, createdAt: '2027-01-01T00:00:00Z', updatedAt: '2027-01-01T00:00:00Z',
  mediaSources: [], canChangeMainMediaSource: true,
  ...over,
});

const withAlbum = (over: Partial<Party> = {}): Party => party({
  mediaSources: [{ albumId: 'a1', albumName: 'Album di Marta', role: 'main', sortOrder: 0 }],
  ...over,
});

const albumParty = (over: Partial<AlbumPartyStatus> = {}): AlbumPartyStatus => ({
  albumId: 'a1', partyId: 'p1', showOnTv: false, partyMode: true, partyUrl: '/party/tok',
  uploadEnabled: false, uploadUrl: null, requireUploadApproval: false,
  requireMessageApproval: false, photoSlideSeconds: 9, maxVideoSlideSeconds: 60,
  maxPhotoUploadsPerParticipant: 0, maxVideoUploadsPerParticipant: 0,
  ...over,
});

const slot = (
  kind: PartyGuestContentSlot['kind'], over: Partial<PartyGuestContentSlot> = {},
): PartyGuestContentSlot => ({
  kind, enabled: false, visibleBefore: true, visibleLive: true, visibleAfter: false,
  content: {}, version: 0, mediaPresentation: 'inline', mediaFileItemId: null, mediaUrl: null,
  ...over,
});

const counts = (
  over: Partial<Omit<GuestDirectorySummary, 'rsvp' | 'attendance'>> & {
    rsvp?: Partial<GuestDirectorySummary['rsvp']>;
    attendance?: Partial<GuestDirectorySummary['attendance']>;
  } = {},
): GuestDirectorySummary => ({
  groups: 0,
  otherArrivals: 0,
  ...over,
  rsvp: {
    groups: 0, invited: 0, missingResponses: 0, attending: 0, declined: 0,
    expectedPeople: 0, unansweredGroups: 0, ...over.rsvp,
  },
  attendance: {
    expectedPeople: 0, expectedArrived: 0, expectedMissing: 0,
    unexpectedKnownGuests: 0, otherArrivals: 0, totalArrivals: 0, ...over.attendance,
  },
});

/** A read that answered. The tests say `loading` or `error` when they mean it. */
const got = <T>(value: T): Loaded<T> => ({ status: 'ready', value });
const LOADING = { status: 'loading' } as const;
const FAILED = { status: 'error' } as const;

const facts = (over: Partial<WorkspaceFacts> = {}): WorkspaceFacts => ({
  party: withAlbum(),
  albumParty: got(albumParty()),
  slots: got([]),
  guests: got(counts()),
  moderation: { uploads: LOADING, messages: LOADING },
  ...over,
});

describe('the sections a party offers', () => {
  it('keeps the same map through the whole lifecycle, and adds Live only while it is', () => {
    const withoutLive = ['summary', 'experience', 'guests', 'photos', 'activities', 'screens', 'settings'];
    for (const status of PARTY_STATUSES) {
      const sections = workspaceSections(status);
      expect(sections.filter((s) => s !== 'live')).toEqual(withoutLive);
      expect(sections.includes('live')).toBe(status === 'live');
    }
  });

  it('opens on the summary, except while the party is happening', () => {
    expect(defaultWorkspaceSection('draft')).toBe('summary');
    expect(defaultWorkspaceSection('published')).toBe('summary');
    // Standing in a room full of people, the door is the page.
    expect(defaultWorkspaceSection('live')).toBe('live');
    expect(defaultWorkspaceSection('ended')).toBe('summary');
  });
});

describe('the one thing to do now', () => {
  it('never offers a move the party cannot make', () => {
    // A draft with no album is not one button away from a party: the summary
    // offers the step that actually unblocks it.
    expect(primaryIntent(facts({ party: party(), albumParty: got(null) })).kind).toBe('link-album');
    expect(primaryIntent(facts({ party: party({ status: 'published' }), albumParty: got(null) })).kind)
      .toBe('link-album');
  });

  it('publishes by opening the party to its guests, never by a second button', () => {
    expect(primaryIntent(facts({ party: withAlbum(), albumParty: got(albumParty({ partyMode: false })) })).kind)
      .toBe('open-to-guests');
    // Published but the capability was turned off again: re-opening is the move.
    expect(primaryIntent(facts({
      party: withAlbum({ status: 'published' }),
      albumParty: got(albumParty({ partyMode: false })),
    })).kind).toBe('open-to-guests');
  });

  it('moves on to starting, running and then the photographs', () => {
    expect(primaryIntent(facts({ party: withAlbum({ status: 'published' }) })).kind).toBe('start-live');
    expect(primaryIntent(facts({ party: withAlbum({ status: 'live' }) })).kind).toBe('go-live-console');
    expect(primaryIntent(facts({ party: withAlbum({ status: 'ended' }) })).kind).toBe('open-photos');
  });
});

describe('what is left to do', () => {
  it('asks a draft for the things a party cannot open without', () => {
    const steps = workspaceSteps(facts({
      party: party(), albumParty: got(null), guests: got(counts()),
    }));
    const byId = Object.fromEntries(steps.map((s) => [s.id, s]));

    expect(byId.album.done).toBe(false);
    expect(byId.date.done).toBe(false);
    expect(byId['guest-access'].done).toBe(false);
    // A guest list is genuinely optional — an open door is a valid party — and
    // the step says so rather than reading as missing data.
    expect(byId['guest-list'].optional).toBe(true);
    expect(byId.album.optional).toBe(false);
  });

  it('ticks off what the party already has', () => {
    const steps = workspaceSteps(facts({
      party: withAlbum({ eventStartsAt: '2027-06-12T18:00:00Z' }),
      slots: got([slot('invitation', { enabled: true, content: { headline: 'Vieni' } })]),
    }));
    const byId = Object.fromEntries(steps.map((s) => [s.id, s]));

    expect(byId.album.done).toBe(true);
    expect(byId.date.done).toBe(true);
    expect(byId.invitation.done).toBe(true);
    expect(byId['guest-access'].done).toBe(true);
  });

  it('hands a live party no checklist at all', () => {
    // The console is the work. A room full of people is not the moment to be
    // handed a list of configuration chores.
    expect(workspaceSteps(facts({ party: withAlbum({ status: 'live' }) }))).toEqual([]);
  });

  it('turns to the photographs once it is over', () => {
    const ids = workspaceSteps(facts({ party: withAlbum({ status: 'ended' }) })).map((s) => s.id);
    expect(ids).toEqual(['thank-you', 'memories']);
  });

  it('counts a slot as written only when it says something', () => {
    expect(slotHasContent(slot('info'))).toBe(false);
    expect(slotHasContent(slot('info', { enabled: true }))).toBe(false);
    expect(slotHasContent(slot('info', { enabled: true, content: { title: '   ' } }))).toBe(false);
    expect(slotHasContent(slot('info', { enabled: true, content: { title: 'Parcheggio' } }))).toBe(true);
    // A photograph alone is content: a poster needs no words.
    expect(slotHasContent(slot('info', { enabled: true, mediaFileItemId: 'f1' }))).toBe(true);
  });
});

describe('what needs attention', () => {
  it('says nothing about a party that is simply not finished yet', () => {
    // A draft missing its album is a STEP, not a problem: the difference is
    // what stops the summary crying wolf on every new party.
    expect(workspaceAttention(facts({ party: party(), albumParty: got(null) }))).toEqual([]);
  });

  it('flags a published party with no album', () => {
    const ids = workspaceAttention(facts({
      party: party({ status: 'published' }), albumParty: got(null),
    })).map((a) => a.id);
    expect(ids).toContain('no-album');
  });

  it('flags a party that is on while its guests are locked out', () => {
    const ids = workspaceAttention(facts({
      party: withAlbum({ status: 'live' }), albumParty: got(albumParty({ partyMode: false })),
    })).map((a) => a.id);
    expect(ids).toContain('access-closed');
  });

  it('flags a guest window that has already passed', () => {
    const now = new Date('2027-06-12T22:00:00Z');
    const ids = workspaceAttention(facts({
      party: withAlbum({ status: 'live', guestAccessExpiresAt: '2027-06-12T20:00:00Z' }),
    }), now).map((a) => a.id);
    expect(ids).toContain('access-expired');
    expect(guestAccessExpired(withAlbum({ guestAccessExpiresAt: '2027-06-12T20:00:00Z' }), now)).toBe(true);
  });

  it('flags a section whose photograph was deleted, and counts them', () => {
    const slots = [
      slot('invitation', { enabled: true, mediaFileItemId: 'gone', mediaUrl: null }),
      slot('menu', { enabled: true, mediaFileItemId: 'here', mediaUrl: '/media/here' }),
    ];
    expect(slotsWithLostMedia(slots)).toHaveLength(1);
    const lost = workspaceAttention(facts({ slots: got(slots) })).find((a) => a.id === 'lost-media');
    expect(lost?.count).toBe(1);
    expect(lost?.section).toBe('experience');
  });

  it('points a waiting queue at the section that empties it', () => {
    const items = workspaceAttention(facts({
      party: withAlbum({ status: 'live' }),
      moderation: { uploads: got(3), messages: got(1) },
    }));
    expect(items.find((a) => a.id === 'pending-uploads')).toMatchObject({ count: 3, section: 'photos' });
    expect(items.find((a) => a.id === 'pending-messages')).toMatchObject({ count: 1, section: 'activities' });
  });

  it('never invents a queue it was not told about', () => {
    // Loading is "not asked for yet", and it must not read as zero or as a
    // problem. Neither must a failure.
    expect(workspaceAttention(facts({
      moderation: { uploads: LOADING, messages: LOADING },
    })).map((a) => a.id)).not.toContain('pending-uploads');
    expect(workspaceAttention(facts({
      moderation: { uploads: FAILED, messages: FAILED },
    })).map((a) => a.id)).not.toContain('pending-uploads');
  });
});

describe('an open party', () => {
  it('has no guest list, and that is a complete party', () => {
    expect(hasGuestList(counts())).toBe(false);
    expect(hasGuestList(counts({ groups: 4 }))).toBe(true);
    // Unknown counts are not an empty list.
    expect(hasGuestList(null)).toBe(false);
  });

  it('is never told it is missing invitations it never sent', () => {
    const steps = workspaceSteps(facts({
      party: withAlbum({ status: 'published' }), guests: got(counts()),
    }));
    expect(steps.find((s) => s.id === 'invitations')!.optional).toBe(true);
  });
});

/** The statuses are the four the product knows, in order. */
it('names the four states of an evening', () => {
  expect(PARTY_STATUSES).toEqual<PartyStatus[]>(['draft', 'published', 'live', 'ended']);
});

describe('unknown is not zero, and it is not "not configured" either', () => {
  it('says it cannot decide the next move rather than guessing one', () => {
    // Loading and failed are DIFFERENT answers: one is worth waiting for, the
    // other is worth retrying, and neither is "publish it".
    expect(primaryIntent(facts({ albumParty: LOADING })).kind).toBe('unknown');
    expect(primaryIntent(facts({ albumParty: FAILED })).kind).toBe('unavailable');
  });

  it('never claims a section is unwritten because the read failed', () => {
    // The whole point: "you have not written the invitation" and "we could not
    // read the invitation" are different sentences, and only one is true.
    const failed = workspaceSteps(facts({ party: party(), slots: FAILED }));
    expect(failed.some((s) => s.id === 'invitation')).toBe(false);
    expect(failed.some((s) => s.id === 'details')).toBe(false);
    // The steps that do not depend on it are still offered.
    expect(failed.some((s) => s.id === 'date')).toBe(true);

    const read = workspaceSteps(facts({ party: party(), slots: got([]) }));
    expect(read.find((s) => s.id === 'invitation')!.done).toBe(false);
  });

  it('never claims the guest access is closed because the read failed', () => {
    expect(workspaceSteps(facts({ albumParty: FAILED })).some((s) => s.id === 'guest-access'))
      .toBe(false);
    expect(workspaceAttention(facts({
      party: withAlbum({ status: 'live' }), albumParty: FAILED,
    })).map((a) => a.id)).not.toContain('access-closed');
  });

  it('lets one queue fail without the other losing its number', () => {
    const items = workspaceAttention(facts({
      party: withAlbum({ status: 'live' }),
      moderation: { uploads: FAILED, messages: got(2) },
    }));
    expect(items.find((a) => a.id === 'pending-uploads')).toBeUndefined();
    expect(items.find((a) => a.id === 'pending-messages')).toMatchObject({ count: 2 });
  });

  it('reports what did not arrive, so a surface can say so once', () => {
    expect(factsFailed(facts())).toBe(false);
    expect(factsFailed(facts({ albumParty: LOADING }))).toBe(false);
    expect(factsFailed(facts({ slots: FAILED }))).toBe(true);
    expect(factsFailed(facts({ guests: FAILED }))).toBe(true);
  });
});

describe('delivery is never inferred from the guest list', () => {
  it('does not tick the invitations off because the list has names in it', () => {
    // `rsvp.invited` counts NAMED GUESTS — the size of the list, not a delivery
    // receipt. A list created a minute ago used to read "Inviti mandati ✓"
    // while no personal link had left the building.
    const steps = workspaceSteps(facts({
      party: withAlbum({ status: 'published' }),
      guests: got(counts({ groups: 4, rsvp: { invited: 11 } })),
    }));
    const invitations = steps.find((s) => s.id === 'invitations')!;
    expect(invitations.done).toBe(false);
    expect(invitations.section).toBe('guests');
  });

  it('says the same thing whether the list is empty or full', () => {
    // Nothing about this entry may move with the size of the guest list,
    // because nothing about the list is evidence of a delivery either way.
    const empty = workspaceSteps(facts({
      party: withAlbum({ status: 'published' }), guests: got(counts({ groups: 0 })),
    })).find((s) => s.id === 'invitations')!;
    const full = workspaceSteps(facts({
      party: withAlbum({ status: 'published' }),
      guests: got(counts({ groups: 9, rsvp: { invited: 30, attending: 21 } })),
    })).find((s) => s.id === 'invitations')!;
    expect(empty.done).toBe(full.done);
    expect(empty.done).toBe(false);
  });
});
