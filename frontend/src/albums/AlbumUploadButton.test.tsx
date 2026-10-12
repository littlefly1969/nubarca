import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AlbumUploadButton } from './AlbumUploadButton';
import { EngineWrapper, fileName, respond, testEngine } from '../uploads/testEngine';

afterEach(cleanup);

function photo(name: string): File {
  return new File([new Uint8Array(8)], name, { type: 'image/jpeg' });
}

describe('AlbumUploadButton', () => {
  it('uploads the picked photos into the album and reloads the album once per batch', async () => {
    const { manager, calls } = testEngine((req) => respond({ id: `id-${fileName(req)}`, name: fileName(req) }));
    const onLanded = vi.fn();
    render(
      <EngineWrapper manager={manager}>
        <AlbumUploadButton albumId="alb-1" albumName="Estate" onLanded={onLanded} />
      </EngineWrapper>,
    );

    expect(screen.getByRole('button', { name: 'Carica foto e video in questo album' })).toBeInTheDocument();
    const input = screen.getByLabelText('Seleziona foto e video da caricare nell’album') as HTMLInputElement;
    expect(input.accept).toBe('image/*,video/*');
    await userEvent.setup().upload(input, [photo('a.jpg'), photo('b.jpg'), photo('c.jpg')]);

    await waitFor(() => expect(onLanded).toHaveBeenCalledTimes(1));
    expect(calls.map((c) => c.url)).toEqual(Array(3).fill('/api/albums/alb-1/uploads'));
    expect(manager.getSnapshot().items.every((i) => i.target.kind === 'album' && i.target.label === 'Estate')).toBe(true);
  });

  it('does not reload for uploads that went elsewhere', async () => {
    const { manager } = testEngine();
    const onLanded = vi.fn();
    render(
      <EngineWrapper manager={manager}>
        <AlbumUploadButton albumId="alb-1" albumName="Estate" onLanded={onLanded} />
      </EngineWrapper>,
    );
    manager.enqueue([{ file: photo('a.jpg') }], { kind: 'album', albumId: 'alb-2', label: 'Altro' });
    await waitFor(() => expect(manager.getSnapshot().items[0].phase).toBe('done'));
    expect(onLanded).not.toHaveBeenCalled();
  });
});
