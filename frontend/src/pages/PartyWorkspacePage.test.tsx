import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { PERMISSIONS } from '@nubarca/api-client';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../test-utils';
import { PartyWorkspacePage } from './PartyWorkspacePage';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const PARTY_ID = 'p1';
const ALBUM_ID = 'a1';

const party = (over: Record<string, unknown> = {}) => ({
  id: PARTY_ID, title: 'Festa di Marta', description: null, status: 'draft',
  eventStartsAt: null, liveStartedAt: null, liveEndedAt: null,
  guestAccessExpiresAt: null, libraryAccessExpiresAt: null,
  version: 1, createdAt: '2027-01-01T00:00:00Z', updatedAt: '2027-01-01T00:00:00Z',
  mediaSources: [], canChangeMainMediaSource: true,
  ...over,
});

const withAlbum = (over: Record<string, unknown> = {}) => party({
  mediaSources: [{ albumId: ALBUM_ID, albumName: 'Album di Marta', role: 'main', sortOrder: 0 }],
  ...over,
});

const slot = (kind: string, over: Record<string, unknown> = {}) => ({
  kind, enabled: false, visibleBefore: true, visibleLive: true, visibleAfter: false,
  content: {}, version: 0, ...over,
});

const EVERY_SLOT = [
  slot('invitation', { visibleLive: false }),
  slot('location'),
  slot('dress-code'),
  slot('menu'),
  slot('info'),
  slot('thank-you', { visibleBefore: false, visibleLive: false, visibleAfter: true }),
];

const albumParty = (over: Record<string, unknown> = {}) => ({
  albumId: ALBUM_ID, partyId: PARTY_ID, showOnTv: false, partyMode: true,
  partyUrl: '/party/tok', uploadEnabled: true, uploadUrl: '/party/uptok/upload',
  requireUploadApproval: false, requireMessageApproval: false,
  photoSlideSeconds: 9, maxVideoSlideSeconds: 60,
  maxPhotoUploadsPerParticipant: 0, maxVideoUploadsPerParticipant: 0,
  maxMessagesPerParticipant: 0, gameEnabled: false,
  ...over,
});

/** The guest counts, as the counts-only directory query answers them. */
const guestCounts = (over: {
  groups?: number;
  rsvp?: Record<string, number>;
  attendance?: Record<string, number>;
} = {}) => ({
  partyId: PARTY_ID, partyStatus: 'draft', mailAvailable: true, shareAvailable: true,
  items: [], nextCursor: null,
  summary: {
    groups: over.groups ?? 0,
    otherArrivals: 0,
    rsvp: {
      groups: over.groups ?? 0, invited: 0, missingResponses: 0, attending: 0,
      declined: 0, expectedPeople: 0, unansweredGroups: 0, ...over.rsvp,
    },
    attendance: {
      expectedPeople: 0, expectedArrived: 0, expectedMissing: 0,
      unexpectedKnownGuests: 0, otherArrivals: 0, totalArrivals: 0, ...over.attendance,
    },
  },
});

/**
 * The reads every section of the workspace may make, plus whatever the test
 * cares about. The page asks for the party, its album settings, its guest
 * content and its guest COUNTS; the sections that state what is waiting also
 * read the two moderation queues.
 */
function mount(
  handlers: Record<string, () => Response>,
  { permissions, at }: { permissions?: readonly string[]; at?: string } = {},
) {
  const mock = installFetchMock({
    [`GET /api/parties/${PARTY_ID}/guest-content`]: () => jsonResponse(EVERY_SLOT),
    [`POST /api/parties/${PARTY_ID}/guest-directory/query`]: () => jsonResponse(guestCounts()),
    [`GET /api/albums/${ALBUM_ID}/party-uploads`]: () =>
      jsonResponse({ albumId: ALBUM_ID, requireUploadApproval: false, items: [] }),
    [`GET /api/albums/${ALBUM_ID}/party-messages`]: () =>
      jsonResponse({ albumId: ALBUM_ID, isOwner: true, partyActive: true, requireMessageApproval: false, items: [] }),
    ...handlers,
  });
  render(
    <AuthedWrapper permissions={permissions}>
      <MemoryRouter initialEntries={[`/parties/${PARTY_ID}${at ?? ''}`]}>
        <Routes>
          <Route path="/parties/:partyId" element={<PartyWorkspacePage />} />
          <Route path="/parties" element={<div data-testid="parties-page-marker" />} />
        </Routes>
      </MemoryRouter>
    </AuthedWrapper>,
  );
  return mock;
}

