import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { PartyGuestContentSlot, PartyGuestContentView } from '@nubarca/api-client';
import { I18nProvider } from '../i18n';
import { installFetchMock, jsonResponse } from '../test-utils';
import { PartyContentCard } from './PartyContentEditors';
import { PartyGuestContentSections, partyThankYou } from './PartyGuestContent';

// P5, from both ends: the host CHOOSES how a photograph is presented, and the
// guest surface renders one of exactly two things because of it.
//
// The thread running through all of it is that this is a presentation choice
// and nothing else. It never invents a label, never exposes a download, and
// never destroys a word — switching to poster hides the typed text from the
// guest while leaving it in the draft the host saves.

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const PARTY = 'p1';
const ALBUM = 'a1';

function slot(over: Partial<PartyGuestContentSlot> = {}): PartyGuestContentSlot {
  return {
    kind: 'menu', enabled: true, visibleBefore: true, visibleLive: true, visibleAfter: false,
    content: { intro: 'Cena in giardino', sections: [] }, version: 1,
    mediaFileItemId: null, mediaUrl: null, mediaPresentation: 'inline', ...over,
  };
}

function view(over: Partial<PartyGuestContentView> = {}): PartyGuestContentView {
  return {
    kind: 'menu', enabled: true, visibleBefore: true, visibleLive: true, visibleAfter: false,
    content: { intro: 'Cena in giardino', sections: [] }, version: 1,
    mediaUrl: null, mediaPresentation: 'inline', ...over,
  };
}

const sentBody = (mock: ReturnType<typeof installFetchMock>) =>
  JSON.parse(mock.calls.find((c) => c.method === 'PUT')!.body!);

function mountOwner(initial: PartyGuestContentSlot) {
  const onSaved = vi.fn();
  const mock = installFetchMock({
    [`GET /api/albums/${ALBUM}/items`]: () => jsonResponse([]),
    [`PUT /api/parties/${PARTY}/guest-content/menu`]: ({ body }) =>
      jsonResponse({ ...initial, ...JSON.parse(body!), version: initial.version + 1 }),
  });
  render(
    <I18nProvider>
      <PartyContentCard
        slot={initial} partyId={PARTY} albumId={ALBUM}
        phases={['before', 'live']} onSaved={onSaved}
      />
    </I18nProvider>,
  );
  return { onSaved, mock };
}

function mountGuest(
  slots: PartyGuestContentView[], onOpenPoster = vi.fn(),
) {
  render(
    <I18nProvider>
      <PartyGuestContentSections slots={slots} onOpenPoster={onOpenPoster} />
    </I18nProvider>,
  );
  return { onOpenPoster };
}

