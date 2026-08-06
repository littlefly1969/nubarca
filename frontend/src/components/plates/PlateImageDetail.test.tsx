import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../../test-utils';
import { PlateImageDetail } from './PlateImageDetail';
import { PlateAnalysisStatusBadge } from './PlateAnalysisStatusBadge';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const completedDetail = {
  id: 'plate-1',
  originalFileName: 'car.png',
  contentType: 'image/png',
  sizeBytes: 4096,
  width: 640,
  height: 480,
  status: 'analysis_completed',
  createdAt: '2026-07-01T10:00:00Z',
  updatedAt: '2026-07-01T10:05:00Z',
  previewUrl: '/api/plates/images/plate-1/preview',
  originalUrl: '/api/plates/images/plate-1/original',
  analysisSummary: {
    platesCount: 1,
    facesRedactedAvailable: false,
    analysisStatus: 'completed',
    latestJobId: 'job-1',
    lastAnalyzedAt: '2026-07-01T10:05:00Z',
  },
  detections: [
    {
      id: 'det-1',
      text: 'AB 123 CD',
      normalizedText: 'AB123CD',
      confidence: 0.91,
      plateConfidence: 0.95,
      ocrConfidence: 0.87,
      countryHint: null,
      regionHint: null,
      bbox: { x: 0.1, y: 0.2, width: 0.3, height: 0.08 },
    },
  ],
  redaction: { available: true, facesCount: 1, profileKey: 'plate-face-redaction-v1' },
};

const redactionUnavailableDetail = {
  ...completedDetail,
  redaction: { available: false, facesCount: 0, profileKey: 'plate-face-redaction-v1' },
};

function renderDetail() {
  return render(
    <AuthedWrapper>
      <PlateImageDetail id="plate-1" onClose={() => {}} onDelete={() => {}} onChanged={() => {}} />
    </AuthedWrapper>,
  );
}

describe('PlateImageDetail (ALPR analysis)', () => {
  it('renders the completed status, overlay, detection text and re-analyze action', async () => {
    installFetchMock({ 'GET /api/plates/images/plate-1': () => jsonResponse(completedDetail) });
    renderDetail();

    // Status badge (completed) + re-analyze button.
    expect(await screen.findByText('Analisi completata')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Analizza di nuovo/i })).toBeInTheDocument();

    // Overlay renders the box + recognized text; detection list too.
    const overlay = screen.getByTestId('plate-overlay');
    expect(within(overlay).getByText('AB123CD')).toBeInTheDocument();
    const list = screen.getByTestId('plate-detection-list');
    expect(within(list).getByText('AB123CD')).toBeInTheDocument();
    expect(within(list).getByText('91%')).toBeInTheDocument();
  });

  it('positions the overlay box from normalized coordinates', async () => {
    installFetchMock({ 'GET /api/plates/images/plate-1': () => jsonResponse(completedDetail) });
    const { container } = renderDetail();
    await screen.findByTestId('plate-overlay');

    const box = container.querySelector('.plate-overlay-box') as HTMLElement;
    expect(box).toBeTruthy();
    expect(box.style.left).toBe('10%');
    expect(box.style.top).toBe('20%');
    expect(box.style.width).toBe('30%');
    expect(box.style.height).toBe('8%');
  });

  it('requests analysis when the button is clicked', async () => {
    const user = userEvent.setup();
    const fetchMock = installFetchMock({
      'GET /api/plates/images/plate-1': () => jsonResponse(completedDetail),
      'POST /api/plates/images/plate-1/analysis': () =>
        jsonResponse({ id: 'job-2', status: 'queued', analysisStatus: 'pending' }, 202),
    });
    renderDetail();

    await user.click(await screen.findByRole('button', { name: /Analizza di nuovo/i }));

    await waitFor(() =>
      expect(
        fetchMock.calls.some((c) => c.method === 'POST' && c.url.includes('/plate-1/analysis')),
      ).toBe(true),
    );
  });

  it('renders a safe error message when the detail request fails', async () => {
    installFetchMock({ 'GET /api/plates/images/plate-1': () => jsonResponse({ error: 'boom' }, 500) });
    renderDetail();

    expect(await screen.findByText(/Impossibile caricare il dettaglio/i)).toBeInTheDocument();
  });

  it('renders the Hide faces toggle and uses the normal preview URL when off', async () => {
    installFetchMock({ 'GET /api/plates/images/plate-1': () => jsonResponse(completedDetail) });
    const { container } = renderDetail();
    await screen.findByTestId('plate-overlay');

    const toggle = screen.getByLabelText('Nascondi volti') as HTMLInputElement;
    expect(toggle).toBeInTheDocument();
    expect(toggle.checked).toBe(false);
    expect(toggle.disabled).toBe(false);

    const img = container.querySelector('.plate-overlay-image') as HTMLImageElement;
    expect(img.getAttribute('src')).toBe('/api/plates/images/plate-1/preview');
  });

  it('uses blurFaces=true on the preview and original URLs when the toggle is on', async () => {
    const user = userEvent.setup();
    installFetchMock({ 'GET /api/plates/images/plate-1': () => jsonResponse(completedDetail) });
    const { container } = renderDetail();
    await screen.findByTestId('plate-overlay');

    await user.click(screen.getByLabelText('Nascondi volti'));

    const img = container.querySelector('.plate-overlay-image') as HTMLImageElement;
    expect(img.getAttribute('src')).toBe('/api/plates/images/plate-1/preview?blurFaces=true');

    // Overlay boxes + recognized plate text still render over the redacted image.
    const overlay = screen.getByTestId('plate-overlay');
    expect(within(overlay).getByText('AB123CD')).toBeInTheDocument();

    // The "open original" link preserves the redaction toggle.
    const original = screen.getByText('Apri originale').closest('a') as HTMLAnchorElement;
    expect(original.getAttribute('href')).toBe('/api/plates/images/plate-1/original?blurFaces=true');
  });

  it('shows a safe unavailable message and disables the toggle when redaction is unavailable', async () => {
    installFetchMock({
      'GET /api/plates/images/plate-1': () => jsonResponse(redactionUnavailableDetail),
    });
    const { container } = renderDetail();
    await screen.findByTestId('plate-overlay');

    const toggle = screen.getByLabelText('Nascondi volti') as HTMLInputElement;
    expect(toggle.disabled).toBe(true);
    expect(screen.getByText(/Oscuramento dei volti non disponibile/i)).toBeInTheDocument();

    // Never requests a redacted image when unavailable.
    const img = container.querySelector('.plate-overlay-image') as HTMLImageElement;
    expect(img.getAttribute('src')).toBe('/api/plates/images/plate-1/preview');
  });

  it('does not render blob/model/owner internals', async () => {
    installFetchMock({ 'GET /api/plates/images/plate-1': () => jsonResponse(completedDetail) });
    const { container } = renderDetail();
    await screen.findByTestId('plate-overlay');

    const html = container.innerHTML.toLowerCase();
    for (const needle of ['storagekey', 'blobobjectid', 'owneruserid', 'sha256', 'polygonjson', 'modelpath']) {
      expect(html).not.toContain(needle);
    }
  });
});

describe('PlateAnalysisStatusBadge', () => {
  it.each([
    ['pending', 'In coda'],
    ['running', 'Analisi in corso'],
    ['completed', 'Analisi completata'],
    ['failed', 'Analisi non riuscita'],
  ])('renders the %s status label', (status, label) => {
    render(
      <AuthedWrapper>
        <PlateAnalysisStatusBadge status={status} />
      </AuthedWrapper>,
    );
    expect(screen.getByText(label)).toBeInTheDocument();
  });
});
