import type { AlbumCoverItem } from '@nubarca/api-client';
import { useI18n } from '../i18n';

// The up-to-four-tile cover an album card shows. The items arrive ALREADY BUILT
// from the server (`coverItems`) — this never asks for a thumbnail of its own,
// and the shared-album variant returns the identical shape, so one component
// covers both the owner's album list and the destination picker.
//
// A tile whose image fails is hidden rather than left as a broken-image glyph:
// a cover is decoration, and a derived artifact is regenerable, so a missing one
// must never make an album look damaged.
export function AlbumCoverMosaic({ items, name }: { items: AlbumCoverItem[]; name: string }) {
  const { t } = useI18n();
  if (items.length === 0) {
    return <div className="album-cover album-cover-empty" aria-hidden="true">🖼</div>;
  }
  return (
    <div
      className={`album-cover album-cover-mosaic count-${Math.min(items.length, 4)}`}
      data-testid="album-cover"
    >
      {items.slice(0, 4).map((c) => (
        <img
          key={c.fileItemId}
          src={c.thumbnailUrl}
          alt={t('albums.coverAlt', { name })}
          loading="lazy"
          onError={(e) => { (e.target as HTMLImageElement).style.visibility = 'hidden'; }}
        />
      ))}
    </div>
  );
}
