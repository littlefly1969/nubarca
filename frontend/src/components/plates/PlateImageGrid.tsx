import { useState } from 'react';
import type { PlateImageListItem } from '@nubarca/api-client';
import { useI18n } from '../../i18n';
import { PlateAnalysisStatusBadge } from './PlateAnalysisStatusBadge';

interface Props {
  items: PlateImageListItem[];
  onOpen: (item: PlateImageListItem) => void;
  onDelete: (item: PlateImageListItem) => void;
}

export function PlateImageGrid({ items, onOpen, onDelete }: Props) {
  return (
    <ul className="plate-grid" data-testid="plate-grid">
      {items.map((item) => (
        <PlateImageCard key={item.id} item={item} onOpen={onOpen} onDelete={onDelete} />
      ))}
    </ul>
  );
}

interface CardProps {
  item: PlateImageListItem;
  onOpen: (item: PlateImageListItem) => void;
  onDelete: (item: PlateImageListItem) => void;
}

function PlateImageCard({ item, onOpen, onDelete }: CardProps) {
  const { t, tn } = useI18n();
  const [thumbFailed, setThumbFailed] = useState(false);

  return (
    <li className="plate-card">
      <button
        type="button"
        className="plate-thumb-wrap"
        onClick={() => onOpen(item)}
        aria-label={t('plates.openDetail', { name: item.originalFileName })}
      >
        {thumbFailed ? (
          <span className="plate-thumb-placeholder" aria-hidden="true" />
        ) : (
          <img
            className="plate-thumb"
            src={item.thumbnailUrl}
            alt={item.originalFileName}
            loading="lazy"
            decoding="async"
            onError={() => setThumbFailed(true)}
          />
        )}
      </button>
      <div className="plate-card-status">
        <PlateAnalysisStatusBadge status={item.analysisStatus} />
        {item.platesCount > 0 && (
          <span className="plate-card-count">{tn(item.platesCount, 'plates.count')}</span>
        )}
      </div>
      <div className="plate-card-meta">
        <span className="plate-card-name" title={item.originalFileName}>
          {item.originalFileName}
        </span>
        <button
          type="button"
          className="btn-danger"
          onClick={() => onDelete(item)}
          aria-label={t('plates.deleteLabel', { name: item.originalFileName })}
        >
          {t('common.delete')}
        </button>
      </div>
    </li>
  );
}