describe('owner: choosing how the photograph is presented', () => {
  it('offers no choice until there is a photograph to present', () => {
    // There is nothing to decide about an image that is not there — and the
    // server would refuse `poster` with no reference anyway.
    mountOwner(slot({ mediaFileItemId: null }));
    expect(screen.queryByTestId('party-presentation-menu')).not.toBeInTheDocument();
  });

  it('offers the choice once a photograph exists, defaulting to in-page', () => {
    mountOwner(slot({ mediaFileItemId: 'f1', mediaUrl: '/api/files/f1/thumbnail?size=medium' }));
    expect(screen.getByTestId('party-presentation-menu')).toBeInTheDocument();
    expect(screen.getByTestId('party-presentation-menu-inline')).toBeChecked();
    expect(screen.getByTestId('party-presentation-menu-poster')).not.toBeChecked();
  });

  it('names the row the guest will actually see, from the product label', () => {
    // No `posterTitle`, no `customCta`: the host reads the same words the guest
    // surface renders, because both come from the kind.
    mountOwner(slot({ mediaFileItemId: 'f1', mediaUrl: '/x' }));
    expect(screen.getByText(/compare .Men/i)).toBeInTheDocument();
  });

  it('sends the chosen presentation with the rest of the card', async () => {
    const user = userEvent.setup();
    const { mock } = mountOwner(slot({ mediaFileItemId: 'f1', mediaUrl: '/x' }));

    await user.click(screen.getByTestId('party-presentation-menu-poster'));
    await user.click(screen.getByTestId('party-content-save-menu'));

    const body = sentBody(mock);
    expect(body.mediaPresentation).toBe('poster');
    // The words are still sent: a presentation change is not a deletion.
    expect(body.content.intro).toBe('Cena in giardino');
    expect(body.mediaFileItemId).toBe('f1');
  });

  it('says the text is kept, because that is the whole worry', async () => {
    const user = userEvent.setup();
    mountOwner(slot({ mediaFileItemId: 'f1', mediaUrl: '/x' }));
    await user.click(screen.getByTestId('party-presentation-menu-poster'));
    expect(screen.getByText(/resta salvato/i)).toBeInTheDocument();
  });

  it('removing the photograph takes the choice back to in-page', async () => {
    const user = userEvent.setup();
    const { mock } = mountOwner(slot({
      mediaFileItemId: 'f1', mediaUrl: '/x', mediaPresentation: 'poster',
    }));
    expect(screen.getByTestId('party-presentation-menu-poster')).toBeChecked();

    await user.click(screen.getByRole('button', { name: /rimuovi/i }));

    // The selector goes with the photograph…
    expect(screen.queryByTestId('party-presentation-menu')).not.toBeInTheDocument();
    await user.click(screen.getByTestId('party-content-save-menu'));
    // …and what is SAVED is inline with no reference, which is the one
    // combination the server accepts after a removal.
    const body = sentBody(mock);
    expect(body.mediaFileItemId).toBeNull();
    expect(body.mediaPresentation).toBe('inline');
  });
});

describe('owner: a poster whose photograph was permanently deleted', () => {
  // ON DELETE SET NULL makes this state reachable and legitimate. The editor
  // used to hide the selector (no id) while still sending `poster` + null, which
  // the server refused — leaving the slot unsaveable until a new photo was
  // chosen. It is now stated, and offers both ways out.
  const lost = () => slot({
    mediaFileItemId: null, mediaUrl: null, mediaPresentation: 'poster',
  });

  it('says so, instead of silently offering nothing', () => {
    mountOwner(lost());
    expect(screen.getByTestId('party-poster-lost-menu')).toBeInTheDocument();
    expect(screen.getByText(/non è più disponibile/i)).toBeInTheDocument();
  });

  it('does not show the choice, because there is nothing to choose about', () => {
    mountOwner(lost());
    expect(screen.queryByTestId('party-presentation-menu')).not.toBeInTheDocument();
  });

  it('can still save other edits with the broken state untouched', async () => {
    // The whole point: a host must be able to fix a typo while the picture is
    // gone. The server tolerates a state the row is already in.
    const user = userEvent.setup();
    const { mock } = mountOwner(lost());

    await user.click(screen.getByTestId('party-content-save-menu'));

    const body = sentBody(mock);
    expect(body.mediaPresentation).toBe('poster');
    expect(body.mediaFileItemId).toBeNull();
  });

  it('offers the way back to in-page', async () => {
    const user = userEvent.setup();
    const { mock } = mountOwner(lost());

    await user.click(screen.getByTestId('party-poster-lost-inline-menu'));
    await user.click(screen.getByTestId('party-content-save-menu'));

    const body = sentBody(mock);
    expect(body.mediaPresentation).toBe('inline');
    expect(body.mediaFileItemId).toBeNull();
  });

  it('shows no recovery notice for an ordinary inline slot with no photo', () => {
    mountOwner(slot({ mediaFileItemId: null, mediaPresentation: 'inline' }));
    expect(screen.queryByTestId('party-poster-lost-menu')).not.toBeInTheDocument();
  });
});

