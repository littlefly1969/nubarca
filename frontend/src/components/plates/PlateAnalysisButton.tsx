import { useState } from 'react';
import { ApiError, requestPlateAnalysis } from '@nubarca/api-client';
import { useAuth } from '../../auth/useAuth';
import { useI18n } from '../../i18n';

interface Props {
  plateImageId: string;
  // The current product analysis status (drives the label + busy state).
  analysisStatus: string;
  // Called after a successful enqueue so the caller can start polling.
  onRequested: () => void;
}

// Requests owner-private ALPR analysis for a plate image. Enqueue only — the
// analysis runs on the worker; the caller polls for the result.
export function PlateAnalysisButton({ plateImageId, analysisStatus, onRequested }: Props) {
  const { invalidateAuth } = useAuth();
  const { t } = useI18n();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const inProgress = analysisStatus === 'pending' || analysisStatus === 'running';
  const label =
    analysisStatus === 'completed' || analysisStatus === 'failed'
      ? t('plates.reAnalyze')
      : t('plates.analyze');

  const handleClick = async () => {
    setBusy(true);
    setError(null);
    try {
      await requestPlateAnalysis(plateImageId);
      onRequested();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        invalidateAuth();
        return;
      }
      setError(t('plates.analysisError'));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="plate-analyze">
      <button type="button" onClick={() => void handleClick()} disabled={busy || inProgress}>
        {busy || inProgress ? t('plates.analyzing') : label}
      </button>
      {error && (
        <span className="inline-error" role="alert">
          {error}
        </span>
      )}
    </div>
  );
}
