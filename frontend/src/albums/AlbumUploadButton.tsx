import { useMemo, useRef } from 'react';
import type { ChangeEvent } from 'react';
import { useI18n } from '../i18n';
import { Icon } from '../components/icons/Icon';
import { useUploads, useUploadsSettled } from '../uploads/UploadProvider';
import { UploadDropTarget } from '../uploads/UploadDropTarget';
import type { AlbumUploadTarget } from '../uploads/engine/targets';

// "Carica" on an album: photos and videos picked here, dragged onto the page
// or pasted go straight into the album through the shared upload engine. The
// server files them under the uploader's "Album/<name>" folder and links the
// ones the uploader already has. `onLanded` runs once a batch has arrived.

interface Props {
  albumId: string;
  albumName: string;
  onLanded(): void;
  // False while a dialog covers the album, so a drop lands nowhere unexpected.
  dropEnabled?: boolean;
}

export function AlbumUploadButton({ albumId, albumName, onLanded, dropEnabled = true }: Props) {
  const { t } = useI18n();
  const { enqueue } = useUploads();
  const inputRef = useRef<HTMLInputElement>(null);
  const target = useMemo<AlbumUploadTarget>(
    () => ({ kind: 'album', albumId, label: albumName }),
    [albumId, albumName],
  );

  useUploadsSettled((candidate) => candidate.kind === 'album' && candidate.albumId === albumId, onLanded);

  function onPicked(event: ChangeEvent<HTMLInputElement>) {
    const files = Array.from(event.target.files ?? []);
    event.target.value = '';
    enqueue(files.map((file) => ({ file, relativePath: '' })), target);
  }

  return (
    <>
      <button
        type="button"
        className="row-action album-upload-button"
        data-testid="album-upload"
        aria-label={t('upload.albumButtonAria')}
        onClick={() => inputRef.current?.click()}
      >
        <Icon name="upload" size={18} />
        <span>{t('upload.albumButton')}</span>
      </button>
      <input
        ref={inputRef}
        type="file"
        multiple
        accept="image/*,video/*"
        className="upload-input"
        onChange={onPicked}
        aria-label={t('upload.selectAlbumMediaAria')}
        data-testid="album-upload-input"
      />
      <UploadDropTarget target={target} enabled={dropEnabled} />
    </>
  );
}
