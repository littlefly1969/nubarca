import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router';
import {
  ApiError,
  getFaceSettings,
  updateFaceSettings,
  type FaceDiagnostics,
  type FaceThresholds,
} from '@nubarca/api-client';
import { useAuth } from '../../auth/useAuth';
import { useI18n } from '../../i18n';

// Provisional defaults (mirror the backend AiFaceOptions defaults).
const DEFAULTS: FaceThresholds = {
  clusterSimilarityThreshold: 0.4,
  candidateSimilarityThreshold: 0.3,
  searchDefaultSimilarityThreshold: 0.35,
  searchMinSimilarity: 0.2,
  searchMaxSimilarity: 0.95,
  maxFacesPerImage: 50,
  knnLouvainResolution: 1.0,
};

type Phase = 'loading' | 'ready' | 'forbidden' | 'error';

// Admin-only Face AI settings: edit the similarity thresholds + trigger bounded
// face jobs. Values are validated server-side; nothing here shows model paths or
// raw vectors.
export function FaceAISettings() {
  const { invalidateAuth } = useAuth();
  const { t } = useI18n();
  const [phase, setPhase] = useState<Phase>('loading');
  const [diag, setDiag] = useState<FaceDiagnostics | null>(null);
  const [form, setForm] = useState<FaceThresholds>(DEFAULTS);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  const load = useCallback(async () => {
    setPhase('loading');
    try {
      const d = await getFaceSettings();
      setDiag(d);
      setForm(d.thresholds);
      setPhase('ready');
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        invalidateAuth();
        return;
      }
      if (err instanceof ApiError && err.status === 403) {
        setPhase('forbidden');
        return;
      }
      setPhase('error');
    }
  }, [invalidateAuth]);

  useEffect(() => {
    void load();
  }, [load]);

  function setField(key: keyof FaceThresholds, value: number) {
    setForm((prev) => ({ ...prev, [key]: value }));
    setSaved(false);
  }

  async function handleSave() {
    setSaving(true);
    setSaveError(null);
    setSaved(false);
    try {
      const updated = await updateFaceSettings(form);
      setDiag(updated);
      setForm(updated.thresholds);
      setSaved(true);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        invalidateAuth();
        return;
      }
      if (err instanceof ApiError && err.status === 400) {
        setSaveError(t('faceSettings.invalidValues'));
        return;
      }
      setSaveError(t('faceSettings.saveFailed'));
    } finally {
      setSaving(false);
    }
  }

  const stateWord = (on: boolean) => (on ? t('faceSettings.on') : t('faceSettings.off'));

  if (phase === 'loading') {
    return <p className="muted" role="status">{t('faceSettings.loading')}</p>;
  }
  if (phase === 'forbidden') {
    return <div className="folder-error" role="alert">{t('faceSettings.forbidden')}</div>;
  }
  if (phase === 'error') {
    return (
      <div className="folder-error" role="alert">
        {t('faceSettings.loadError')}
        <button type="button" className="retry-button" onClick={() => void load()}>{t('common.tryAgain')}</button>
      </div>
    );
  }

  return (
    <section className="face-settings" aria-label={t('people.tabSettings')}>
      <h3>{t('people.tabSettings')}</h3>
      {diag && (
        <ul className="face-settings-status muted">
          <li>{t('faceSettings.stDetection', { state: stateWord(diag.faceDetectionEnabled) })}</li>
          <li>{t('faceSettings.stEmbeddings', { state: stateWord(diag.faceEmbeddingsEnabled) })}</li>
          <li>{t('faceSettings.stClustering', { state: stateWord(diag.faceClusteringEnabled) })}</li>
          <li>{t('faceSettings.stActiveProfile', { profile: diag.activeFaceProfileKey ?? t('faceSettings.notSet') })}</li>
        </ul>
      )}

      <p className="muted face-settings-legend">
        {t('faceSettings.legendPre')}<strong>{t('faceSettings.legendBold')}</strong>{t('faceSettings.legendPost')}
      </p>

      {/* ── Clustering (raggruppamento persone) ──────────────────────────── */}
      <div className="face-settings-form" aria-label={t('faceSettings.clusterSectionAria')}>
        <h4>{t('faceSettings.clusterSectionH4')}</h4>
        <ThresholdField
          label={t('faceSettings.clusterThresholdLabel')}
          level={t('faceSettings.clusterThresholdLevel')}
          description={t('faceSettings.clusterThresholdDesc')}
          value={form.clusterSimilarityThreshold}
          onChange={(v) => setField('clusterSimilarityThreshold', v)}
        />
        <NumberField
          label={t('faceSettings.louvainLabel')}
          level={t('faceSettings.louvainLevel')}
          description={t('faceSettings.louvainDesc')}
          value={form.knnLouvainResolution}
          min={0.5}
          max={3}
          step={0.1}
          onChange={(v) => setField('knnLouvainResolution', v)}
        />
        <ThresholdField
          label={t('faceSettings.candidateLabel')}
          level={t('faceSettings.candidateLevel')}
          description={t('faceSettings.candidateDesc')}
          value={form.candidateSimilarityThreshold}
          onChange={(v) => setField('candidateSimilarityThreshold', v)}
        />
      </div>

      {/* ── Ricerca volti ────────────────────────────────────────────────── */}
      <div className="face-settings-form" aria-label={t('faceSettings.searchSectionAria')}>
        <h4>{t('faceSettings.searchSectionH4')}</h4>
        <ThresholdField
          label={t('faceSettings.searchDefaultLabel')}
          level={t('faceSettings.levelSearch')}
          description={t('faceSettings.searchDefaultDesc')}
          value={form.searchDefaultSimilarityThreshold}
          onChange={(v) => setField('searchDefaultSimilarityThreshold', v)}
        />
        <ThresholdField
          label={t('faceSettings.searchMinLabel')}
          level={t('faceSettings.levelSearch')}
          description={t('faceSettings.searchMinDesc')}
          value={form.searchMinSimilarity}
          onChange={(v) => setField('searchMinSimilarity', v)}
        />
        <ThresholdField
          label={t('faceSettings.searchMaxLabel')}
          level={t('faceSettings.levelSearch')}
          description={t('faceSettings.searchMaxDesc')}
          value={form.searchMaxSimilarity}
          onChange={(v) => setField('searchMaxSimilarity', v)}
        />
      </div>

      {/* ── Rilevamento ──────────────────────────────────────────────────── */}
      <div className="face-settings-form" aria-label={t('faceSettings.detectionSectionAria')}>
        <h4>{t('faceSettings.detectionSectionH4')}</h4>
        <label className="face-settings-row face-settings-field">
          <span className="face-settings-label">
            {t('faceSettings.maxFacesLabel')} <em className="face-settings-level">{t('faceSettings.maxFacesLevel')}</em>
          </span>
          <input type="number" min={1} max={1000} step={1} value={form.maxFacesPerImage}
            onChange={(e) => setField('maxFacesPerImage', Number(e.target.value))}
            aria-label={t('faceSettings.maxFacesLabel')} />
          <small className="muted face-settings-desc">
            {t('faceSettings.maxFacesDesc')}
          </small>
        </label>
      </div>

      {saveError && <div className="folder-error" role="alert">{saveError}</div>}
      {saved && <p className="muted" role="status">{t('faceSettings.saved')}</p>}

      <div className="face-settings-actions">
        <button type="button" className="row-action-primary" onClick={() => void handleSave()} disabled={saving}>
          {saving ? t('faceSettings.saving') : t('faceSettings.save')}
        </button>
        <button type="button" onClick={() => setForm(DEFAULTS)}>{t('faceSettings.restoreDefaults')}</button>
      </div>

      {diag?.clustering && (
        <div className="face-settings-clustering" aria-label={t('faceSettings.advancedAria')}>
          <h4>{t('faceSettings.advancedH4')}</h4>
          <p className="muted face-settings-hint">
            {t('faceSettings.advHintPre')}<code>Ai:Face:*</code>{t('faceSettings.advHintPost')}
          </p>
          <ul className="face-settings-status muted">
            <li>
              {t('faceSettings.advModeLabel')}{' '}
              <strong>{diag.clustering.mode === 'pgvector_knn' ? t('faceSettings.advModeKnn') : t('faceSettings.advModeExact', { cap: diag.clustering.exactMaxFacesToCluster })}</strong>
            </li>
            {diag.clustering.mode === 'pgvector_knn' && (
              <>
                <li>{t('faceSettings.advNeighbors', { k: diag.clustering.knnNeighbors })}</li>
                <li>{t('faceSettings.advEfSearch', { ef: diag.clustering.knnEfSearch })}</li>
                <li>{t('faceSettings.advMaxEligible', { n: diag.clustering.knnMaxEligibleFacesPerRun })}</li>
                <li>{t('faceSettings.advMaxClusterSize', { n: diag.clustering.knnMaxClusterSize > 0 ? diag.clustering.knnMaxClusterSize : 300 })}</li>
              </>
            )}
          </ul>
        </div>
      )}

      <h4>{t('faceSettings.opsH4')}</h4>
      <p className="muted">
        {t('faceSettings.opsMovedPre')}
        <Link to="/admin/jobs">{t('faceSettings.opsMovedLink')}</Link>
        {t('faceSettings.opsMovedPost')}
      </p>
    </section>
  );
}