describe('the party workspace — one map, whatever the phase', () => {
  it('offers the same sections from the draft to the end', async () => {
    mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum()),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
    });

    for (const id of ['summary', 'experience', 'guests', 'photos', 'activities', 'screens', 'settings']) {
      expect(await screen.findByTestId(`party-tab-${id}`)).toBeInTheDocument();
    }
    // Live is a room with the lights on: it does not exist yet.
    expect(screen.queryByTestId('party-tab-live')).not.toBeInTheDocument();
  });

  it('lands on the summary, and on the console while the party is happening', async () => {
    mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum()),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
    });
    expect(await screen.findByTestId('party-next')).toBeInTheDocument();
    cleanup();

    mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum({ status: 'live' })),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
    });
    expect(await screen.findByTestId('party-live-arrivals')).toBeInTheDocument();
    expect(screen.getByTestId('party-tab-live')).toHaveAttribute('aria-selected', 'true');
  });

  it('still lands a bookmark from before the sections existed', async () => {
    mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum()),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
    }, { at: '?tab=before' });

    // The old "Before" tab was the guest-facing content, which is Experience.
    expect(await screen.findByTestId('party-content-invitation')).toBeInTheDocument();
  });

  it('moves through its sections with the arrow keys', async () => {
    // The rail carries a roving tabIndex, which is only half of a tablist's
    // contract: without arrow keys it is one stop in the tab order that cannot
    // be moved through at all.
    mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum()),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
    });

    const summary = await screen.findByTestId('party-tab-summary');
    summary.focus();
    await userEvent.keyboard('{ArrowRight}');
    expect(screen.getByTestId('party-tab-experience')).toHaveAttribute('aria-selected', 'true');

    await userEvent.keyboard('{End}');
    expect(screen.getByTestId('party-tab-settings')).toHaveAttribute('aria-selected', 'true');

    // And it wraps, so the last section's right arrow is the first one.
    await userEvent.keyboard('{ArrowRight}');
    expect(screen.getByTestId('party-tab-summary')).toHaveAttribute('aria-selected', 'true');
  });

  it('never leaves a guest search in the URL, wherever the link came from', async () => {
    // The one piece of console state that is personal data about somebody else.
    // A pre-release link can still carry it; arriving anywhere strips it.
    mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum()),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
    }, { at: '?section=settings&guestSearch=mario%20rossi' });

    await screen.findByTestId('party-settings-form');
    expect(window.location.search).not.toContain('mario');
    expect(document.body.innerHTML).not.toContain('mario');
  });
});

