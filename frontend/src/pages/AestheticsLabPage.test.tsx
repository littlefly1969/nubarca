import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { AestheticsLabPage } from './AestheticsLabPage';
import { AuthedWrapper, installFetchMock, jsonResponse, emptyResponse } from '../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const overallMetric = {
  key: 'overall_aesthetic',
  group: 'overall',
  value: 0.72,
  scaleMin: 0,
  scaleMax: 1,
  confidence: null,
  version: 1,
};

const item = {
  id: 'item-1',
  originalFileName: 'portrait.png',
  contentType: 'image/png',
  sizeBytes: 2048,
  width: 512,
  height: 512,
  createdAt: '2026-07-14T10:00:00Z',
  latestRunStatus: 'succeeded',
  latestRunErrorCode: null,
  overallScore: 0.72,
  profileKey: 'human-aesexpert-1b-expert-v1',
  thumbnailUrl: '/api/aesthetics-lab/items/item-1/thumbnail?size=small',
  previewUrl: '/api/aesthetics-lab/items/item-1/preview',
};

const comparisonMetricDefs = [
  ['facial_brightness', 'face'],
  ['facial_feature_clarity', 'face'],
  ['facial_skin_tone', 'face'],
  ['facial_structure', 'face'],
  ['facial_contour_clarity', 'face'],
  ['facial_aesthetic', 'face'],
  ['outfit', 'appearance'],
  ['body_shape', 'appearance'],
  ['looks', 'appearance'],
  ['environment', 'environment'],
  ['general_appearance_aesthetic', 'appearance'],
  ['overall_aesthetic', 'overall'],
] as const;

function comparisonDetail(id: string, name: string, base: number) {
  return {
    id,
    originalFileName: name,
    contentType: 'image/png',
    sizeBytes: 2048,
    width: 512,
    height: 512,
    createdAt: '2026-07-14T10:00:00Z',
    previewUrl: `/api/aesthetics-lab/items/${id}/preview`,
    latestRun: {
      id: `run-${id}`,
      status: 'succeeded',
      profileKey: 'human-aesexpert-1b-expert-v1',
      modelName: 'KlingTeam/HumanAesExpert-1B',
      modelRevision: 'rev',
      runtimeName: 'transformers',
      runtimeVersion: '4.44.2',
      preprocessingProfileKey: 'human-aesexpert-official-v1',
      requestedCapabilities: ['expert_scores'],
      completedCapabilities: ['expert_scores'],
      createdAt: '2026-07-14T10:00:00Z',
      startedAt: '2026-07-14T10:00:01Z',
      completedAt: '2026-07-14T10:00:05Z',
      durationMs: 4200,
      errorCode: null,
      warnings: [],
      metrics: comparisonMetricDefs.map(([key, group], index) => ({
        key,
        group,
        value: Math.min(0.99, base + index * 0.01),
        scaleMin: 0,
        scaleMax: 1,
        confidence: null,
        version: 1,
      })),
      texts: [],
    },
    history: [],
  };
}

function wrapper(children: React.ReactNode) {
  return (
    <AuthedWrapper>
      <MemoryRouter>{children}</MemoryRouter>
    </AuthedWrapper>
  );
}

