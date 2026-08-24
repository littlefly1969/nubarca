import { Link } from 'react-router';
import { useI18n } from '../i18n';
import { AlbumCoverMosaic } from './AlbumCoverMosaic';
import type { AlbumCardModel } from './albumCardModel';

// ONE album card, whoever owns the album.
//
// Owned and shared albums are the same object to the person looking at them —
// same cover mosaic, same name, same counts, same density — and the difference
// is stated in a restrained line under the title rather than by relegating
// somebody else's album to a second-class strip elsewhere in the product.
//
// The one thing that is NOT uniform is the actions: `onDelete` is only ever
// passed for an album the caller owns, and a shared card therefore has no
// destructive control in its tree at all.

interface Props {
  card: AlbumCardModel;
  onDelete?: (card: AlbumCardModel) => void;
}

export function AlbumCard({ card, onDelete }: Props) {
  const { t, tn, formatDate } = useI18n();
  const shared = card.ownerKind === 'shared';

  const roleLabel = card.role === 'editor'
    ? t('albumRole.editor')
    : card.role === 'contributor' ? t('albumRole.contributor') : t('albumRole.viewer');

  return (
    <li className="album-card" data-testid="album-card" data-owner={card.ownerKind}>
      <Link to={card.href} className="album-card-link" aria-label={card.name}>
        <AlbumCoverMosaic items={card.coverItems} name={card.name} />
      </Link>
      <div className="album-card-body">
        <div className="album-card-titlerow">
          <Link to={card.href} className="album-card-name">{card.name}</Link>
          {card.showOnTv && (
            <span className="album-badge album-badge-tv" data-testid="album-tv-badge">
              {t('albums.tvBadge')}
            </span>
          )}
        </div>

        {/* Whose album this is. One quiet line, never a banner: ownership has to
            be unmistakable without overpowering the media above it. */}
        {shared ? (
          <p className="album-card-owner" data-testid="album-card-shared-owner">
            {t('albums.sharedBy', { owner: card.ownerDisplayName ?? '' })}
            {' · '}
            <span data-testid="album-card-role">{roleLabel}</span>
          </p>
        ) : (
          <p className="album-card-owner muted" data-testid="album-card-mine">{t('albums.mine')}</p>
        )}

        {card.description && <p className="album-card-desc">{card.description}</p>}

        <p className="album-card-counts">
          <span>{tn(card.itemCount, 'albums.itemsCount')}</span>
          {/* Absent counts are absent, never rendered as zero: the recipient's
              summary carries no per-kind split and inventing one would be a
              number the server never said. */}
          {card.photoCount != null && card.photoCount > 0 && (
            <span> · {t('albums.photoCount', { count: card.photoCount })}</span>
          )}
          {card.videoCount != null && card.videoCount > 0 && (
            <span> · {t('albums.videoCount', { count: card.videoCount })}</span>
          )}
          {card.excludedCount != null && card.excludedCount > 0 && (
            <span> · {t('albums.excludedCount', { count: card.excludedCount })}</span>
          )}
        </p>

        {/* An owned album's recent fact is when it was UPDATED; a shared one's is
            when it was SHARED with you. Sorting them together is fine; calling
            them the same thing would not be, so the label says which. */}
        <p className="album-card-updated muted">
          {card.recentKind === 'shared'
            ? t('albums.sharedAt', { date: formatDate(card.recentAt) })
            : t('albums.updatedAt', { date: formatDate(card.recentAt) })}
        </p>
      </div>

      {onDelete && !shared && (
        <button
          type="button"
          className="btn-danger album-card-delete"
          onClick={() => onDelete(card)}
          aria-label={t('albums.deleteLabel', { name: card.name })}
          data-testid="album-delete-btn"
        >
          {t('common.delete')}
        </button>
      )}
    </li>
  );
}
