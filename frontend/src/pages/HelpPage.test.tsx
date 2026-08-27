import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { HelpPage } from './HelpPage';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../test-utils';
import { I18nProvider } from '../i18n';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function renderHelp(handlers: Record<string, unknown> = {}) {
  const mock = installFetchMock({
    'GET /api/help/ai/status': () =>
      jsonResponse({
        enabled: true,
        providerLabel: 'Test Provider',
        knowledgeAvailable: true,
        modelBoundary: 'external',
      }),
    ...handlers,
  });
  render(
    <I18nProvider>
      <AuthedWrapper>
        <MemoryRouter><HelpPage /></MemoryRouter>
      </AuthedWrapper>
    </I18nProvider>,
  );
  return mock;
}

it('states the external privacy boundary in the words that are actually true', async () => {
  renderHelp();
  // Not "no data leaves NubArca" — the user's own words do, by definition, and a
  // privacy claim that is false in the easy case is worth nothing in the hard one.
  const disclosure = await screen.findByText(/viene inviata al provider/i);
  expect(disclosure.textContent).toContain('Test Provider');
  expect(disclosure.textContent).toMatch(/non allega né recupera file, foto, persone/i);
  expect(await screen.findByText('IA esterna')).toBeTruthy();
});

it('tells the truth about a local model instead of still saying external', async () => {
  // The page used to say "external" unconditionally. Once an operator can point
  // Help at their own endpoint, that copy is simply false — and a privacy
  // disclosure that is wrong is worse than none.
  renderHelp({
    'GET /api/help/ai/status': () =>
      jsonResponse({
        enabled: true,
        providerLabel: 'Local Model',
        knowledgeAvailable: true,
        modelBoundary: 'localTrusted',
      }),
  });

  const disclosure = await screen.findByText(/elaborata dal modello locale/i);
  expect(disclosure.textContent).toContain('Local Model');
  // Slice 1 still answers from public product documentation only, and says so.
  expect(disclosure.textContent).toMatch(/documentazione pubblica di prodotto/i);
  expect(disclosure.textContent).toMatch(/non accede alla tua libreria/i);
  // …and does NOT claim something NubArca cannot prove about someone else's
  // process. That guarantee belongs to a managed runtime NubArca does not ship.
  expect(disclosure.textContent).not.toMatch(/nessun accesso a internet|no internet|isolat/i);

  // A distinct badge, not the same one with different words.
  const badge = await screen.findByText('IA locale');
  expect(badge.className).toContain('help-ai-badge--local');
  expect(screen.queryByText('IA esterna')).toBeNull();
});

it('offers no library-context controls whichever side the model is on', async () => {
  renderHelp({
    'GET /api/help/ai/status': () =>
      jsonResponse({
        enabled: true,
        providerLabel: 'Local Model',
        knowledgeAvailable: true,
        modelBoundary: 'localTrusted',
      }),
  });
  await screen.findByLabelText('La tua domanda');

  // Trust widened what the MODEL is eligible for. It did not add an attach
  // button.
  expect(screen.queryByRole('button', { name: /allega|attach|carica|upload/i })).toBeNull();
  expect(document.querySelector('input[type="file"]')).toBeNull();
  for (const forbidden of [/foto corrente/i, /album corrente/i, /la mia libreria/i]) {
    expect(screen.queryByText(forbidden)).toBeNull();
  }
});

it('offers no way to attach library content', async () => {
  renderHelp();
  await screen.findByLabelText('La tua domanda');
  // The affordances that would turn an explainer into a data pipeline.
  expect(screen.queryByRole('button', { name: /allega|attach|carica|upload/i })).toBeNull();
  expect(document.querySelector('input[type="file"]')).toBeNull();
  for (const forbidden of [/foto corrente/i, /album corrente/i, /persona corrente/i, /ricerca corrente/i]) {
    expect(screen.queryByText(forbidden)).toBeNull();
  }
});

it('sends only the question and a bounded history', async () => {
  const mock = renderHelp({
    'POST /api/help/ai/chat': () =>
      jsonResponse({ ok: true, text: 'An album is a named collection.', sources: ['docs/albums.md'] }),
  });
  await screen.findByLabelText('La tua domanda');

  await userEvent.type(screen.getByLabelText('La tua domanda'), 'Come funzionano gli album?');
  await userEvent.click(screen.getByRole('button', { name: 'Chiedi' }));

  await waitFor(() => expect(screen.getByText('An album is a named collection.')).toBeTruthy());

  const call = mock.calls.find((c) => c.method === 'POST')!;
  const body = JSON.parse(call.body as string);
  expect(Object.keys(body).sort()).toEqual(['history', 'message']);
  expect(body.message).toBe('Come funzionano gli album?');
  // Sources are shown, so an answer can be traced to the documentation.
  expect(screen.getByText(/docs\/albums\.md/)).toBeTruthy();
});

it('reads as not-configured rather than broken when disabled', async () => {
  renderHelp({
    'GET /api/help/ai/status': () =>
      jsonResponse({
        enabled: false, providerLabel: '', knowledgeAvailable: false, modelBoundary: 'external',
      }),
  });
  expect(await screen.findByText(/non è configurato/i)).toBeTruthy();
  expect(screen.queryByLabelText('La tua domanda')).toBeNull();
});

it('offers no chat when configured but without product knowledge', async () => {
  // The server refuses to call the provider in this state, so a composer would
  // invite a question guaranteed to fail.
  renderHelp({
    'GET /api/help/ai/status': () =>
      jsonResponse({
        enabled: true, providerLabel: 'Test Provider',
        knowledgeAvailable: false, modelBoundary: 'external',
      }),
  });

  expect(await screen.findByText(/non è pronto su questa installazione/i)).toBeTruthy();
  expect(screen.queryByLabelText('La tua domanda')).toBeNull();
  expect(screen.queryByRole('button', { name: 'Chiedi' })).toBeNull();
});

it('survives a provider failure without implying NubArca is down', async () => {
  renderHelp({
    'POST /api/help/ai/chat': () => jsonResponse({ ok: false, reason: 'provider_unavailable' }),
  });
  await screen.findByLabelText('La tua domanda');
  await userEvent.type(screen.getByLabelText('La tua domanda'), 'ciao');
  await userEvent.click(screen.getByRole('button', { name: 'Chiedi' }));

  const message = await screen.findByText(/non è raggiungibile/i);
  expect(message.textContent).toMatch(/NubArca continua a funzionare/i);
});

it('distinguishes "nothing answers this" from "the assistant is not ready"', async () => {
  // Two different situations with two different audiences: nobody can fix a
  // question the documentation does not cover, and an administrator CAN fix an
  // installation with no product knowledge for its revision.
  renderHelp({
    'POST /api/help/ai/chat': () =>
      jsonResponse({ ok: false, reason: 'help_no_supporting_knowledge' }),
  });
  await screen.findByLabelText('La tua domanda');
  await userEvent.type(screen.getByLabelText('La tua domanda'), 'quanto costa un abbonamento?');
  await userEvent.click(screen.getByRole('button', { name: 'Chiedi' }));

  const message = await screen.findByText(/documentazione di prodotto non copre/i);
  expect(message.textContent).not.toMatch(/non è pronto su questa installazione/i);
});
