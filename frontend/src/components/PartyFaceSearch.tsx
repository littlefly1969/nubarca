import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  activatePartyFaceSearchTv,
  partyFaceSearch,
  type PartyFaceSearchResponse,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { Modal } from './Overlay';
import './PartyFaceSearch.css';

// Temporary product gate. Keep the activation implementation in place so it
// can be restored deliberately; the server independently rejects old clients.
const PARTY_FACE_TV_ACTIVATION_ENABLED: boolean = false;

// Public, anonymous "find your photos" experience on the party landing page. A
// guest takes a selfie; the backend detects the most prominent face, matches it
// against THIS party album, and returns party-safe media. The full selfie is
// never stored server-side. The response carries NO names, scores, face/person
// ids, or vectors — only a safe status code + matching media items.
//
// Completing a search only FILTERS THIS PHONE (via onFilterChange). The dormant
// TV activation path below is intentionally gated off for now.
//
// PRESENTATION: an immersive sheet opened by the "find your photos" capability
// card, not an inline panel with its own launcher. The card is the single entry
// point; `open`/`onOpenChange` make the page the owner of that state.
type FaceState =
  | { kind: 'idle' }
  | { kind: 'searching' }
  | { kind: 'done'; res: PartyFaceSearchResponse }
  | { kind: 'error' };

type TvState = 'idle' | 'activating' | 'active' | 'error';

// The local phone-only filter a completed search produces: the search id (for
// activation/cancellation) + the matching item ids in rank order.
export interface PartyFaceFilter {
  searchId: string | null;
  itemIds: string[];
}

