import { useRef, useState } from 'react';
import type { ChangeEvent, DragEvent } from 'react';
import { ApiError } from '@nubarca/api-client';
import { uploadFileToFolder, uploadRootFile } from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { formatSize } from './format';
import { useI18n } from '../i18n';
import type { I18nContextValue, MessageKey } from '../i18n';

type TranslateFn = I18nContextValue['t'];

export type UploadStatus =
  | 'pending'
  | 'uploading'
  | 'uploaded'
  | 'duplicate'
  | 'failed';

interface PendingUpload {
  id: string;
  name: string;
  // Slice 76: relative path within a folder upload (e.g. "sub/photo.jpg").
  // Empty for a plain file. Shown instead of the bare name when present so
  // the user can see the structure being created.
  relativePath: string;
  sizeBytes: number;
  status: UploadStatus;
  errorMessage?: string;
}

interface UploadPanelProps {
  // `null` means root.
  parentFolderId: string | null;
  // True while the parent folder listing is loading; we keep upload controls
  // disabled then so the post-upload refresh doesn't race the in-flight load.
  disabled?: boolean;
  // Called once the batch finishes (success, mixed, or all failed) so the
  // FolderBrowser can reload the current folder.
  onUploadsComplete: () => void;
}

const PILL_LABEL_KEY: Record<UploadStatus, MessageKey> = {
  pending: 'upload.pillQueued',
  uploading: 'upload.pillUploading',
  uploaded: 'upload.pillUploaded',
  duplicate: 'upload.pillDuplicate',
  failed: 'upload.pillFailed',
};

// Status pill rendered for each per-file row. Pure render helper.
function StatusPill({ status }: { status: UploadStatus }) {
  const { t } = useI18n();
  return <span className={`upload-pill upload-pill-${status}`}>{t(PILL_LABEL_KEY[status])}</span>;
}

export function UploadPanel({
  parentFolderId,
  disabled,
  onUploadsComplete,
}: UploadPanelProps) {
  const { invalidateAuth } = useAuth();
  const { t } = useI18n();
  const inputRef = useRef<HTMLInputElement>(null);
  const folderInputRef = useRef<HTMLInputElement>(null);
  const [pending, setPending] = useState<PendingUpload[]>([]);
  const [isUploading, setIsUploading] = useState(false);
  const [isDragging, setIsDragging] = useState(false);

  const controlsDisabled = disabled === true || isUploading;

  async function uploadAll(files: File[]) {
    if (files.length === 0) {
      return;
    }

    const batchId = Date.now();
    const items: PendingUpload[] = files.map((file, index) => ({
      id: `${batchId}-${index}`,
      name: file.name,
      // webkitRelativePath is set by a webkitdirectory picker; "" for a
      // plain file picker / drop.
      relativePath: relativePathOf(file),
      sizeBytes: file.size,
      status: 'pending',
    }));
    setPending(items);
    setIsUploading(true);

    // Sequential per-file loop. One stuck upload never blocks the next; per-
    // file errors are categorised below and shown in the result list.
    let invalidated = false;
    for (let i = 0; i < files.length; i++) {
      if (invalidated) {
        // Session went away mid-batch — leave remaining files as queued so
        // the user sees what didn't happen, but skip the network calls.
        setPending((prev) =>
          prev.map((p, idx) =>
            idx === i
              ? { ...p, status: 'failed', errorMessage: t('upload.sessionExpired') }
              : p,
          ),
        );
        continue;
      }

      const file = files[i];
      const id = items[i].id;
      setPending((prev) =>
        prev.map((p) => (p.id === id ? { ...p, status: 'uploading' } : p)),
      );

      try {
        const rel = relativePathOf(file);
        if (parentFolderId === null) {
          await uploadRootFile(file, undefined, rel);
        } else {
          await uploadFileToFolder(parentFolderId, file, undefined, rel);
        }
        setPending((prev) =>
          prev.map((p) => (p.id === id ? { ...p, status: 'uploaded' } : p)),
        );
      } catch (err) {
        const failure = classifyUploadError(err, t);
        if (failure.kind === 'auth') {
          invalidated = true;
          invalidateAuth();
        }
        setPending((prev) =>
          prev.map((p) =>
            p.id === id
              ? { ...p, status: failure.status, errorMessage: failure.message }
              : p,
          ),
        );
      }
    }

    setIsUploading(false);
    onUploadsComplete();
  }

  function onFileInputChange(event: ChangeEvent<HTMLInputElement>) {
    const files = Array.from(event.target.files ?? []);
    // Reset the input so picking the same file twice still triggers change.
    event.target.value = '';
    void uploadAll(files);
  }

  function onFolderInputChange(event: ChangeEvent<HTMLInputElement>) {
    const files = Array.from(event.target.files ?? []);
    event.target.value = '';
    void uploadAll(files);
  }

  function onDragOver(event: DragEvent<HTMLDivElement>) {
    if (controlsDisabled) return;
    event.preventDefault();
    if (!isDragging) {
      setIsDragging(true);
    }
  }

  function onDragLeave(event: DragEvent<HTMLDivElement>) {
    event.preventDefault();
    setIsDragging(false);
  }

  function onDrop(event: DragEvent<HTMLDivElement>) {
    event.preventDefault();
    setIsDragging(false);
    if (controlsDisabled) return;
    const files = Array.from(event.dataTransfer.files);
    void uploadAll(files);
  }

  function openPicker() {
    if (controlsDisabled) return;
    inputRef.current?.click();
  }

  function openFolderPicker() {
    if (controlsDisabled) return;
    folderInputRef.current?.click();
  }

  function clearResults() {
    setPending([]);
  }

  return (
    <section className="upload-panel" aria-label={t('upload.aria')}>
      <div
        className={`upload-zone${isDragging ? ' upload-zone-dragging' : ''}${
          controlsDisabled ? ' upload-zone-disabled' : ''
        }`}
        onDragOver={onDragOver}
        onDragLeave={onDragLeave}
        onDrop={onDrop}
      >
        <p className="upload-zone-hint">
          {t('upload.dropHint')}
        </p>
        <div className="upload-button-row">
          <button
            type="button"
            className="upload-button"
            onClick={openPicker}
            disabled={controlsDisabled}
          >
            {t('upload.chooseFiles')}
          </button>
          <button
            type="button"
            className="upload-button upload-button-folder"
            onClick={openFolderPicker}
            disabled={controlsDisabled}
          >
            {t('upload.chooseFolder')}
          </button>
        </div>
        <input
          ref={inputRef}
          type="file"
          multiple
          className="upload-input"
          onChange={onFileInputChange}
          disabled={controlsDisabled}
          aria-label={t('upload.selectFilesAria')}
        />
        {/* Slice 76: webkitdirectory picker preserves the folder structure.
            `webkitdirectory` isn't in the standard DOM typings, so it's set
            via a ref effect-free attribute spread below. */}
        <input
          ref={folderInputRef}
          type="file"
          multiple
          className="upload-input"
          onChange={onFolderInputChange}
          disabled={controlsDisabled}
          aria-label={t('upload.selectFolderAria')}
          // @ts-expect-error non-standard but widely supported directory picker
          webkitdirectory=""
          directory=""
        />
      </div>

      {pending.length > 0 && (
        <div className="upload-results">
          <div className="upload-results-header">
            <span>
              {isUploading
                ? t('upload.uploading')
                : t('upload.finished', { done: pending.filter((p) => p.status === 'uploaded').length, total: pending.length })}
            </span>
            {!isUploading && (
              <button
                type="button"
                className="upload-clear"
                onClick={clearResults}
              >
                {t('common.clear')}
              </button>
            )}
          </div>
          <ul className="upload-result-list" aria-live="polite">
            {pending.map((item) => (
              <li key={item.id} className="upload-result-item">
                <span
                  className="upload-result-name"
                  title={item.relativePath.length > 0 ? item.relativePath : item.name}
                >
                  {item.relativePath.length > 0 ? item.relativePath : item.name}
                </span>
                <span className="upload-result-size">{formatSize(item.sizeBytes)}</span>
                <StatusPill status={item.status} />
                {item.errorMessage !== undefined && (
                  <span className="upload-result-error" title={item.errorMessage}>
                    {item.errorMessage}
                  </span>
                )}
              </li>
            ))}
          </ul>
        </div>
      )}
    </section>
  );
}