describe('the summary — what do I do now', () => {
  it('offers the step that actually unblocks the party, not a move it cannot make', async () => {
    mount({ [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(party()), 'GET /api/albums': () => jsonResponse([]) });

    // A draft with no album is not one button away from a party.
    expect(await screen.findByTestId('party-next-move')).toHaveTextContent('Scegli l’album');
  });

  it('publishes by opening the party to its guests, and adopts what came back', async () => {
    const mock = mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum()),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty({ partyMode: false })),
      [`PATCH /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
    });

    await userEvent.click(await screen.findByTestId('party-next-move'));

    // The capability's own transition, not a second "publish" racing it.
    const patch = mock.calls.find((c) => c.method === 'PATCH')!;
    expect(patch.url).toContain('party-settings');
    expect(await screen.findByTestId('party-share-url')).toHaveTextContent('/party/tok');
  });

  it('states the party’s state separately from the action', async () => {
    mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum({ status: 'published' })),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
    });

    // The badge says where the evening is; the button says what pressing it
    // does. A host must never have to read a CTA to learn the state.
    expect(await screen.findByTestId('party-status')).toHaveTextContent('Pubblicata');
    // And the action waits for the album's settings rather than guessing: until
    // they arrive, whether the guests can already reach this party is unknown.
    expect(await screen.findByTestId('party-next-move')).toHaveTextContent('Avvia festa');
  });

  it('an open party is never told it is missing zero guests', async () => {
    mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum({ status: 'live' })),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
      [`POST /api/parties/${PARTY_ID}/guest-directory/query`]: () =>
        jsonResponse(guestCounts({ groups: 0, attendance: { totalArrivals: 84 } })),
    });

    const metrics = await screen.findByTestId('party-live-metrics');
    expect(within(metrics).getByText('84')).toBeInTheDocument();
    expect(within(metrics).queryByText('Attesi')).not.toBeInTheDocument();
    expect(within(metrics).queryByText('Mancano')).not.toBeInTheDocument();
  });

  it('separates a problem from a step still to do', async () => {
    mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum({ status: 'live' })),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty({ partyMode: false })),
    });

    // A party that is on with its guests locked out is WRONG, and is stated as
    // such rather than sitting in a checklist.
    expect(await screen.findByTestId('party-live-attention-access-closed')).toBeInTheDocument();
  });
});

describe('the experience — what the guests will see', () => {
  it('offers a typed card per kind, and no page builder', async () => {
    mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum()),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
    }, { at: '?section=experience' });

    for (const kind of ['invitation', 'location', 'dress-code', 'menu', 'info', 'thank-you']) {
      expect(await screen.findByTestId(`party-content-${kind}`)).toBeInTheDocument();
    }
    // No palette, no blocks, no drag handles: six named shapes.
    expect(screen.queryByText(/blocco|block|trascina|drag/i)).not.toBeInTheDocument();
  });

  it('reveals a slot’s form only once the host turns it on', async () => {
    mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum()),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
    }, { at: '?section=experience' });

    const card = await screen.findByTestId('party-content-location');
    expect(within(card).queryByLabelText('Luogo')).not.toBeInTheDocument();
    // The state is on the card before it is opened, and it is not the switch.
    expect(within(card).getByTestId('party-content-state-location')).toHaveTextContent(/non la vedono/i);

    await userEvent.click(within(card).getByTestId('party-content-enable-location'));
    expect(within(card).getByLabelText('Luogo')).toBeInTheDocument();
    expect(within(card).getByLabelText('Indirizzo')).toBeInTheDocument();
  });

  it('saves one slot with its OWN version', async () => {
    const mock = mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum()),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
      [`PUT /api/parties/${PARTY_ID}/guest-content/location`]: () =>
        jsonResponse(slot('location', { enabled: true, version: 1 })),
    }, { at: '?section=experience' });

    const card = await screen.findByTestId('party-content-location');
    await userEvent.click(within(card).getByTestId('party-content-enable-location'));
    await userEvent.type(within(card).getByLabelText('Luogo'), 'Villa Aurora');
    await userEvent.click(within(card).getByTestId('party-content-save-location'));

    const put = mock.calls.find((c) => c.method === 'PUT')!;
    const body = JSON.parse(String(put.body));
    expect(body.enabled).toBe(true);
    expect(body.content.venueName).toBe('Villa Aurora');
    // The SLOT's version, not the party's: editing the menu never contends
    // with renaming the party.
    expect(body.version).toBe(0);
  });

  it('adopts the server’s slot on a conflict rather than overwriting', async () => {
    mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum()),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
      [`PUT /api/parties/${PARTY_ID}/guest-content/info`]: () => jsonResponse({
        error: 'version_conflict',
        content: slot('info', {
          enabled: true, version: 4, content: { title: 'Scritto da qualcun altro', body: 'Testo' },
        }),
      }, 409),
    }, { at: '?section=experience' });

    const card = await screen.findByTestId('party-content-info');
    await userEvent.click(within(card).getByTestId('party-content-enable-info'));
    await userEvent.type(within(card).getByLabelText('Titolo'), 'Il mio');
    await userEvent.click(within(card).getByTestId('party-content-save-info'));

    expect(await screen.findByTestId('party-content-conflict-info')).toBeInTheDocument();
    expect(within(await screen.findByTestId('party-content-info')).getByLabelText('Titolo'))
      .toHaveValue('Scritto da qualcun altro');
  });

  it('opens the real guest page as the preview, and says so when it cannot', async () => {
    mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum()),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty({ partyMode: false })),
    }, { at: '?section=experience' });

    expect(await screen.findByTestId('party-experience-no-preview')).toBeInTheDocument();
    expect(screen.queryByTestId('party-experience-preview')).not.toBeInTheDocument();
  });
});

describe('the photos', () => {
  it('a party with no album invites one instead of failing', async () => {
    const mock = mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(party()),
      'GET /api/albums': () => jsonResponse([]),
    }, { at: '?section=photos' });

    expect(await screen.findByTestId('party-album-empty')).toBeInTheDocument();
    // Not a permission problem and not an error: an unfinished configuration.
    // Nothing album-scoped is requested with an id that does not exist.
    expect(mock.calls.some((c) => c.url.includes('party-settings'))).toBe(false);
    expect(mock.calls.some((c) => c.url.includes('party-uploads'))).toBe(false);
  });

  it('links an existing album without building a second album browser', async () => {
    const mock = mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(party()),
      'GET /api/albums': () => jsonResponse([
        { id: ALBUM_ID, name: 'Album di Marta', description: null, itemCount: 0, showOnTv: false,
          createdAt: '2027-01-01T00:00:00Z', updatedAt: '2027-01-01T00:00:00Z',
          photoCount: 0, videoCount: 0, excludedCount: 0, coverItems: [] },
      ]),
      [`PUT /api/parties/${PARTY_ID}/media/main`]: () => jsonResponse(withAlbum({ version: 2 })),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty({ partyMode: false })),
    }, { at: '?section=photos' });

    await userEvent.click(await screen.findByRole('button', { name: 'Usa album esistente' }));
    await userEvent.selectOptions(await screen.findByLabelText('Scegli un album'), ALBUM_ID);
    await userEvent.click(screen.getByRole('button', { name: 'Collega' }));

    expect(await screen.findByTestId('party-album-name')).toHaveTextContent('Album di Marta');
    const link = mock.calls.find((c) => c.method === 'PUT')!;
    expect(JSON.parse(String(link.body))).toEqual({ albumId: ALBUM_ID, version: 1 });
  });

  it('says the album is fixed once the party has published a QR', async () => {
    mount({
      [`GET /api/parties/${PARTY_ID}`]: () =>
        jsonResponse(withAlbum({ status: 'published', canChangeMainMediaSource: false })),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
    }, { at: '?section=photos' });

    expect(await screen.findByTestId('party-album-locked')).toBeInTheDocument();
    // Said plainly, rather than discovered from a refusal after choosing.
    expect(screen.queryByRole('button', { name: 'Cambia album' })).not.toBeInTheDocument();
  });

  it('keeps the queue reachable when the contribution permission is gone', async () => {
    // `party.contributions` governs OPENING the channel, never tidying up what
    // already came through it. The backend allows this, and the UI must agree.
    mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum({ status: 'live' })),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
    }, { permissions: [PERMISSIONS.partyAccess], at: '?section=photos' });

    const queue = await screen.findByTestId('party-photos-queue');
    expect(queue).toHaveAttribute('href', expect.stringContaining(`/albums/${ALBUM_ID}/party-uploads`));
    // The SWITCH is absent, not disabled: the host may not open the channel.
    expect(screen.queryByTestId('party-photos-uploads')).not.toBeInTheDocument();
  });

  it('counts what is waiting, beside the section that empties it', async () => {
    mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum({ status: 'live' })),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
      [`GET /api/albums/${ALBUM_ID}/party-uploads`]: () => jsonResponse({
        albumId: ALBUM_ID, requireUploadApproval: true,
        items: [
          { fileItemId: 'f1', name: 'a.jpg', status: 'pending', thumbnailPath: null },
          { fileItemId: 'f2', name: 'b.jpg', status: 'pending', thumbnailPath: null },
          { fileItemId: 'f3', name: 'c.jpg', status: 'approved', thumbnailPath: null },
        ],
      }),
    }, { at: '?section=photos' });

    expect(await within(await screen.findByTestId('party-photos-queue')).findByText('2 in attesa'))
      .toBeInTheDocument();
    expect(within(screen.getByTestId('party-tab-photos')).getByText('2')).toBeInTheDocument();
  });
});

describe('the live console', () => {
  it('leads with the arrivals and opens the door in one tap', async () => {
    mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum({ status: 'live' })),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
      [`POST /api/parties/${PARTY_ID}/guest-directory/query`]: () => jsonResponse(guestCounts({
        groups: 12,
        attendance: { expectedPeople: 40, totalArrivals: 22, expectedMissing: 18 },
      })),
    });

    const metrics = await screen.findByTestId('party-live-metrics');
    expect(within(metrics).getByText('22')).toBeInTheDocument();
    expect(within(metrics).getByText('18')).toBeInTheDocument();

    await userEvent.click(screen.getByTestId('party-live-door'));
    expect(await screen.findByTestId('party-guests')).toBeInTheDocument();
  });

  it('jumps into the list already filtered to who is missing', async () => {
    mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum({ status: 'live' })),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
      [`POST /api/parties/${PARTY_ID}/guest-directory/query`]: () => jsonResponse({
        ...guestCounts({ groups: 12, attendance: { expectedPeople: 40, totalArrivals: 22, expectedMissing: 18 } }),
        partyStatus: 'live',
      }),
    });

    await userEvent.click(await screen.findByTestId('party-live-filter-to_arrive'));

    // The console's own filter, not a second list.
    expect(await screen.findByTestId('guest-filter-to_arrive')).toHaveAttribute('aria-pressed', 'true');
  });

  it('asks before ending the evening, and one press ends nothing', async () => {
    const mock = mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum({ status: 'live' })),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
      [`POST /api/parties/${PARTY_ID}/end-live`]: () => jsonResponse(withAlbum({ status: 'ended', version: 2 })),
    });

    await userEvent.click(await screen.findByTestId('party-end-live'));
    expect(await screen.findByTestId('party-end-live-confirm')).toBeInTheDocument();
    expect(mock.calls.some((c) => c.url.endsWith('/end-live'))).toBe(false);

    await userEvent.click(screen.getByTestId('party-end-live-yes'));
    const ended = mock.calls.find((c) => c.url.endsWith('/end-live'))!;
    expect(JSON.parse(String(ended.body))).toEqual({ version: 1 });
    expect(await screen.findByTestId('party-status')).toHaveTextContent('Conclusa');
    // The console goes away with the evening; the map does not change.
    expect(screen.queryByTestId('party-tab-live')).not.toBeInTheDocument();
  });
});

describe('activities and screens', () => {
  it('shows only what the caller may actually run', async () => {
    mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum({ status: 'live' })),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
    }, { permissions: [PERMISSIONS.partyAccess, PERMISSIONS.partyGames], at: '?section=activities' });

    expect(await screen.findByTestId('party-game-settings')).toBeInTheDocument();
    expect(screen.queryByTestId('party-activities-game-locked')).not.toBeInTheDocument();
  });

  it('says a locked capability is not available rather than showing dead controls', async () => {
    mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum({ status: 'live' })),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
    }, { permissions: [PERMISSIONS.partyAccess], at: '?section=screens' });

    expect(await screen.findByTestId('party-print-locked')).toBeInTheDocument();
    expect(screen.queryByTestId('party-print')).not.toBeInTheDocument();
  });

  it('waits for an album rather than calling album routes without one', async () => {
    const mock = mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(party()),
    }, { at: '?section=activities' });

    expect(await screen.findByTestId('party-activities-needs-album')).toBeInTheDocument();
    expect(mock.calls.some((c) => c.url.includes('/albums/'))).toBe(false);
  });
});

describe('the settings', () => {
  it('saves the party’s facts and BOTH windows in one request', async () => {
    // One form, one version, one concurrency check: two forms over one version
    // would mean saving either silently discarded the other's edits.
    const mock = mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum()),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
      [`PATCH /api/parties/${PARTY_ID}`]: () =>
        jsonResponse(withAlbum({ version: 2, libraryAccessExpiresAt: '2027-07-20T00:00:00Z' })),
    }, { at: '?section=settings' });

    await userEvent.type(
      await screen.findByTestId('party-library-window'), '2027-07-20T00:00');
    await userEvent.click(screen.getByTestId('party-details-save'));

    const patch = mock.calls.filter((c) => c.method === 'PATCH');
    expect(patch).toHaveLength(1);
    const body = JSON.parse(String(patch[0].body));
    expect(body.libraryAccessExpiresAt).toBeTruthy();
    expect(body.version).toBe(1);
  });

  it('adopts the server state on a version conflict rather than overwriting', async () => {
    mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum()),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
      [`PATCH /api/parties/${PARTY_ID}`]: () => jsonResponse(
        { error: 'version_conflict', party: withAlbum({ title: 'Nome di qualcun altro', version: 7 }) },
        409,
      ),
    }, { at: '?section=settings' });

    const title = await screen.findByLabelText('Nome');
    await userEvent.clear(title);
    await userEvent.type(title, 'Il mio nome');
    await userEvent.click(screen.getByTestId('party-details-save'));

    expect(await screen.findByTestId('party-conflict')).toBeInTheDocument();
    // The form now shows what actually happened, not what was typed.
    expect(await screen.findByTestId('party-title')).toHaveTextContent('Nome di qualcun altro');
    expect(screen.getByLabelText('Nome')).toHaveValue('Nome di qualcun altro');
  });

  it('an ended party offers no false re-open and keeps its guest access', async () => {
    mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum({ status: 'ended' })),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
    }, { at: '?section=settings' });

    expect(await screen.findByTestId('party-status')).toHaveTextContent('Conclusa');
    // Guest access is NOT switched off behind the host's back: an ended party
    // keeping its capability is what the post-event library needs.
    expect(await screen.findByTestId('party-guest-access-switch'))
      .toHaveAttribute('aria-checked', 'true');
  });
});

describe('deleting the party', () => {
  const settings = () => mount({
    [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum({ version: 7 })),
    [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
    [`DELETE /api/parties/${PARTY_ID}`]: () => new Response(null, { status: 204 }),
  }, { at: '?section=settings' });

  it('says what SURVIVES before the host commits, not afterwards', async () => {
    settings();
    const card = await screen.findByTestId('party-teardown');
    expect(within(card).getByText(/L’album resta/i)).toBeInTheDocument();
    expect(within(card).getByText(/Cestino/i)).toBeInTheDocument();
  });

  it('asks first, and one click deletes nothing', async () => {
    const mock = settings();
    await userEvent.click(await screen.findByTestId('party-teardown-start'));

    expect(await screen.findByTestId('party-teardown-confirm')).toBeInTheDocument();
    expect(mock.calls.some((c) => c.method === 'DELETE')).toBe(false);
  });

  it('confirming sends the party’s CURRENT version and returns to the list', async () => {
    const mock = settings();
    await userEvent.click(await screen.findByTestId('party-teardown-start'));
    await userEvent.click(screen.getByTestId('party-teardown-confirm-yes'));

    const del = mock.calls.find((c) => c.method === 'DELETE')!;
    // Optimistic concurrency travels with the request: a party somebody else
    // edited meanwhile must be refused, not torn down from a stale read.
    expect(del.url).toContain('version=7');
    expect(await screen.findByTestId('parties-page-marker')).toBeInTheDocument();
  });

  it('adopts the server’s party on a version conflict instead of insisting', async () => {
    mount({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum({ version: 3 })),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
      [`DELETE /api/parties/${PARTY_ID}`]: () =>
        jsonResponse({ party: withAlbum({ version: 4, title: 'Rinominata' }) }, 409),
    }, { at: '?section=settings' });

    await userEvent.click(await screen.findByTestId('party-teardown-start'));
    await userEvent.click(screen.getByTestId('party-teardown-confirm-yes'));

    expect(await screen.findByTestId('party-teardown-conflict')).toBeInTheDocument();
    expect(screen.getByTestId('party-title')).toHaveTextContent('Rinominata');
  });
});
