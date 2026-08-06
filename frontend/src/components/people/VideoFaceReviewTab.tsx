import { useCallback, useEffect, useState } from 'react';
import {
  ApiError,
  assignVideoFaceTrack,
  getVideoFaceTrackSuggestions,
  ignoreVideoFaceTrack,
  listPeople,
  listUndecidedVideoFaceTracks,
  type Person,
  type VideoFaceTrackReview,
  type VideoFaceTrackSuggestion,
} from '@nubarca/api-client';
import { MediaViewer, type MediaViewerItem } from '../MediaViewer';
import { videoPosterUrl } from '../files/types';
import { useI18n } from '../../i18n';
import { formatTrackInterval } from './videoTrackTime';

// VFACE-02: the review queue for canonical VIDEO face tracks.
//
// The contract this UI enforces is the whole point of the slice: the model only
// ever SUGGESTS, and a track becomes someone's only when the owner picks a person
// they already created. There is deliberately no "create a new person from this
// track" affordance — naming people stays in the existing People flows.
//
// Every card opens the EXISTING media viewer at the track's representative
// timestamp, so reviewing means watching the actual moment rather than trusting a
// crop.
export function VideoFaceReviewTab({ invalidateAuth }: { invalidateAuth: () => void }) {
  const { t } = useI18n();
  const [tracks, setTracks] = useState<VideoFaceTrackReview[]>([]);
  const [people, setPeople] = useState<Person[]>([]);
  const [phase, setPhase] = useState<'loading' | 'ready' | 'error'>('loading');
  const [viewer, setViewer] = useState<{ items: MediaViewerItem[]; index: number } | null>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    setPhase('loading');
    try {
      const page = await listUndecidedVideoFaceTracks(undefined, signal);
      if (signal?.aborted === true) return;
      setTracks(page.items);
      setPhase('ready');
      // The person list drives the assign menu — best-effort, never blocking.
      void listPeople().then(setPeople).catch(() => { /* non-fatal */ });
    } catch (err) {
      if (signal?.aborted === true) return;
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setPhase('error');
    }
  }, [invalidateAuth]);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  // A decided track leaves the queue immediately — no full reload, so the
  // operator's place in a long queue is preserved.
  function drop(trackId: string) {
    setTracks((prev) => prev.filter((tr) => tr.trackId !== trackId));
  }

  async function run(trackId: string, action: () => Promise<void>) {
    try {
      await action();
      drop(trackId);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      // A 404 means the track stopped being visible (deleted or vaulted
      // meanwhile): dropping it is the correct outcome either way.
      if (err instanceof ApiError && err.status === 404) { drop(trackId); return; }
      setPhase('error');
    }
  }

  function open(track: VideoFaceTrackReview) {
    setViewer({
      items: [{
        id: track.fileItemId,
        name: track.name,
        displayName: track.name,
        kind: 'video',
        initialPositionMilliseconds: track.representativeMilliseconds,
      }],
      index: 0,
    });
  }

  if (phase === 'loading') {
    return <p className="muted" role="status">{t('videoFaces.loading')}</p>;
  }
  if (phase === 'error') {
    return (
      <div className="folder-error" role="alert">
        {t('videoFaces.error')}{' '}
        <button type="button" className="retry-button" onClick={() => void load()}>
          {t('common.tryAgain')}
        </button>
      </div>
    );
  }
  if (tracks.length === 0) {
    return <p className="muted">{t('videoFaces.empty')}</p>;
  }

  return (
    <section aria-label={t('videoFaces.sectionAria')}>
      <p className="muted">{t('videoFaces.intro')}</p>
      <ul className="people-grid">
        {tracks.map((track) => (
          <li key={track.trackId} className="people-card person-video-card">
            <button
              type="button"
              className="person-video-poster"
              onClick={() => open(track)}
              aria-label={t('person.openVideoAt', {
                name: track.name,
                time: formatTrackInterval(track.startMilliseconds, track.endMilliseconds),
              })}
            >
              <img src={videoPosterUrl(track.fileItemId)} alt="" loading="lazy" />
            </button>
            <span className="people-card-name">{track.name}</span>
            <span className="person-video-interval">
              {formatTrackInterval(track.startMilliseconds, track.endMilliseconds)}
            </span>

            <TrackSuggestions
              trackId={track.trackId}
              people={people}
              onAssign={(personId) => void run(
                track.trackId, () => assignVideoFaceTrack(track.trackId, personId))}
              invalidateAuth={invalidateAuth}
            />

            <button
              type="button"
              onClick={() => void run(track.trackId, () => ignoreVideoFaceTrack(track.trackId))}
            >
              {t('videoFaces.ignore')}
            </button>
          </li>
        ))}
      </ul>

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

// Bounded candidates for one track, plus the plain "pick any of my people"
// fallback. Suggestions are loaded lazily, on the first render of the card, and
// asking for them never changes a decision.
function TrackSuggestions({
  trackId, people, onAssign, invalidateAuth,
}: {
  trackId: string;
  people: Person[];
  onAssign: (personId: string) => void;
  invalidateAuth: () => void;
}) {
  const { t } = useI18n();
  const [items, setItems] = useState<VideoFaceTrackSuggestion[]>([]);
  const [status, setStatus] = useState<'loading' | 'ready' | 'error'>('loading');

  useEffect(() => {
    const controller = new AbortController();
    (async () => {
      try {
        const page = await getVideoFaceTrackSuggestions(trackId, 3, controller.signal);
        if (controller.signal.aborted) return;
        setItems(page.items);
        setStatus('ready');
      } catch (err) {
        if (controller.signal.aborted) return;
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        setStatus('error');
      }
    })();
    return () => controller.abort();
  }, [trackId, invalidateAuth]);

  return (
    <div className="video-face-suggestions">
      {status === 'loading' && (
        <span className="muted" role="status">{t('videoFaces.suggestionsLoading')}</span>
      )}
      {status === 'error' && (
        <span className="muted">{t('videoFaces.suggestionsError')}</span>
      )}
      {status === 'ready' && items.length === 0 && (
        <span className="muted">{t('videoFaces.noSuggestions')}</span>
      )}
      {status === 'ready' && items.map((candidate) => (
        <button
          key={candidate.personId}
          type="button"
          className="video-face-suggestion"
          onClick={() => onAssign(candidate.personId)}
        >
          {t('videoFaces.confirmSuggestion', {
            name: candidate.name ?? t('people.unnamed'),
            pct: Math.round(candidate.similarity * 100),
          })}
        </button>
      ))}

      {people.length > 0 && (
        <label className="video-face-assign-any">
          {t('videoFaces.assignTo')}
          <select
            defaultValue=""
            aria-label={t('videoFaces.assignToAria')}
            onChange={(e) => {
              if (e.target.value !== '') {
                onAssign(e.target.value);
              }
            }}
          >
            <option value="">{t('videoFaces.choosePerson')}</option>
            {people.map((person) => (
              <option key={person.personId} value={person.personId}>
                {person.name ?? t('people.unnamed')}
              </option>
            ))}
          </select>
        </label>
      )}
    </div>
  );
}
