import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { VaultFile } from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { useVaultMediaObjectUrl } from './useVaultMediaObjectUrl';
import { VaultMediaInfoPanel } from './VaultMediaInfoPanel';

// Full-screen vault viewer (slice 4). Photos load their MEDIUM preview via an
// authenticated object URL (never the original); videos show their poster plus
// a "playback not available yet" message (no player, no HLS). Left/right and
// the arrow keys step between the PHOTOS of the current folder; Escape closes;
// closing restores focus to the card that opened it. A details toggle shows the
// read-only info panel. Every object URL is revoked when the file changes or the
// viewer unmounts (useVaultMediaObjectUrl).

export function VaultImageViewer({
  token,
  files,
  startId,
  onClose,
  onExpired,
}: {
  token: string;
  // All files of the current folder, in display order.
  files: VaultFile[];
  startId: string;
  onClose: () => void;
  onExpired: () => void;
}) {
  const { t } = useI18n();
  const [currentId, setCurrentId] = useState(startId);
  const [showDetails, setShowDetails] = useState(false);

  // Restore focus to whatever was focused when the viewer opened (the card).
  const restoreFocusRef = useRef<HTMLElement | null>(
    typeof document !== 'undefined' ? (document.activeElement as HTMLElement | null) : null,
  );
  const dialogRef = useRef<HTMLDivElement | null>(null);

  // Photos are the navigable set; videos/others open directly but don't take
  // part in prev/next.
  const imageIds = useMemo(() => files.filter((f) => f.mediaKind === 'image').map((f) => f.id), [files]);
  const current = files.find((f) => f.id === currentId) ?? null;
  const imageIndex = imageIds.indexOf(currentId);
  const hasPrev = imageIndex > 0;
  const hasNext = imageIndex >= 0 && imageIndex < imageIds.length - 1;

  const goPrev = useCallback(() => {
    if (imageIndex > 0) setCurrentId(imageIds[imageIndex - 1]);
  }, [imageIndex, imageIds]);
  const goNext = useCallback(() => {
    if (imageIndex >= 0 && imageIndex < imageIds.length - 1) setCurrentId(imageIds[imageIndex + 1]);
  }, [imageIndex, imageIds]);

  useEffect(() => {
    dialogRef.current?.focus();
  }, []);

  // Restore focus on unmount (close / lock / expiry all unmount this).
  useEffect(
    () => () => {
      restoreFocusRef.current?.focus?.();
    },
    [],
  );

  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') {
        e.preventDefault();
        onClose();
      } else if (e.key === 'ArrowLeft') {
        goPrev();
      } else if (e.key === 'ArrowRight') {
        goNext();
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose, goPrev, goNext]);

  const isImage = current?.mediaKind === 'image';
  const isVideo = current?.mediaKind === 'video';
  const { url, status } = useVaultMediaObjectUrl({
    token,
    fileId: currentId,
    variant: isImage ? 'preview' : 'poster',
    enabled: current != null && (isImage || isVideo),
    onExpired,
  });

  return (
    <div
      className="vault-viewer"
      role="dialog"
      aria-modal="true"
      aria-label={current?.displayName ?? ''}
      ref={dialogRef}
      tabIndex={-1}
      data-testid="vault-viewer"
    >
      <div className="vault-viewer-bar">
        <span className="vault-viewer-title">{current?.displayName}</span>
        <div className="vault-viewer-actions">
          <button
            type="button"
            className="row-action"
            aria-pressed={showDetails}
            onClick={() => setShowDetails((v) => !v)}
            data-testid="vault-viewer-details"
          >
            {t('vault.details')}
          </button>
          <button
            type="button"
            className="row-action"
            onClick={onClose}
            data-testid="vault-viewer-close"
          >
            {t('common.close')}
          </button>
        </div>
      </div>

      <div className="vault-viewer-stage">
        <button
          type="button"
          className="vault-viewer-nav vault-viewer-prev"
          onClick={goPrev}
          disabled={!hasPrev}
          aria-label={t('vault.viewerPrev')}
        >
          ‹
        </button>

        <div className="vault-viewer-frame">
          {isImage && status === 'ready' && url && (
            <img className="vault-viewer-image" src={url} alt={current?.displayName ?? ''} />
          )}
          {isImage && status === 'loading' && <p className="muted">{t('common.loading')}</p>}
          {isImage && status === 'error' && (
            <p className="muted" data-testid="vault-viewer-unavailable">
              {t('vault.previewUnavailable')}
            </p>
          )}
          {isVideo && (
            <div className="vault-viewer-video">
              {status === 'ready' && url && (
                <img className="vault-viewer-poster" src={url} alt={current?.displayName ?? ''} />
              )}
              <p className="muted" data-testid="vault-viewer-video-message">
                {t('vault.videoPlaybackUnavailable')}
              </p>
            </div>
          )}
        </div>

        <button
          type="button"
          className="vault-viewer-nav vault-viewer-next"
          onClick={goNext}
          disabled={!hasNext}
          aria-label={t('vault.viewerNext')}
        >
          ›
        </button>
      </div>

      {showDetails && current && (
        <VaultMediaInfoPanel token={token} file={current} onExpired={onExpired} />
      )}
    </div>
  );
}