// Downscale + re-encode the selfie to a small JPEG in the browser BEFORE upload.
// A full-resolution phone photo is several MB, and uploading it over mobile data
// frequently aborts mid-body (the request never reaches the server → generic
// error). Shrinking to a ~1600px JPEG makes the upload tiny and fast, applies
// EXIF orientation (better face detection), strips EXIF/GPS (privacy), and stays
// well under the server's max-dimension cap. Falls back to the original file on
// any failure or in a non-DOM/test environment, so behaviour degrades safely.
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
  const [file, setFile] = useState<File | null>(null);
  const [previewUrl, setPreviewUrl] = useState<string | null>(null);
  const [state, setState] = useState<FaceState>({ kind: 'idle' });
  const [tvState, setTvState] = useState<TvState>('idle');
  const inputRef = useRef<HTMLInputElement>(null);
  // The in-flight search, so closing or cancelling can stop it. Without this a
  // response arriving after the guest walked away re-applied a filter to an
  // album they were already browsing unfiltered.
  const requestRef = useRef<AbortController | null>(null);

  const abortInFlight = useCallback(() => {
    requestRef.current?.abort();
    requestRef.current = null;
  }, []);

  const reset = useCallback(() => {
    setFile(null);
    setState({ kind: 'idle' });
    setTvState('idle');
    if (inputRef.current) inputRef.current.value = '';
  }, []);

  // Local preview of the chosen selfie. Nothing is uploaded until the guest
  // confirms the search; the object URL is released as soon as it is replaced.
  useEffect(() => {
    if (!file || typeof URL.createObjectURL !== 'function') {
      setPreviewUrl(null);
      return undefined;
    }
    const url = URL.createObjectURL(file);
    setPreviewUrl(url);
    return () => {
      if (typeof URL.revokeObjectURL === 'function') URL.revokeObjectURL(url);
    };
  }, [file]);

  useEffect(() => abortInFlight, [abortInFlight]);

  // Discard the search: clear the phone filter, drop the server-side search,
  // and go back to a fresh sheet.
  const cancelSearch = useCallback(() => {
    abortInFlight();
    onCancelSearch(state.kind === 'done' ? state.res.searchId : null);
    reset();
  }, [state, onCancelSearch, reset, abortInFlight]);

  const submit = useCallback(() => {
    if (!file) return;
    abortInFlight();
    const ctrl = new AbortController();
    requestRef.current = ctrl;
    setState({ kind: 'searching' });
    setTvState('idle');
    onFilterChange(null);
    downscaleSelfie(file)
      .then((toSend) => partyFaceSearch(token, toSend, ctrl.signal))
      .then((res) => {
        // Aborted while in flight (sheet closed or search cancelled): the guest
        // has moved on, so this answer changes nothing.
        if (ctrl.signal.aborted) return;
        requestRef.current = null;
        setState({ kind: 'done', res });
        // A successful search filters ONLY this phone; the TV is untouched.
        if (res.status === 'ready' && res.searchId && res.items.length > 0) {
          onFilterChange({ searchId: res.searchId, itemIds: res.items.map((i) => i.id) });
        }
      })
      .catch((err: unknown) => {
        if (ctrl.signal.aborted) return;
        requestRef.current = null;
        // A 404 (party revoked mid-search) or any unexpected error → generic error.
        if (err instanceof ApiError) {
          setState({ kind: 'error' });
          return;
        }
        setState({ kind: 'error' });
      });
  }, [file, token, onFilterChange, abortInFlight]);

  const showOnTv = useCallback(() => {
    if (state.kind !== 'done' || !state.res.searchId || state.res.items.length === 0) return;
    setTvState('activating');
    activatePartyFaceSearchTv(token, state.res.searchId)
      .then(() => setTvState('active'))
      .catch(() => setTvState('error'));
  }, [state, token]);

  // A search whose matches are ON SCREEN behind the sheet.
  const hasActiveMatches = state.kind === 'done'
    && state.res.status === 'ready'
    && state.res.items.length > 0;

  // X, Escape and the backdrop, in one rule:
  //
  //   with matches applied  → leave them applied and step back to the gallery,
  //                           exactly like "see my photos". The page shows a
  //                           banner saying the album is filtered, so this can
  //                           never become an invisible state.
  //   otherwise             → nothing is worth keeping: stop any request in
  //                           flight, drop the search, reset.
  const requestClose = useCallback(() => {
    if (hasActiveMatches) {
      onOpenChange(false);
      return;
    }
    cancelSearch();
    onOpenChange(false);
  }, [hasActiveMatches, cancelSearch, onOpenChange]);

  const showResults = useCallback(() => {
    onOpenChange(false);
    onShowResults();
  }, [onOpenChange, onShowResults]);

  if (!open) return null;

  const searching = state.kind === 'searching';

  return (
    <Modal
      className="party-face-overlay"
      title={t('partyFace.title')}
      onClose={requestClose}
      testId="party-face"
      focusPanelOnOpen
      footer={<div className="party-face-actions">{renderActions()}</div>}
    >
      <div className="party-face-body">
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
    if (searching) {
      return (
        <div className="party-face-stage party-face-stage--searching">
          <span className="party-face-scanner" aria-hidden="true">
            {previewUrl
              ? <img className="party-face-scanner-img" src={previewUrl} alt="" />
              : <FaceFrameIcon />}
          </span>
          <p className="party-face-lede" role="status">{t('partyFace.searching')}</p>
        </div>
      );
    }

    if (state.kind === 'error') {
      return (
        <div className="party-face-stage">
          <p className="party-face-lede" role="alert">{t('partyFace.error')}</p>
        </div>
      );
    }

    if (state.kind === 'done') return renderResult(state.res);

    // idle: choose a selfie, then preview it before anything is uploaded.
    return (
      <div className="party-face-stage">
        <p className="party-face-lede">{t('partyFace.intro')}</p>
        {previewUrl || file ? (
          <div className="party-face-preview" data-testid="party-face-preview">
            {previewUrl
              ? <img className="party-face-preview-img" src={previewUrl} alt="" />
              : <span className="party-face-preview-fallback" aria-hidden="true"><FaceFrameIcon /></span>}
          </div>
        ) : (
          <span className="party-face-hero-icon" aria-hidden="true"><FaceFrameIcon /></span>
        )}
      </div>
    );
  }

  function renderResult(res: PartyFaceSearchResponse) {
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
    if (res.status === 'no_face') {
      return (
        <div className="party-face-stage">
          <p className="party-face-lede" role="alert" data-testid="party-face-noface">
            {t('partyFace.noFace')}
          </p>
        </div>
      );
    }
    // ready: the matching photos are shown by the page grid (local filter).
    if (res.items.length === 0) {
      return (
        <div className="party-face-stage">
          <p className="party-face-lede" data-testid="party-face-empty">{t('partyFace.noMatches')}</p>
        </div>
      );
    }
    return (
      <div className="party-face-stage party-face-stage--found">
        <span className="party-face-hero-icon party-face-hero-icon--found" aria-hidden="true">
          <FaceFrameIcon />
        </span>
        <p className="party-face-found" data-testid="party-face-count">
          {tn(res.items.length, 'partyFace.resultsTitle')}
        </p>
      </div>
    );
  }

  function renderActions() {
    if (searching) {
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
    }

    if (state.kind === 'done' && state.res.status === 'ready' && state.res.items.length > 0) {
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

    // Every remaining state ends in "pick a selfie": the first visit, a retry
    // after no face / an invalid image / an empty result / an error. Only the
    // unavailable capability has nothing to retry.
    const unavailable = state.kind === 'done' && state.res.status === 'unavailable';
    if (unavailable) {
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

    const retrying = state.kind === 'done' || state.kind === 'error';
    const pickLabel = retrying
      ? t('partyFace.newSearch')
      : file ? t('partyFace.change') : t('partyFace.chooseSelfie');

    return (
      <>
        {/* Visually hidden but still a real, focusable input, so the label is a
            48px+ target for touch AND reachable from the keyboard. */}
        <input
          ref={inputRef}
          id="party-face-file"
          className="party-face-file-input"
          type="file"
          accept="image/*"
          capture="user"
          data-testid="party-face-input"
          onChange={(e) => {
            const f = e.target.files?.[0] ?? null;
            setFile(f);
            setState({ kind: 'idle' });
            setTvState('idle');
          }}
        />
        <label
          className={file && !retrying ? 'party-face-secondary' : 'party-face-primary'}
          htmlFor="party-face-file"
          data-testid="party-face-pick"
        >
          <span className="party-face-pick-icon" aria-hidden="true"><FaceFrameIcon /></span>
          {pickLabel}
        </label>
        {file && !retrying && (
          <button
            type="button"
            className="party-face-primary"
            data-testid="party-face-submit"
            onClick={submit}
          >
            {t('partyFace.search')}
          </button>
        )}
      </>
    );
  }
}
