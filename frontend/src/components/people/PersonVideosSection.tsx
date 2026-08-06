import { useCallback, useEffect, useState } from 'react';
import {
  ApiError,
  getPersonVideos,
  type PersonVideo,
  type PersonVideoMatch,
} from '@nubarca/api-client';
import { MediaViewer, type MediaViewerItem } from '../MediaViewer';
import { videoPosterUrl } from '../files/types';
import { useI18n } from '../../i18n';
import { formatTrackInterval } from './videoTrackTime';

// VFACE-02: the person-media surface, extended with VIDEO results.
//
// A video appears here only through a track the owner CONFIRMED — undecided and
// ignored tracks never surface. Each card carries the person's best interval plus
// a bounded set of further intervals, and any of them opens the EXISTING media
// viewer at that timestamp (the same handoff VSEM-03 semantic results use).
//
// The tile shows the video's poster with `object-fit: contain` on the ambient
// backdrop the gallery already uses, so a vertical clip keeps its own aspect
// instead of being cropped to a square.
export function PersonVideosSection({
  personId,
  invalidateAuth,
}: {
  personId: string;
  invalidateAuth: () => void;
}) {
  const { t } = useI18n();
  const [videos, setVideos] = useState<PersonVideo[]>([]);
  const [phase, setPhase] = useState<'loading' | 'ready' | 'error'>('loading');
  const [viewer, setViewer] = useState<{ items: MediaViewerItem[]; index: number } | null>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    setPhase('loading');
    try {
      const items = await getPersonVideos(personId, signal);
      if (signal?.aborted === true) return;
      setVideos(items);
      setPhase('ready');
    } catch (err) {
      if (signal?.aborted === true) return;
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setPhase('error');
    }
  }, [personId, invalidateAuth]);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  // Open the viewer over the WHOLE result list (so navigation still works) with
  // the clicked video positioned at the chosen match.
  function open(video: PersonVideo, match: PersonVideoMatch) {
    const items: MediaViewerItem[] = videos.map((v) => ({
      id: v.fileItemId,
      name: v.name,
      displayName: v.name,
      kind: 'video',
      initialPositionMilliseconds:
        v.fileItemId === video.fileItemId
          ? match.representativeMilliseconds
          : v.bestMatch.representativeMilliseconds,
    }));
    setViewer({ items, index: videos.findIndex((v) => v.fileItemId === video.fileItemId) });
  }

  return (
    <section className="person-videos" aria-label={t('person.videosAria')}>
      <h3>{t('person.videosHeading', { count: videos.length })}</h3>

      {phase === 'loading' && <p className="muted" role="status">{t('person.videosLoading')}</p>}
      {phase === 'error' && (
        <div className="folder-error" role="alert">
          {t('person.videosError')}{' '}
          <button type="button" className="retry-button" onClick={() => void load()}>
            {t('common.tryAgain')}
          </button>
        </div>
      )}
      {phase === 'ready' && videos.length === 0 && (
        <p className="muted">{t('person.noVideos')}</p>
      )}

      {phase === 'ready' && videos.length > 0 && (
        <ul className="people-grid">
          {videos.map((video) => (
            <li key={video.fileItemId} className="people-card person-video-card">
              <button
                type="button"
                className="person-video-poster"
                onClick={() => open(video, video.bestMatch)}
                aria-label={t('person.openVideoAt', {
                  name: video.name,
                  time: formatTrackInterval(
                    video.bestMatch.startMilliseconds, video.bestMatch.endMilliseconds),
                })}
              >
                <img src={videoPosterUrl(video.fileItemId)} alt="" loading="lazy" />
              </button>
              <span className="people-card-name">{video.name}</span>
              <span className="person-video-interval">
                {formatTrackInterval(
                  video.bestMatch.startMilliseconds, video.bestMatch.endMilliseconds)}
              </span>
              {video.additionalMatches.length > 0 && (
                <ul className="person-video-matches">
                  {video.additionalMatches.map((match) => (
                    <li key={`${match.startMilliseconds}-${match.endMilliseconds}`}>
                      <button type="button" onClick={() => open(video, match)}>
                        {formatTrackInterval(match.startMilliseconds, match.endMilliseconds)}
                      </button>
                    </li>
                  ))}
                </ul>
              )}
            </li>
          ))}
        </ul>
      )}

      {viewer && (
        <MediaViewer
          items={viewer.items}
          index={viewer.index}
          onIndexChange={(next) => setViewer((v) => (v ? { ...v, index: next } : v))}
          onClose={() => setViewer(null)}
        />
      )}
    </section>
  );
}
