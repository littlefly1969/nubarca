import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { PartyChallenge } from '@nubarca/api-client';
import { I18nProvider } from '../i18n';
import { installFetchMock, jsonResponse } from '../test-utils';
import { PartyChallengeManager } from './PartyChallengeManager';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const ALBUM = 'a1';
const DECK = `/api/albums/${ALBUM}/party-challenges`;
const ITEMS = `/api/albums/${ALBUM}/items`;

function challenge(over: Partial<PartyChallenge> = {}): PartyChallenge {
  return {
    id: 'c1', title: 'Canta', body: 'Sali sul tavolo.', kind: 'dare',
    mediaFileItemId: null, mediaUrl: null, isEnabled: true, sortOrder: 0, voteCount: 0,
    createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
    durationSeconds: null, votingMode: 'binary', voteQuestion: null, ...over,
  };
}

function media(count: number) {
  return Array.from({ length: count }, (_, i) => ({
    fileItemId: `f${i}`, name: `IMG_000${i}.jpg`,
    thumbnailUrl: `/api/files/f${i}/thumbnail?size=small`, sortOrder: i,
  }));
}

function mount(items: PartyChallenge[], photos = 0, extra: Record<string, unknown> = {}) {
  const mock = installFetchMock({
    [`GET ${DECK}`]: () => jsonResponse({ albumId: ALBUM, items }),
    [`GET ${ITEMS}`]: () => jsonResponse(media(photos)),
    ...extra,
  } as Parameters<typeof installFetchMock>[0]);
  render(<I18nProvider><PartyChallengeManager albumId={ALBUM} /></I18nProvider>);
  return mock;
}

async function openComposer(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByTestId('party-deck-add'));
  return screen.getByTestId('party-composer');
}

