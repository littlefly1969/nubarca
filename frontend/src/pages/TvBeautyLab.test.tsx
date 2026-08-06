import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { TvBeautyLab } from './TvBeautyLab';
import { installFetchMock, jsonResponse, type MockHandler } from '../test-utils';
import { I18nProvider } from '../i18n';

beforeAll(() => {
  (URL as unknown as { createObjectURL: unknown }).createObjectURL = vi.fn(() => 'blob:mock');
  (URL as unknown as { revokeObjectURL: unknown }).revokeObjectURL = vi.fn();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const mediaHandler: MockHandler = () =>
  new Response(new Blob([new Uint8Array([1, 2, 3])]), {
    status: 200,
    headers: { 'content-type': 'image/png' },
  });

const FACE = ['facial_brightness', 'facial_feature_clarity', 'facial_skin_tone', 'facial_structure', 'facial_contour_clarity', 'facial_aesthetic'];
const APPEARANCE = ['outfit', 'body_shape', 'looks'];
const ENVIRONMENT = ['environment'];
const OVERALL = ['general_appearance_aesthetic', 'overall_aesthetic'];

function metricsFor(base: number) {
  const groups: [string, string[]][] = [['face', FACE], ['appearance', APPEARANCE], ['environment', ENVIRONMENT], ['overall', OVERALL]];
  const out: unknown[] = [];
  let i = 0;
  for (const [group, keys] of groups) {
    for (const key of keys) {
      out.push({ key, group, value: base + i * 0.1, scaleMin: 0, scaleMax: 10, confidence: null, version: 1 });
      i++;
    }
  }
  return out;
}

function completedRun(id: string, base: number) {
  return {
    id: `run-${id}`,
    status: 'succeeded',
    profileKey: 'p',
    modelName: 'HumanAesExpert',
    modelRevision: 'r1',
    runtimeName: 'onnx',
    runtimeVersion: '1',
    preprocessingProfileKey: 'official-v1',
    requestedCapabilities: ['expert-scores'],
    completedCapabilities: ['expert-scores'],
    createdAt: new Date().toISOString(),
    startedAt: new Date().toISOString(),
    completedAt: new Date().toISOString(),
    durationMs: 1234,
    errorCode: null,
    warnings: [],
    metrics: metricsFor(base),
    texts: [],
  };
}

function item(id: string, score: number | null, status: string | null) {
  return {
    id,
    originalFileName: `${id}.jpg`,
    contentType: 'image/jpeg',
    sizeBytes: 1000,
    width: 100,
    height: 100,
    createdAt: new Date().toISOString(),
    latestRunStatus: status,
    latestRunErrorCode: null,
    overallScore: score,
    profileKey: 'p',
    thumbnailUrl: `/api/tv/personal/aesthetics/items/${id}/thumbnail`,
    previewUrl: `/api/tv/personal/aesthetics/items/${id}/preview`,
  };
}

function wrapper(props: Partial<Parameters<typeof TvBeautyLab>[0]> = {}) {
  return (
    <I18nProvider>
      <TvBeautyLab grant="g" onBack={vi.fn()} onPersonalError={() => false} {...props} />
    </I18nProvider>
  );
}

describe('TvBeautyLab (/tv Beauty Lab)', () => {
  it('renders the grid with score/status and no internal fields', async () => {
    installFetchMock({
      'GET /api/tv/personal/aesthetics/items': () =>
        jsonResponse({ items: [item('a', 7.2, 'succeeded'), item('b', null, null)], nextCursor: null }),
      'GET /api/tv/personal/aesthetics/items/a/thumbnail': mediaHandler,
      'GET /api/tv/personal/aesthetics/items/b/thumbnail': mediaHandler,
    });

    render(wrapper());
    const tiles = await screen.findAllByTestId('tv-beauty-lab-tile');
    expect(tiles).toHaveLength(2);
    expect(screen.getByTestId('tv-beauty-lab-score')).toHaveTextContent('7.2/10');

    const html = document.body.innerHTML;
    for (const forbidden of ['blobObjectId', 'storageKey', 'sha256', 'logicalContainerKey']) {
      expect(html).not.toContain(forbidden);
    }
  });

  it('BACK from the grid root locks (calls onBack)', async () => {
    const onBack = vi.fn();
    installFetchMock({
      'GET /api/tv/personal/aesthetics/items': () => jsonResponse({ items: [], nextCursor: null }),
    });
    render(wrapper({ onBack }));
    await screen.findByTestId('tv-beauty-lab-empty');
    fireEvent.keyDown(screen.getByTestId('tv-beauty-lab'), { key: 'Escape' });
    expect(onBack).toHaveBeenCalledTimes(1);
  });

  it('opens the action menu and starts analysis for the selection', async () => {
    let analysisBody: string | null = null;
    installFetchMock({
      'GET /api/tv/personal/aesthetics/items': () =>
        jsonResponse({ items: [item('a', null, null)], nextCursor: null }),
      'GET /api/tv/personal/aesthetics/items/a/thumbnail': mediaHandler,
      'POST /api/tv/personal/aesthetics/analyses': (req) => {
        analysisBody = req.body;
        return jsonResponse({ enqueued: [{ itemId: 'a', runId: 'r', status: 'queued' }], skipped: [] });
      },
    });

    render(wrapper());
    const container = await screen.findByTestId('tv-beauty-lab');
    fireEvent.keyDown(container, { key: 'm' });
    await userEvent.setup().click(screen.getByRole('menuitem', { name: /Seleziona/i }));

    await userEvent.setup().click(screen.getByTestId('tv-beauty-lab-tile'));
    fireEvent.keyDown(container, { key: 'm' });
    await userEvent.setup().click(screen.getByRole('menuitem', { name: /Avvia analisi/i }));

    expect(await screen.findByTestId('tv-beauty-lab-notice')).toBeInTheDocument();
    expect(analysisBody).toContain('"a"');
  });

  it('Add images opens the QR screen and creates one upload session', async () => {
    let created = 0;
    installFetchMock({
      'GET /api/tv/personal/aesthetics/items': () => jsonResponse({ items: [], nextCursor: null }),
      'POST /api/tv/personal/aesthetics/upload-sessions': () => {
        created++;
        return jsonResponse({
          id: 's1',
          uploadUrl: '/beauty-lab-upload/tok',
          expiresAt: new Date(Date.now() + 600000).toISOString(),
          maxFiles: 40,
          maxTotalBytes: 5e8,
          accepted: 0,
          rejected: 0,
          status: 'active',
        });
      },
    });

    render(wrapper());
    const container = await screen.findByTestId('tv-beauty-lab');
    fireEvent.keyDown(container, { key: 'm' });
    await userEvent.setup().click(screen.getByRole('menuitem', { name: /Aggiungi immagini/i }));

    expect(await screen.findByTestId('tv-beauty-lab-qr-code')).toBeInTheDocument();
    expect(created).toBe(1);
    expect(screen.getByTestId('tv-beauty-lab-qr-counts')).toBeInTheDocument();
  });

  it('detail view shows all 12 metrics across the four groups', async () => {
    installFetchMock({
      'GET /api/tv/personal/aesthetics/items': () =>
        jsonResponse({ items: [item('a', 7, 'succeeded')], nextCursor: null }),
      'GET /api/tv/personal/aesthetics/items/a/thumbnail': mediaHandler,
      'GET /api/tv/personal/aesthetics/items/a/preview': mediaHandler,
      'GET /api/tv/personal/aesthetics/items/a': () =>
        jsonResponse({
          id: 'a',
          originalFileName: 'a.jpg',
          contentType: 'image/jpeg',
          sizeBytes: 1000,
          width: 100,
          height: 100,
          createdAt: new Date().toISOString(),
          previewUrl: '/api/tv/personal/aesthetics/items/a/preview',
          latestRun: completedRun('a', 5),
          history: [],
        }),
    });

    render(wrapper());
    await userEvent.setup().click(await screen.findByTestId('tv-beauty-lab-tile'));

    const detail = await screen.findByTestId('tv-beauty-lab-detail');
    const rows = within(detail).getAllByRole('listitem');
    expect(rows).toHaveLength(12);
    for (const g of ['face', 'appearance', 'environment', 'overall']) {
      expect(within(detail).getByTestId(`tv-beauty-lab-group-${g}`)).toBeInTheDocument();
    }
  });

  it('compare matrix highlights the highest value per metric and excludes uncompleted', async () => {
    installFetchMock({
      'GET /api/tv/personal/aesthetics/items': () =>
        jsonResponse({ items: [item('a', 5, 'succeeded'), item('b', 6, 'succeeded'), item('c', null, null)], nextCursor: null }),
      'GET /api/tv/personal/aesthetics/items/a/thumbnail': mediaHandler,
      'GET /api/tv/personal/aesthetics/items/b/thumbnail': mediaHandler,
      'GET /api/tv/personal/aesthetics/items/c/thumbnail': mediaHandler,
      'GET /api/tv/personal/aesthetics/items/a': () =>
        jsonResponse({ id: 'a', originalFileName: 'a.jpg', contentType: 'image/jpeg', sizeBytes: 1, width: 1, height: 1, createdAt: new Date().toISOString(), previewUrl: '', latestRun: completedRun('a', 3), history: [] }),
      'GET /api/tv/personal/aesthetics/items/b': () =>
        jsonResponse({ id: 'b', originalFileName: 'b.jpg', contentType: 'image/jpeg', sizeBytes: 1, width: 1, height: 1, createdAt: new Date().toISOString(), previewUrl: '', latestRun: completedRun('b', 7), history: [] }),
      // 'c' has no completed run — excluded from the matrix.
      'GET /api/tv/personal/aesthetics/items/c': () =>
        jsonResponse({ id: 'c', originalFileName: 'c.jpg', contentType: 'image/jpeg', sizeBytes: 1, width: 1, height: 1, createdAt: new Date().toISOString(), previewUrl: '', latestRun: null, history: [] }),
    });

    render(wrapper());
    const container = await screen.findByTestId('tv-beauty-lab');
    fireEvent.keyDown(container, { key: 'm' });
    await userEvent.setup().click(screen.getByRole('menuitem', { name: /Seleziona/i }));
    const tiles = screen.getAllByTestId('tv-beauty-lab-tile');
    await userEvent.setup().click(tiles[0]);
    await userEvent.setup().click(tiles[1]);
    await userEvent.setup().click(tiles[2]);
    fireEvent.keyDown(container, { key: 'm' });
    await userEvent.setup().click(screen.getByRole('menuitem', { name: /Confronta punteggi/i }));

    const compare = await screen.findByTestId('tv-beauty-lab-compare');
    // Two data columns (a, b); c excluded (no completed run).
    const headerCells = within(compare).getAllByRole('columnheader');
    expect(headerCells).toHaveLength(3); // metric label + 2 images
    // Every metric row's best cell is b's column (b has higher base values).
    const best = within(compare).getAllByRole('cell').filter((c) => c.getAttribute('data-best') === 'true');
    expect(best.length).toBe(12);
  });
});
