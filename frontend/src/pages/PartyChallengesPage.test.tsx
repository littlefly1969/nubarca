import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { I18nProvider } from '../i18n';
import { installFetchMock, jsonResponse } from '../test-utils';
import { PartyChallengesPage } from './PartyChallengesPage';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

function page() {
  return <I18nProvider><MemoryRouter initialEntries={['/party/tok/challenges']}><Routes>
    <Route path="/party/:token/challenges" element={<PartyChallengesPage />} />
  </Routes></MemoryRouter></I18nProvider>;
}

describe('PartyChallengesPage', () => {
  it('shows no public ranking, enforces the displayed budget and can unvote', async () => {
    const mock = installFetchMock({
      'GET /api/party/tok/challenges': () => jsonResponse({
        albumName: 'Festa', votesPerGuest: 1, votesUsed: 1, votesRemaining: 0,
        items: [
          { id: 'c1', title: 'Canta', body: 'Una canzone', kind: 'dare', mediaUrl: null, voted: true },
          { id: 'c2', title: 'Balla', body: 'Per un minuto', kind: 'custom', mediaUrl: null, voted: false },
        ],
      }),
      'DELETE /api/party/tok/challenges/c1/vote': () => jsonResponse({
        voted: false, votesUsed: 0, votesRemaining: 1,
      }),
    });
    render(page());
    expect(await screen.findByText('Canta')).toBeInTheDocument();
    expect(screen.queryByText(/classifica|ranking|2 voti/i)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Vota' })).toBeDisabled();
    await userEvent.setup().click(screen.getByRole('button', { name: 'Rimuovi voto' }));
    expect(await screen.findByText(/^1$/)).toBeInTheDocument();
    expect(mock.calls.some((x) => x.method === 'DELETE' && x.url.endsWith('/c1/vote'))).toBe(true);
    expect(screen.getAllByRole('button', { name: 'Vota' }).every((button) => !button.hasAttribute('disabled'))).toBe(true);
  });
});
