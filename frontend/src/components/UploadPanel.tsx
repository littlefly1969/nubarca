import { useRef, useState } from 'react';
import type { ChangeEvent, DragEvent } from 'react';
import { useI18n } from '../i18n';
import { useUploads } from '../uploads/UploadProvider';
import { filesFromDataTransfer } from '../uploads/collectFiles';
import type { UploadEntry } from '../uploads/engine/uploadManager';
import type { UploadTarget } from '../uploads/engine/targets';

// The pickers for one destination. The panel only HANDS files to the shared
// upload engine: progress, retries, pause and results live in the tray, which
// keeps going after this panel (or the whole page) is gone.

interface UploadPanelProps {
  target: UploadTarget;
  disabled?: boolean;
}

// A folder pick (webkitdirectory) sets webkitRelativePath on each File
// ("Holiday/2024/IMG_001.jpg"); a plain pick leaves it empty, which the
// server treats as a single file in the destination itself.
function relativePathOf(file: File): string {
  const rel = (file as File & { webkitRelativePath?: string }).webkitRelativePath;
  return typeof rel === 'string' ? rel : '';
}

export function UploadPanel({ target, disabled }: UploadPanelProps) {
  const { t } = useI18n();
  const { enqueue } = useUploads();
  const inputRef = useRef<HTMLInputElement>(null);
  const folderInputRef = useRef<HTMLInputElement>(null);
  const [isDragging, setIsDragging] = useState(false);
  const controlsDisabled = disabled === true;

  function onPicked(event: ChangeEvent<HTMLInputElement>) {
    const entries: UploadEntry[] = Array.from(event.target.files ?? []).map((file) => ({
      file,
      relativePath: relativePathOf(file),
    }));
    // Cleared so picking the same file again still fires a change.
    event.target.value = '';
    enqueue(entries, target);
  }

  function onDragOver(event: DragEvent<HTMLDivElement>) {
    event.preventDefault();
    // The page-wide drop target listens on the window too; this zone owns the drop.
    event.stopPropagation();
    if (!controlsDisabled) setIsDragging(true);
  }

  function onDrop(event: DragEvent<HTMLDivElement>) {
    event.preventDefault();
    event.stopPropagation();
    setIsDragging(false);
    if (controlsDisabled) return;
    void filesFromDataTransfer(event.dataTransfer).then((entries) => enqueue(entries, target));
  }

  return (
    <section className="upload-panel" aria-label={t('upload.aria')}>
      <div
        className={`upload-zone${isDragging ? ' upload-zone-dragging' : ''}${controlsDisabled ? ' upload-zone-disabled' : ''}`}
        onDragOver={onDragOver}
        onDragLeave={() => setIsDragging(false)}
        onDrop={onDrop}
        data-testid="upload-zone"
      >
        <p className="upload-zone-title">{t('upload.dropTo', { target: target.label })}</p>
        <p className="upload-zone-hint">{t('upload.dropHint')}</p>
        <div className="upload-button-row">
          <button
            type="button"
            className="upload-button"
            onClick={() => inputRef.current?.click()}
            disabled={controlsDisabled}
          >
            {t('upload.chooseFiles')}
          </button>
          <button
            type="button"
            className="upload-button upload-button-folder"
            onClick={() => folderInputRef.current?.click()}
            disabled={controlsDisabled}
          >
            {t('upload.chooseFolder')}
          </button>
        </div>
        <p className="upload-zone-note">{t('upload.hint')}</p>
        <input
          ref={inputRef}
          type="file"
          multiple
          className="upload-input"
          onChange={onPicked}
          disabled={controlsDisabled}
          aria-label={t('upload.selectFilesAria')}
        />
        <input
          ref={folderInputRef}
          type="file"
          multiple
          className="upload-input"
          onChange={onPicked}
          disabled={controlsDisabled}
          aria-label={t('upload.selectFolderAria')}
          // @ts-expect-error non-standard but widely supported directory picker
          webkitdirectory=""
          directory=""
        />
      </div>
    </section>
  );
}
