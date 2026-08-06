import type { PlateDetection } from '@nubarca/api-client';
import { useI18n } from '../../i18n';

// Owner-private list of recognized plates. Text is prominent; confidence is
// shown subtly (muted percentage), matching the People UI convention.
export function PlateDetectionList({ detections }: { detections: PlateDetection[] }) {
  const { t } = useI18n();

  if (detections.length === 0) {
    return <p className="empty-state">{t('plates.noDetections')}</p>;
  }

  return (
    <ul className="plate-detection-list" data-testid="plate-detection-list">
      {detections.map((d) => (
        <li key={d.id} className="plate-detection-row">
          <span className="plate-detection-text">{d.normalizedText}</span>
          <span className="muted plate-detection-confidence">
            {t('plates.confidence', { pct: Math.round(d.confidence * 100) })}
          </span>
        </li>
      ))}
    </ul>
  );
}
