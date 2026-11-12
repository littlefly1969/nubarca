import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { I18nProvider } from '../i18n';
import { errorResponse, installFetchMock, jsonResponse } from '../test-utils';
import { AlbumPrintLogoSettings } from './AlbumPrintLogoSettings';

afterEach(() => cleanup());

const ALBUM = 'A1';
const PATH = `/api/albums/${ALBUM}/print-logo`;

function mount(initial: unknown, extra: Record<string, () => Response> = {}) {
  const mock = installFetchMock({ [`GET ${PATH}`]: () => jsonResponse(initial), ...extra });
  render(<I18nProvider><AlbumPrintLogoSettings albumId={ALBUM} /></I18nProvider>);
  return mock;
}

function lastPut(calls: { url: string; method: string; body: string | null }[]) {
  const put = [...calls].reverse().find((c) => c.method === 'PUT');
  return put ? JSON.parse(put.body ?? '{}') : null;
}

describe('AlbumPrintLogoSettings', () => {
  it('says there is no logo, and that guests at the party print one unless allowed otherwise', async () => {
    mount({ fileItemId: null, previewUrl: null, optional: false });
    expect(await screen.findByTestId('album-print-logo-none')).toBeInTheDocument();
    expect(screen.getByTestId('album-print-logo-optional')).not.toBeChecked();
    expect(screen.queryByTestId('album-print-logo-remove')).toBeNull();
  });

  it('shows the logo and removes it, keeping the guests\' option', async () => {
    const user = userEvent.setup();
    const mock = mount(
      { fileItemId: 'logo1', previewUrl: `${PATH}/image?v=logo1`, optional: true },
      { [`PUT ${PATH}`]: () => jsonResponse({ fileItemId: null, previewUrl: null, optional: true }) },
    );
    expect(await screen.findByTestId('album-print-logo-preview')).toHaveAttribute('src', `${PATH}/image?v=logo1`);
    await user.click(screen.getByTestId('album-print-logo-remove'));
    await waitFor(() => expect(lastPut(mock.calls)).toEqual({ fileItemId: null, optional: true }));
    expect(await screen.findByTestId('album-print-logo-none')).toBeInTheDocument();
  });

  it('lets guests print with no logo only when switched on', async () => {
    const user = userEvent.setup();
    const mock = mount(
      { fileItemId: 'logo1', previewUrl: '/logo.png', optional: false },
      { [`PUT ${PATH}`]: () => jsonResponse({ fileItemId: 'logo1', previewUrl: '/logo.png', optional: true }) },
    );
    await user.click(await screen.findByTestId('album-print-logo-optional'));
    await waitFor(() => expect(lastPut(mock.calls)).toEqual({ fileItemId: 'logo1', optional: true }));
    expect(screen.getByTestId('album-print-logo-optional')).toBeChecked();
  });

  it('says when the chosen logo can no longer be used', async () => {
    mount({ fileItemId: 'logo1', previewUrl: null, optional: false });
    expect(await screen.findByTestId('album-print-logo-unavailable')).toBeInTheDocument();
  });

  it('shows nothing on an album that is not the caller\'s own', async () => {
    const mock = installFetchMock({ [`GET ${PATH}`]: () => errorResponse(404, 'Not found') });
    const { container } = render(<I18nProvider><AlbumPrintLogoSettings albumId={ALBUM} /></I18nProvider>);
    await waitFor(() => expect(mock.calls.some((c) => c.url.includes('print-logo'))).toBe(true));
    expect(container).toBeEmptyDOMElement();
  });
});
