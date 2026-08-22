import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { ApiError, getFaceContext, ignoreFace, listPeople, type FaceContext, type Person } from '@nubarca/api-client';
import { mediumPreviewUrl } from '../files/types';
import { useAuth } from '../../auth/useAuth';
import { AssignToPersonMenu } from './AssignToPersonMenu';
import { isEditableKeyboardTarget, ownsKeyboardEvent } from '../keyboardOwnership';
import { useI18n } from '../../i18n';

const MIN_ZOOM = 1;
const MAX_ZOOM = 8;

// Owner-private full-photo context viewer for a selected face. Shows the medium
// preview with the selected face highlighted (others subtle), supports
// zoom/pan/fit/focus, and previous/next navigation across the opening list.
// Never loads the original bytes; never renders internals.
//
// `onFaceIgnored` / `onFaceRestored` report a face LEAVING or REJOINING the pool
// the caller opened this with. The caller owns `faceIds`, so it is the only place
// that can drop the face from the sequence and from the grid behind — which is
// why this is an explicit typed callback and not a refresh the viewer does to
// itself. See faceViewerSequence.ts for the shared "what happens to the list"
// rule both People surfaces use.
export function FaceContextViewer({
  faceIds,
  index,
  onIndexChange,
  onClose,
  onFaceIgnored,
  onFaceRestored,
  onFaceAssigned,
  progressLabel,
  extraActions,
}: {
  faceIds: string[];
  index: number;
  onIndexChange: (next: number) => void;
  onClose: () => void;
  onFaceIgnored?: (faceId: string) => void;
  onFaceRestored?: (faceId: string) => void;
  // A face was given to a person. Reported separately from onChanged because for
  // a queue that is the same event as an ignore — the face has been DECIDED and
  // leaves the work — while for a plain viewer it is just a label change. Without
  // it a review queue sits on a face it has already assigned.
  onFaceAssigned?: (faceId: string) => void;
  // What the caller is counting through, when it is counting something more
  // specific than "face n of m" — the photo review says "undecided face 1 of 3".
  progressLabel?: string;
  // Actions that belong to the CALLER's workflow, not to looking at a face:
  // "skip", "ignore every undecided face on this photo". They live here so the
  // reviewer reaches them without leaving the photo, and stay out of this
  // component so it does not learn about queues it does not own.
  extraActions?: ReactNode;
}) {
  const { invalidateAuth } = useAuth();
  const { t } = useI18n();
  const [activeFaceId, setActiveFaceId] = useState(faceIds[index]);
  const [ctx, setCtx] = useState<FaceContext | null>(null);
  const [status, setStatus] = useState<'loading' | 'ready' | 'error'>('loading');
  const [people, setPeople] = useState<Person[]>([]);
  const [refreshTick, setRefreshTick] = useState(0);
  const [ignoring, setIgnoring] = useState(false);
  const [zoom, setZoom] = useState(1);
  const [pan, setPan] = useState({ x: 0, y: 0 });
  const canvasRef = useRef<HTMLDivElement | null>(null);
  const rootRef = useRef<HTMLDivElement | null>(null);
  const drag = useRef<{ x: number; y: number; panX: number; panY: number } | null>(null);

  // The displayed face follows the nav list unless the user clicks another box.
  useEffect(() => setActiveFaceId(faceIds[index]), [faceIds, index]);

  const focusFace = useCallback((box: FaceContext['selectedBox']) => {
    const el = canvasRef.current;
    const cw = el?.clientWidth ?? 0;
    const ch = el?.clientHeight ?? 0;
    const targetZoom = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, 0.6 / Math.max(box.width, box.height || 0.0001)));
    const fx = box.x + box.width / 2;
    const fy = box.y + box.height / 2;
    setZoom(targetZoom);
    setPan({ x: (0.5 - fx) * cw * targetZoom, y: (0.5 - fy) * ch * targetZoom });
  }, []);

  const fitImage = useCallback(() => {
    setZoom(1);
    setPan({ x: 0, y: 0 });
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    setStatus('loading');
    (async () => {
      try {
        const c = await getFaceContext(activeFaceId, controller.signal);
        if (controller.signal.aborted) return;
        setCtx(c);
        setStatus('ready');
      } catch (err) {
        if (controller.signal.aborted) return;
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        setStatus('error');
      }
    })();
    return () => controller.abort();
  }, [activeFaceId, invalidateAuth, refreshTick]);

  // People list for the assign menu (loaded once; refreshed after a change).
  // Ignore the face currently highlighted in the photo — and only that one.
  //
  // The caller is told through onFaceIgnored, exactly as when the action came
  // from inside the assign menu: this viewer does not own the sequence, so it
  // cannot decide whether to advance or close. Reporting rather than refetching
  // also avoids asking the server for a face we just removed from the pool.
  const ignoreSelected = useCallback(async () => {
    if (!ctx || ignoring) return;
    const faceId = ctx.selectedFaceId;
    setIgnoring(true);
    try {
      await ignoreFace(faceId);
      if (onFaceIgnored) onFaceIgnored(faceId);
      else setRefreshTick((n) => n + 1);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) invalidateAuth();
      // Any other failure leaves the face exactly where it was; the button
      // re-enables and the reviewer can try again or move on.
    } finally {
      setIgnoring(false);
    }
  }, [ctx, ignoring, onFaceIgnored, invalidateAuth]);

  const loadPeople = useCallback(() => {
    void listPeople().then(setPeople).catch(() => { /* non-fatal for the viewer */ });
  }, []);
  useEffect(() => { loadPeople(); }, [loadPeople]);

  // The viewer opens at 100% (fit) with all boxes visible — the selected face is
  // highlighted but NOT auto-zoomed. The user focuses it explicitly via "Centra
  // volto". (No auto-focus effect on open.)

  const hasPrev = index > 0;
  const hasNext = index < faceIds.length - 1;

  // Shortcuts live on `window` so the photo answers arrows wherever focus is —
  // which means this viewer must decide for itself when a key is NOT its own.
  // A modal opened on top of it (assign/move) owns the keyboard entirely, and an
  // editable target owns its arrows as caret moves. See keyboardOwnership.ts.
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (!ownsKeyboardEvent(rootRef.current, e.target)) return;
      if (e.key === 'Escape') onClose();
      else if (isEditableKeyboardTarget(e.target)) return;
      else if (e.key === 'ArrowLeft' && hasPrev) onIndexChange(index - 1);
      else if (e.key === 'ArrowRight' && hasNext) onIndexChange(index + 1);
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [index, hasPrev, hasNext, onIndexChange, onClose]);

  function onPointerDown(e: React.PointerEvent) {
    drag.current = { x: e.clientX, y: e.clientY, panX: pan.x, panY: pan.y };
    (e.target as Element).setPointerCapture?.(e.pointerId);
  }
  function onPointerMove(e: React.PointerEvent) {
    if (!drag.current) return;
    setPan({ x: drag.current.panX + (e.clientX - drag.current.x), y: drag.current.panY + (e.clientY - drag.current.y) });
  }
  function onPointerUp() {
    drag.current = null;
  }

  function zoomBy(factor: number) {
    setZoom((z) => Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, z * factor)));
  }

  return (
    <div className="face-viewer" role="dialog" aria-modal="true" aria-label={t('face.viewerAria')} ref={rootRef}>
      <button type="button" className="face-viewer-backdrop" aria-label={t('common.close')} onClick={onClose} />

      <div
        className="face-viewer-stage"
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        onPointerLeave={onPointerUp}
      >
        {status === 'loading' && <p className="muted" role="status">{t('common.loading')}</p>}
        {status === 'error' && <p className="folder-error" role="alert">{t('face.photoUnavailable')}</p>}
        {status === 'ready' && ctx && (
          <div
            ref={canvasRef}
            className="face-viewer-canvas"
            style={{ transform: `translate(${pan.x}px, ${pan.y}px) scale(${zoom})` }}
          >
            <img className="face-viewer-image" src={mediumPreviewUrl(ctx.fileItemId)} alt={ctx.fileName} draggable={false} />
            {ctx.faces.map((fb) => {
              const selected = fb.faceId === ctx.selectedFaceId;
              return (
                <button
                  key={fb.faceId}
                  type="button"
                  className={selected ? 'face-viewer-box is-selected' : 'face-viewer-box'}
                  style={{
                    left: `${fb.box.x * 100}%`,
                    top: `${fb.box.y * 100}%`,
                    width: `${fb.box.width * 100}%`,
                    height: `${fb.box.height * 100}%`,
                  }}
                  aria-label={selected ? t('face.selectedFace') : t('face.otherFaces')}
                  onClick={(e) => {
                    e.stopPropagation();
                    if (!selected) setActiveFaceId(fb.faceId);
                  }}
                />
              );
            })}
          </div>
        )}
      </div>

      <div className="face-viewer-toolbar">
        <div className="face-viewer-title">
          {ctx?.personName ? <strong>{ctx.personName}</strong> : <span className="muted">{t('face.notAssigned')}</span>}
          <span className="muted">{ctx?.fileName}</span>
          {ctx && !ctx.isIgnored && (
            // Ignore is ALSO inside the assign menu, and that is where it was
            // only reachable: a two-step action for the decision a reviewer
            // makes most often after "this is X" — "this is nobody worth
            // naming". One click, beside the other one.
            <button
              type="button"
              className="face-viewer-ignore"
              disabled={ignoring}
              onClick={() => { void ignoreSelected(); }}
            >
              {t('face.ignoreFace')}
            </button>
          )}
          {ctx && (
            <AssignToPersonMenu
              faceId={ctx.selectedFaceId}
              people={people}
              currentPersonId={ctx.personId}
              currentPersonName={ctx.personName}
              // Ignore has MOVED OUT of the menu and into the button above:
              // offering it in both places is the same action twice in one
              // toolbar. The menu keeps it only for an already-ignored face,
              // where what it offers is "Ripristina" and the explicit button
              // does not apply.
              allowIgnore={ctx.isIgnored}
              isIgnored={ctx.isIgnored}
              onChanged={(personId) => {
                // personId === null is a REMOVAL from a person, which puts the
                // face back into the undecided pool rather than taking it out.
                if (personId !== null && onFaceAssigned) {
                  onFaceAssigned(ctx.selectedFaceId);
                  return;
                }
                setRefreshTick((t) => t + 1);
                loadPeople();
              }}
              onIgnored={(id) => {
                // No refetch of the face we just removed from the pool: the
                // caller drops it from the sequence and this viewer either
                // advances or closes.
                if (onFaceIgnored) onFaceIgnored(id);
                else setRefreshTick((t) => t + 1);
              }}
              onRestored={(id) => {
                if (onFaceRestored) onFaceRestored(id);
                else setRefreshTick((t) => t + 1);
              }}
              invalidateAuth={invalidateAuth}
            />
          )}
        </div>
        <div className="face-viewer-controls">
          {progressLabel && <span className="face-viewer-progress">{progressLabel}</span>}
          {extraActions}
          <button type="button" onClick={fitImage}>{t('face.showWholePhoto')}</button>
          <button type="button" onClick={() => ctx && focusFace(ctx.selectedBox)}>{t('face.centerFace')}</button>
          <button type="button" aria-label={t('face.zoomOut')} onClick={() => zoomBy(1 / 1.25)}>−</button>
          <span className="face-viewer-zoom" aria-label={t('face.zoom')}>{Math.round(zoom * 100)}%</span>
          <button type="button" aria-label={t('face.zoomIn')} onClick={() => zoomBy(1.25)}>+</button>
          {faceIds.length > 1 && (
            <>
              <button type="button" disabled={!hasPrev} onClick={() => onIndexChange(index - 1)}>{t('face.prevFace')}</button>
              <button type="button" disabled={!hasNext} onClick={() => onIndexChange(index + 1)}>{t('face.nextFace')}</button>
            </>
          )}
          <button type="button" className="face-viewer-close" aria-label={t('common.close')} onClick={onClose}>✕</button>
        </div>
      </div>
    </div>
  );
}
