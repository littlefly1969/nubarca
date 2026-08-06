import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { AlbumsPage } from './AlbumsPage';
import { AuthedWrapper, emptyResponse, installFetchMock, jsonResponse } from '../test-utils';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

function summary(over: Partial<Record<string, unknown>> = {}) {
  return {
    id: 'a1', name: 'Alpha', description: 'first', itemCount: 3, showOnTv: false,
    createdAt: '2025-01-01T00:00:00Z', updatedAt: '2025-01-01T00:00:00Z',
    photoCount: 2, videoCount: 1, excludedCount: 0,
    coverItems: [{ fileItemId: 'f1', kind: 'image', thumbnailUrl: '/api/files/f1/thumbnail?size=small' }],
    ...over,
  };
}

const beta = summary({
  id: 'a2', name: 'Beta', description: null, showOnTv: true,
  updatedAt: '2025-06-01T00:00:00Z', photoCount: 5, videoCount: 0, coverItems: [],
});

function renderPage() {
  return render(
    <AuthedWrapper>
      <MemoryRouter><AlbumsPage /></MemoryRouter>
    </AuthedWrapper>,
  );
}

describe('AlbumsPage', () => {
  it('renders modern cards with per-kind counts, cover and TV badge', async () => {
    installFetchMock({ 'GET /api/albums': () => jsonResponse([summary(), beta]) });
    renderPage();
    const cards = await screen.findAllByTestId('album-card');
    expect(cards).toHaveLength(2);
    // Alpha carries a cover mosaic + per-kind counts; Beta is TV-enabled.
    expect(screen.getByTestId('album-cover')).toBeInTheDocument();
    expect(screen.getByText(/2 foto/)).toBeInTheDocument();
    expect(screen.getByText(/1 video/)).toBeInTheDocument();
    expect(screen.getByTestId('album-tv-badge')).toBeInTheDocument();
  });

  it('filters by name search', async () => {
    installFetchMock({ 'GET /api/albums': () => jsonResponse([summary(), beta]) });
    renderPage();
    await screen.findAllByTestId('album-card');
    await userEvent.type(screen.getByTestId('albums-search'), 'bet');
    const cards = screen.getAllByTestId('album-card');
    expect(cards).toHaveLength(1);
    expect(within(cards[0]).getByText('Beta')).toBeInTheDocument();
  });

  it('sorts by name', async () => {
    installFetchMock({ 'GET /api/albums': () => jsonResponse([beta, summary()]) });
    renderPage();
    await screen.findAllByTestId('album-card');
    await userEvent.selectOptions(screen.getByTestId('albums-sort'), 'name');
    const names = screen.getAllByTestId('album-card')
      .map((c) => c.querySelector('.album-card-name')?.textContent);
    expect(names).toEqual(['Alpha', 'Beta']);
  });

  it('shows the empty state', async () => {
    installFetchMock({ 'GET /api/albums': () => jsonResponse([]) });
    renderPage();
    expect(await screen.findByTestId('albums-empty')).toBeInTheDocument();
  });

  it('creates an album and reloads', async () => {
    let listCalls = 0;
    installFetchMock({
      'GET /api/albums': () => { listCalls += 1; return jsonResponse(listCalls === 1 ? [] : [summary()]); },
      'POST /api/albums': () => jsonResponse(summary(), 201),
    });
    renderPage();
    await screen.findByTestId('albums-empty');
    await userEvent.type(screen.getByLabelText(/nome/i), 'Alpha');
    await userEvent.click(screen.getByRole('button', { name: /crea/i }));
    expect(await screen.findByTestId('album-card')).toBeInTheDocument();
  });

  it('deletes an album after confirmation', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);
    let listCalls = 0;
    installFetchMock({
      'GET /api/albums': () => { listCalls += 1; return jsonResponse(listCalls === 1 ? [summary()] : []); },
      'DELETE /api/albums/a1': () => emptyResponse(),
    });
    renderPage();
    await screen.findByTestId('album-card');
    await userEvent.click(screen.getByTestId('album-delete-btn'));
    expect(await screen.findByTestId('albums-empty')).toBeInTheDocument();
    confirmSpy.mockRestore();
  });
});
