import { useCallback, useEffect, useRef, useState } from 'react';
import {
  createSubjectCutout, saveSubjectCutout, subjectPointOnPhoto, SubjectCutoutError,
  type SubjectCutoutFailure, type SubjectCutoutRegion, type SubjectPoint,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';
import type { MessageKey } from '../i18n/it';
import { useSubjectCutoutSupport } from './useSubjectCutoutAvailable';

// "Copy subject" for one of the owner's photos: the server lifts the subject
// off its background, and this dialog shows the result on a checkerboard and
// offers to copy it, download it, or keep it as a new file next to the photo.
//
// When the server offers it, tapping a subject in the cutout keeps only that
// subject (the tap is mapped back onto the photo through the cutout's region),
// and "All subjects" goes back. Copy, download and save act on what is shown.
//
// The dialog is a modal ON TOP of the media viewer, so it owns the keyboard
// while open (see keyboardOwnership): Escape closes the dialog, not the viewer.

type Phase =
  | { kind: 'working' }
  | { kind: 'ready'; blob: Blob; url: string; region: SubjectCutoutRegion | null }
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
  // The touched point on the photo, or null for every subject.
  const [point, setPoint] = useState<SubjectPoint | null>(null);
  const { pointSelection } = useSubjectCutoutSupport(true);
  const dialogRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    let url: string | null = null;
    setPhase({ kind: 'working' });
    setNotice(null);
    createSubjectCutout(fileId, { point, signal: controller.signal })
      .then(({ png, region }) => {
        if (controller.signal.aborted) return;
        url = URL.createObjectURL(png);
        setPhase({ kind: 'ready', blob: png, url, region });
      })
      .catch((e: unknown) => {
        if (controller.signal.aborted) return;
        setPhase({ kind: 'failed', reason: e instanceof SubjectCutoutError ? e.reason : 'failed' });
      });
    return () => {
      controller.abort();
      if (url !== null) URL.revokeObjectURL(url);
    };
  }, [fileId, attempt, point]);

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

  const canTap = pointSelection && phase.kind === 'ready' && phase.region !== null;

  const tap = useCallback((event: React.MouseEvent<HTMLImageElement>) => {
    if (!canTap || phase.kind !== 'ready' || phase.region === null) return;
    const box = event.currentTarget.getBoundingClientRect();
    if (box.width <= 0 || box.height <= 0) return;
    setPoint(subjectPointOnPhoto(
      phase.region, (event.clientX - box.left) / box.width, (event.clientY - box.top) / box.height));
  }, [canTap, phase]);

  const save = useCallback(async () => {
    setNotice({ kind: 'saving' });
    try {
      const saved = await saveSubjectCutout(fileId, point);
      setNotice({ kind: 'saved', name: saved.name });
    } catch (e) {
      setNotice({ kind: 'saveFailed', reason: e instanceof SubjectCutoutError ? e.reason : null });
    }
  }, [fileId, point]);

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
            <img
              src={phase.url}
              alt={t('subjectCutout.previewAlt')}
              data-testid="subject-cutout-image"
              className={canTap ? 'subject-cutout-tappable' : undefined}
              onClick={canTap ? tap : undefined}
            />
          )}
          {phase.kind === 'failed' && (
            <p className="subject-cutout-error" role="alert">
              {point !== null && phase.reason === 'noSubject'
                ? t('subjectCutout.error.noSubjectHere')
                : t(FAILURE_KEYS[phase.reason])}
            </p>
          )}
        </div>

        {canTap && (
          <p className="subject-cutout-hint" data-testid="subject-cutout-hint">
            {point === null ? t('subjectCutout.tapHint') : t('subjectCutout.tapAgainHint')}
          </p>
        )}

        <div className="subject-cutout-actions">
          {point !== null && (phase.kind !== 'working') && (
            <button type="button" onClick={() => setPoint(null)}>{t('subjectCutout.allSubjects')}</button>
          )}
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
          {phase.kind === 'failed' && phase.reason !== 'notFound' && phase.reason !== 'unavailable'
            && !(point !== null && phase.reason === 'noSubject') && (
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
