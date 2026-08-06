import { useI18n } from '../i18n';

// The single persistent bulk-action surface used by every media view. On
// desktop it sticks just below the toolbar; on narrow screens CSS pins it to
// the bottom of the viewport as a touch-friendly action bar. It is keyboard
// reachable and never traps focus.
export interface BulkActionBarProps {
  count: number;
  onAddToAlbum(): void;
  // Only supplied inside an album (removes membership, never deletes the file).
  onRemoveFromAlbum?: () => void;
  // Only supplied when a safe trash action exists for this surface.
  onMoveToTrash?: () => void;
  // Only supplied where the owner-private Aesthetics Lab add is offered
  // (the gallery). Reuses this selection instead of a separate picker.
  onAddToAestheticsLab?: () => void;
  onClear(): void;
  busy?: boolean;
}

export function BulkActionBar({
  count,
  onAddToAlbum,
  onRemoveFromAlbum,
  onMoveToTrash,
  onAddToAestheticsLab,
  onClear,
  busy = false,
}: BulkActionBarProps) {
  const { t, tn } = useI18n();
  if (count === 0) return null;

  return (
    <div className="bulk-action-bar" role="region" aria-label={tn(count, 'gallerySel.itemsSelected')}>
      <span className="bulk-action-count" data-testid="bulk-selected-count">
        {tn(count, 'gallerySel.itemsSelected')}
      </span>
      <div className="bulk-action-buttons">
        <button
          type="button"
          className="row-action-primary"
          onClick={onAddToAlbum}
          disabled={busy}
          data-testid="bulk-add-to-album"
        >
          {t('gallerySel.addToAlbum')}
        </button>
        {onRemoveFromAlbum && (
          <button
            type="button"
            className="row-action row-action-destructive"
            onClick={onRemoveFromAlbum}
            disabled={busy}
            data-testid="bulk-remove-from-album"
          >
            {t('gallerySel.removeFromAlbum')}
          </button>
        )}
        {onMoveToTrash && (
          <button
            type="button"
            className="row-action row-action-destructive"
            onClick={onMoveToTrash}
            disabled={busy}
            data-testid="bulk-move-to-trash"
          >
            {t('gallerySel.moveToTrash')}
          </button>
        )}
        {onAddToAestheticsLab && (
          <button
            type="button"
            className="row-action"
            onClick={onAddToAestheticsLab}
            disabled={busy}
            data-testid="bulk-add-to-aesthetics-lab"
          >
            {t('gallerySel.addToAestheticsLab')}
          </button>
        )}
        <button
          type="button"
          className="row-action"
          onClick={onClear}
          disabled={busy}
          data-testid="bulk-clear"
        >
          {t('gallerySel.clear')}
        </button>
      </div>
    </div>
  );
}
