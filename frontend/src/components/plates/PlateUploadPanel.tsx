import { useRef, useState } from 'react';
import { ApiError, uploadPlateImage } from '@nubarca/api-client';
import { useAuth } from '../../auth/useAuth';
import { useI18n } from '../../i18n';
import type { I18nContextValue } from '../../i18n';

type TranslateFn = I18nContextValue['t'];

type PendingStatus = 'pending' | 'uploading' | 'uploaded' | 'duplicate' | 'failed';

interface PendingUpload {
  name: string;
  status: PendingStatus;
  message?: string;
}

interface Props {
  onUploaded: () => void;
}

// Multi-image upload for the owner-private Plates surface. Mirrors the library
// UploadPanel UX (per-file status + robust error classification) but targets the
// dedicated /api/plates/images endpoint, so nothing here creates a FileItem or a
// gallery entry.
export function PlateUploadPanel({ onUploaded }: Props) {
  const { invalidateAuth } = useAuth();
  const { t } = useI18n();
  const [pending, setPending] = useState<PendingUpload[]>([]);
  const [busy, setBusy] = useState(false);
  const [dragOver, setDragOver] = useState(false);
  const inputRef = useRef<HTMLInputElement | null>(null);

  const uploadAll = async (files: File[]) => {
    const images = files.filter((f) => f.type.startsWith('image/') || f.type === '');
    if (images.length === 0) return;

    setBusy(true);
    setPending(images.map((f) => ({ name: f.name, status: 'pending' as const })));

    let anyUploaded = false;
    for (let i = 0; i < images.length; i++) {
      setPending((prev) => prev.map((p, idx) => (idx === i ? { ...p, status: 'uploading' } : p)));
      try {
        await uploadPlateImage(images[i]);
        anyUploaded = true;
        setPending((prev) => prev.map((p, idx) => (idx === i ? { ...p, status: 'uploaded' } : p)));
      } catch (err) {
        const classified = classifyUploadError(err, t);
        if (classified.kind === 'auth') {
          setBusy(false);
          invalidateAuth();
          return;
        }
        setPending((prev) =>
          prev.map((p, idx) =>
            idx === i ? { ...p, status: classified.status, message: classified.message } : p,
          ),
        );
      }
    }

    setBusy(false);
    if (anyUploaded) onUploaded();
  };

  const onPick = (list: FileList | null) => {
    if (!list) return;
    void uploadAll(Array.from(list));
    if (inputRef.current) inputRef.current.value = '';
  };

  return (
    <section
      className={dragOver ? 'plate-upload plate-upload-dragover' : 'plate-upload'}
      aria-label={t('plates.uploadTitle')}
      onDragOver={(e) => {
        e.preventDefault();
        setDragOver(true);
      }}
      onDragLeave={() => setDragOver(false)}
      onDrop={(e) => {
        e.preventDefault();
        setDragOver(false);
        onPick(e.dataTransfer.files);
      }}
    >
      <h3>{t('plates.uploadTitle')}</h3>
      <p className="plate-upload-hint">{t('plates.uploadHint')}</p>
      <input
        ref={inputRef}
        type="file"
        accept="image/*"
        multiple
        aria-label={t('plates.selectFiles')}
        onChange={(e) => onPick(e.target.files)}
        disabled={busy}
      />
      {busy && <p className="plate-upload-busy">{t('plates.uploading')}</p>}
      {pending.length > 0 && (
        <ul className="plate-upload-list" data-testid="plate-upload-list">
          {pending.map((p, idx) => (
            <li key={`${p.name}-${idx}`} className={`plate-upload-item is-${p.status}`}>
              <span className="plate-upload-name">{p.name}</span>
              <span className="plate-upload-status">
                {p.status === 'uploaded' && t('plates.uploaded')}
                {p.status === 'duplicate' && t('plates.duplicate')}
                {p.status === 'failed' && (p.message ?? t('plates.failed'))}
                {(p.status === 'pending' || p.status === 'uploading') && '…'}
              </span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

type ClassifiedFailure =
  | { kind: 'duplicate'; status: 'duplicate'; message: string }
  | { kind: 'validation'; status: 'failed'; message: string }
  | { kind: 'auth'; status: 'failed'; message: string }
  | { kind: 'unknown'; status: 'failed'; message: string };

function classifyUploadError(err: unknown, t: TranslateFn): ClassifiedFailure {
  if (err instanceof ApiError) {
    if (err.status === 401) {
      return { kind: 'auth', status: 'failed', message: t('plates.sessionExpired') };
    }
    if (err.status === 409) {
      return { kind: 'duplicate', status: 'duplicate', message: t('plates.duplicate') };
    }
    const code =
      typeof err.body === 'object' && err.body !== null && 'error' in err.body
        ? (err.body as { error?: unknown }).error
        : undefined;
    if (err.status === 413) {
      return { kind: 'validation', status: 'failed', message: t('plates.tooLarge') };
    }
    if (err.status === 400) {
      return {
        kind: 'validation',
        status: 'failed',
        message: mapValidationCode(code, t),
      };
    }
  }
  return { kind: 'unknown', status: 'failed', message: t('plates.uploadGenericError') };
}

function mapValidationCode(code: unknown, t: TranslateFn): string {
  switch (code) {
    case 'not_an_image':
      return t('plates.notAnImage');
    case 'dimensions_too_large':
      return t('plates.dimensionsTooLarge');
    case 'too_large':
      return t('plates.tooLarge');
    default:
      return t('plates.uploadRejected');
  }
}
