import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { UploadPanel } from './UploadPanel';
import { EngineWrapper, fileName, testEngine } from '../uploads/testEngine';
import type { FolderUploadTarget } from '../uploads/engine/targets';

afterEach(cleanup);

const TRIPS: FolderUploadTarget = { kind: 'folder', folderId: 'f-1', label: 'Viaggi' };
const ROOT: FolderUploadTarget = { kind: 'folder', folderId: null, label: 'File' };

function textFile(name: string, body = 'hello'): File {
  return new File([body], name, { type: 'text/plain' });
}

describe('UploadPanel', () => {
  it('names its destination and hands picked files to the engine', async () => {
    const { manager, calls } = testEngine();
    render(<EngineWrapper manager={manager}><UploadPanel target={TRIPS} /></EngineWrapper>);

    expect(screen.getByText('Carica in «Viaggi»')).toBeInTheDocument();
    await userEvent.setup().upload(screen.getByLabelText('Seleziona i file da caricare'), [textFile('a.txt'), textFile('b.txt')]);

    const tray = await screen.findByTestId('upload-tray');
    await waitFor(() => expect(within(tray).getAllByTestId('upload-row').every((r) => r.dataset.phase === 'done')).toBe(true));
    expect(within(tray).getByText('2 file caricati')).toBeInTheDocument();
    const posts = calls.filter((c) => c.method === 'POST');
    expect(posts.map((c) => c.url)).toEqual(['/api/folders/f-1/files', '/api/folders/f-1/files']);
    expect(posts.map(fileName).sort()).toEqual(['a.txt', 'b.txt']);
    expect((posts[0].body as FormData).get('conflict')).toBe('rename');
  });

  it('sends a root upload to /api/files', async () => {
    const { manager, calls } = testEngine();
    render(<EngineWrapper manager={manager}><UploadPanel target={ROOT} /></EngineWrapper>);
    await userEvent.setup().upload(screen.getByLabelText('Seleziona i file da caricare'), textFile('note.txt'));
    await waitFor(() => expect(calls.some((c) => c.url === '/api/files')).toBe(true));
  });

  it('keeps the folder structure of a folder pick', async () => {
    const { manager, calls } = testEngine();
    render(<EngineWrapper manager={manager}><UploadPanel target={TRIPS} /></EngineWrapper>);
    const picked = textFile('IMG_1.jpg');
    Object.defineProperty(picked, 'webkitRelativePath', { value: 'Estate/2026/IMG_1.jpg' });
    await userEvent.setup().upload(screen.getByLabelText('Seleziona una cartella da caricare'), picked);
    await waitFor(() => expect(calls.length).toBe(1));
    expect((calls[0].body as FormData).get('relativePath')).toBe('Estate/2026/IMG_1.jpg');
  });

  it('disables the pickers while the destination is not ready', () => {
    const { manager } = testEngine();
    render(<EngineWrapper manager={manager}><UploadPanel target={TRIPS} disabled /></EngineWrapper>);
    expect(screen.getByRole('button', { name: 'Scegli file…' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Scegli cartella…' })).toBeDisabled();
  });
});
