import { useEffect, useRef, useState } from 'react';
import type { VaultFile } from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { formatSize } from '../components/format';
import { useVaultMediaObjectUrl, type VaultMediaVariant } from './useVaultMediaObjectUrl';

// A single vault file rendered as a visual card. Photos show their small
// thumbnail, videos their poster; other files (or a missing derivative) show a
// neutral icon. The derived bytes are fetched lazily — only once the card is at
// (or near) the viewport — and the object URL is owned by useVaultMediaObjectUrl
// (revoked on unmount / token change). No original bytes are ever requested.

function badgeKey(kind: VaultFile['mediaKind']): 'vault.badgePhoto' | 'vault.badgeVideo' | 'vault.badgeFile' {
  if (kind === 'image') return 'vault.badgePhoto';
  if (kind === 'video') return 'vault.badgeVideo';
  return 'vault.badgeFile';
}

// Photos pull the small grid thumbnail; videos the poster; others fetch nothing.
function variantFor(file: VaultFile): VaultMediaVariant | null {
  if (file.mediaKind === 'image' && file.thumbnailAvailable) return 'thumbnail-small';
  if (file.mediaKind === 'video' && file.posterAvailable) return 'poster';
  return null;
}

export function VaultMediaCard({
  token,
  file,
  selectable,
  selected,
  onToggleSelect,
  onOpen,
  onExpired,
}: {
  token: string;
  file: VaultFile;
  selectable: boolean;
  selected: boolean;
  onToggleSelect: () => void;
  onOpen: (file: VaultFile, trigger: HTMLElement) => void;
  onExpired: () => void;
}) {
  const { t } = useI18n();
  const cardRef = useRef<HTMLDivElement | null>(null);
  // Lazy gate: becomes (and stays) true once the card enters/approaches the
  // viewport, so the whole vault is never prefetched.
  const [visible, setVisible] = useState(false);

  useEffect(() => {
    const node = cardRef.current;
    if (!node) return;
    if (typeof IntersectionObserver === 'undefined') {
      // Environments without the observer (older jsdom) load eagerly.
      setVisible(true);
      return;
    }
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting)) {
          setVisible(true);
          observer.disconnect();
        }
      },
      { rootMargin: '200px' },
    );
    observer.observe(node);
    return () => observer.disconnect();
  }, []);

  const variant = variantFor(file);
  const { url, status } = useVaultMediaObjectUrl({
    token,
    fileId: file.id,
    variant: variant ?? 'thumbnail-small',
    enabled: visible && variant !== null,
    onExpired,
  });

  const badge = t(badgeKey(file.mediaKind));
  const openable = file.mediaKind === 'image' || file.mediaKind === 'video';

  return (
    <div className="vault-card" ref={cardRef} data-testid="vault-card" data-media-kind={file.mediaKind}>
      {selectable && (
        <input
          type="checkbox"
          className="vault-card-select"
          aria-label={t('vault.selectFile', { name: file.displayName })}
          checked={selected}
          onChange={onToggleSelect}
        />
      )}
      <button
        type="button"
        className="vault-card-thumb"
        title={file.name}
        aria-label={t('vault.openItem', { name: file.displayName })}
        disabled={!openable}
        onClick={(e) => openable && onOpen(file, e.currentTarget)}
      >
        {variant && url && status === 'ready' ? (
          <img className="vault-card-image" src={url} alt={file.displayName} />
        ) : (
          <span className="vault-card-placeholder" aria-hidden="true">
            {file.mediaKind === 'video' ? '🎞' : file.mediaKind === 'image' ? '🖼' : '📄'}
          </span>
        )}
        <span className="vault-card-badge">{badge}</span>
      </button>
      <div className="vault-card-meta">
        <span className="vault-card-name" title={file.name}>
          {file.displayName}
        </span>
        <span className="vault-card-size">{formatSize(file.sizeBytes)}</span>
      </div>
    </div>
  );
}