describe('AestheticsLabPage', () => {
  it('renders the heading, disclaimer, and empty state', async () => {
    installFetchMock({ 'GET /api/aesthetics-lab/items': () => jsonResponse({ items: [], nextCursor: null }) });
    render(wrapper(<AestheticsLabPage />));

    // Inside the Laboratory shell the section heading is the tab name.
    expect(await screen.findByRole('heading', { name: 'Estetica' })).toBeInTheDocument();
    expect(screen.getByRole('note')).toHaveTextContent(/valutazione estetica sperimentale/i);
    expect(await screen.findByTestId('aesthetics-empty')).toBeInTheDocument();
  });

  it('renders a grid with the overall score and status', async () => {
    installFetchMock({
      'GET /api/aesthetics-lab/items': () => jsonResponse({ items: [item], nextCursor: null }),
    });
    render(wrapper(<AestheticsLabPage />));

    expect(await screen.findByTestId('aesthetics-grid')).toBeInTheDocument();
    expect(screen.getByText('portrait.png')).toBeInTheDocument();
    // overallScore 0.72 → 7.2 / 10
    expect(screen.getByTestId('aesthetics-overall')).toHaveTextContent('7.2');
  });

  it('selecting an item and pressing Start analysis posts a batch request', async () => {
    const mock = installFetchMock({
      'GET /api/aesthetics-lab/items': () => jsonResponse({ items: [item], nextCursor: null }),
      'POST /api/aesthetics-lab/analyses': () =>
        jsonResponse({ enqueued: [{ itemId: 'item-1', runId: 'run-1', status: 'queued' }], skipped: [] }, 202),
    });
    render(wrapper(<AestheticsLabPage />));

    await screen.findByTestId('aesthetics-grid');
    await userEvent.click(screen.getByRole('checkbox', { name: /seleziona portrait\.png/i }));
    expect(screen.getByTestId('aesthetics-selected-count')).toBeInTheDocument();
    await userEvent.click(screen.getByTestId('aesthetics-start-analysis'));

    await waitFor(() => {
      const call = mock.calls.find((c) => c.method === 'POST' && c.url.includes('/analyses'));
      expect(call).toBeTruthy();
      expect(call!.body).toContain('item-1');
    });
  });

  it('opens the detail modal and shows grouped Expert metrics', async () => {
    const detail = {
      id: 'item-1',
      originalFileName: 'portrait.png',
      contentType: 'image/png',
      sizeBytes: 2048,
      width: 512,
      height: 512,
      createdAt: '2026-07-14T10:00:00Z',
      previewUrl: '/api/aesthetics-lab/items/item-1/preview',
      latestRun: {
        id: 'run-1',
        status: 'succeeded',
        profileKey: 'human-aesexpert-1b-expert-v1',
        modelName: 'KwaiVGI/HumanAesExpert-1B',
        modelRevision: 'rev',
        runtimeName: 'transformers',
        runtimeVersion: '4.44.2',
        preprocessingProfileKey: 'human-aesexpert-official-v1',
        requestedCapabilities: ['expert_scores'],
        completedCapabilities: ['expert_scores'],
        createdAt: '2026-07-14T10:00:00Z',
        startedAt: '2026-07-14T10:00:01Z',
        completedAt: '2026-07-14T10:00:05Z',
        durationMs: 4200,
        errorCode: null,
        warnings: [],
        metrics: [overallMetric],
        texts: [],
      },
      history: [],
    };
    installFetchMock({
      'GET /api/aesthetics-lab/items': () => jsonResponse({ items: [item], nextCursor: null }),
      'GET /api/aesthetics-lab/items/item-1': () => jsonResponse(detail),
    });
    render(wrapper(<AestheticsLabPage />));

    await screen.findByTestId('aesthetics-grid');
    await userEvent.click(screen.getByRole('button', { name: /apri dettaglio di portrait\.png/i }));

    expect(await screen.findByTestId('aesthetics-metrics')).toBeInTheDocument();
    expect(screen.getByTestId('aesthetics-run-status')).toHaveTextContent(/completata/i);
    // Prepared text/score-head sections are NOT rendered (capabilities absent).
    expect(screen.queryByTestId('aesthetics-text')).not.toBeInTheDocument();
    expect(screen.queryByTestId('aesthetics-score-head')).not.toBeInTheDocument();
  });

  it('compares all 12 Expert scores across selected completed images', async () => {
    const secondItem = {
      ...item,
      id: 'item-2',
      originalFileName: 'full-body.png',
      overallScore: 0.81,
      thumbnailUrl: '/api/aesthetics-lab/items/item-2/thumbnail?size=small',
      previewUrl: '/api/aesthetics-lab/items/item-2/preview',
    };
    installFetchMock({
      'GET /api/aesthetics-lab/items': () => jsonResponse({ items: [item, secondItem], nextCursor: null }),
      'GET /api/aesthetics-lab/items/item-1': () => jsonResponse(comparisonDetail('item-1', 'portrait.png', 0.5)),
      'GET /api/aesthetics-lab/items/item-2': () => jsonResponse(comparisonDetail('item-2', 'full-body.png', 0.6)),
    });
    render(wrapper(<AestheticsLabPage />));

    await screen.findByTestId('aesthetics-grid');
    await userEvent.click(screen.getByRole('checkbox', { name: /seleziona portrait\.png/i }));
    await userEvent.click(screen.getByRole('checkbox', { name: /seleziona full-body\.png/i }));
    await userEvent.click(screen.getByTestId('aesthetics-compare-scores'));

    const table = await screen.findByTestId('aesthetics-comparison-table');
    expect(table).toHaveTextContent('portrait.png');
    expect(table).toHaveTextContent('full-body.png');
    expect(table).toHaveTextContent('Forma del corpo');
    expect(table.querySelectorAll('tr[data-metric-key]')).toHaveLength(12);
    expect(table.querySelector('tr[data-metric-key="facial_brightness"] td.is-best')).toHaveTextContent('6.0');
  });

  it('removes an item after confirmation', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const mock = installFetchMock({
      'GET /api/aesthetics-lab/items': () => jsonResponse({ items: [item], nextCursor: null }),
      'DELETE /api/aesthetics-lab/items/item-1': () => emptyResponse(204),
    });
    render(wrapper(<AestheticsLabPage />));

    await screen.findByTestId('aesthetics-grid');
    await userEvent.click(screen.getByRole('button', { name: /^rimuovi$/i }));

    await waitFor(() => {
      expect(mock.calls.some((c) => c.method === 'DELETE' && c.url.includes('/items/item-1'))).toBe(true);
    });
  });
});
