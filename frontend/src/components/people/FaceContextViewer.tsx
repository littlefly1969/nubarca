import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError, getFaceContext, ignoreFace, listPeople, type FaceContext, type Person } from '@nubarca/api-client';
import { mediumPreviewUrl } from '../files/types';
import { useAuth } from '../../auth/useAuth';
import { Icon } from '../icons/Icon';
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
// The chrome is organised by PURPOSE, because the controls in here answer three
// completely different questions and used to sit in one undifferentiated row:
//
//   top      — where am I: close, file name, review progress, photo-level
//              navigation, and the secondary bulk action
//   edges    — which FACE am I looking at: prev/next, on the image itself
//   bottom L — how am I looking at it: fit, focus, zoom (viewport tools)
//   bottom R — what do I decide about it: assign (primary), ignore, skip
//
// `onFaceIgnored` / `onFaceRestored` report a face LEAVING or REJOINING the pool
// the caller opened this with. The caller owns `faceIds`, so it is the only place
// that can drop the face from the sequence and from the grid behind — which is
// why this is an explicit typed callback and not a refresh the viewer does to
// itself. See faceViewerSequence.ts for the shared "what happens to the list"
// rule both People surfaces use.

// The workflow a QUEUE owner adds on top of looking at a face.
//
// A structured contract, deliberately not a ReactNode bucket: with an arbitrary
// slot the caller's markup decided where its buttons landed, which is how "skip"
// and "ignore every face on this photo" ended up wedged between the zoom
// controls. The viewer owns the visual hierarchy; the caller owns the queue.
export interface FaceReviewControls {
  /** "Undecided face 1 of 3" — what the caller is actually counting through. */
  progressLabel: string;
  /** Leave this face undecided and move to another one on the SAME photo. */
  canSkipFace: boolean;
  onSkipFace(): void;
  /**
   * Open the next photo in the queue. NOT a completion: the current photo keeps
   * its undecided faces and its place in the list. Disabled when there is no
   * next loaded photo — it never wraps.
   */
  canNextPhoto: boolean;
  onNextPhoto(): void;
  /** ONE bulk operation over the current photo's still-unassigned faces. */
  onIgnoreRemaining(): void;
  ignoreRemainingBusy: boolean;
}

