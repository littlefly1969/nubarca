import { useI18n } from '../i18n';
import './publicMedia.css';

// THE PUBLIC GALLERY: a party's photographs and an album shared by link, laid
// out the same way — the mosaic a guest already knows from the party. SMALL
// thumbnails, as every grid in this product uses; what a tile opens is the
// caller's (PublicMediaViewer).

/* Gallery composition.

   An editorial 2-column grid rather than a uniform contact sheet, but a
   DETERMINISTIC one: a tile's shape comes from its index, never from the
   image's real dimensions, so nothing reflows once the photos load and the
   visual order is exactly the DOM order (no masonry, no `columns`, no dense
   packing).

   Two rules keep the composition whole at any album size:

     * a wide tile every FEATURE_EVERY items, which always starts a fresh row
       because the six tiles between two of them fill exactly three rows —
       so a wide tile can never leave a gap beside it;
     * the two tiles sharing a row always share a shape, so a row never has one
       short tile and one tall one with dead space under the short one.

   The one thing that does depend on the total is the LAST tile: if it would sit
   alone it widens to fill its row. That is a single tile at the very end, so a
   photo arriving from the poll changes that row and nothing above it. */
export type GalleryShape = 'featured' | 'portrait' | 'square';

const FEATURE_EVERY = 7;

export function galleryShapes(count: number): GalleryShape[] {
  const shapes: GalleryShape[] = [];
  let row = 0;
  let col = 0;
  let cells = 0;
  for (let i = 0; i < count; i += 1) {
    if (i % FEATURE_EVERY === 0) {
      shapes.push('featured');
      cells += 2;
      row += 1;
      col = 0;
      continue;
    }
    shapes.push(row % 2 === 0 ? 'portrait' : 'square');
    cells += 1;
    col += 1;
    if (col === 2) {
      col = 0;
      row += 1;
    }
  }
  // An odd number of cells means the last row holds one tile: widen it.
  if (cells % 2 === 1 && shapes.length > 0) shapes[shapes.length - 1] = 'featured';
  return shapes;
}

export interface PublicGalleryItem {
  id: string;
  thumbnailUrl: string;
  isVideo: boolean;
}

export function PublicGallery({
  items, onOpen, testId, itemTestId,
}: {
  items: PublicGalleryItem[];
  /** The tile is handed back so focus can return to it when the viewer closes. */
  onOpen(index: number, tile: HTMLButtonElement): void;
  testId?: string;
  itemTestId?: (item: PublicGalleryItem) => string;
}) {
  const { t } = useI18n();
  const shapes = galleryShapes(items.length);
  return (
    <div className="public-gallery" data-testid={testId}>
      {items.map((item, index) => (
        <button
          key={item.id}
          type="button"
          className="public-gallery-tile"
          data-shape={shapes[index]}
          data-testid={itemTestId?.(item)}
          onClick={(e) => onOpen(index, e.currentTarget)}
          aria-label={item.isVideo ? t('party.openVideo') : t('party.openPhoto')}
        >
          <img className="public-gallery-tile-img" src={item.thumbnailUrl} alt="" loading="lazy" />
          {/* A poster and a play mark, and no duration: none is served, so
              none is invented. */}
          {item.isVideo && (
            <span className="public-gallery-tile-play" aria-hidden="true"><PlayIcon /></span>
          )}
        </button>
      ))}
    </div>
  );
}

function PlayIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d="M9.5 7.8 16.8 12l-7.3 4.2Z" />
    </svg>
  );
}
