// The mail routes against the REAL <App /> route table, anonymously: both the
// section and the message deep link sit behind the authenticated layout.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, waitFor } from '@testing-library/react';
import { App } from '../App';
import { errorResponse, installFetchMock } from '../test-utils';

beforeEach(() => {
  installFetchMock({ '* ': () => errorResponse(401) });
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  window.history.replaceState({}, '', '/');
});

describe('mail routes', () => {
  it('protects /mail and /mail/messages/:messageId', async () => {
    for (const path of ['/mail', '/mail/messages/msg-1']) {
      window.history.replaceState({}, '', path);
      render(<App />);
      await waitFor(() => expect(window.location.pathname).toBe('/login'));
      cleanup();
    }
  });
});