// A 0–1 similarity threshold: range slider + numeric box, with a description and a
// level tag so the admin knows exactly what it controls and where it applies.
function ThresholdField({
  label, level, description, value, onChange,
}: {
  label: string;
  level: string;
  description: string;
  value: number;
  onChange: (v: number) => void;
}) {
  const { t } = useI18n();
  const pct = Math.round(value * 100);
  return (
    <label className="face-settings-row face-settings-field">
      <span className="face-settings-label">
        {label} <em className="face-settings-level">{level}</em>
      </span>
      <input
        type="range" min={0} max={100} step={1} value={pct}
        onChange={(e) => onChange(Number(e.target.value) / 100)}
        aria-label={label}
      />
      <input
        type="number" min={0} max={1} step={0.01} value={value}
        onChange={(e) => onChange(Number(e.target.value))}
        aria-label={`${label} (${t('faceSettings.valueWord')})`}
      />
      <small className="muted face-settings-desc">{description}</small>
    </label>
  );
}

// A free numeric field (e.g. the Louvain resolution γ, range 0.5–3) with the same
// description + level treatment.
function NumberField({
  label, level, description, value, min, max, step, onChange,
}: {
  label: string;
  level: string;
  description: string;
  value: number;
  min: number;
  max: number;
  step: number;
  onChange: (v: number) => void;
}) {
  const { t } = useI18n();
  return (
    <label className="face-settings-row face-settings-field">
      <span className="face-settings-label">
        {label} <em className="face-settings-level">{level}</em>
      </span>
      <input
        type="range" min={min} max={max} step={step} value={value}
        onChange={(e) => onChange(Number(e.target.value))}
        aria-label={label}
      />
      <input
        type="number" min={min} max={max} step={step} value={value}
        onChange={(e) => onChange(Number(e.target.value))}
        aria-label={`${label} (${t('faceSettings.valueWord')})`}
      />
      <small className="muted face-settings-desc">{description}</small>
    </label>
  );
}
