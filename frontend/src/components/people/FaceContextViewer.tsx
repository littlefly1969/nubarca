import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { ApiError, getFaceContext, ignoreFace, listPeople, type FaceContext, type Person } from '@nubarca/api-client';
import { mediumPreviewUrl } from '../files/types';
import { useAuth } from '../../auth/useAuth';
import { Icon } from '../icons/Icon';
import { AssignToPersonMenu } from './AssignToPersonMenu';
import { isEditableKeyboardTarget, isModalOwnedKey, ownsKeyboardEvent } from '../keyboardOwnership';
import { useI18n } from '../../i18n';
import {
  FIT_TRANSFORM,
  clamp,
  computeContainCanvas,
  focusTransform,
} from './faceViewerGeometry';

const MIN_ZOOM = 1;
const MAX_ZOOM = 8;

// Owner-private full-photo context viewer for a selected face. Shows the medium
// preview with the selected face highlighted (others subtle), supports
// zoom/pan/fit/focus, and previous/next navigation across the opening list.
// Never loads the original bytes; never renders internals.
//
// THREE ZONES, three questions:
//
//   top     — WHICH PHOTO is this: close, its name, and when it was taken.
//             Nothing else. No actions at all, so the reviewer's eye has one
//             place to read identity and never has to hunt for a control there.
//   stage   — the picture, with the face boxes over it.
//   bottom  — the whole operating surface, in two groups:
//               left  — what am I LOOKING AT (next photo, fit, focus, zoom)
//               right — what do I DECIDE (skip, ignore, ignore all, assign)
//
// The split is by consequence, not by frequency: everything on the left changes
// only the view and touches no data; everything on the right resolves or defers
// a face. Mixing them is how "ignore every face on this photo" once ended up
// wedged between the zoom buttons.
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
  const { t, formatDate } = useI18n();
  const [activeFaceId, setActiveFaceId] = useState(faceIds[index]);
  const [ctx, setCtx] = useState<FaceContext | null>(null);
  const [status, setStatus] = useState<'loading' | 'ready' | 'error'>('loading');
  const [people, setPeople] = useState<Person[]>([]);
  const [refreshTick, setRefreshTick] = useState(0);
  const [ignoring, setIgnoring] = useState(false);
  const [confirmIgnoreAll, setConfirmIgnoreAll] = useState(false);
  const [assignOpen, setAssignOpen] = useState(false);
  const [zoom, setZoom] = useState(1);
  const [pan, setPan] = useState({ x: 0, y: 0 });
  // The stage's measured box and the bitmap's own size. The canvas is derived
  // from the two and from nothing else — see faceViewerGeometry.
  const [stageSize, setStageSize] = useState<{ width: number; height: number } | null>(null);
  // The bitmap's size CARRIES THE FILE IT CAME FROM. Storing the dimensions
  // alone and clearing them in an effect leaves one painted frame in which the
  // new photo's boxes are laid out against the previous photo's aspect ratio —
  // an effect runs after paint, so the mismatch is visible before it is undone.
  // Pairing them makes the check a render-time one, and the window disappears.
  const [naturalSize, setNaturalSize] = useState<
    { fileItemId: string; width: number; height: number } | null
  >(null);
  const stageRef = useRef<HTMLDivElement | null>(null);
  const rootRef = useRef<HTMLDivElement | null>(null);
  const ignoreAllRef = useRef<HTMLButtonElement | null>(null);
  const confirmRef = useRef<HTMLDivElement | null>(null);
  const confirmCancelRef = useRef<HTMLButtonElement | null>(null);
  const drag = useRef<{ x: number; y: number; panX: number; panY: number } | null>(null);
  // A double-click can land on a face that is NOT the one currently loaded. The
  // dialog must describe the face that was clicked, so the request to open it
  // waits here until that face's context has actually arrived — otherwise the
  // reviewer double-clicks a stranger and is offered "Già assegnato a Mario".
  const [pendingAssignFaceId, setPendingAssignFaceId] = useState<string | null>(null);

  // The displayed face follows the nav list unless the user clicks another box.
  useEffect(() => setActiveFaceId(faceIds[index]), [faceIds, index]);

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

  // The pending dialog opens only once its own face is the loaded one.
  useEffect(() => {
    if (pendingAssignFaceId === null || ctx?.selectedFaceId !== pendingAssignFaceId) return;
    setPendingAssignFaceId(null);
    setAssignOpen(true);
  }, [pendingAssignFaceId, ctx?.selectedFaceId]);

  // ---- geometry -------------------------------------------------------------
  // The stage is measured rather than assumed: it is whatever is left between
  // the two chrome rows, which changes with the viewport and with the chrome
  // wrapping.
  useLayoutEffect(() => {
    const node = stageRef.current;
    if (!node) return;
    const measure = () => {
      const rect = node.getBoundingClientRect();
      setStageSize((prev) => (prev
        && Math.abs(prev.width - rect.width) < 1
        && Math.abs(prev.height - rect.height) < 1
        ? prev
        : { width: rect.width, height: rect.height }));
    };
    measure();
    if (typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(measure);
    ro.observe(node);
    return () => ro.disconnect();
  }, [status]);

  // Only a measurement belonging to THE PHOTO ON SCREEN counts. Anything else
  // is the previous picture's, and a canvas built from it would be the wrong
  // rectangle — which is the one thing the boxes cannot survive.
  const natural = naturalSize !== null && naturalSize.fileItemId === ctx?.fileItemId
    ? naturalSize
    : null;

  const canvas = useMemo(() => (stageSize && natural
    ? computeContainCanvas({
      availableWidth: stageSize.width,
      availableHeight: stageSize.height,
      naturalWidth: natural.width,
      naturalHeight: natural.height,
    })
    : null), [stageSize, natural]);

  // A NEW PHOTO always opens whole. Moving between two faces of the SAME photo
  // deliberately keeps the viewport: the reviewer zoomed in for a reason, and
  // resetting it on every face would undo that work several times per picture.
  // The viewport itself is reset in an effect, which is safe: while the new
  // photo's measurement has not arrived no box is drawn at all, so a frame at
  // the previous zoom states nothing false about where a face is.
  const fileItemId = ctx?.fileItemId;
  useEffect(() => {
    if (fileItemId === undefined) return;
    setZoom(FIT_TRANSFORM.zoom);
    setPan(FIT_TRANSFORM.pan);
  }, [fileItemId]);

  const fitImage = useCallback(() => {
    setZoom(FIT_TRANSFORM.zoom);
    setPan(FIT_TRANSFORM.pan);
  }, []);

  // Centre the selected face. Computed against the CANVAS, which is the picture,
  // so the image and the boxes take one identical transform and a box that was
  // over a face stays over it.
  const focusFace = useCallback(() => {
    if (!ctx || !canvas) return;
    const next = focusTransform({
      box: ctx.selectedBox,
      canvas,
      minZoom: MIN_ZOOM,
      maxZoom: MAX_ZOOM,
    });
    setZoom(next.zoom);
    setPan(next.pan);
  }, [ctx, canvas]);

  // Ignore the face currently highlighted in the photo — and only that one.
  //
  // No confirmation: one face, one reversible decision. The caller is told
  // through onFaceIgnored, exactly as when the action came from inside the
  // assign menu — this viewer does not own the sequence, so it cannot decide
  // whether to advance or close.
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

  const hasPrev = index > 0;
  const hasNext = index < faceIds.length - 1;

  // Shortcuts live on `window` so the photo answers arrows wherever focus is —
  // which means this viewer must decide for itself when a key is NOT its own.
  // A modal opened on top of it (assign/move, the bulk confirmation) owns the
  // keyboard entirely, and an editable target owns its arrows as caret moves.
  // See keyboardOwnership.ts.
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

  // The bulk confirmation owns Escape while it is up, so dismissing the question
  // does not also close the viewer behind it.
  //
  // It also has to own FOCUS. `aria-modal` is a promise to assistive technology,
  // not a mechanism: without moving focus in, the keyboard stays on the controls
  // underneath and the very next Tab walks a dialog the user cannot see they
  // have left. `isModalOwnedKey` deliberately does NOT include Tab — that is
  // what leaves focus traps possible — so the trap below is this dialog's own,
  // exactly as AssignToPersonMenu implements its own.
  useEffect(() => {
    if (!confirmIgnoreAll) return;
    function onKey(e: KeyboardEvent) {
      if (!isModalOwnedKey(e.key)) return;
      e.stopPropagation();
      if (e.key === 'Escape') {
        setConfirmIgnoreAll(false);
        ignoreAllRef.current?.focus();
      }
    }
    window.addEventListener('keydown', onKey, true);
    // Cancel, not the destructive answer: a question about several faces at
    // once should not have "yes" one Enter away from an unaware keyboard.
    const id = window.setTimeout(() => confirmCancelRef.current?.focus(), 0);
    return () => {
      window.removeEventListener('keydown', onKey, true);
      window.clearTimeout(id);
    };
  }, [confirmIgnoreAll]);

  /** Keep Tab inside the confirmation while it is open. */
  function onConfirmKeyDown(e: React.KeyboardEvent) {
    if (e.key !== 'Tab' || !confirmRef.current) return;
    const focusable = confirmRef.current.querySelectorAll<HTMLElement>(
      'button:not([disabled]), [tabindex]:not([tabindex="-1"])',
    );
    if (focusable.length === 0) return;
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (e.shiftKey && document.activeElement === first) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && document.activeElement === last) {
      e.preventDefault();
      first.focus();
    }
  }

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
    setZoom((z) => clamp(z * factor, MIN_ZOOM, MAX_ZOOM));
  }

  /**
   * Double-click a face box: select it AND open its assign dialog.
   *
   * A power shortcut for the action a reviewer takes most, on the object they
   * are already pointing at. When the box is not the loaded face the request is
   * parked in `pendingAssignFaceId` and consumed when that face's context
   * arrives, so the dialog never describes the previous face.
   */
  function openAssignFor(faceId: string) {
    if (ctx?.selectedFaceId === faceId) {
      setAssignOpen(true);
      return;
    }
    setActiveFaceId(faceId);
    setPendingAssignFaceId(faceId);
  }

  // What the photo's date actually is. An "uploaded" source is the moment the
  // file arrived, never a capture time, and captioning it "Scattata il" would
  // state something false about the photograph.
  const dateLine = ctx
    ? (ctx.effectiveDateTakenSource === 'uploaded'
      ? t('face.uploadedOn', { date: formatDate(ctx.effectiveDateTaken) })
      : t('face.takenOn', { date: formatDate(ctx.effectiveDateTaken) }))
    : null;

  return (
    <div className="face-viewer" role="dialog" aria-modal="true" aria-label={t('face.viewerAria')} ref={rootRef}>
      <button type="button" className="face-viewer-backdrop" aria-label={t('common.close')} onClick={onClose} />

      {/* ---- Top: which photo is this. No actions live here. -------------- */}
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
          <span className="face-viewer-file" title={ctx?.fileName} data-testid="face-viewer-file-name">
            {ctx?.fileName}
          </span>
          {dateLine && (
            <span className="face-viewer-date" data-testid="face-viewer-date">{dateLine}</span>
          )}
        </div>

        {/* Where the reviewer is in the queue. A label, never a control: it
            reports, it cannot be pressed, and it takes no operating space. */}
        {reviewControls && (
          <span className="face-viewer-progress" data-testid="face-viewer-progress">
            {reviewControls.progressLabel}
          </span>
        )}
      </header>

      <div
        className="face-viewer-stage"
        ref={stageRef}
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        onPointerLeave={onPointerUp}
      >
        {status === 'loading' && <p className="muted" role="status">{t('common.loading')}</p>}
        {status === 'error' && <p className="folder-error" role="alert">{t('face.photoUnavailable')}</p>}
        {status === 'ready' && ctx && (
          <div
            className={canvas ? 'face-viewer-canvas' : 'face-viewer-canvas is-measuring'}
            data-testid="face-viewer-canvas"
            style={canvas
              ? {
                width: `${canvas.width}px`,
                height: `${canvas.height}px`,
                transform: `translate(${pan.x}px, ${pan.y}px) scale(${zoom})`,
              }
              : undefined}
          >
            <img
              // Keyed by the file: a new photo mounts a new element, so it
              // cannot show the previous picture's decoded frame and its load
              // event cannot be skipped for an already-decoded one.
              key={ctx.fileItemId}
              className="face-viewer-image"
              src={mediumPreviewUrl(ctx.fileItemId)}
              alt={ctx.fileName}
              draggable={false}
              onLoad={(e) => {
                const img = e.currentTarget;
                setNaturalSize({
                  fileItemId: ctx.fileItemId,
                  width: img.naturalWidth,
                  height: img.naturalHeight,
                });
              }}
            />
            {/* Boxes are percentages OF THE CANVAS, so they are only correct
                once the canvas is the picture. Until then none is drawn — a box
                placed against a provisional rectangle would sit beside its face
                for a frame and then jump. */}
            {canvas && ctx.faces.map((fb) => {
              const selected = fb.faceId === ctx.selectedFaceId;
              return (
                <button
                  key={fb.faceId}
                  type="button"
                  className={selected ? 'face-viewer-box is-selected' : 'face-viewer-box'}
                  data-testid="face-viewer-box"
                  data-face-id={fb.faceId}
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
                  onDoubleClick={(e) => {
                    e.stopPropagation();
                    openAssignFor(fb.faceId);
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

      {/* ---- Bottom: viewport tools | face decisions ---------------------- */}
      <footer className="face-viewer-bottom">
        <div className="face-viewer-tools" role="group" aria-label={t('face.viewToolsAria')}>
          {/* Opening another photo changes WHAT I am looking at and decides no
              face at all, which is why it sits with the viewport tools rather
              than among the decisions. */}
          {reviewControls && (
            <button
              type="button"
              className="face-viewer-tool"
              data-testid="face-viewer-next-photo"
              disabled={!reviewControls.canNextPhoto}
              onClick={reviewControls.onNextPhoto}
            >
              <Icon name="next-photo" size={16} />
              <span>{t('people.photoReviewNextPhoto')}</span>
            </button>
          )}
          <button type="button" className="face-viewer-tool" data-testid="face-viewer-fit" onClick={fitImage}>
            <Icon name="fit" size={16} />
            <span>{t('face.showWholePhoto')}</span>
          </button>
          <button
            type="button"
            className="face-viewer-tool"
            data-testid="face-viewer-focus"
            onClick={focusFace}
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
            {/* Beside the single ignore, not hidden behind an overflow: it is
                the same kind of answer at a different scale, and a reviewer
                looking at a photo full of strangers should be able to see it.
                It is the one action here that decides several faces at once, so
                it is also the one that asks first. */}
            {reviewControls && (
              <button
                type="button"
                ref={ignoreAllRef}
                className="face-viewer-secondary"
                data-testid="face-viewer-ignore-all"
                disabled={reviewControls.ignoreRemainingBusy}
                onClick={() => setConfirmIgnoreAll(true)}
              >
                <Icon name="eye-off" size={16} />
                <span>{t('people.photoReviewIgnoreAll')}</span>
              </button>
            )}
            <AssignToPersonMenu
              faceId={ctx.selectedFaceId}
              people={people}
              currentPersonId={ctx.personId}
              currentPersonName={ctx.personName}
              open={assignOpen}
              onOpenChange={setAssignOpen}
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

      {/* One question, asked once, before one bulk call. Cancelling makes no
          request at all. */}
      {confirmIgnoreAll && reviewControls && (
        <div className="face-viewer-confirm-backdrop">
          <div
            className="face-viewer-confirm"
            role="dialog"
            aria-modal="true"
            aria-label={t('face.ignoreAllTitle')}
            data-testid="face-viewer-ignore-all-confirm"
            ref={confirmRef}
            onKeyDown={onConfirmKeyDown}
          >
            <h3 className="face-viewer-confirm-title">{t('face.ignoreAllTitle')}</h3>
            <p className="face-viewer-confirm-body">{t('face.ignoreAllQuestion')}</p>
            <div className="face-viewer-confirm-actions">
              <button
                type="button"
                ref={confirmCancelRef}
                className="face-viewer-tertiary"
                data-testid="face-viewer-ignore-all-cancel"
                onClick={() => {
                  setConfirmIgnoreAll(false);
                  ignoreAllRef.current?.focus();
                }}
              >
                {t('common.cancel')}
              </button>
              <button
                type="button"
                className="face-viewer-secondary"
                data-testid="face-viewer-ignore-all-accept"
                disabled={reviewControls.ignoreRemainingBusy}
                onClick={() => {
                  setConfirmIgnoreAll(false);
                  reviewControls.onIgnoreRemaining();
                }}
              >
                {t('face.ignoreAllConfirm')}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
