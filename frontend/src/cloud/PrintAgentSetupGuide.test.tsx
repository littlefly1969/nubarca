import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { PrintAgentBundle } from '@nubarca/api-client';
import { AuthedWrapper } from '../test-utils';
import { PrintAgentSetupGuide, printBoxCommands } from './PrintAgentSetupGuide';

afterEach(() => { cleanup(); vi.restoreAllMocks(); });

const bundle: PrintAgentBundle = {
  available: true, sharing: true, canManage: false, minimumVersion: '0.2.2', version: '0.7.0',
  runtime: 'linux-x64', fileName: 'nubarca-print-agent-0.7.0-linux-x64.zip', sha256: 'b'.repeat(64),
  sizeBytes: 1, sourceCommit: 'c'.repeat(40),
  downloadPath: '/api/print-agent/download/nubarca-print-agent-0.7.0-linux-x64.zip',
};

function guide(props: Partial<Parameters<typeof PrintAgentSetupGuide>[0]> = {}) {
  const handlers = { onNewCode: vi.fn(), onDone: vi.fn(), onOpenStation: vi.fn() };
  render(
    <AuthedWrapper>
      <PrintAgentSetupGuide stationName="Sala" stationId="s-1" bundle={bundle} connectedVersion={null}
        enrollment={{ id: 's-1', name: 'Sala', enrollmentToken: 'tok', enrollmentExpiresAt: new Date(Date.now() + 90_000).toISOString() }}
        {...handlers} {...props} />
    </AuthedWrapper>,
  );
  return handlers;
}

describe('PrintAgentSetupGuide', () => {
  it('stops the extraction from failing on a running agent, and checks the download before it', () => {
    const { download, install } = printBoxCommands('https://example.test', 's-1', bundle);
    expect(download!.split('\n')).toEqual([
      'curl -fLO https://example.test/api/print-agent/download/nubarca-print-agent-0.7.0-linux-x64.zip',
      `echo "${'b'.repeat(64)}  nubarca-print-agent-0.7.0-linux-x64.zip" | sha256sum -c -`,
    ]);
    expect(install!.split('\n')[0]).toBe('sudo systemctl stop nubarca-print-agent@box 2>/dev/null || true');
    expect(install).toContain('--server https://example.test --station s-1');
  });

  it('counts the code down, and offers a new one once it has expired', async () => {
    const user = userEvent.setup();
    const { onNewCode } = guide();
    expect(screen.getByRole('timer')).toHaveTextContent(/Valido ancora 1:(29|30)/);
    cleanup();
    const expired = guide({ enrollment: { id: 's-1', name: 'Sala', enrollmentToken: 'tok',
      enrollmentExpiresAt: new Date(Date.now() - 1000).toISOString() } });
    expect(screen.getByText('Il codice è scaduto.')).toBeInTheDocument();
    expect(screen.queryByTestId('print-guide-code')).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Nuovo codice' }));
    expect(expired.onNewCode).toHaveBeenCalledOnce();
    expect(onNewCode).not.toHaveBeenCalled();
  });

  it('says when the box has connected, and opens its printer', async () => {
    const user = userEvent.setup();
    const { onOpenStation } = guide({ connectedVersion: '0.7.0' });
    expect(screen.getByTestId('print-guide-connected')).toHaveTextContent('Collegata: Print Agent 0.7.0.');
    expect(screen.queryByTestId('print-guide-waiting')).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Apri la stampante' }));
    expect(onOpenStation).toHaveBeenCalledOnce();
  });
});
