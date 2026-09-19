import { useCallback, useEffect, useRef, useState } from 'react';
import { frameFace, type FaceBox } from './faceScanCrop';
import { SCAN_MIN_PASSES, SCAN_PASS_MS, useFaceScan } from './useFaceScan';
import { useSelfieCamera } from './useSelfieCamera';
import {
  ApiError,
  activatePartyFaceSearchTv,
  partyFaceDetect,
  partyFaceSearch,
  type PartyFaceSearchResponse,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { Modal } from './Overlay';
import './PartyFaceSearch.css';

// Temporary product gate. Keep the activation implementation in place so it
// can be restored deliberately; the server independently rejects old clients.
const PARTY_FACE_TV_ACTIVATION_ENABLED: boolean = false;

// Public, anonymous "find your photos" on the party landing page.
//
// IT IS A SEQUENCE, AND THE SEQUENCE IS THE PRODUCT. A guest opens the camera,
// frames themselves, takes a selfie, watches the frame freeze, watches their
// own face be found and pulled into the scanner, and watches it be scanned.
// What it used to be was a picture chosen from the operating system's camera
// app and a line sweeping over a preview nothing was looking at — the animation
// was a decoration, not a description.
//
//   camera → capture → detecting_face → face_confirmed → scanning → results
//                               ↘ no_face ↘ multiple_faces
//   camera_error, search_error, cancelled
//
// Every one of those is a STATE, not a flag. `isLoading` cannot tell "we are
// looking for your face" apart from "we are looking for your photographs", and
// those two are different sentences with different buttons under them.
//
// THE FACE IN THE FRAME IS THE FACE IN THE SEARCH. Detection happens on the
// server, in one call that embeds nothing and records nothing, and returns the
// box the search will use — so the crop the guest is looking at is provably the
// crop their results come from. No second face algorithm runs in the browser.
//
// THE SELFIE IS A QUERY AND NOTHING ELSE. It is never uploaded to the album,
// never becomes a FileItem, never reaches the media library or the Vault. The
// object URL is revoked and the File dropped on every exit — cancel, retake,
// close, unmount.
//
// Completing a search only FILTERS THIS PHONE (via onFilterChange). The dormant
// TV activation path below is intentionally gated off for now.

/** How long the guest sees their face land in the frame before the sweep starts. */
export const FACE_CONFIRM_MS = 500;

type FaceState =
  /** The camera is open (or opening) and the guest is framing themselves. */
  | { kind: 'camera' }
  /** No camera, or permission refused. Two different sentences. */
  | { kind: 'camera_error'; reason: 'denied' | 'unavailable' }
  /** The shutter has fired: the frame is frozen and about to be examined. */
  | { kind: 'capture' }
  /** Asking the server where the face is. No search has started. */
  | { kind: 'detecting_face' }
  /** Nothing face-shaped. NO search was made. */
  | { kind: 'no_face' }
  /** Several faces and no dominant one. NO search was made. */
  | { kind: 'multiple_faces' }
  /** The face is found and has just landed in the frame. */
  | { kind: 'face_confirmed'; face: FaceBox }
  /** The sweep is running and the search is in flight (or already answered). */
  | { kind: 'scanning'; face: FaceBox }
  | { kind: 'results'; res: PartyFaceSearchResponse; face: FaceBox | null }
  | { kind: 'search_error' };

type TvState = 'idle' | 'activating' | 'active' | 'error';

// The local phone-only filter a completed search produces: the search id (for
// activation/cancellation) + the matching item ids in rank order.
export interface PartyFaceFilter {
  searchId: string | null;
  itemIds: string[];
}

// Downscale + re-encode a selfie to a small JPEG in the browser BEFORE upload.
// Still used for the FALLBACK path, where the operating system's camera hands
// back a several-megabyte original: uploading one over mobile data frequently
// aborts mid-body. The in-page capture already produces a small frame, so it
// does not go through here.
//
// Shrinking applies EXIF orientation (better detection), strips EXIF/GPS
// (privacy), and stays well under the server's max-dimension cap. Falls back to
// the original file on any failure or in a non-DOM environment.
export async function downscaleSelfie(
  file: File,
  maxDim = 1600,
  quality = 0.85,
): Promise<File> {
  try {
    if (typeof createImageBitmap !== 'function' || typeof document === 'undefined') {
      return file;
    }
    const bitmap = await createImageBitmap(file, { imageOrientation: 'from-image' });
    const { width, height } = bitmap;
    if (!width || !height) {
      bitmap.close();
      return file;
    }
    const scale = Math.min(1, maxDim / Math.max(width, height));
    const canvas = document.createElement('canvas');
    canvas.width = Math.max(1, Math.round(width * scale));
    canvas.height = Math.max(1, Math.round(height * scale));
    const ctx = canvas.getContext('2d');
    if (!ctx) {
      bitmap.close();
      return file;
    }
    ctx.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
    bitmap.close();
    const blob = await new Promise<Blob | null>((resolve) =>
      canvas.toBlob(resolve, 'image/jpeg', quality));
    if (!blob || blob.size === 0) {
      return file;
    }
    return new File([blob], 'selfie.jpg', { type: 'image/jpeg' });
  } catch {
    return file;
  }
}

function FaceFrameIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d="M4 8.5V6a2 2 0 0 1 2-2h2.5M15.5 4H18a2 2 0 0 1 2 2v2.5M20 15.5V18a2 2 0 0 1-2 2h-2.5M8.5 20H6a2 2 0 0 1-2-2v-2.5" />
      <circle cx="12" cy="10.6" r="2.6" />
      <path d="M8.2 16.4a4.2 4.2 0 0 1 7.6 0" />
    </svg>
  );
}

function ShieldIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d="M12 3.5l6.5 2.4v5.3c0 3.9-2.6 7.4-6.5 8.8-3.9-1.4-6.5-4.9-6.5-8.8V5.9Z" />
      <path d="m9.4 12.1 1.9 1.9 3.5-3.7" />
    </svg>
  );
}

export function PartyFaceSearch({
  token,
  open,
  onOpenChange,
  onFilterChange,
  onCancelSearch,
  onShowResults,
}: {
  token: string;
  /** Owned by the page: the capability card is the only thing that opens this. */
  open: boolean;
  onOpenChange: (open: boolean) => void;
  // null → no local filter (full album); otherwise only itemIds are shown.
  onFilterChange: (filter: PartyFaceFilter | null) => void;
  /**
   * Discard a search: the PAGE owns the filter, so it also owns the server-side
   * delete (and its stored face crop). Called with the search this sheet knows
   * about, which is not always the applied filter's — an empty result has a
   * search id and no filter.
   */
  onCancelSearch: (searchId: string | null) => void;
  /** Close and take the guest to the (now filtered) gallery. */
  onShowResults: () => void;
}) {
  const { t, tn } = useI18n();
  const camera = useSelfieCamera();
  const [state, setState] = useState<FaceState>({ kind: 'camera' });
  const [previewUrl, setPreviewUrl] = useState<string | null>(null);
  // The selfie's own shape, learned when the frozen frame decodes. Needed
  // because a square crop in pixels is not a square crop in fractions.
  const [selfieAspect, setSelfieAspect] = useState(0);
  const [tvState, setTvState] = useState<TvState>('idle');
  const fileInputRef = useRef<HTMLInputElement>(null);
  // The captured bytes, held only for as long as the search needs them.
  const shotRef = useRef<File | null>(null);
  // The in-flight request, so closing or cancelling can stop it. Without this a
  // response arriving after the guest walked away re-applied a filter to an
  // album they were already browsing unfiltered.
  const requestRef = useRef<AbortController | null>(null);
  // The confirm beat, so a retake cannot leave one pending.
  const confirmTimerRef = useRef<number | null>(null);

  const abortInFlight = useCallback(() => {
    requestRef.current?.abort();
    requestRef.current = null;
    if (confirmTimerRef.current !== null) {
      window.clearTimeout(confirmTimerRef.current);
      confirmTimerRef.current = null;
    }
  }, []);

  /** Let go of the selfie: the object URL, the bytes, the measured shape. */
  const dropShot = useCallback(() => {
    setPreviewUrl((url) => {
      if (url && typeof URL.revokeObjectURL === 'function') URL.revokeObjectURL(url);
      return null;
    });
    shotRef.current = null;
    setSelfieAspect(0);
    if (fileInputRef.current) fileInputRef.current.value = '';
  }, []);

  // Releasing a result is the scan's job, not the request's: it hands the
  // answer over once its passes are done, which may be after the answer landed.
  const scan = useFaceScan<{ res: PartyFaceSearchResponse; face: FaceBox | null }>(
    useCallback(({ res, face }) => {
      setState({ kind: 'results', res, face });
      // A successful search filters ONLY this phone; the TV is untouched.
      if (res.status === 'ready' && res.searchId && res.items.length > 0) {
        onFilterChange({ searchId: res.searchId, itemIds: res.items.map((i) => i.id) });
      }
    }, [onFilterChange]));

  /** Back to the camera, with nothing of the last attempt left behind. */
  const retake = useCallback(() => {
    abortInFlight();
    scan.reset();
    dropShot();
    setTvState('idle');
    setState({ kind: 'camera' });
    camera.start();
  }, [abortInFlight, scan, dropShot, camera]);

  // Opening the sheet opens the camera; closing it releases everything. The
  // camera light going out when the overlay does is not a nicety — it is the
  // visible half of "the selfie is a query and nothing else".
  useEffect(() => {
    if (!open) {
      abortInFlight();
      scan.reset();
      dropShot();
      camera.stop();
      return;
    }
    setState({ kind: 'camera' });
    camera.start();
    // `camera` and `scan` are stable for the life of the tree; listing them
    // would restart the camera on every render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  // The camera's own failures are the sheet's state: a blank rectangle is not
  // an explanation, and a guest whose browser refused the camera needs the
  // fallback offered rather than nothing happening.
  useEffect(() => {
    if (state.kind !== 'camera' && state.kind !== 'camera_error') return;
    if (camera.status === 'denied') setState({ kind: 'camera_error', reason: 'denied' });
    else if (camera.status === 'unavailable') {
      setState({ kind: 'camera_error', reason: 'unavailable' });
    } else if (camera.status === 'live' && state.kind === 'camera_error') {
      setState({ kind: 'camera' });
    }
  }, [camera.status, state.kind]);

  // Everything in flight stops when this unmounts, whatever route change or
  // error took it away.
  useEffect(() => abortInFlight, [abortInFlight]);

  /**
   * THE WHOLE SEQUENCE, from a frozen frame to results.
   *
   * Detection first, in a call that embeds nothing and records nothing — so a
   * selfie with no face, or with a crowd in it, costs the party nothing and the
   * guest one retake. Only a confirmed face reaches the search.
   */
  const examine = useCallback(async (shot: File, url: string) => {
    shotRef.current = shot;
    setPreviewUrl((previous) => {
      if (previous && typeof URL.revokeObjectURL === 'function') URL.revokeObjectURL(previous);
      return url;
    });
    setSelfieAspect(0);
    setState({ kind: 'capture' });
    // The preview is frozen, so the camera has nothing left to show. Released
    // here rather than at the end: the light goes out at the shutter, which is
    // both what a guest expects and one less thing for an error path to forget.
    camera.stop();

    abortInFlight();
    const ctrl = new AbortController();
    requestRef.current = ctrl;
    setState({ kind: 'detecting_face' });

    let face: FaceBox;
    try {
      const detected = await partyFaceDetect(token, shot, ctrl.signal);
      if (ctrl.signal.aborted) return;

      if (detected.status === 'no_face') { setState({ kind: 'no_face' }); return; }
      if (detected.status === 'multiple_faces') { setState({ kind: 'multiple_faces' }); return; }
      if (detected.status !== 'found' || !detected.face) {
        // invalid_image, unavailable, or a `found` with no box: nothing to
        // frame and nothing to search for.
        setState({ kind: 'search_error' });
        return;
      }
      face = detected.face;
    } catch (err) {
      if (ctrl.signal.aborted) return;
      void err;
      setState({ kind: 'search_error' });
      return;
    }

    // THE FACE LANDS, and the guest watches it land. A state with a duration
    // rather than a frame nobody sees: the crop snapping into the scanner is
    // the moment the product says "this is you, and this is what we are looking
    // for".
    setState({ kind: 'face_confirmed', face });

    confirmTimerRef.current = window.setTimeout(() => {
      confirmTimerRef.current = null;
      if (ctrl.signal.aborted) return;
      setState({ kind: 'scanning', face });
      scan.begin();

      partyFaceSearch(token, shot, ctrl.signal)
        .then((res) => {
          if (ctrl.signal.aborted) return;
          requestRef.current = null;
          // THE RESULT WAITS FOR THE SWEEP. Two things arrive together and are
          // shown at different moments on purpose: the guest watches the line
          // pass over their own face, which is the whole point of the effect,
          // and an answer in 80ms would flash the lot past before anyone could
          // read it. A slow answer simply keeps the line going.
          scan.settle({ res, face });
        })
        .catch((err: unknown) => {
          if (ctrl.signal.aborted) return;
          requestRef.current = null;
          // A failure is not a scan to sit through.
          scan.reset();
          void (err instanceof ApiError);
          setState({ kind: 'search_error' });
        });
    }, FACE_CONFIRM_MS);
  }, [token, camera, abortInFlight, scan]);

  /** The shutter. */
  const shoot = useCallback(async () => {
    const shot = await camera.capture();
    if (!shot) { setState({ kind: 'camera_error', reason: 'unavailable' }); return; }
    const url = typeof URL.createObjectURL === 'function' ? URL.createObjectURL(shot) : '';
    await examine(shot, url);
  }, [camera, examine]);

  /** The fallback: the operating system's own camera, for a browser with none. */
  const usePickedFile = useCallback(async (picked: File) => {
    const shot = await downscaleSelfie(picked);
    const url = typeof URL.createObjectURL === 'function' ? URL.createObjectURL(shot) : '';
    await examine(shot, url);
  }, [examine]);

  // Discard the search: clear the phone filter, drop the server-side search,
  // and go back to a fresh camera.
  const cancelSearch = useCallback(() => {
    abortInFlight();
    onCancelSearch(state.kind === 'results' ? state.res.searchId : null);
    onFilterChange(null);
    retake();
  }, [state, onCancelSearch, onFilterChange, retake, abortInFlight]);

  const showOnTv = useCallback(() => {
    if (state.kind !== 'results' || !state.res.searchId || state.res.items.length === 0) return;
    setTvState('activating');
    activatePartyFaceSearchTv(token, state.res.searchId)
      .then(() => setTvState('active'))
      .catch(() => setTvState('error'));
  }, [state, token]);

  // A search whose matches are ON SCREEN behind the sheet.
  const hasActiveMatches = state.kind === 'results'
    && state.res.status === 'ready'
    && state.res.items.length > 0;

  // X, Escape and the backdrop, in one rule:
  //
  //   with matches applied  → leave them applied and step back to the gallery,
  //                           exactly like "see my photos". The page shows a
  //                           banner saying the album is filtered, so this can
  //                           never become an invisible state.
  //   otherwise             → nothing is worth keeping: stop any request in
  //                           flight, drop the search, release the camera.
  const requestClose = useCallback(() => {
    if (hasActiveMatches) {
      onOpenChange(false);
      return;
    }
    abortInFlight();
    scan.reset();
    onCancelSearch(state.kind === 'results' ? state.res.searchId : null);
    onFilterChange(null);
    dropShot();
    camera.stop();
    onOpenChange(false);
  }, [
    hasActiveMatches, onOpenChange, abortInFlight, scan, onCancelSearch, state,
    onFilterChange, dropShot, camera,
  ]);

  const showResults = useCallback(() => {
    onOpenChange(false);
    onShowResults();
  }, [onOpenChange, onShowResults]);

  if (!open) return null;

  return (
    <Modal
      className="party-face-overlay"
      title={t('partyFace.title')}
      onClose={requestClose}
      testId="party-face"
      focusPanelOnOpen
      footer={<div className="party-face-actions">{renderActions()}</div>}
    >
      <div className="party-face-body" data-state={state.kind}>
        {renderStage()}

        {/* Privacy, stated once and compactly — the same guarantee the backend
            makes, never a broader one. */}
        <p className="party-face-privacy">
          <span className="party-face-privacy-icon" aria-hidden="true"><ShieldIcon /></span>
          <span>{t('partyFace.notStored')}</span>
        </p>
      </div>
    </Modal>
  );

  function renderStage() {
    switch (state.kind) {
      case 'camera':
        return (
          <div className="party-face-stage party-face-stage--camera">
            <p className="party-face-lede">{t('partyFace.frameYourself')}</p>
            <div className="party-face-viewfinder" data-testid="party-face-viewfinder">
              {/* MIRRORED, and only here. Framing yourself in a picture that
                  moves the wrong way is genuinely hard; the capture is the true
                  image, which is what the search sees. */}
              <video
                ref={camera.videoRef}
                className="party-face-video"
                data-testid="party-face-video"
                playsInline
                muted
                autoPlay
              />
              {camera.status !== 'live' && (
                <span className="party-face-viewfinder-wait" role="status">
                  {t('partyFace.openingCamera')}
                </span>
              )}
            </div>
          </div>
        );

      case 'camera_error':
        return (
          <div className="party-face-stage">
            <span className="party-face-hero-icon" aria-hidden="true"><FaceFrameIcon /></span>
            <p className="party-face-lede" role="alert" data-testid="party-face-camera-error">
              {t(state.reason === 'denied'
                ? 'partyFace.cameraDenied'
                : 'partyFace.cameraUnavailable')}
            </p>
          </div>
        );

      case 'capture':
      case 'detecting_face':
      case 'face_confirmed':
      case 'scanning': {
        const face = state.kind === 'face_confirmed' || state.kind === 'scanning'
          ? state.face
          : null;
        return (
          <div className="party-face-stage party-face-stage--searching">
            {/* The frozen frame. Once the server says where the face is, the
                SAME image is pushed and scaled so that face fills the tile — a
                crop, not a second frame, and not a box drawn on top. The bytes
                never leave the phone again: this is CSS on an image it already
                decoded, and the box came from the call that will do the
                searching. */}
            <span
              className="party-face-scanner"
              data-testid="party-face-scanner"
              data-framed={face ? 'face' : 'selfie'}
              aria-hidden="true"
            >
              {previewUrl
                ? (
                  <img
                    className="party-face-scanner-img"
                    data-testid="party-face-scanner-img"
                    src={previewUrl}
                    alt=""
                    style={face ? frameFace(face, selfieAspect) : undefined}
                    onLoad={(e) => {
                      const { naturalWidth, naturalHeight } = e.currentTarget;
                      if (naturalWidth > 0 && naturalHeight > 0) {
                        setSelfieAspect(naturalWidth / naturalHeight);
                      }
                    }}
                  />
                )
                : <FaceFrameIcon />}
              {/* Sweeps while the search runs. Not a progress bar: there is no
                  progress to report, and pretending otherwise would be
                  inventing a number. */}
              {scan.scanning && (
                <span
                  className="party-face-scanline"
                  data-testid="party-face-scanline"
                  // From the constant the gate counts in, so the sweeps a guest
                  // watches and the sweeps the code waits for are the same thing.
                  style={{ animationDuration: `${SCAN_PASS_MS}ms` }}
                />
              )}
            </span>
            <p className="party-face-lede" role="status" data-testid="party-face-step">
              {state.kind === 'detecting_face' || state.kind === 'capture'
                ? t('partyFace.findingFace')
                : state.kind === 'face_confirmed'
                  ? t('partyFace.faceFound')
                  : t('partyFace.searching')}
            </p>
            {state.kind === 'scanning' && (
              <span
                className="visually-hidden"
                data-testid="party-face-passes"
                data-passes={scan.passes}
                data-min-passes={SCAN_MIN_PASSES}
              />
            )}
          </div>
        );
      }

      case 'no_face':
        return (
          <div className="party-face-stage">
            {renderShot()}
            <p className="party-face-lede" role="alert" data-testid="party-face-noface">
              {t('partyFace.noFace')}
            </p>
          </div>
        );

      case 'multiple_faces':
        return (
          <div className="party-face-stage">
            {renderShot()}
            <p className="party-face-lede" role="alert" data-testid="party-face-multiple">
              {t('partyFace.multipleFaces')}
            </p>
          </div>
        );

      case 'search_error':
        return (
          <div className="party-face-stage">
            <p className="party-face-lede" role="alert" data-testid="party-face-error">
              {t('partyFace.error')}
            </p>
          </div>
        );

      case 'results':
        return renderResult(state.res, state.face);
    }
  }

  /** The selfie the guest just took, shown whole beside a refusal. */
  function renderShot() {
    if (!previewUrl) {
      return <span className="party-face-hero-icon" aria-hidden="true"><FaceFrameIcon /></span>;
    }
    return (
      <div className="party-face-preview" data-testid="party-face-preview">
        <img className="party-face-preview-img" src={previewUrl} alt="" />
      </div>
    );
  }

  function renderResult(res: PartyFaceSearchResponse, face: FaceBox | null) {
    if (res.status === 'unavailable') {
      return (
        <div className="party-face-stage">
          <p className="party-face-lede" role="alert" data-testid="party-face-unavailable">
            {t('partyFace.unavailable')}
          </p>
        </div>
      );
    }
    if (res.status === 'invalid_image') {
      return (
        <div className="party-face-stage">
          <p className="party-face-lede" role="alert" data-testid="party-face-invalid">
            {t('partyFace.invalidImage')}
          </p>
        </div>
      );
    }
    // The server can still refuse on the SEARCH call — the two calls are
    // separate, and a party can lose its face package between them.
    if (res.status === 'no_face' || res.status === 'multiple_faces') {
      return (
        <div className="party-face-stage">
          {renderShot()}
          <p className="party-face-lede" role="alert" data-testid="party-face-noface">
            {t(res.status === 'no_face' ? 'partyFace.noFace' : 'partyFace.multipleFaces')}
          </p>
        </div>
      );
    }
    if (res.items.length === 0) {
      return (
        <div className="party-face-stage">
          <p className="party-face-lede" data-testid="party-face-empty">{t('partyFace.noMatches')}</p>
        </div>
      );
    }
    return (
      <div className="party-face-stage party-face-stage--found">
        {/* THE FACE THAT WAS SEARCHED FOR, still in the frame. The guest sees
            what produced these photographs rather than a generic icon. */}
        <span
          className="party-face-scanner"
          data-testid="party-face-result-face"
          data-framed={face ? 'face' : 'selfie'}
          aria-hidden="true"
        >
          {previewUrl
            ? (
              <img
                className="party-face-scanner-img"
                src={previewUrl}
                alt=""
                style={face ? frameFace(face, selfieAspect) : undefined}
              />
            )
            : <FaceFrameIcon />}
        </span>
        <p className="party-face-found" data-testid="party-face-count">
          {tn(res.items.length, 'partyFace.resultsTitle')}
        </p>
      </div>
    );
  }

  function renderActions() {
    switch (state.kind) {
      case 'camera':
        return (
          <button
            type="button"
            className="party-face-primary"
            data-testid="party-face-shutter"
            disabled={camera.status !== 'live'}
            onClick={() => void shoot()}
          >
            <span className="party-face-pick-icon" aria-hidden="true"><FaceFrameIcon /></span>
            {t('partyFace.takeSelfie')}
          </button>
        );

      case 'camera_error':
        // The operating system's camera, for a browser that will not lend us
        // its own. A real, focusable input behind a 48px label.
        return (
          <>
            <input
              ref={fileInputRef}
              id="party-face-file"
              className="party-face-file-input"
              type="file"
              accept="image/*"
              capture="user"
              data-testid="party-face-input"
              onChange={(e) => {
                const picked = e.target.files?.[0] ?? null;
                if (picked) void usePickedFile(picked);
              }}
            />
            <label
              className="party-face-primary"
              htmlFor="party-face-file"
              data-testid="party-face-pick"
            >
              <span className="party-face-pick-icon" aria-hidden="true"><FaceFrameIcon /></span>
              {t('partyFace.chooseSelfie')}
            </label>
          </>
        );

      case 'capture':
      case 'detecting_face':
      case 'face_confirmed':
      case 'scanning':
        return (
          <button
            type="button"
            className="party-face-secondary"
            data-testid="party-face-cancel"
            onClick={cancelSearch}
          >
            {t('partyFace.cancelSearch')}
          </button>
        );

      case 'no_face':
      case 'multiple_faces':
      case 'search_error':
        return (
          <button
            type="button"
            className="party-face-primary"
            data-testid="party-face-retry"
            onClick={retake}
          >
            {t('partyFace.retry')}
          </button>
        );

      case 'results': {
        if (state.res.status === 'unavailable') {
          return (
            <button
              type="button"
              className="party-face-secondary"
              data-testid="party-face-dismiss"
              onClick={requestClose}
            >
              {t('common.close')}
            </button>
          );
        }
        if (state.res.status !== 'ready' || state.res.items.length === 0) {
          return (
            <button
              type="button"
              className="party-face-primary"
              data-testid="party-face-retry"
              onClick={retake}
            >
              {t('partyFace.retry')}
            </button>
          );
        }
        return (
          <>
            <button
              type="button"
              className="party-face-secondary"
              data-testid="party-face-cancel"
              onClick={cancelSearch}
            >
              {t('partyFace.cancelSearch')}
            </button>
            {PARTY_FACE_TV_ACTIVATION_ENABLED && (
              <button
                type="button"
                className="party-face-secondary party-face-tv"
                data-testid="party-face-show-tv"
                disabled={!state.res.searchId || tvState === 'activating'}
                onClick={showOnTv}
              >
                {tvState === 'active' ? t('partyFace.showingOnTv') : t('partyFace.showOnTv')}
              </button>
            )}
            {PARTY_FACE_TV_ACTIVATION_ENABLED && tvState === 'error' && (
              <p role="alert" data-testid="party-face-tv-error">{t('partyFace.tvError')}</p>
            )}
            <button
              type="button"
              className="party-face-primary"
              data-testid="party-face-show-results"
              onClick={showResults}
            >
              {t('partyFace.seeMyPhotos')}
            </button>
          </>
        );
      }
    }
  }
}
