import { useCallback, useEffect, useRef, useState } from 'react';
import { useI18n } from '../i18n';
import { useCast } from './useCast';
import './castSdkTypes';

// The Cast affordance shown beside a VIDEO.
//
// It is two controls that read as one. `<google-cast-launcher>` is the official
// custom element the framework registers — it owns device discovery, the picker,
// and its own connected/disconnected art, which is exactly why NubArca does not
// draw an imitation Cast icon. Next to it sits a NubArca button that sends THIS
// video to an already-connected receiver.
//
// The one-click case is handled by intent: pressing the launcher while a video
// is open records that the user wants to cast it, and the video is sent as soon
// as a session exists. Choosing a receiver from the Chrome menu without pressing
// anything here does NOT start playing something — a session is not a request.
//
// No Cast control is rendered for images: this phase casts video only.

interface CastVideoControlProps {
  fileId: string;
  title: string;
  subtitle?: string | null;
  /** Where local playback has reached right now, in seconds. */
  getPositionSeconds: () => number;
  /** Called just before the receiver is asked to play, so local audio can stop. */
  onHandoff?: () => void;
}

export function CastVideoControl({
  fileId, title, subtitle, getPositionSeconds, onHandoff,
}: CastVideoControlProps) {
  const { t } = useI18n();
  const cast = useCast();
  const [intent, setIntent] = useState(false);
  const castingRef = useRef(false);

  const send = useCallback(() => {
    if (cast === null || castingRef.current) return;
    castingRef.current = true;
    // Local playback stops FIRST. Two devices playing the same soundtrack a
    // second apart is the single most jarring way to get this wrong.
    onHandoff?.();
    void cast.castVideo({
      fileId,
      title,
      subtitle: subtitle ?? null,
      positionSeconds: getPositionSeconds(),
    }).finally(() => { castingRef.current = false; });
  }, [cast, fileId, title, subtitle, getPositionSeconds, onHandoff]);

  // The intent resolves the moment a session exists.
  useEffect(() => {
    if (!intent || cast?.sessionState !== 'connected') return;
    setIntent(false);
    send();
  }, [intent, cast?.sessionState, send]);

  // Navigating to another item drops a pending intent: the user asked to cast
  // what they were looking at, not whatever they moved on to.
  useEffect(() => { setIntent(false); }, [fileId]);

  if (cast === null) return null;

  const { availability, sessionState, status, error, remote } = cast;

  if (availability === 'no-permission') return null;

  if (availability !== 'available' && availability !== 'unknown') {
    // Present but disabled, with the reason. A control that silently vanishes
    // teaches the user nothing about why their television is not an option.
    return (
      <span className="cast-control cast-control--unavailable">
        <button type="button" className="cast-button" disabled
          data-testid="cast-unavailable"
          aria-label={t('cast.unavailable')}
          title={t(UNAVAILABLE_REASON[availability])}>
          <CastGlyph />
        </button>
      </span>
    );
  }

  const isPlayingThis = remote?.fileId === fileId;

  return (
    <span className="cast-control" data-testid="cast-control">
      {/* Official launcher: framework-owned availability and picker. */}
      <google-cast-launcher
        className="cast-launcher"
        data-testid="cast-launcher"
        aria-label={t('cast.cast')}
        title={t('cast.cast')}
        onClick={() => { if (!isPlayingThis) setIntent(true); }}
      />

      {/* Send THIS video to a receiver that is already connected. Hidden when
          it is already what is playing — there is nothing to ask for. */}
      {sessionState === 'connected' && !isPlayingThis && (
        <button
          type="button"
          className="cast-button cast-button--send"
          data-testid="cast-send"
          onClick={send}
        >
          {t('cast.playHere')}
        </button>
      )}

      {status === 'preparing' && (
        <span className="cast-status" role="status" data-testid="cast-preparing">
          {t('cast.preparing')}
        </span>
      )}
      {status === 'error' && error !== null && (
        <span className="cast-status cast-status--error" role="alert" data-testid="cast-error">
          {t(ERROR_MESSAGE[error])}
        </span>
      )}
      {isPlayingThis && remote?.deviceName != null && (
        <span className="cast-status" role="status" data-testid="cast-playing-on">
          {t('cast.playingOn', { device: remote.deviceName })}
        </span>
      )}
    </span>
  );
}

// A minimal Cast glyph for the DISABLED state only. The framework's launcher
// element does not exist when Cast is unavailable, so there is nothing official
// to render — and this is never shown next to a working one.
function CastGlyph() {
  return (
    <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor"
      strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M3 20h.01M3 16a4 4 0 0 1 4 4M3 12a8 8 0 0 1 8 8" />
      <path d="M21 18V7a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v1" />
    </svg>
  );
}

const UNAVAILABLE_REASON = {
  unsupported: 'cast.unsupportedBrowser',
  'insecure-origin': 'cast.insecureOrigin',
  'unreachable-origin': 'cast.unreachableOrigin',
  failed: 'cast.sdkFailed',
} as const;

const ERROR_MESSAGE = {
  grant: 'cast.errorGrant',
  preparing: 'cast.errorPreparing',
  media: 'cast.errorMedia',
} as const;
