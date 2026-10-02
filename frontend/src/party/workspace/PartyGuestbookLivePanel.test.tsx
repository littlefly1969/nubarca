import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import type { PartyGuestbookLiveControl } from '@nubarca/api-client';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../../test-utils';
import { CREW_CAPABILITIES } from '../crew/crewModel';
import { PartyGuestbookLivePanel } from './PartyGuestbookLivePanel';
import { crewPartyApi, ownerPartyApi, PartyApiProvider, type PartyApi } from './partyApi';

/**
 * THE GUEST BOOK IN THE REGIA — the room's half and the television's half.
 *
 * What these defend:
 *   * the switch and the television line show what the SERVER committed and
 *     projects, never a local wish — no optimism, and a refusal adopts the
 *     state it carries;
 *   * every control is one the server offered: the game holding the screen and
 *     an empty book are said in words, not by drawing a refused button;
 *   * the television line follows `tvPresentation`, even when the regia's own
 *     flag says otherwise;
 *   * the crew drives the same commands on its own routes.
 */
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const ALBUM = 'a1';
const PARTY = 'p1';
const OWNER_GET = `GET /api/albums/${ALBUM}/party-guestbook-live`;
const OWNER_POST = `POST /api/albums/${ALBUM}/party-guestbook-live/commands`;

function control(over: Partial<PartyGuestbookLiveControl> = {}): PartyGuestbookLiveControl {
  return {
    version: 3,
    viewingEnabled: false,
    tvActive: false,
    tvPresentation: 'slideshow',
    visibleEntries: 2,
    pendingEntries: 1,
    availableCommands: ['enable_viewing', 'show_on_tv'],
    tvUnavailableReason: null,
    partyLive: true,
    guestbookEnabled: true,
    ...over,
  };
}

function mount(api: PartyApi = ownerPartyApi) {
  render(
    <AuthedWrapper>
      <MemoryRouter>
        <PartyApiProvider api={api}>
          <PartyGuestbookLivePanel albumId={ALBUM} />
        </PartyApiProvider>
      </MemoryRouter>
    </AuthedWrapper>,
  );
}

