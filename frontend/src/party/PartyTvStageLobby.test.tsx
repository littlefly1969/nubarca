import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { I18nProvider } from '../i18n';
import { PartyTvStage } from './PartyTvStage';

// The lobby, composed for a television: the words from the top of the safe
// area at the sizes every scene uses, then the join code taking all the height
// they leave.
//
// jsdom has no layout engine, so what can be proved here is the STRUCTURE and
// the CSS CONTRACT that produces the layout. The layout itself — bounding boxes
// inside the safe area, no clipping, the code's real size — is measured in a
// real browser by scripts/check-party-stage-layout.mjs at 960x540, 1280x720,
// 1920x1080 and the smaller 16:9 viewports (see docs/testing.md).

const here = dirname(fileURLToPath(import.meta.url));
const css = readFileSync(resolve(here, 'PartyTvStage.css'), 'utf8');
const publicPage = readFileSync(resolve(here, '../pages/PartyTvStagePage.tsx'), 'utf8');

/** The declarations of the rule whose selector is exactly `selector`. */
function rule(selector: string): string {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = new RegExp(`(?:^|\\n|\\})\\s*${escaped}\\s*\\{([^}]*)\\}`).exec(css);
  if (!match) throw new Error(`no rule for ${selector}`);
  return match[1];
}

const lobby = {
  albumName: 'Festa', status: 'lobby', phase: 'lobby', version: 0,
  roundNumber: 0, totalChallenges: 3, phaseEndsAt: null, challenge: null,
  roundId: null, voting: null, myVote: null,
} as const;

function mountLobby(qr: string | null) {
  render(
    <I18nProvider>
      <PartyTvStage snapshot={lobby as never} connection="ready" stale={false} lobbyQr={qr} />
    </I18nProvider>,
  );
}

afterEach(() => cleanup());

describe('the lobby on a television', () => {
  it('is the words, top to bottom, then the code — and nothing else', () => {
    mountLobby('<svg viewBox="0 0 41 41"><rect width="41" height="41" fill="#fff"/></svg>');
    const container = screen.getByTestId('party-stage-lobby');
    expect(Array.from(container.children).map((child) => child.className)).toEqual([
      'party-stage-eyebrow', 'party-stage-headline', 'party-stage-sub', 'party-stage-qr',
    ]);
    // Not the centred column every other scene uses.
    expect(container.classList.contains('party-stage-centre')).toBe(false);
    expect(screen.getByTestId('party-stage-qr').querySelector('svg')).not.toBeNull();
  });

  it('keeps the words where they are while the code is still on its way', () => {
    mountLobby(null);
    const container = screen.getByTestId('party-stage-lobby');
    expect(Array.from(container.children).map((child) => child.className)).toEqual([
      'party-stage-eyebrow', 'party-stage-headline', 'party-stage-sub',
    ]);
  });
});

describe('the lobby CSS contract', () => {
  it('keeps every approved text size exactly as it was', () => {
    expect(rule('.party-stage-eyebrow')).toContain('font-size: clamp(1rem, 2.4vh, 2.2rem)');
    expect(rule('.party-stage-headline')).toContain('font-size: clamp(2rem, 8vh, 7rem)');
    expect(rule('.party-stage-sub')).toContain('font-size: clamp(1.1rem, 3.2vh, 2.6rem)');
    // The lobby only stops its words from flexing. It sizes none of them.
    const lobbyText = /\.party-stage-lobby > \.party-stage-eyebrow,[^{]*\{([^}]*)\}/.exec(css);
    expect(lobbyText?.[1].trim()).toBe('flex: none;');
    expect(css).not.toMatch(/\.party-stage-lobby[^{]*\{[^}]*font-size/);
  });

  it('starts at the top of the safe area, and the code takes what is left', () => {
    const container = rule('.party-stage-lobby');
    expect(container).toContain('height: 100%');
    expect(container).toContain('flex-direction: column');
    expect(container).toContain('justify-content: flex-start');

    const code = rule('.party-stage-qr');
    // A zero basis and a zero minimum: exactly the remaining height, never more.
    expect(code).toContain('flex: 1 1 0');
    expect(code).toContain('min-height: 0');
    expect(code).toContain('width: 100%');
    const picture = rule('.party-stage-qr svg');
    expect(picture).toContain('width: 100%');
    expect(picture).toContain('height: 100%');

    // The fixed slice of screen the code used to be confined to is gone, and
    // so is the frame that sat around a fixed-size image.
    expect(css).not.toMatch(/22vh/);
    expect(code).not.toMatch(/aspect-ratio|padding|background/);
  });

  it('keeps the overscan safe area and the no-scroll rule untouched', () => {
    const stage = rule('.party-stage');
    expect(stage).toContain('--stage-inset-x: max(3.5vw, 1.5rem)');
    expect(stage).toContain('--stage-inset-y: max(3.5vh, 1rem)');
    expect(stage).toContain('padding: var(--stage-inset-y) var(--stage-inset-x)');
    expect(stage).toContain('overflow: hidden');
  });

  it('draws the quiet zone inside the picture on the public route too', () => {
    // The paired display's server-built code carries four modules of quiet
    // zone (backend test); the public route's must match now that no padded
    // frame surrounds it.
    expect(publicPage).toMatch(/type: 'svg', margin: 4/);
  });
});