describe('guest: inline versus poster', () => {
  it('renders an inline menu exactly as P4 did — photo, then the words', () => {
    mountGuest([view({ mediaUrl: '/api/party/t/content/menu/media?v=1' })]);
    expect(screen.getByTestId('party-content-media'))
      .toHaveAttribute('src', '/api/party/t/content/menu/media?v=1');
    expect(screen.getByText('Cena in giardino')).toBeInTheDocument();
    expect(screen.queryByTestId('party-poster-open-menu')).not.toBeInTheDocument();
  });

  it('renders a poster as a navigation row, with no inline content at all', () => {
    mountGuest([view({
      mediaPresentation: 'poster', mediaUrl: '/api/party/t/content/menu/media?v=1',
    })]);
    expect(screen.getByTestId('party-poster-open-menu')).toBeInTheDocument();
    // The photograph is not drawn in the page, and neither are the words.
    expect(screen.queryByTestId('party-content-media')).not.toBeInTheDocument();
    expect(screen.queryByText('Cena in giardino')).not.toBeInTheDocument();
  });

  it('labels the row from the kind, never from the payload', () => {
    // `info` carries a title of its own; the row still reads as the product's
    // own word for the kind. One configuration fewer, and rows that read alike.
    mountGuest([view({
      kind: 'info',
      content: { title: 'Parcheggio segreto', body: 'dietro la chiesa' },
      mediaPresentation: 'poster',
      mediaUrl: '/m',
    })]);
    const row = screen.getByTestId('party-poster-open-info');
    expect(row).toHaveTextContent(/Informazioni/i);
    expect(row).not.toHaveTextContent(/Parcheggio segreto/i);
  });

  it('opens the poster through the caller, by kind', async () => {
    const user = userEvent.setup();
    const { onOpenPoster } = mountGuest([view({ mediaPresentation: 'poster', mediaUrl: '/m' })]);
    await user.click(screen.getByTestId('party-poster-open-menu'));
    expect(onOpenPoster).toHaveBeenCalledWith('menu');
  });

  it('offers nothing when a poster photograph stopped being servable', () => {
    // No broken image, no dead CTA — and the hidden words are NOT revealed:
    // poster stays the host's choice until they change it.
    mountGuest([view({ mediaPresentation: 'poster', mediaUrl: null })]);
    expect(screen.queryByTestId('party-poster-open-menu')).not.toBeInTheDocument();
    expect(screen.queryByText('Cena in giardino')).not.toBeInTheDocument();
  });

  it('applies to every kind, not just the menu', () => {
    mountGuest([
      view({ kind: 'location', content: {}, mediaPresentation: 'poster', mediaUrl: '/a' }),
      view({ kind: 'dress-code', content: {}, mediaPresentation: 'poster', mediaUrl: '/b' }),
      view({ kind: 'menu', mediaPresentation: 'poster', mediaUrl: '/c' }),
    ]);
    expect(screen.getByTestId('party-poster-open-location')).toHaveTextContent(/Dove/i);
    expect(screen.getByTestId('party-poster-open-dress-code')).toHaveTextContent(/Dress code/i);
    expect(screen.getByTestId('party-poster-open-menu')).toHaveTextContent(/Men/i);
  });
});

describe('guest: the thank-you', () => {
  it('an inline thank-you still supplies the After hero', () => {
    const result = partyThankYou([view({
      kind: 'thank-you',
      content: { headline: 'Grazie!', message: 'È stata una serata bellissima' },
      mediaUrl: '/thanks.jpg',
    })]);
    expect(result).toEqual({
      headline: 'Grazie!',
      message: 'È stata una serata bellissima',
      mediaUrl: '/thanks.jpg',
    });
  });

  it('a poster thank-you contributes neither photograph nor words to the hero', () => {
    // The hero falls back to the product's greeting; the picture is a document
    // opened from its own row, not a band cropped across the top of the page.
    const result = partyThankYou([view({
      kind: 'thank-you',
      content: { headline: 'Grazie!', message: 'È stata una serata bellissima' },
      mediaPresentation: 'poster',
      mediaUrl: '/thanks.jpg',
    })]);
    expect(result).toEqual({ headline: null, message: null, mediaUrl: null });
  });

  it('a poster thank-you is offered as its own row', () => {
    mountGuest([view({
      kind: 'thank-you', content: { headline: 'Grazie!' },
      mediaPresentation: 'poster', mediaUrl: '/thanks.jpg',
    })]);
    expect(screen.getByTestId('party-poster-open-thank-you')).toHaveTextContent(/Ringraziamento/i);
  });
});
