import { useCallback, useRef, useState } from 'react';
import {
  ApiError,
  activatePartyFaceSearchTv,
  deletePartyFaceSearch,
  partyFaceSearch,
  type PartyFaceSearchResponse,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';

// Public, anonymous "find your face" panel on the party landing page. A guest
// picks/takes a selfie; the backend detects the most prominent face, matches it
// against THIS party album, and returns party-safe media. The full selfie is
// never stored server-side (only a small detected-face crop backs the TV
// indicator while a search is shown on the TV). The response carries NO names,
// scores, face/person ids, or vectors — only a safe status code + matching
// media items.
//
// Completing a search only FILTERS THIS PHONE (via onFilterChange); the TV is
// untouched until the guest explicitly presses "Show these photos on TV",
// which the backend bridges to the paired TV. "Cancel search" clears the local
// filter AND deletes the search server-side (deactivating the TV if this very
// search was active — the server protects newer searches from stale cancels).
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

export function PartyFaceSearch({
  token,
  onFilterChange,
}: {
  token: string;
  // null → no local filter (full album); otherwise only itemIds are shown.
  onFilterChange: (filter: PartyFaceFilter | null) => void;
}) {
  const { t, tn } = useI18n();
  const [open, setOpen] = useState(false);
  const [file, setFile] = useState<File | null>(null);
  const [state, setState] = useState<FaceState>({ kind: 'idle' });
  const [tvState, setTvState] = useState<TvState>('idle');
  const inputRef = useRef<HTMLInputElement>(null);

  const reset = useCallback(() => {
    setFile(null);
    setState({ kind: 'idle' });
    setTvState('idle');
    if (inputRef.current) inputRef.current.value = '';
  }, []);

  // Cancel search: clear the phone filter, delete the search (and its stored
  // face crop) server-side — which also deactivates the TV when THIS search is
  // the active filter. Best-effort + idempotent server-side, so a concurrent
  // TV-side BACK never surfaces an error here.
  const cancelSearch = useCallback(() => {
    if (state.kind === 'done' && state.res.searchId) {
      void deletePartyFaceSearch(token, state.res.searchId).catch(() => { /* best effort */ });
    }
    onFilterChange(null);
    reset();
  }, [state, token, onFilterChange, reset]);

  const submit = useCallback(() => {
    if (!file) return;
    setState({ kind: 'searching' });
    setTvState('idle');
    onFilterChange(null);
    downscaleSelfie(file)
      .then((toSend) => partyFaceSearch(token, toSend))
      .then((res) => {
        setState({ kind: 'done', res });
        // A successful search filters ONLY this phone; the TV is untouched.
        if (res.status === 'ready' && res.searchId && res.items.length > 0) {
          onFilterChange({ searchId: res.searchId, itemIds: res.items.map((i) => i.id) });
        }
      })
      .catch((err: unknown) => {
        // A 404 (party revoked mid-search) or any unexpected error → generic error.
        if (err instanceof ApiError) {
          setState({ kind: 'error' });
          return;
        }
        setState({ kind: 'error' });
      });
  }, [file, token, onFilterChange]);

  const showOnTv = useCallback(() => {
    if (state.kind !== 'done' || !state.res.searchId || state.res.items.length === 0) return;
    setTvState('activating');
    activatePartyFaceSearchTv(token, state.res.searchId)
      .then(() => setTvState('active'))
      .catch(() => setTvState('error'));
  }, [state, token]);

  if (!open) {
    return (
      <div className="party-face-launch">
        <button
          type="button"
          className="party-face-open"
          data-testid="party-face-open"
          onClick={() => setOpen(true)}
        >
          {t('partyFace.open')}
        </button>
      </div>
    );
  }

  return (
    <section className="party-face" data-testid="party-face" aria-label={t('partyFace.title')}>
      <div className="party-face-head">
        <h2>{t('partyFace.title')}</h2>
        <button
          type="button"
          className="party-face-close"
          onClick={() => { cancelSearch(); setOpen(false); }}
        >
          {t('common.close')}
        </button>
      </div>

      <p className="party-face-intro">{t('partyFace.intro')}</p>
      <p className="party-face-note">{t('partyFace.notStored')}</p>

      <div className="party-face-controls">
        <label className="party-face-file">
          <input
            ref={inputRef}
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
          <span>{file ? t('partyFace.change') : t('partyFace.chooseSelfie')}</span>
        </label>

        <button
          type="button"
          className="party-face-submit"
          data-testid="party-face-submit"
          disabled={!file || state.kind === 'searching'}
          onClick={submit}
        >
          {state.kind === 'searching' ? t('partyFace.searching') : t('partyFace.search')}
        </button>
      </div>

      {state.kind === 'searching' && (
        <p className="party-status" role="status">{t('partyFace.searching')}</p>
      )}

      {state.kind === 'error' && (
        <div className="party-face-result">
          <p role="alert">{t('partyFace.error')}</p>
          <button type="button" onClick={cancelSearch}>{t('partyFace.newSearch')}</button>
        </div>
      )}

      {state.kind === 'done' && <FaceResult res={state.res} />}
    </section>
  );

  function FaceResult({ res }: { res: PartyFaceSearchResponse }) {
    if (res.status === 'unavailable') {
      return (
        <div className="party-face-result">
          <p role="alert" data-testid="party-face-unavailable">{t('partyFace.unavailable')}</p>
        </div>
      );
    }
    if (res.status === 'invalid_image') {
      return (
        <div className="party-face-result">
          <p role="alert">{t('partyFace.invalidImage')}</p>
          <button type="button" onClick={cancelSearch}>{t('partyFace.newSearch')}</button>
        </div>
      );
    }
    if (res.status === 'no_face') {
      return (
        <div className="party-face-result">
          <p role="alert" data-testid="party-face-noface">{t('partyFace.noFace')}</p>
          <button type="button" onClick={cancelSearch}>{t('partyFace.newSearch')}</button>
        </div>
      );
    }
    // ready: the matching photos are shown by the page grid (local filter). Here
    // only the count + the two explicit actions. "Show on TV" stays disabled for
    // an empty result — an empty search must never reach the TV.
    const empty = res.items.length === 0;
    return (
      <div className="party-face-result">
        {empty ? (
          <p data-testid="party-face-empty">{t('partyFace.noMatches')}</p>
        ) : (
          <p className="party-face-count" data-testid="party-face-count">
            {tn(res.items.length, 'partyFace.resultsTitle')}
          </p>
        )}
        <div className="party-face-actions">
          <button
            type="button"
            data-testid="party-face-cancel"
            onClick={cancelSearch}
          >
            {t('partyFace.cancelSearch')}
          </button>
          <button
            type="button"
            className="party-face-tv"
            data-testid="party-face-show-tv"
            disabled={empty || !res.searchId || tvState === 'activating'}
            onClick={showOnTv}
          >
            {tvState === 'active' ? t('partyFace.showingOnTv') : t('partyFace.showOnTv')}
          </button>
        </div>
        {tvState === 'error' && (
          <p role="alert" data-testid="party-face-tv-error">{t('partyFace.tvError')}</p>
        )}
      </div>
    );
  }
}
