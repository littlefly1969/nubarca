import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { EngineWrapper, fileName, queueHandle, respond, testEngine } from './testEngine';
import { useUploadLanded } from './UploadProvider';
import { UploadDropTarget } from './UploadDropTarget';
import type { FolderUploadTarget, UploadTarget } from './engine/targets';

afterEach(cleanup);

const TRIPS: FolderUploadTarget = { kind: 'folder', folderId: 'f-1', label: 'Viaggi' };

function photo(name: string): File {
  return new File([new Uint8Array(16)], name, { type: 'image/jpeg' });
}

describe('UploadTray', () => {
  it('stays out of the way until something is queued', () => {
    const { manager } = testEngine();
    const queue = queueHandle();
    render(<EngineWrapper manager={manager}><queue.Probe /></EngineWrapper>);
    expect(screen.queryByTestId('upload-tray')).not.toBeInTheDocument();
    expect(screen.queryByTestId('upload-tray-pill')).not.toBeInTheDocument();
  });

  it('explains a failure in words and retries it on request', async () => {
    let quotaFull = true;
    const { manager } = testEngine((req) => quotaFull
      ? respond({ error: 'quota', code: 'quota_exceeded' }, 413)
      : respond({ id: 'id-1', name: fileName(req) }));
    const queue = queueHandle();
    render(<EngineWrapper manager={manager}><queue.Probe /></EngineWrapper>);

    act(() => queue.enqueue([{ file: photo('mare.jpg') }], TRIPS));
    const tray = screen.getByTestId('upload-tray');
    expect(await within(tray).findByText('Spazio di archiviazione esaurito.')).toBeInTheDocument();
    expect(within(tray).getByText('0 caricati, 1 non riusciti')).toBeInTheDocument();

    quotaFull = false;
    await userEvent.setup().click(within(tray).getByTestId('upload-tray-retry-all'));
    await waitFor(() => expect(within(tray).getByTestId('upload-row').dataset.phase).toBe('done'));
  });

  it('tells the person a name clash was renamed', async () => {
    const { manager } = testEngine(() => respond({ id: 'id-1', name: 'mare (2).jpg' }));
    const queue = queueHandle();
    render(<EngineWrapper manager={manager}><queue.Probe /></EngineWrapper>);
    act(() => queue.enqueue([{ file: photo('mare.jpg') }], TRIPS));
    expect(await screen.findByText('Caricato come mare (2).jpg')).toBeInTheDocument();
  });

  it('minimizes to a pill and opens again', async () => {
    const { manager } = testEngine();
    const queue = queueHandle();
    render(<EngineWrapper manager={manager}><queue.Probe /></EngineWrapper>);
    act(() => queue.enqueue([{ file: photo('a.jpg') }], TRIPS));
    const user = userEvent.setup();
    await user.click(within(screen.getByTestId('upload-tray')).getByRole('button', { name: 'Riduci' }));
    const pill = screen.getByTestId('upload-tray-pill');
    expect(screen.queryByTestId('upload-tray')).not.toBeInTheDocument();
    await user.click(pill);
    expect(screen.getByTestId('upload-tray')).toBeInTheDocument();
  });

  it('pauses and resumes the whole queue', async () => {
    let release: (() => void) | null = null;
    const { manager } = testEngine((req) => new Promise((resolve) => {
      release = () => resolve(respond({ id: 'id-1', name: fileName(req) }));
    }));
    const queue = queueHandle();
    render(<EngineWrapper manager={manager}><queue.Probe /></EngineWrapper>);
    act(() => queue.enqueue([{ file: photo('a.jpg') }, { file: photo('b.jpg') }], TRIPS));
    const user = userEvent.setup();
    const tray = screen.getByTestId('upload-tray');
    await user.click(within(tray).getByRole('button', { name: 'Metti in pausa' }));
    expect(manager.getSnapshot().paused).toBe(true);
    expect(within(tray).getByRole('button', { name: 'Riprendi' })).toBeInTheDocument();
    await user.click(within(tray).getByRole('button', { name: 'Riprendi' }));
    expect(manager.getSnapshot().paused).toBe(false);
    await waitFor(() => expect(release).not.toBeNull());
  });
});

describe('useUploadLanded', () => {
  function Watcher({ matches, onLanded }: { matches: (t: UploadTarget) => boolean; onLanded: () => void }) {
    useUploadLanded(matches, onLanded, 0);
    return null;
  }

  it('reloads only the destination the files landed in, once per burst', async () => {
    const { manager } = testEngine();
    const here = vi.fn();
    const elsewhere = vi.fn();
    render(
      <EngineWrapper manager={manager}>
        <Watcher matches={(t) => t.kind === 'folder' && t.folderId === 'f-1'} onLanded={here} />
        <Watcher matches={(t) => t.kind === 'folder' && t.folderId === 'f-2'} onLanded={elsewhere} />
      </EngineWrapper>,
    );
    act(() => manager.enqueue([{ file: photo('a.jpg') }, { file: photo('b.jpg') }], TRIPS));
    await waitFor(() => expect(here).toHaveBeenCalled());
    expect(elsewhere).not.toHaveBeenCalled();
  });
});

describe('UploadDropTarget', () => {
  function paste(files: File[]) {
    const event = new Event('paste', { bubbles: true, cancelable: true }) as Event & { clipboardData: unknown };
    event.clipboardData = { files };
    window.dispatchEvent(event);
  }

  it('uploads pasted files to the destination on screen', async () => {
    const { manager, calls } = testEngine();
    render(<EngineWrapper manager={manager}><UploadDropTarget target={TRIPS} /></EngineWrapper>);
    act(() => paste([photo('screenshot.png')]));
    await waitFor(() => expect(calls.map((c) => c.url)).toEqual(['/api/folders/f-1/files']));
  });

  it('leaves a paste into a text field alone', () => {
    const { manager } = testEngine();
    render(<EngineWrapper manager={manager}><input aria-label="nome" /><UploadDropTarget target={TRIPS} /></EngineWrapper>);
    screen.getByLabelText('nome').focus();
    act(() => paste([photo('screenshot.png')]));
    expect(manager.getSnapshot().items).toHaveLength(0);
  });

  it('shows where a drag will land and hides when the drag leaves', () => {
    const { manager } = testEngine();
    render(<EngineWrapper manager={manager}><UploadDropTarget target={TRIPS} /></EngineWrapper>);
    const dataTransfer = { types: ['Files'], dropEffect: 'none' };
    fireEvent.dragEnter(window, { dataTransfer });
    expect(screen.getByText('Rilascia per caricare in «Viaggi»')).toBeInTheDocument();
    fireEvent.dragLeave(window, { dataTransfer });
    expect(screen.queryByTestId('upload-drop-overlay')).not.toBeInTheDocument();
  });

  it('does nothing while disabled', () => {
    const { manager } = testEngine();
    render(<EngineWrapper manager={manager}><UploadDropTarget target={TRIPS} enabled={false} /></EngineWrapper>);
    act(() => paste([photo('a.png')]));
    expect(manager.getSnapshot().items).toHaveLength(0);
  });
});