// Slice 76: a folder picker (webkitdirectory) sets webkitRelativePath on each
// File (e.g. "Holiday/2024/IMG_001.jpg"); a plain file picker / drop leaves it
// empty. The backend treats an empty/absent path as a normal single-file
// upload, so this is safe to always read.
function relativePathOf(file: File): string {
  const rel = (file as File & { webkitRelativePath?: string }).webkitRelativePath;
  return typeof rel === 'string' ? rel : '';
}

type ClassifiedFailure =
  | { kind: 'duplicate'; status: 'duplicate'; message: string }
  | { kind: 'validation'; status: 'failed'; message: string }
  | { kind: 'auth'; status: 'failed'; message: string }
  | { kind: 'unknown'; status: 'failed'; message: string };

function classifyUploadError(err: unknown, t: TranslateFn): ClassifiedFailure {
  if (err instanceof ApiError) {
    if (err.status === 401) {
      return { kind: 'auth', status: 'failed', message: t('upload.sessionExpired') };
    }
    if (err.status === 409) {
      return {
        kind: 'duplicate',
        status: 'duplicate',
        message: t('upload.duplicate'),
      };
    }
    if (err.status === 413) {
      // Slice 65 + 78: upload too large (Kestrel limit, app-layer limit) or
      // per-user quota exceeded. Surface the backend's message verbatim when
      // present (slice 78 maps Kestrel's BadHttpRequestException to a clean
      // JSON body so the user sees a clear message instead of a generic error).
      const fromBody =
        typeof err.body === 'object' && err.body !== null && 'error' in err.body
          ? (err.body as { error?: unknown }).error
          : undefined;
      return {
        kind: 'validation',
        status: 'failed',
        message: typeof fromBody === 'string' && fromBody.length > 0
          ? fromBody
          : t('upload.tooLarge'),
      };
    }
    if (err.status === 400) {
      const fromBody =
        typeof err.body === 'object' && err.body !== null && 'error' in err.body
          ? (err.body as { error?: unknown }).error
          : undefined;
      return {
        kind: 'validation',
        status: 'failed',
        message: typeof fromBody === 'string' && fromBody.length > 0
          ? fromBody
          : t('upload.rejected'),
      };
    }
  }
  return {
    kind: 'unknown',
    status: 'failed',
    message: t('upload.genericError'),
  };
}
