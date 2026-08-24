import { useCallback, useMemo, useState } from 'react';
import {
  ApiError,
  withdrawSharedAlbumContribution,
  type SharedAlbumItem,
  type SharedAlbumItemKind,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';
import { MediaViewer, type MediaViewerItem } from '../components/MediaViewer';
import { MediaKindTabs } from '../media/workspace/MediaKindTabs';
import { useWallSentinel } from '../media/workspace/useWallSentinel';
import { useJustifiedWall } from '../media/layout/useJustifiedWall';
import { MEDIA_WALL_GAP_PX } from '../media/layout/mediaWallGeometry';
import type { JustifiedLayoutItem } from '../media/layout/computeJustifiedRows';
import { useSequencePlayback } from '../media/playback/useSequencePlayback';
import { useSharedAlbumItems } from './useSharedAlbumItems';
import type { AlbumExperienceCapabilities } from './albumCapabilities';

// The recipient's media browser for ONE shared album.
//
// It is the same browsing LANGUAGE as the owner's album — All / Photos / Videos,
// the same justified wall, the same full-screen viewer, the same Play — built
// from the same components, over a completely different authority. Every media
// URL here was built by the SERVER and is album-scoped; nothing in this file
// composes one from a file id, because a file id addresses the owner's library
// and this caller has no grant on it.
//
// What is absent is as deliberate as what is present. There is no selection, no
// metadata drawer, no exclude, no trash, no People, no similarity, no album
// membership editing — not disabled, absent, so no regression can turn one back
// on. The capability model decides the rest.

// The kind tabs speak the workspace's vocabulary; the shared endpoint speaks
// 'all' | 'image' | 'video'. They agree today, and this states that they must.
const KIND_TAB_VALUES = { all: 'all', image: 'image', video: 'video' } as const;

interface Props {
  albumId: string;
  albumName: string;
  capabilities: AlbumExperienceCapabilities;
  // A withdrawal changed what the album contains; the page refreshes its header.
  onItemsChanged(): void;
  onNotice(message: string): void;
}

export function SharedAlbumBrowser({
  albumId, albumName, capabilities, onItemsChanged, onNotice,
}: Props) {
  const { t, tn } = useI18n();
  const { invalidateAuth } = useAuth();
  const [kind, setKind] = useState<SharedAlbumItemKind>('all');
  const [openIndex, setOpenIndex] = useState<number | null>(null);

  const wall = useSharedAlbumItems(albumId, kind, invalidateAuth);
  const { items } = wall;

  const setSentinelNode = useWallSentinel({
    ready: wall.phase.kind === 'ready',
    hasMore: wall.hasMore,
    loadMore: wall.loadMore,
  });

  // The SAME justified geometry as the library and album walls: every full row
  // spans the container exactly and each tile keeps its real aspect ratio.
  const layoutItems = useMemo<JustifiedLayoutItem[]>(
    () => items.map((item, index) => ({
      id: item.fileItemId,
      originalIndex: index,
      aspectRatio: item.width && item.height ? item.width / item.height : 1,
    })),
    [items],
  );
  const { ref: wallRef, measured, rows } = useJustifiedWall(layoutItems);

  // Play walks the CURRENT sequence — the active kind tab — so "Videos, then
  // Play" plays the videos and nothing else.
  const kindAtIndex = useCallback((i: number) => items[i]?.kind, [items]);
  const openAt = useCallback((i: number) => setOpenIndex(i), []);
  const play = useSequencePlayback({
    count: items.length,
    index: openIndex,
    kindAt: kindAtIndex,
    onOpen: openAt,
    onIndexChange: openAt,
    hasMore: wall.hasMore,
    onNeedMore: wall.loadMore,
  });

  // Every URL comes from the server's own item shape. There is deliberately no
  // branch that falls back to an owner route when one of them is null.
  const viewerItems = useMemo<MediaViewerItem[]>(
    () => items.map((item, index) => ({
      id: item.fileItemId,
      sources: {
        kind: 'albumScoped',
        previewUrl: item.previewUrl,
        posterUrl: item.posterUrl,
        videoUrl: item.videoUrl,
        downloadUrl: item.downloadUrl,
      },
      // A shared item carries NO file name by design — a filename is
      // owner-authored free text that can hold a person's name. The viewer needs
      // something to announce, so it announces the POSITION in the album.
      name: t('sharedAlbum.itemPosition', { index: index + 1, total: items.length }),
      displayName: t('sharedAlbum.itemPosition', { index: index + 1, total: items.length }),
      kind: item.kind,
    })),
    [items, t],
  );

  const withdraw = useCallback(async (item: SharedAlbumItem) => {
    if (!window.confirm(t('sharedAlbum.confirmWithdraw'))) return;
    try {
      await withdrawSharedAlbumContribution(albumId, item.fileItemId);
      setOpenIndex(null);
      wall.refresh();
      onItemsChanged();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      // Already gone — the owner removed it, or a second tab withdrew it.
      // Reload to the truth rather than reporting a failure for a no-op.
      if (err instanceof ApiError && err.status === 404) {
        onNotice(t('sharedAlbum.itemGone'));
        setOpenIndex(null);
        wall.refresh();
        onItemsChanged();
        return;
      }
      onNotice(t('sharedAlbum.withdrawError'));
    }
  }, [albumId, invalidateAuth, onItemsChanged, onNotice, t, wall]);

  if (wall.phase.kind === 'unavailable') {
    return (
      <p className="empty-state" data-testid="shared-album-unavailable" role="status">
        {t('sharedAlbum.unavailable')}
      </p>
    );
  }

  const isEmpty = items.length === 0 && wall.phase.kind !== 'loading';

  return (
    <>
      <div className="ws-sticky-chrome" data-testid="shared-album-chrome">
        <div className="ws-kind-row">
          <MediaKindTabs
            value={KIND_TAB_VALUES[kind]}
            onChange={(next) => {
              // Changing what is on screen changes what Play would play, so a
              // run in progress ends with the sequence it was playing.
              play.stop();
              setOpenIndex(null);
              setKind(next as SharedAlbumItemKind);
            }}
            panelId="shared-album-panel"
            counts={{ all: wall.total, image: wall.photoCount, video: wall.videoCount }}
          />
          {capabilities.play && (
            <button
              type="button"
              className="row-action album-play-button"
              data-testid="album-play"
              disabled={items.length === 0}
              onClick={() => play.start(0)}
            >
              {t('albumPlay.start')}
            </button>
          )}
        </div>
      </div>

      <div id="shared-album-panel" role="tabpanel" aria-labelledby={`media-kind-tab-${KIND_TAB_VALUES[kind]}`}>
        {wall.phase.kind === 'loading' && <p className="muted" role="status">{t('mediaWs.loading')}</p>}

        {wall.phase.kind === 'error' && (
          <div className="folder-error" role="alert">
            {t('sharedAlbum.loadError')}
            <button type="button" className="retry-button" onClick={wall.refresh}>
              {t('common.tryAgain')}
            </button>
          </div>
        )}

        {isEmpty && wall.phase.kind !== 'error' && (
          <p className="empty-state" data-testid="shared-album-empty">
            {kind === 'image'
              ? t('mediaWs.emptyPhotos')
              : kind === 'video' ? t('mediaWs.emptyVideos') : t('sharedAlbum.empty')}
          </p>
        )}

        {items.length > 0 && (
          <>
            <p className="muted">{tn(wall.total, 'albums.itemsCount')}</p>
            <div
              className="shared-media-wall"
              ref={wallRef}
              role="list"
              aria-label={t('sharedAlbum.wallAria', { name: albumName })}
              data-testid="shared-album-items"
              aria-busy={measured ? undefined : true}
            >
              {rows.map((row) => (
                <div
                  key={row.key}
                  className="shared-media-row"
                  style={{ height: `${row.height}px`, gap: `${MEDIA_WALL_GAP_PX}px` }}
                >
                  {row.items.map((tile) => {
                    const item = items[tile.originalIndex];
                    return (
                      <button
                        key={tile.id}
                        type="button"
                        className="shared-media-tile"
                        data-testid="shared-media-tile"
                        style={{ width: `${tile.width}px`, height: `${tile.height}px` }}
                        aria-label={t('sharedAlbum.openItem', {
                          index: tile.originalIndex + 1, total: items.length,
                        })}
                        onClick={() => setOpenIndex(tile.originalIndex)}
                      >
                        <img
                          src={item.kind === 'video'
                            ? (item.posterUrl ?? item.thumbnailUrl)
                            : item.thumbnailUrl}
                          alt=""
                          loading="lazy"
                        />
                        {item.kind === 'video' && (
                          <span className="shared-media-video-badge" aria-hidden="true">▶</span>
                        )}
                        {/* Which of these are mine, so "withdraw" is never a
                            guess. Discreet: no contributor identity is shown to
                            a member — that provenance is the owner's surface. */}
                        {item.canWithdraw && (
                          <span className="shared-media-mine-badge" data-testid="shared-media-mine">
                            {t('sharedAlbum.mine')}
                          </span>
                        )}
                      </button>
                    );
                  })}
                </div>
              ))}
            </div>

            <div className="gallery-scroll-footer">
              {wall.hasMore && (
                <div ref={setSentinelNode} className="gallery-scroll-sentinel" aria-hidden="true" />
              )}
              <p className="muted" role="status" aria-live="polite">
                {wall.phase.kind === 'loadingMore' ? t('mediaWs.loadingMore') : ''}
              </p>
            </div>
          </>
        )}
      </div>

      {openIndex !== null && viewerItems[openIndex] !== undefined && (
        <MediaViewer
          items={viewerItems}
          index={openIndex}
          onClose={() => setOpenIndex(null)}
          onIndexChange={setOpenIndex}
          onNearEnd={wall.loadMore}
          // A recipient has no metadata document and casts nothing: those
          // controls do not exist here rather than being disabled.
          capabilities={{ metadata: false, cast: false, download: capabilities.download }}
          renderActions={(viewerItem) => {
            const item = items.find((i) => i.fileItemId === viewerItem.id);
            if (item === undefined) return null;
            return (
              <>
                {capabilities.withdrawOwnContribution && item.canWithdraw && (
                  <button
                    type="button"
                    data-testid="shared-withdraw"
                    aria-label={t('sharedAlbum.withdrawAria')}
                    onClick={() => void withdraw(item)}
                  >
                    {t('sharedAlbum.withdraw')}
                  </button>
                )}
                {/* Rendered only when the SERVER supplied a download URL for
                    this item. Viewing and downloading the original are
                    different capabilities, and this is the second one. */}
                {item.downloadUrl !== null && (
                  <a
                    className="media-viewer-download-action"
                    href={item.downloadUrl}
                    data-testid="shared-download"
                    aria-label={t('common.download')}
                    // No referrer: the album-scoped URL should not travel to
                    // anything the download navigation might touch.
                    rel="noreferrer"
                  >
                    ⤓
                  </a>
                )}
              </>
            );
          }}
          playback={play.active || play.finished
            ? {
              active: play.active,
              finished: play.finished,
              onVideoEnded: play.onVideoEnded,
              onStop: play.stop,
              onReplay: play.replay,
            }
            : undefined}
        />
      )}
    </>
  );
}
