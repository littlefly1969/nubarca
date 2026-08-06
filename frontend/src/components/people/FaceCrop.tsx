import { useEffect, useState } from 'react';
import { facePreviewUrl, type FaceBox } from '@nubarca/api-client';
import { mediumPreviewUrl, smallThumbnailUrl } from '../files/types';
import { useI18n } from '../../i18n';

type Stage = 'preview' | 'medium' | 'small' | 'placeholder';

// Owner-private face chip. Prefers the SERVER-generated high-quality crop
// (/api/people/faces/{id}/preview); on error it degrades gracefully to a
// client-side CSS crop of the medium preview, then the small thumbnail, then a
// placeholder. Optionally clickable to open the context viewer.
export function FaceCrop({
  faceId,
  fileItemId,
  box,
  size = 96,
  previewSize = 'small',
  alt,
  onClick,
}: {
  faceId: string;
  fileItemId: string;
  box: FaceBox;
  size?: number;
  previewSize?: 'small' | 'medium' | 'large';
  alt?: string;
  onClick?: () => void;
}) {
  const { t } = useI18n();
  const label = alt ?? t('face.faceAlt');
  const [stage, setStage] = useState<Stage>('preview');

  // Reset the fallback chain whenever the face changes.
  useEffect(() => setStage('preview'), [faceId]);

  const region = paddedSquare(box);
  const cropStyle: React.CSSProperties = {
    position: 'absolute',
    width: `${100 / region.width}%`,
    height: `${100 / region.height}%`,
    left: `${(-region.x * 100) / region.width}%`,
    top: `${(-region.y * 100) / region.height}%`,
    maxWidth: 'none',
    objectFit: 'cover',
  };

  let inner: React.ReactNode;
  if (stage === 'preview') {
    // The server crop is already square → fill the chip, cover.
    inner = (
      <img
        src={facePreviewUrl(faceId, previewSize)}
        alt={label}
        loading="lazy"
        draggable={false}
        style={{ width: '100%', height: '100%', objectFit: 'cover' }}
        onError={() => setStage('medium')}
      />
    );
  } else if (stage === 'medium') {
    inner = (
      <img src={mediumPreviewUrl(fileItemId)} alt={label} loading="lazy" draggable={false} style={cropStyle} onError={() => setStage('small')} />
    );
  } else if (stage === 'small') {
    inner = (
      <img src={smallThumbnailUrl(fileItemId)} alt={label} loading="lazy" draggable={false} style={cropStyle} onError={() => setStage('placeholder')} />
    );
  } else {
    inner = <span className="face-crop-placeholder" aria-hidden="true" />;
  }

  const boxStyle: React.CSSProperties = {
    width: size,
    height: size,
    position: 'relative',
    overflow: 'hidden',
    display: 'inline-block',
  };

  // data-stage is dev/test-only visibility of which source rendered (server
  // preview vs a CSS fallback). It carries no storage internals.
  if (onClick) {
    return (
      <button type="button" className="face-crop face-crop-button" style={boxStyle} onClick={onClick} title={label} aria-label={label} data-stage={stage}>
        {inner}
      </button>
    );
  }
  return (
    <span className="face-crop" style={boxStyle} data-stage={stage}>
      {inner}
    </span>
  );
}

// Expand a face box to a padded square, clamped to [0, 1] (FALLBACK CSS crop only —
// never applied to the server preview). Matches the server crop geometry: 15% margin
// per side (max(w, h) * 1.30), squared and centered.
function paddedSquare(box: FaceBox): FaceBox {
  const cx = box.x + box.width / 2;
  const cy = box.y + box.height / 2;
  let side = Math.max(box.width, box.height) * 1.3;
  side = Math.min(side, 1);
  let x = cx - side / 2;
  let y = cy - side / 2;
  x = Math.min(Math.max(x, 0), 1 - side);
  y = Math.min(Math.max(y, 0), 1 - side);
  return { x, y, width: side, height: side };
}
