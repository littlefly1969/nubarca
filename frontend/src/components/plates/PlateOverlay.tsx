import { useState } from 'react';
import type { PlateDetection } from '@nubarca/api-client';
import { useI18n } from '../../i18n';

interface Props {
  src: string;
  alt: string;
  detections: PlateDetection[];
}

// Draws detected-plate bounding boxes over the (owner-private) preview image.
// Boxes use PERCENTAGE positioning derived from the normalized [0..1] bbox, so
// they scale correctly as the image resizes (modeled on FaceContextViewer). Each
// box carries a compact text badge with the recognized plate — text is the
// primary signal, not colour/hover alone.
export function PlateOverlay({ src, alt, detections }: Props) {
  const { t } = useI18n();
  const [failed, setFailed] = useState(false);

  return (
    <div className="plate-overlay-canvas" data-testid="plate-overlay">
      {failed ? (
        <span className="plate-thumb-placeholder" aria-hidden="true" />
      ) : (
        <img
          className="plate-overlay-image"
          src={src}
          alt={alt}
          draggable={false}
          onError={() => setFailed(true)}
        />
      )}
      {!failed &&
        detections.map((d) => (
          <div
            key={d.id}
            className="plate-overlay-box"
            style={{
              left: `${d.bbox.x * 100}%`,
              top: `${d.bbox.y * 100}%`,
              width: `${d.bbox.width * 100}%`,
              height: `${d.bbox.height * 100}%`,
            }}
            role="img"
            aria-label={t('plates.detectionAria', {
              text: d.normalizedText,
              pct: Math.round(d.confidence * 100),
            })}
          >
            <span className="plate-overlay-badge">{d.normalizedText}</span>
          </div>
        ))}
    </div>
  );
}
