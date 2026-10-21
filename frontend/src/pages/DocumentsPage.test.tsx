import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { DocumentsPage } from './DocumentsPage';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../test-utils';
import { I18nProvider } from '../i18n';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const FILE_ID = '11111111-2222-3333-4444-555555555555';

const STATUS = {
  enabled: false,
  documents: 3,
  chunks: 12,
  semanticEnabled: false,
  visualEnabled: false,
  visualReason: 'disabled',
  indexing: { candidateFiles: 4, indexedFiles: 3, notIndexedFiles: 1, chunks: 12, embeddedChunks: null },
};

const OK_RESULT = {
  status: 'ok',
  reason: null,
  mode: 'lexical',
  semantic: false,
  fallbackReason: null,
  visualMode: 'unavailable',
  visualUsed: false,
  documents: [
    {
      sourceKind: 'file',
      sourceId: FILE_ID,
      name: 'contratto.pdf',
      rank: 1,
      passages: [
        {
          rank: 1,
          snippet: 'In caso di recesso anticipato è dovuta una penale.',
          section: 'Recesso',
          page: 3,
          ordinal: 4,
          locator: { kind: 'page', index: 3, label: null },
        },
      ],
    },
    {
      sourceKind: 'file',
      sourceId: '99999999-2222-3333-4444-555555555555',
      name: 'lancio.pptx',
      rank: 2,
      passages: [
        {
          rank: 2,
          snippet: 'Roadmap del lancio trimestrale.',
          section: null,
          page: null,
          ordinal: 1,
          locator: { kind: 'slide', index: 7, label: 'Piano di lancio' },
        },
      ],
    },
  ],
};

function renderPage(handlers: Record<string, unknown> = {}) {
  const mock = installFetchMock({
    'GET /api/assistant/documents/status': () => jsonResponse(STATUS),
    'POST /api/documents/search': () => jsonResponse(OK_RESULT),
    ...handlers,
  });
  render(
    <I18nProvider>
      <AuthedWrapper>
        <MemoryRouter><DocumentsPage /></MemoryRouter>
      </AuthedWrapper>
    </I18nProvider>,
  );
  return mock;
}

async function searchFor(text: string) {
  await userEvent.type(screen.getByLabelText('Cerca nei tuoi documenti'), text);
  await userEvent.click(screen.getByRole('button', { name: 'Cerca' }));
}

it('shows the indexing progress from the status counts', async () => {
  renderPage();
  expect(await screen.findByText('3 di 4 documenti pronti per la ricerca')).toBeTruthy();
  expect(screen.getByText(/Scrivi cosa cerchi/)).toBeTruthy();
});

it('sends only the query and groups results by document with location badges', async () => {
  const mock = renderPage();
  await searchFor('penale recesso');

  await waitFor(() => expect(screen.getByText('contratto.pdf')).toBeTruthy());

  const call = mock.calls.find((c) => c.method === 'POST')!;
  const body = JSON.parse(call.body as string);
  // No owner, no folder, no file, no source: the shape has nowhere for them.
  expect(Object.keys(body)).toEqual(['query']);
  expect(body.query).toBe('penale recesso');

  expect(screen.getByText('2 documenti')).toBeTruthy();
  expect(screen.getByText('Pagina 3')).toBeTruthy();
  expect(screen.getByText('Recesso')).toBeTruthy();
  // A slide is said as a slide, never as a page.
  expect(screen.getByText('Diapositiva 7 — Piano di lancio')).toBeTruthy();
  expect(screen.getByText(/recesso anticipato è dovuta/)).toBeTruthy();
});

it('opens the file through the existing content endpoint with the owner’s own id', async () => {
  renderPage();
  await searchFor('penale');

  const open = await screen.findByRole('link', { name: 'Apri contratto.pdf' });
  expect(open.getAttribute('href')).toBe(`/api/files/${FILE_ID}/content`);
});

it('says which retrieval ran, discreetly', async () => {
  renderPage({
    'POST /api/documents/search': () =>
      jsonResponse({ ...OK_RESULT, mode: 'lexical-fallback-model_unavailable', fallbackReason: 'model_unavailable' }),
  });
  await searchFor('penale');
  expect(await screen.findByText(/ricerca per significato non disponibile/)).toBeTruthy();
});

it('reports a hybrid search and a visual contribution', async () => {
  renderPage({
    'POST /api/documents/search': () =>
      jsonResponse({ ...OK_RESULT, mode: 'hybrid', semantic: true, visualUsed: true }),
  });
  await searchFor('penale');
  expect(await screen.findByText(/Ricerca per parole e significato/)).toBeTruthy();
  expect(screen.getByText(/aspetto delle pagine/)).toBeTruthy();
});

it('distinguishes no evidence from not indexed', async () => {
  renderPage({
    'POST /api/documents/search': () =>
      jsonResponse({ ...OK_RESULT, status: 'no_evidence', documents: [] }),
  });
  await searchFor('Madagascar');
  expect(await screen.findByText(/Nessun documento risponde/)).toBeTruthy();

  cleanup();
  renderPage({
    'POST /api/documents/search': () =>
      jsonResponse({ ...OK_RESULT, status: 'not_indexed', documents: [] }),
  });
  await searchFor('Madagascar');
  expect(await screen.findByText(/non sono ancora stati letti/)).toBeTruthy();
});

it('shows an error rather than stale results when the search fails', async () => {
  renderPage({
    'POST /api/documents/search': () => new Response('boom', { status: 500 }),
  });
  await searchFor('penale');
  expect(await screen.findByText(/La ricerca non è riuscita/)).toBeTruthy();
});
