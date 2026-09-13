import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { I18nProvider } from '../i18n';
import { PartyChallengesPage } from './PartyChallengesPage';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

function page(initial = '/party/tok/challenges') {
  return <I18nProvider><MemoryRouter initialEntries={[initial]}><Routes>
    <Route path="/party/:token/challenges" element={<PartyChallengesPage />} />
    <Route path="/party/:token/game" element={<p>il game</p>} />
    <Route path="/" element={<p>fuori</p>} />
  </Routes></MemoryRouter></I18nProvider>;
}

describe('the retired second vote', () => {
  it('sends the old challenge route into the one Party Game', async () => {
    // There used to be TWO entries in the guest hub — "Game" and "Vote the
    // challenges" — which were two different votes wearing one word. There is
    // now one game, and the lobby is where a guest says which activities they
    // would like to see. This route survives only for printed material and a
    // guest's own history.
    render(page());
    expect(await screen.findByText('il game')).toBeInTheDocument();
  });

  it('asks the server for nothing on its way there', () => {
    // A redirect is a redirect: no list to fetch, no budget to read, and no
    // vote endpoint touched. A stub that throws proves it rather than a mock
    // that merely was not called.
    vi.stubGlobal('fetch', () => { throw new Error('the redirect fetched something'); });
    render(page());
    expect(screen.getByText('il game')).toBeInTheDocument();
  });
});