export function FaceContextViewer({
  faceIds,
  index,
  onIndexChange,
  onClose,
  onFaceIgnored,
  onFaceRestored,
  onFaceAssigned,
  reviewControls,
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
  // Present only when a review queue opened this viewer. The People grids pass
  // nothing and get no workflow chrome at all.
  reviewControls?: FaceReviewControls;
}) {
  const { invalidateAuth } = useAuth();
  const { t } = useI18n();
  const [activeFaceId, setActiveFaceId] = useState(faceIds[index]);
  const [ctx, setCtx] = useState<FaceContext | null>(null);
  const [status, setStatus] = useState<'loading' | 'ready' | 'error'>('loading');
  const [people, setPeople] = useState<Person[]>([]);
  const [refreshTick, setRefreshTick] = useState(0);
  const [ignoring, setIgnoring] = useState(false);
  const [moreOpen, setMoreOpen] = useState(false);
  const [zoom, setZoom] = useState(1);
  const [pan, setPan] = useState({ x: 0, y: 0 });
  const canvasRef = useRef<HTMLDivElement | null>(null);
  const rootRef = useRef<HTMLDivElement | null>(null);
  const moreRef = useRef<HTMLDivElement | null>(null);
  const moreTriggerRef = useRef<HTMLButtonElement | null>(null);
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

  // People list for the assign menu (loaded once; refreshed after a change).
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

  // The overflow menu closes on Escape and on an outside click, returning focus
  // to its trigger. Its Escape is stopped so it does not also close the viewer.
  useEffect(() => {
    if (!moreOpen) return;
    function onDocPointer(e: MouseEvent) {
      if (moreRef.current && !moreRef.current.contains(e.target as Node)) setMoreOpen(false);
    }
    function onKey(e: KeyboardEvent) {
      if (e.key !== 'Escape') return;
      e.stopPropagation();
      setMoreOpen(false);
      moreTriggerRef.current?.focus();
    }
    document.addEventListener('mousedown', onDocPointer);
    document.addEventListener('keydown', onKey, true);
    return () => {
      document.removeEventListener('mousedown', onDocPointer);
      document.removeEventListener('keydown', onKey, true);
    };
  }, [moreOpen]);

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

      {/* ---- Top chrome: where am I -------------------------------------- */}
      <header className="face-viewer-top">
        <button
          type="button"
          className="face-viewer-icon-button"
          aria-label={t('common.close')}
          onClick={onClose}
        >
          <Icon name="close" size={18} />
        </button>

        <div className="face-viewer-identity">
          {ctx?.personName
            ? <strong className="face-viewer-person">{ctx.personName}</strong>
            : <span className="face-viewer-person is-unassigned">{t('face.notAssigned')}</span>}
          <span className="face-viewer-file" title={ctx?.fileName}>{ctx?.fileName}</span>
        </div>

        {reviewControls && (
          <div className="face-viewer-top-review">
            <span className="face-viewer-progress" data-testid="face-viewer-progress">
              {reviewControls.progressLabel}
            </span>
            <button
              type="button"
              className="face-viewer-chrome-button"
              data-testid="face-viewer-next-photo"
              disabled={!reviewControls.canNextPhoto}
              onClick={reviewControls.onNextPhoto}
            >
              <Icon name="next-photo" size={16} />
              <span>{t('people.photoReviewNextPhoto')}</span>
            </button>

            {/* The bulk "ignore everything still undecided here" is a real
                shortcut, but it decides a whole photo at once — it belongs
                behind an overflow, not beside the per-face decisions. */}
            <div className="face-viewer-more" ref={moreRef}>
              <button
                ref={moreTriggerRef}
                type="button"
                className="face-viewer-icon-button"
                data-testid="face-viewer-more"
                aria-label={t('face.moreActions')}
                aria-haspopup="menu"
                aria-expanded={moreOpen}
                onClick={() => setMoreOpen((v) => !v)}
              >
                <Icon name="more" size={18} />
              </button>
              {moreOpen && (
                <ul className="face-viewer-more-list" role="menu" aria-label={t('face.moreActions')}>
                  <li role="none">
                    <button
                      type="button"
                      role="menuitem"
                      className="face-viewer-more-item"
                      disabled={reviewControls.ignoreRemainingBusy}
                      onClick={() => {
                        setMoreOpen(false);
                        moreTriggerRef.current?.focus();
                        reviewControls.onIgnoreRemaining();
                      }}
                    >
                      <Icon name="eye-off" size={16} />
                      <span>{t('people.photoReviewIgnoreAll')}</span>
                    </button>
                  </li>
                </ul>
              )}
            </div>
          </div>
        )}
      </header>

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

        {/* ---- Face navigation, ON the image ----------------------------- */}
        {faceIds.length > 1 && (
          <>
            <button
              type="button"
              className="face-viewer-edge is-prev"
              aria-label={t('face.prevFace')}
              disabled={!hasPrev}
              onClick={() => onIndexChange(index - 1)}
            >
              <Icon name="chevron-left" size={22} />
            </button>
            <button
              type="button"
              className="face-viewer-edge is-next"
              aria-label={t('face.nextFace')}
              disabled={!hasNext}
              onClick={() => onIndexChange(index + 1)}
            >
              <Icon name="chevron-right" size={22} />
            </button>
          </>
        )}
      </div>

      {/* ---- Bottom chrome: viewport tools | decisions -------------------- */}
      <footer className="face-viewer-bottom">
        <div className="face-viewer-tools" role="group" aria-label={t('face.viewToolsAria')}>
          <button type="button" className="face-viewer-tool" onClick={fitImage}>
            <Icon name="fit" size={16} />
            <span>{t('face.showWholePhoto')}</span>
          </button>
          <button
            type="button"
            className="face-viewer-tool"
            onClick={() => ctx && focusFace(ctx.selectedBox)}
          >
            <Icon name="focus" size={16} />
            <span>{t('face.centerFace')}</span>
          </button>
          <span className="face-viewer-zoom-group">
            <button
              type="button"
              className="face-viewer-icon-button"
              aria-label={t('face.zoomOut')}
              onClick={() => zoomBy(1 / 1.25)}
            >
              <Icon name="minus" size={16} />
            </button>
            <span className="face-viewer-zoom" aria-label={t('face.zoom')}>{Math.round(zoom * 100)}%</span>
            <button
              type="button"
              className="face-viewer-icon-button"
              aria-label={t('face.zoomIn')}
              onClick={() => zoomBy(1.25)}
            >
              <Icon name="plus" size={16} />
            </button>
          </span>
        </div>

        {ctx && (
          <div className="face-viewer-decisions" role="group" aria-label={t('face.decisionsAria')}>
            {reviewControls && (
              <button
                type="button"
                className="face-viewer-tertiary"
                data-testid="face-viewer-skip"
                disabled={!reviewControls.canSkipFace}
                onClick={reviewControls.onSkipFace}
              >
                <Icon name="skip" size={16} />
                <span>{t('people.photoReviewSkip')}</span>
              </button>
            )}
            {!ctx.isIgnored && (
              // Ignore is ALSO inside the assign menu, and that is where it was
              // only reachable: a two-step action for the decision a reviewer
              // makes most often after "this is X" — "this is nobody worth
              // naming". Secondary here, never styled as a deletion.
              <button
                type="button"
                className="face-viewer-secondary"
                data-testid="face-viewer-ignore"
                disabled={ignoring}
                onClick={() => { void ignoreSelected(); }}
              >
                <Icon name="eye-off" size={16} />
                <span>{t('face.ignoreFace')}</span>
              </button>
            )}
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
                setRefreshTick((n) => n + 1);
                loadPeople();
              }}
              onIgnored={(id) => {
                // No refetch of the face we just removed from the pool: the
                // caller drops it from the sequence and this viewer either
                // advances or closes.
                if (onFaceIgnored) onFaceIgnored(id);
                else setRefreshTick((n) => n + 1);
              }}
              onRestored={(id) => {
                if (onFaceRestored) onFaceRestored(id);
                else setRefreshTick((n) => n + 1);
              }}
              invalidateAuth={invalidateAuth}
            />
          </div>
        )}
      </footer>
    </div>
  );
}
