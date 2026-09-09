import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { PERMISSIONS } from '@nubarca/api-client';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../test-utils';
import { buildNavGroups } from '../components/nav/navModel';
import { PartiesPage } from './PartiesPage';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const party = (over: Record<string, unknown> = {}) => ({
  id: 'p1', title: 'Festa di Marta', status: 'draft',
  eventStartsAt: null, liveStartedAt: null, liveEndedAt: null,
  updatedAt: '2027-01-01T00:00:00Z', mainAlbumId: null, mainAlbumName: null,
  ...over,
});

function page(permissions?: readonly string[]) {
  return (
    <AuthedWrapper permissions={permissions}>
      <MemoryRouter initialEntries={['/parties']}>
        <Routes>
          <Route path="/parties" element={<PartiesPage />} />
          <Route path="/parties/:partyId" element={<p>workspace</p>} />
        </Routes>
      </MemoryRouter>
    </AuthedWrapper>
  );
}

describe('PartiesPage', () => {
  it('is absent from the navigation without party.access', () => {
    // Absent, never disabled: a door nobody may open is not drawn. The server
    // refuses the API independently, and a direct URL still meets the route
    // guard — this is only about what is offered.
    const withParty = buildNavGroups({ permissions: [PERMISSIONS.partyAccess] });
    const without = buildNavGroups({ permissions: [] });

    expect(withParty.flatMap((g) => g.items).some((i) => i.to === '/parties')).toBe(true);
    expect(without.flatMap((g) => g.items).some((i) => i.to === '/parties')).toBe(false);
  });

  it('invites a first party rather than showing an empty table', async () => {
    installFetchMock({ 'GET /api/parties': () => jsonResponse([]) });
    render(page());

    expect(await screen.findByTestId('parties-empty')).toBeInTheDocument();
    expect(screen.getByText('La tua prima festa inizia qui.')).toBeInTheDocument();
    expect(screen.queryByTestId('party-list')).not.toBeInTheDocument();
  });

  it('lists parties with a product label, never the raw status', async () => {
    installFetchMock({
      'GET /api/parties': () => jsonResponse([
        party({ id: 'ended', title: 'Conclusa', status: 'ended' }),
        party({ id: 'live', title: 'Stasera', status: 'live', mainAlbumName: 'Album' }),
      ]),
    });
    render(page());

    const items = await screen.findAllByRole('listitem');
    // Live first: what is happening comes before what is over.
    expect(items[0]).toHaveTextContent('Stasera');
    expect(items[1]).toHaveTextContent('Conclusa');

    expect(screen.getByText('Live')).toBeInTheDocument();
    expect(screen.getByText('Conclusa', { selector: '.party-list-title' })).toBeInTheDocument();
    // The wire values themselves never reach the screen.
    expect(screen.queryByText('ended')).not.toBeInTheDocument();
  });

  it('creates a party from a name alone and opens it', async () => {
    const mock = installFetchMock({
      'GET /api/parties': () => jsonResponse([]),
      'POST /api/parties': () => jsonResponse({ id: 'new-party' }, 201),
    });
    render(page());

    await userEvent.click(await screen.findByTestId('party-new'));
    await userEvent.type(
      screen.getByLabelText('Nome'), 'Festa di Marta');
    await userEvent.click(screen.getByTestId('party-create-submit'));

    // Straight into the workspace: the rest of the setup lives there.
    expect(await screen.findByText('workspace')).toBeInTheDocument();

    const created = mock.calls.find((c) => c.method === 'POST');
    const body = JSON.parse(String(created!.body));
    expect(body.title).toBe('Festa di Marta');
    // A party asks for nothing it does not need: no album, no television, no
    // game, no printing, no quotas.
    expect(Object.keys(body).sort()).toEqual(['description', 'eventStartsAt', 'title']);
  });

  it('will not submit a party with no name', async () => {
    installFetchMock({ 'GET /api/parties': () => jsonResponse([]) });
    render(page());

    await userEvent.click(await screen.findByTestId('party-new'));
    expect(screen.getByTestId('party-create-submit')).toBeDisabled();
  });
});