describe('the activity composer', () => {
  it('walks three steps and cannot leave the first one empty', async () => {
    mount([]);
    const user = userEvent.setup();
    const composer = await openComposer(user);

    // Step 1 is where the activity is written, and "Next" is dead until it is.
    const next = within(composer).getByRole('button', { name: /avanti/i });
    expect(next).toBeDisabled();

    await user.type(within(composer).getByLabelText(/titolo/i), 'Canta');
    expect(next).toBeDisabled();
    await user.type(within(composer).getByLabelText(/cosa deve fare/i), 'Sali sul tavolo.');
    expect(next).toBeEnabled();

    await user.click(next);
    expect(within(composer).getByText(/come decide la sala/i)).toBeInTheDocument();

    await user.click(within(composer).getByRole('button', { name: /avanti/i }));
    expect(within(composer).getByTestId('party-composer-preview')).toBeInTheDocument();
  });

  it('previews with THE renderer, so the host approves what the television shows', async () => {
    mount([]);
    const user = userEvent.setup();
    const composer = await openComposer(user);
    await user.type(within(composer).getByLabelText(/titolo/i), 'Canta');
    await user.type(within(composer).getByLabelText(/cosa deve fare/i), 'Sali sul tavolo.');
    await user.click(within(composer).getByRole('button', { name: /avanti/i }));
    await user.click(within(composer).getByRole('button', { name: /avanti/i }));

    const preview = within(composer).getByTestId('party-composer-preview');
    // The canonical card, in preview mode, with the round context the TV uses.
    expect(preview).toHaveAttribute('data-mode', 'preview');
    expect(preview).toHaveAttribute('data-kind', 'dare');
    expect(within(preview).getByText('Canta')).toBeInTheDocument();
    expect(within(preview).getByText(/attività 1 di 1/i)).toBeInTheDocument();
  });

  it('writes the rules as fields, never inside the instructions', async () => {
    const posted: string[] = [];
    mount([], 0, {
      [`POST ${DECK}`]: ({ body }: { body: string | null }) => {
        posted.push(body ?? '');
        return jsonResponse(challenge(), 201);
      },
    });
    const user = userEvent.setup();
    const composer = await openComposer(user);

    await user.type(within(composer).getByLabelText(/titolo/i), 'Canta');
    await user.type(within(composer).getByLabelText(/cosa deve fare/i), 'Sali sul tavolo.');
    await user.click(within(composer).getByRole('button', { name: /avanti/i }));

    await user.click(within(composer).getByRole('button', { name: '2 min' }));
    await user.type(within(composer).getByLabelText(/la domanda per gli invitati/i), 'Ce l’ha fatta?');
    await user.click(within(composer).getByRole('button', { name: /avanti/i }));
    await user.click(within(composer).getByTestId('party-composer-save'));

    await waitFor(() => expect(posted).toHaveLength(1));
    const sent = JSON.parse(posted[0]);
    expect(sent).toMatchObject({
      title: 'Canta', body: 'Sali sul tavolo.', kind: 'dare',
      durationSeconds: 120, votingMode: 'binary', voteQuestion: 'Ce l’ha fatta?',
    });
    // The instructions carry the instructions and nothing else.
    expect(sent.body).toBe('Sali sul tavolo.');
  });

  it('hides the vote question when nobody is voting', async () => {
    mount([]);
    const user = userEvent.setup();
    const composer = await openComposer(user);
    await user.type(within(composer).getByLabelText(/titolo/i), 'Brindisi');
    await user.type(within(composer).getByLabelText(/cosa deve fare/i), 'Alza il calice.');
    await user.click(within(composer).getByRole('button', { name: /avanti/i }));

    expect(within(composer).getByLabelText(/la domanda per gli invitati/i)).toBeInTheDocument();
    await user.click(within(composer).getByRole('radio', { name: /nessun voto/i }));
    expect(within(composer).queryByLabelText(/la domanda per gli invitati/i)).not.toBeInTheDocument();
  });

  it('picks a photograph from the album, not from a list of filenames', async () => {
    mount([], 3);
    const user = userEvent.setup();
    const composer = await openComposer(user);

    const picker = await within(composer).findByTestId('party-photo-picker');
    const choices = within(picker).getAllByRole('button');
    // "No photo" plus one per album member, each showing the picture itself.
    expect(choices).toHaveLength(4);
    expect(picker.querySelectorAll('img')).toHaveLength(3);

    await user.click(within(picker).getByRole('button', { name: 'IMG_0001.jpg' }));
    expect(within(picker).getByRole('button', { name: 'IMG_0001.jpg' })).toHaveAttribute('aria-pressed', 'true');
  });

  it('loads an existing activity without losing anything it already had', async () => {
    const existing = challenge({
      title: 'Ballo', body: 'Tre minuti di liscio.', kind: 'penalty',
      mediaFileItemId: 'f1', mediaUrl: '/api/files/f1/thumbnail?size=medium',
      durationSeconds: 60, votingMode: 'none', voteQuestion: null, isEnabled: false,
    });
    mount([existing], 3);
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: /modifica/i }));
    const composer = screen.getByTestId('party-composer');

    expect(within(composer).getByLabelText(/titolo/i)).toHaveValue('Ballo');
    expect(within(composer).getByLabelText(/cosa deve fare/i)).toHaveValue('Tre minuti di liscio.');
    expect(within(composer).getByRole('radio', { name: /penitenza/i })).toHaveAttribute('aria-checked', 'true');
    expect(within(composer).getByRole('button', { name: 'IMG_0001.jpg' })).toHaveAttribute('aria-pressed', 'true');

    await user.click(within(composer).getByRole('button', { name: /avanti/i }));
    expect(within(composer).getByRole('button', { name: '1 min' })).toHaveAttribute('aria-pressed', 'true');
    expect(within(composer).getByRole('radio', { name: /nessun voto/i })).toHaveAttribute('aria-checked', 'true');
  });

  it('refuses to throw away unsaved work on a stray Escape', async () => {
    mount([]);
    const user = userEvent.setup();
    const composer = await openComposer(user);
    await user.type(within(composer).getByLabelText(/titolo/i), 'Canta');

    await user.keyboard('{Escape}');
    expect(screen.getByTestId('party-composer')).toBeInTheDocument();

    // Closing deliberately asks once, in place, and the destructive answer is
    // marked as destructive.
    await user.click(screen.getByTestId('party-composer-close'));
    expect(screen.getByText(/modifiche non salvate/i)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: /continua a modificare/i }));
    expect(screen.getByTestId('party-composer')).toBeInTheDocument();

    await user.click(screen.getByTestId('party-composer-close'));
    await user.click(screen.getByRole('button', { name: /scarta le modifiche/i }));
    await waitFor(() => expect(screen.queryByTestId('party-composer')).not.toBeInTheDocument());
  });

  it('closes without a question when nothing was touched', async () => {
    mount([]);
    const user = userEvent.setup();
    await openComposer(user);
    await user.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByTestId('party-composer')).not.toBeInTheDocument());
  });

  it('takes a new picture through the library upload without adding it to the album', async () => {
    const created: { mediaFileItemId: string | null }[] = [];
    const mock = mount([], 2, {
      'POST /api/files': () => jsonResponse({
        id: 'up1', name: 'poster.png', mimeType: 'image/png', sizeBytes: 10,
        createdAt: '2026-09-10T10:00:00Z',
      }),
      [`POST ${DECK}`]: ({ body }: { body: string | null }) => {
        created.push(JSON.parse(body!));
        return jsonResponse(challenge({ id: 'c9', mediaFileItemId: 'up1' }));
      },
    });
    const user = userEvent.setup();
    const composer = await openComposer(user);
    await user.type(within(composer).getByLabelText(/titolo/i), 'Canta');
    await user.type(within(composer).getByLabelText(/cosa deve fare/i), 'Sali sul tavolo.');

    fireEvent.change(within(composer).getByTestId('party-photo-upload-input'), {
      target: { files: [new File(['x'], 'poster.png', { type: 'image/png' })] },
    });
    // The uploaded picture is chosen, beside — not inside — the album's own.
    expect(await within(composer).findByTestId('party-photo-extra'))
      .toHaveAttribute('aria-pressed', 'true');

    await user.click(within(composer).getByRole('button', { name: /avanti/i }));
    await user.click(within(composer).getByRole('button', { name: /avanti/i }));
    expect(within(composer).getByTestId('party-composer-preview')
      .querySelector('img[src="/api/files/up1/thumbnail?size=small"]')).not.toBeNull();
    await user.click(within(composer).getByTestId('party-composer-save'));

    await waitFor(() => expect(created).toHaveLength(1));
    expect(created[0].mediaFileItemId).toBe('up1');
    expect(mock.calls.some((c) => c.method === 'POST' && /\/api\/albums\/[^/]+\/items/.test(c.url)))
      .toBe(false);
  });

  it('keeps showing an activity’s picture that is not one of the album’s', async () => {
    mount([challenge({ mediaFileItemId: 'x1', mediaUrl: '/api/files/x1/thumbnail?size=medium' })], 2);
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: /modifica/i }));

    const extra = within(screen.getByTestId('party-composer')).getByTestId('party-photo-extra');
    expect(extra).toHaveAttribute('aria-pressed', 'true');
    expect(extra.querySelector('img')).toHaveAttribute('src', '/api/files/x1/thumbnail?size=medium');
  });
});

