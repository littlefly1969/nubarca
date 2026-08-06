import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  getPlateImage,
  withBlurFaces,
  type PlateImageDetail as PlateDetail,
} from '@nubarca/api-client';
import { useAuth } from '../../auth/useAuth';
import { useI18n } from '../../i18n';
import { PlateAnalysisButton } from './PlateAnalysisButton';
import { PlateAnalysisStatusBadge } from './PlateAnalysisStatusBadge';
import { PlateOverlay } from './PlateOverlay';
import { PlateDetectionList } from './PlateDetectionList';

interface Props {
  id: string;
  onClose: () => void;
  onDelete: () => void;
  // Called when the analysis outcome changes so the parent grid can refresh.
  onChanged: () => void;
}

type State =
  | { kind: 'loading' }
  | { kind: 'ready'; detail: PlateDetail }
  | { kind: 'error'; message: string };

const POLL_MS = 2500;

// Owner-private detail/preview modal with the ALPR analysis surface: status
// badge, Analyze/Re-analyze action, a bounding-box overlay on the medium preview
// (never the original), and a recognized-plate list. Polls while an analysis is
// pending/running.
export function PlateImageDetail({ id, onClose, onDelete, onChanged }: Props) {
  const { invalidateAuth } = useAuth();
  const { t, formatDate } = useI18n();
  const [state, setState] = useState<State>({ kind: 'loading' });
  // Server-side privacy redaction toggle. Default off; preserved across the
  // preview and the "open original" view. Redaction is baked into the served
  // media (blurFaces=true) — no face boxes are ever exposed to the client.
  const [hideFaces, setHideFaces] = useState(false);
  const abortRef = useRef<AbortController | null>(null);
  const pollRef = useRef<number | null>(null);
  const lastStatusRef = useRef<string | null>(null);

  const fetchDetail = useCallback(() => {
    abortRef.current?.abort();
    const ctrl = new AbortController();
    abortRef.current = ctrl;
    getPlateImage(id, ctrl.signal)
      .then((detail) => {
        setState({ kind: 'ready', detail });
        const next = detail.analysisSummary.analysisStatus;
        if (lastStatusRef.current !== null && lastStatusRef.current !== next) {
          onChanged();
        }
        lastStatusRef.current = next;
      })
      .catch((err) => {
        if ((err as Error).name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 401) {
          invalidateAuth();
          return;
        }
        setState({ kind: 'error', message: t('plates.detailLoadError') });
      });
  }, [id, invalidateAuth, onChanged, t]);

  useEffect(() => {
    lastStatusRef.current = null;
    fetchDetail();
    return () => abortRef.current?.abort();
  }, [fetchDetail]);

  // Poll while an analysis is in progress.
  useEffect(() => {
    if (state.kind !== 'ready') return;
    const status = state.detail.analysisSummary.analysisStatus;
    if (status !== 'pending' && status !== 'running') return;
    pollRef.current = window.setTimeout(fetchDetail, POLL_MS);
    return () => {
      if (pollRef.current !== null) window.clearTimeout(pollRef.current);
    };
  }, [state, fetchDetail]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <div
      className="plate-detail-backdrop"
      role="dialog"
      aria-modal="true"
      aria-label={t('plates.detailTitle')}
      onClick={onClose}
    >
      <div className="plate-detail" onClick={(e) => e.stopPropagation()}>
        <div className="plate-detail-header">
          <h3>{t('plates.detailTitle')}</h3>
          <button type="button" onClick={onClose} aria-label={t('plates.close')}>
            {t('plates.close')}
          </button>
        </div>

        {state.kind === 'loading' && <p>{t('common.loading')}</p>}
        {state.kind === 'error' && (
          <p className="page-error" role="alert">
            {state.message}
          </p>
        )}
        {state.kind === 'ready' && (
          <div className="plate-detail-body">
            <div className="plate-detail-analysis-bar">
              <PlateAnalysisStatusBadge status={state.detail.analysisSummary.analysisStatus} />
              <PlateAnalysisButton
                plateImageId={state.detail.id}
                analysisStatus={state.detail.analysisSummary.analysisStatus}
                onRequested={fetchDetail}
              />
            </div>

            <div className="plate-detail-redaction">
              <label className="plate-detail-hide-faces">
                <input
                  type="checkbox"
                  checked={hideFaces && state.detail.redaction.available}
                  disabled={!state.detail.redaction.available}
                  onChange={(e) => setHideFaces(e.target.checked)}
                />
                <span>{t('plates.hideFaces')}</span>
              </label>
              {state.detail.redaction.available ? (
                <p className="plate-detail-hide-faces-hint">{t('plates.hideFacesHint')}</p>
              ) : (
                <p className="plate-detail-hide-faces-hint" role="note">
                  {t('plates.redactionUnavailable')}
                </p>
              )}
            </div>

            <div className="plate-detail-preview">
              <PlateOverlay
                key={hideFaces && state.detail.redaction.available ? 'redacted' : 'plain'}
                src={withBlurFaces(
                  state.detail.previewUrl,
                  hideFaces && state.detail.redaction.available,
                )}
                alt={state.detail.originalFileName}
                detections={state.detail.detections}
              />
            </div>

            <h4>{t('plates.detections')}</h4>
            <PlateDetectionList detections={state.detail.detections} />

            <dl className="plate-detail-meta">
              <dt>{t('common.type')}</dt>
              <dd>{state.detail.contentType}</dd>
              {state.detail.width && state.detail.height && (
                <>
                  <dt>{t('plates.dimensions')}</dt>
                  <dd>
                    {state.detail.width} × {state.detail.height}
                  </dd>
                </>
              )}
              <dt>{t('plates.uploadedOn')}</dt>
              <dd>{formatDate(state.detail.createdAt)}</dd>
            </dl>

            <div className="plate-detail-actions">
              <a
                className="plate-detail-original"
                href={withBlurFaces(
                  state.detail.originalUrl,
                  hideFaces && state.detail.redaction.available,
                )}
                target="_blank"
                rel="noreferrer"
              >
                {t('plates.viewOriginal')}
              </a>
              <button type="button" className="btn-danger" onClick={onDelete}>
                {t('common.delete')}
              </button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
