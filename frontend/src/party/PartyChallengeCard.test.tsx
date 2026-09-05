import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import type { PartyChallengeKind } from '@nubarca/api-client';
import { I18nProvider } from '../i18n';
import {
  PartyChallengeCard, splitActivityDuration,
  type PartyChallengeCardChallenge, type PartyChallengeCardMode,
} from './PartyChallengeCard';

afterEach(cleanup);

const MODES: PartyChallengeCardMode[] = ['preview', 'tv', 'compact'];
const KINDS: PartyChallengeKind[] = ['dare', 'penalty', 'guess', 'custom'];

const LONG_TITLE = 'Un titolo deliberatamente lunghissimo che nessun host '
  + 'ragionevole scriverebbe ma che il renderer deve comunque reggere senza rompere nulla';
const LONG_BODY = Array.from({ length: 12 }, (_, i) =>
  `Riga ${i + 1} di istruzioni molto dettagliate per questa attività.`).join(' ');

function challenge(over: Partial<PartyChallengeCardChallenge> = {}): PartyChallengeCardChallenge {
  return { kind: 'dare', title: 'Canta una canzone', body: 'Sali sul tavolo.', ...over };
}

function renderCard(props: Parameters<typeof PartyChallengeCard>[0]) {
  return render(<I18nProvider><PartyChallengeCard {...props} /></I18nProvider>);
}

function card() {
  return screen.getByTestId('party-activity-card');
}

describe('the canonical activity card', () => {
  it('renders the same structure in every mode — one renderer, three presentations', () => {
    // The point of the component: the DOM does not branch on mode. If a later
    // change starts emitting different markup per mode, the preview stops being
    // a preview of anything and this fails.
    const shapes = MODES.map((mode) => {
      cleanup();
      renderCard({ challenge: challenge({ mediaUrl: '/m.jpg', durationSeconds: 90 }), mode, voting: 'open' });
      const root = card();
      return {
        mode: root.getAttribute('data-mode'),
        html: root.innerHTML,
      };
    });

    expect(shapes.map((s) => s.mode)).toEqual(MODES);
    expect(new Set(shapes.map((s) => s.html)).size).toBe(1);
  });

  it('names the activity kind in words, so meaning never rests on colour alone', () => {
    const labels: Record<PartyChallengeKind, RegExp> = {
      dare: /^sfida$/i, penalty: /^penitenza$/i, guess: /^indovina$/i, custom: /^attività$/i,
    };
    for (const kind of KINDS) {
      cleanup();
      renderCard({ challenge: challenge({ kind }), mode: 'tv' });
      expect(card()).toHaveAttribute('data-kind', kind);
      expect(within(card()).getByText(labels[kind])).toBeInTheDocument();
    }
  });

  it('drops the media column entirely when there is no photograph', () => {
    renderCard({ challenge: challenge(), mode: 'preview' });
    expect(card()).toHaveAttribute('data-media', 'false');
    expect(within(card()).queryByRole('presentation')).not.toBeInTheDocument();
    expect(card().querySelector('img')).toBeNull();
  });

  it('renders a photograph decoratively — the title is the accessible name', () => {
    renderCard({ challenge: challenge({ mediaUrl: '/api/files/1/thumbnail?size=medium' }), mode: 'tv' });
    expect(card()).toHaveAttribute('data-media', 'true');
    const image = card().querySelector('img')!;
    expect(image).toHaveAttribute('src', '/api/files/1/thumbnail?size=medium');
    // Empty alt, not a filename and not a description nobody could write.
    expect(image).toHaveAttribute('alt', '');
  });

  it('accepts every copy length without changing shape', () => {
    for (const [title, body] of [
      ['Ok', 'Vai.'],
      [LONG_TITLE, 'Vai.'],
      ['Ok', LONG_BODY],
      [LONG_TITLE, LONG_BODY],
    ] as const) {
      cleanup();
      renderCard({ challenge: challenge({ title, body, mediaUrl: '/m.jpg' }), mode: 'tv' });
      // The text is present in full — clamping is a CSS concern, so nothing is
      // truncated in the DOM and a screen reader still gets everything.
      expect(card().querySelector('.party-activity-title')).toHaveTextContent(title);
      expect(card().querySelector('.party-activity-body')).toHaveTextContent(body.slice(0, 40));
      expect(card().querySelectorAll('.party-activity-card')).toHaveLength(1);
    }
  });

  it('shows a placeholder title while the composer is still empty', () => {
    renderCard({
      challenge: challenge({ title: '   ' }), mode: 'preview', titlePlaceholder: 'Titolo',
    });
    expect(card().querySelector('.party-activity-title')).toHaveTextContent('Titolo');
  });

  it('omits the body paragraph rather than rendering an empty one', () => {
    renderCard({ challenge: challenge({ body: '  ' }), mode: 'preview' });
    expect(card().querySelector('.party-activity-body')).toBeNull();
  });

  it('shows the round context only when it is given', () => {
    renderCard({ challenge: challenge(), mode: 'tv', context: { round: 2, total: 6 } });
    expect(within(card()).getByText('Attività 2 di 6')).toBeInTheDocument();

    cleanup();
    renderCard({ challenge: challenge(), mode: 'tv' });
    expect(card().querySelector('.party-activity-context')).toBeNull();
  });

  it('shows voting state as a word, with the state on the element', () => {
    renderCard({ challenge: challenge(), mode: 'tv', voting: 'open' });
    const open = card().querySelector('.party-activity-voting')!;
    expect(open).toHaveTextContent('Votazione aperta');
    expect(open).toHaveAttribute('data-state', 'open');

    cleanup();
    renderCard({ challenge: challenge(), mode: 'tv', voting: 'closed' });
    expect(card().querySelector('.party-activity-voting')).toHaveTextContent('Votazione chiusa');
  });

  it('renders no meta row at all when there is nothing to put in it', () => {
    renderCard({ challenge: challenge(), mode: 'tv' });
    expect(card().querySelector('.party-activity-meta')).toBeNull();
  });

  it('states a duration in the units a host thinks in', () => {
    for (const [seconds, text] of [
      [1, '1 secondo'], [45, '45 secondi'], [60, '1 minuto'],
      [120, '2 minuti'], [90, '1 minuto 30 secondi'],
    ] as const) {
      cleanup();
      renderCard({ challenge: challenge({ durationSeconds: seconds }), mode: 'tv' });
      expect(card().querySelector('.party-activity-duration')).toHaveTextContent(text);
    }
  });

  it('prints no duration rather than a nonsense one', () => {
    for (const value of [null, undefined, 0, -30, Number.NaN]) {
      cleanup();
      renderCard({ challenge: challenge({ durationSeconds: value }), mode: 'tv' });
      expect(card().querySelector('.party-activity-duration')).toBeNull();
    }
  });
});

describe('splitActivityDuration', () => {
  it('splits whole minutes and seconds', () => {
    expect(splitActivityDuration(45)).toEqual({ minutes: 0, seconds: 45 });
    expect(splitActivityDuration(60)).toEqual({ minutes: 1, seconds: 0 });
    expect(splitActivityDuration(3661)).toEqual({ minutes: 61, seconds: 1 });
  });

  it('rejects anything that is not a positive finite number', () => {
    for (const value of [null, undefined, 0, -1, Number.NaN, Number.POSITIVE_INFINITY]) {
      expect(splitActivityDuration(value)).toBeNull();
    }
  });
});