describe('the activity deck', () => {
  it('renders each activity with the canonical card and says which are off', async () => {
    mount([
      challenge({ id: 'c1', title: 'Canta' }),
      challenge({ id: 'c2', title: 'Ballo', isEnabled: false, sortOrder: 1 }),
    ]);
    expect(await screen.findByTestId('party-deck-item-c1')).toHaveAttribute('data-mode', 'compact');
    const off = screen.getByTestId('party-deck-item-c2').closest('li')!;
    expect(within(off).getByText('disattivata')).toBeInTheDocument();
  });

  it('confirms a deletion in the product, not in a browser dialog', async () => {
    const deleted: string[] = [];
    const confirmSpy = vi.fn(() => true);
    vi.stubGlobal('confirm', confirmSpy);
    mount([challenge({ title: 'Canta' })], 0, {
      [`DELETE ${DECK}/c1`]: () => { deleted.push('c1'); return jsonResponse(null, 204); },
    });
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: /elimina/i }));
    const dialog = screen.getByTestId('party-deck-delete');
    // It names what is being lost, which window.confirm could never do.
    expect(within(dialog).getByText(/«Canta»/)).toBeInTheDocument();
    expect(confirmSpy).not.toHaveBeenCalled();

    await user.click(screen.getByTestId('party-deck-delete-confirm'));
    await waitFor(() => expect(deleted).toEqual(['c1']));
  });

  it('leaves the activity alone when the deletion is cancelled', async () => {
    mount([challenge({ title: 'Canta' })]);
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: /elimina/i }));
    await user.click(within(screen.getByTestId('party-deck-delete')).getByRole('button', { name: /annulla/i }));
    await waitFor(() => expect(screen.queryByTestId('party-deck-delete')).not.toBeInTheDocument());
    expect(screen.getByTestId('party-deck-item-c1')).toBeInTheDocument();
  });
});