describe('the guest book live card', () => {
  it('shows both halves as the server sees them, with viewing off', async () => {
    installFetchMock({ [OWNER_GET]: () => jsonResponse(control()) });
    mount();

    const viewing = await screen.findByTestId('party-guestbook-live-viewing');
    expect(viewing).toHaveAttribute('aria-checked', 'false');
    expect(screen.getByTestId('party-guestbook-live-viewing-row'))
      .toHaveTextContent('Guestbook visibile agli ospiti');
    expect(screen.getByTestId('party-guestbook-live-viewing-state'))
      .toHaveTextContent('Il Guestbook continua a ricevere nuovi ricordi.');
    expect(screen.getByTestId('party-guestbook-live-tv-state')).toHaveTextContent('In TV: slideshow');
    expect(screen.getByTestId('party-guestbook-live-counts')).toHaveTextContent('2');
    expect(screen.getByTestId('party-guestbook-live-show')).toHaveTextContent('Mostra Guestbook sulla TV');
    expect(screen.queryByTestId('party-guestbook-live-return')).not.toBeInTheDocument();
  });

  it('turns viewing on and shows only what the server answered', async () => {
    const fetchMock = installFetchMock({
      [OWNER_GET]: () => jsonResponse(control()),
      [OWNER_POST]: () => jsonResponse(control({
        version: 4, viewingEnabled: true, availableCommands: ['disable_viewing', 'show_on_tv'],
      })),
    });
    mount();

    await userEvent.setup().click(await screen.findByTestId('party-guestbook-live-viewing'));

    await waitFor(() => expect(screen.getByTestId('party-guestbook-live-viewing'))
      .toHaveAttribute('aria-checked', 'true'));
    expect(screen.getByTestId('party-guestbook-live-viewing-state'))
      .toHaveTextContent('Gli ospiti possono vedere il Guestbook dalla festa.');
    const post = fetchMock.calls.find((c) => c.method === 'POST');
    expect(JSON.parse(post!.body!)).toEqual({ command: 'enable_viewing', expectedVersion: 3 });
  });

  it('turns viewing off with the version on screen', async () => {
    const fetchMock = installFetchMock({
      [OWNER_GET]: () => jsonResponse(control({
        version: 7, viewingEnabled: true, availableCommands: ['disable_viewing', 'show_on_tv'],
      })),
      [OWNER_POST]: () => jsonResponse(control({ version: 8, viewingEnabled: false })),
    });
    mount();

    await userEvent.setup().click(await screen.findByTestId('party-guestbook-live-viewing'));

    await waitFor(() => expect(screen.getByTestId('party-guestbook-live-viewing'))
      .toHaveAttribute('aria-checked', 'false'));
    const post = fetchMock.calls.find((c) => c.method === 'POST');
    expect(JSON.parse(post!.body!)).toEqual({ command: 'disable_viewing', expectedVersion: 7 });
  });

  it('invents nothing while a command is in flight', async () => {
    let release: (r: Response) => void = () => {};
    installFetchMock({
      [OWNER_GET]: () => jsonResponse(control()),
      [OWNER_POST]: () => new Promise<Response>((resolve) => { release = resolve; }),
    });
    mount();

    await userEvent.setup().click(await screen.findByTestId('party-guestbook-live-viewing'));

    // Still what the server last said, and nothing else can be pressed.
    expect(screen.getByTestId('party-guestbook-live-viewing')).toHaveAttribute('aria-checked', 'false');
    expect(screen.getByTestId('party-guestbook-live-show')).toBeDisabled();
    release(jsonResponse(control({ version: 4, viewingEnabled: true, availableCommands: ['disable_viewing'] })));
    await waitFor(() => expect(screen.getByTestId('party-guestbook-live-viewing'))
      .toHaveAttribute('aria-checked', 'true'));
  });

  it('adopts the state a version conflict carries, and says what happened', async () => {
    installFetchMock({
      [OWNER_GET]: () => jsonResponse(control()),
      [OWNER_POST]: () => jsonResponse({
        code: 'version_conflict',
        control: control({ version: 5, viewingEnabled: true, availableCommands: ['disable_viewing', 'show_on_tv'] }),
      }, 409),
    });
    mount();

    await userEvent.setup().click(await screen.findByTestId('party-guestbook-live-viewing'));

    // Realigned to the winner's state, not to what this phone asked for.
    await waitFor(() => expect(screen.getByTestId('party-guestbook-live-viewing'))
      .toHaveAttribute('aria-checked', 'true'));
    expect(screen.getByTestId('party-guestbook-live-refusal'))
      .toHaveTextContent('Il Guestbook è appena cambiato');
  });

  it('puts the book on the television, then offers the way back', async () => {
    installFetchMock({
      [OWNER_GET]: () => jsonResponse(control()),
      [OWNER_POST]: () => jsonResponse(control({
        version: 4, tvActive: true, tvPresentation: 'guestbook',
        availableCommands: ['enable_viewing', 'return_to_slideshow'],
      })),
    });
    mount();

    await userEvent.setup().click(await screen.findByTestId('party-guestbook-live-show'));

    await waitFor(() => expect(screen.getByTestId('party-guestbook-live-tv-state'))
      .toHaveTextContent('Guestbook in TV'));
    expect(screen.getByTestId('party-guestbook-live-on-tv')).toBeInTheDocument();
    expect(screen.getByTestId('party-guestbook-live-return')).toHaveTextContent('Torna allo slideshow');
    expect(screen.queryByTestId('party-guestbook-live-show')).not.toBeInTheDocument();
  });

  it('returns the television to the slideshow', async () => {
    const fetchMock = installFetchMock({
      [OWNER_GET]: () => jsonResponse(control({
        version: 9, tvActive: true, tvPresentation: 'guestbook',
        availableCommands: ['enable_viewing', 'return_to_slideshow'],
      })),
      [OWNER_POST]: () => jsonResponse(control({ version: 10 })),
    });
    mount();

    await userEvent.setup().click(await screen.findByTestId('party-guestbook-live-return'));

    await waitFor(() => expect(screen.getByTestId('party-guestbook-live-show')).toBeInTheDocument());
    const post = fetchMock.calls.find((c) => c.method === 'POST');
    expect(JSON.parse(post!.body!)).toEqual({ command: 'return_to_slideshow', expectedVersion: 9 });
  });

  it('says the game holds the television instead of offering a refused command', async () => {
    installFetchMock({
      [OWNER_GET]: () => jsonResponse(control({
        tvPresentation: 'game', availableCommands: ['enable_viewing'], tvUnavailableReason: 'game_active',
      })),
    });
    mount();

    expect(await screen.findByTestId('party-guestbook-live-unavailable-game_active'))
      .toHaveTextContent('Il gioco è in TV. Torna prima allo slideshow per mostrare il Guestbook.');
    expect(screen.queryByTestId('party-guestbook-live-show')).not.toBeInTheDocument();
    expect(screen.getByTestId('party-guestbook-live-tv-state')).toHaveTextContent('In TV: il gioco');
  });

  it('says there is nothing to show when no memory is visible, and still lets the room read', async () => {
    installFetchMock({
      [OWNER_GET]: () => jsonResponse(control({
        visibleEntries: 0, availableCommands: ['enable_viewing'], tvUnavailableReason: 'guestbook_empty',
      })),
    });
    mount();

    expect(await screen.findByTestId('party-guestbook-live-unavailable-guestbook_empty'))
      .toHaveTextContent('Nessun ricordo visibile da mostrare in TV.');
    expect(screen.queryByTestId('party-guestbook-live-show')).not.toBeInTheDocument();
    // An empty book may still be opened to the room.
    expect(screen.getByTestId('party-guestbook-live-viewing')).not.toBeDisabled();
  });

  it('follows the projection when it differs from the regia\'s request', async () => {
    // The regia asked for the book, but the game is what the television shows.
    installFetchMock({
      [OWNER_GET]: () => jsonResponse(control({
        tvActive: true, tvPresentation: 'game', availableCommands: ['enable_viewing', 'return_to_slideshow'],
      })),
    });
    mount();

    expect(await screen.findByTestId('party-guestbook-live-tv-state')).toHaveTextContent('In TV: il gioco');
    expect(screen.queryByTestId('party-guestbook-live-on-tv')).not.toBeInTheDocument();
  });

  it('names a game refusal and adopts the state it carries', async () => {
    installFetchMock({
      [OWNER_GET]: () => jsonResponse(control()),
      [OWNER_POST]: () => jsonResponse({
        code: 'game_active',
        control: control({
          version: 4, tvPresentation: 'game', availableCommands: ['enable_viewing'],
          tvUnavailableReason: 'game_active',
        }),
      }, 409),
    });
    mount();

    await userEvent.setup().click(await screen.findByTestId('party-guestbook-live-show'));

    expect(await screen.findByTestId('party-guestbook-live-refusal'))
      .toHaveTextContent('Il gioco è in TV');
    expect(screen.getByTestId('party-guestbook-live-tv-state')).toHaveTextContent('In TV: il gioco');
    expect(screen.queryByTestId('party-guestbook-live-show')).not.toBeInTheDocument();
  });

  it('is not part of a regia with no live party behind the album', async () => {
    const fetchMock = installFetchMock({ [OWNER_GET]: () => jsonResponse({}, 404) });
    mount();

    await waitFor(() => expect(fetchMock.calls).toHaveLength(1));
    await waitFor(() => expect(screen.queryByTestId('party-guestbook-live')).not.toBeInTheDocument());
  });

  it('drives the same commands from a Party Crew device, on its own routes', async () => {
    const at = `/api/party-crew/parties/${PARTY}/guestbook-live`;
    const fetchMock = installFetchMock({
      [`GET ${at}`]: () => jsonResponse(control({ availableCommands: ['show_on_tv'] })),
      [`POST ${at}/commands`]: () => jsonResponse(control({
        version: 4, tvActive: true, tvPresentation: 'guestbook', availableCommands: ['return_to_slideshow'],
      })),
    });
    mount(crewPartyApi(PARTY, [CREW_CAPABILITIES.screensManage]));

    // A director without contributions.moderate: the room's switch is shown
    // as it is, and not offered.
    expect(await screen.findByTestId('party-guestbook-live-viewing')).toBeDisabled();
    await userEvent.setup().click(screen.getByTestId('party-guestbook-live-show'));

    await waitFor(() => expect(screen.getByTestId('party-guestbook-live-tv-state'))
      .toHaveTextContent('Guestbook in TV'));
    expect(fetchMock.calls.every((c) => c.url.startsWith(at))).toBe(true);
  });
});
