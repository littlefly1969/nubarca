import { useCallback, useEffect, useRef, useState } from 'react';
import {
  createSubjectCutout, saveSubjectCutout, SubjectCutoutError, type SubjectCutoutFailure,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';
import type { MessageKey } from '../i18n/it';

// "Copy subject" for one of the owner's photos: the server lifts the subject
// off its background, and this dialog shows the result on a checkerboard and
// offers to copy it, download it, or keep it as a new file next to the photo.
//
// The dialog is a modal ON TOP of the media viewer, so it owns the keyboard
// while open (see keyboardOwnership): Escape closes the dialog, not the viewer.

type Phase =
  | { kind: 'working' }
  | { kind: 'ready'; blob: Blob; url: string }
  | { kind: 'failed'; reason: SubjectCutoutFailure };

type Notice =
  | { kind: 'copied' }
  | { kind: 'copyFailed' }
  | { kind: 'saving' }
  | { kind: 'saved'; name: string }
  | { kind: 'saveFailed'; reason: SubjectCutoutFailure | null };

const FAILURE_KEYS: Record<SubjectCutoutFailure, MessageKey> = {
  noSubject: 'subjectCutout.error.noSubject',
  busy: 'subjectCutout.error.busy',
  unavailable: 'subjectCutout.error.unavailable',
  notFound: 'subjectCutout.error.notFound',
  failed: 'subjectCutout.error.failed',
};

export interface SubjectCutoutDialogProps {
  fileId: string;
  // The photo's file name, used for the downloaded PNG's name.
  fileName: string;
  onClose: () => void;
}

export function SubjectCutoutDialog({ fileId, fileName, onClose }: SubjectCutoutDialogProps) {
  const { t } = useI18n();
  const [phase, setPhase] = useState<Phase>({ kind: 'working' });
  const [notice, setNotice] = useState<Notice | null>(null);
  const [attempt, setAttempt] = useState(0);
  const dialogRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    let url: string | null = null;
    setPhase({ kind: 'working' });
    setNotice(null);
    createSubjectCutout(fileId, controller.signal)
      .then((blob) => {
        if (controller.signal.aborted) return;
        url = URL.createObjectURL(blob);
        setPhase({ kind: 'ready', blob, url });
      })
      .catch((e: unknown) => {
        if (controller.signal.aborted) return;
        setPhase({ kind: 'failed', reason: e instanceof SubjectCutoutError ? e.reason : 'failed' });
      });
    return () => {
      controller.abort();
      if (url !== null) URL.revokeObjectURL(url);
    };
  }, [fileId, attempt]);

  useEffect(() => { dialogRef.current?.focus(); }, []);

  const onKeyDown = useCallback((event: React.KeyboardEvent) => {
    if (event.key === 'Escape') {
      event.stopPropagation();
      onClose();
    }
  }, [onClose]);

  const canCopy = typeof navigator !== 'undefined'
    && typeof navigator.clipboard?.write === 'function'
    && typeof ClipboardItem !== 'undefined';

  const copy = useCallback(async () => {
    if (phase.kind !== 'ready') return;
    try {
      await navigator.clipboard.write([new ClipboardItem({ 'image/png': phase.blob })]);
      setNotice({ kind: 'copied' });
    } catch {
      setNotice({ kind: 'copyFailed' });
    }
  }, [phase]);

  const save = useCallback(async () => {
    setNotice({ kind: 'saving' });
    try {
      const saved = await saveSubjectCutout(fileId);
      setNotice({ kind: 'saved', name: saved.name });
    } catch (e) {
      setNotice({ kind: 'saveFailed', reason: e instanceof SubjectCutoutError ? e.reason : null });
    }
  }, [fileId]);

  return (
    <div
      ref={dialogRef}
      className="subject-cutout-dialog"
      role="dialog"
      aria-modal="true"
      aria-label={t('subjectCutout.action')}
      tabIndex={-1}
      onKeyDown={onKeyDown}
      data-testid="subject-cutout-dialog"
    >
      <div className="subject-cutout-panel">
        <div className="subject-cutout-head">
          <h3>{t('subjectCutout.action')}</h3>
          <button type="button" aria-label={t('common.close')} onClick={onClose}>✕</button>
        </div>

        <div className="subject-cutout-stage">
          {phase.kind === 'working' && (
            <p className="subject-cutout-working" role="status">{t('subjectCutout.working')}</p>
          )}
          {phase.kind === 'ready' && (
            <img src={phase.url} alt={t('subjectCutout.previewAlt')} data-testid="subject-cutout-image" />
          )}
          {phase.kind === 'failed' && (
            <p className="subject-cutout-error" role="alert">{t(FAILURE_KEYS[phase.reason])}</p>
          )}
        </div>

        <div className="subject-cutout-actions">
          {phase.kind === 'ready' && (
            <>
              {canCopy && (
                <button type="button" onClick={() => void copy()}>{t('subjectCutout.copy')}</button>
              )}
              <a href={phase.url} download={cutoutDownloadName(fileName)}>{t('common.download')}</a>
              <button
                type="button"
                onClick={() => void save()}
                disabled={notice?.kind === 'saving' || notice?.kind === 'saved'}
              >
                {notice?.kind === 'saving' ? t('subjectCutout.saving') : t('subjectCutout.save')}
              </button>
            </>
          )}
          {phase.kind === 'failed' && phase.reason !== 'notFound' && phase.reason !== 'unavailable' && (
            <button type="button" onClick={() => setAttempt((n) => n + 1)}>{t('common.retry')}</button>
          )}
        </div>

        {notice !== null && notice.kind !== 'saving' && (
          <p className="subject-cutout-notice" role="status" data-testid="subject-cutout-notice">
            {noticeText(notice, t)}
          </p>
        )}
      </div>
    </div>
  );
}

function noticeText(
  notice: Exclude<Notice, { kind: 'saving' }>,
  t: (key: MessageKey, params?: Record<string, string | number>) => string,
): string {
  switch (notice.kind) {
    case 'copied': return t('subjectCutout.copied');
    case 'copyFailed': return t('subjectCutout.copyFailed');
    case 'saved': return t('subjectCutout.saved', { name: notice.name });
    case 'saveFailed':
      return notice.reason === null || notice.reason === 'failed'
        ? t('subjectCutout.saveFailed')
        : t(FAILURE_KEYS[notice.reason]);
  }
}

// "IMG_0001.HEIC" → "IMG_0001-cutout.png", the same name a saved copy gets.
export function cutoutDownloadName(fileName: string): string {
  const dot = fileName.lastIndexOf('.');
  const stem = dot >= 0 ? fileName.slice(0, dot) : fileName;
  return `${stem.trim() === '' ? 'photo' : stem}-cutout.png`;
}
