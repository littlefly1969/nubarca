import { useI18n } from '../i18n';
import { useCast } from './useCast';

// The compact controller that lives in the authenticated shell.
//
// It exists so closing the media viewer does not mean giving up control of the
// television. The provider that owns the session is mounted above every page, so
// the cast survives; this is the surface that admits it — the title, the device,
// a scrubber and the three controls a person actually reaches for.
//
// Deliberately restrained: no artwork, no queue, no volume slider competing with
// the TV's own. It is a reminder plus a stop button, not a second player.

function formatClock(totalSeconds: number): string {
  const s = Math.max(0, Math.floor(totalSeconds));
  const hours = Math.floor(s / 3600);
  const minutes = Math.floor((s % 3600) / 60);
  const seconds = s % 60;
  const pad = (n: number) => n.toString().padStart(2, '0');
  return hours > 0 ? `${hours}:${pad(minutes)}:${pad(seconds)}` : `${minutes}:${pad(seconds)}`;
}

export function CastMiniController() {
  const { t } = useI18n();
  const cast = useCast();

  if (cast === null) return null;
  const { remote, sessionState, playOrPause, seek, setVolume, toggleMute, stopCasting } = cast;
  // Nothing is playing on a receiver: nothing to control.
  if (remote === null || sessionState !== 'connected') return null;

  const hasDuration = Number.isFinite(remote.duration) && remote.duration > 0;

  return (
    <aside className="cast-mini" role="region" aria-label={t('cast.miniAria')}
      data-testid="cast-mini-controller">
      <div className="cast-mini__identity">
        <span className="cast-mini__title" title={remote.title ?? undefined}>
          {remote.title ?? t('cast.untitled')}
        </span>
        <span className="cast-mini__device">
          {remote.deviceName ?? t('cast.unknownDevice')}
        </span>
      </div>

      <div className="cast-mini__transport">
        <button type="button" className="icon-button" data-testid="cast-mini-playpause"
          aria-label={remote.isPaused ? t('cast.play') : t('cast.pause')}
          onClick={playOrPause}>
          {remote.isPaused ? '▶' : '❚❚'}
        </button>

        <button type="button" className="icon-button" data-testid="cast-mini-mute"
          aria-label={remote.isMuted ? t('cast.unmute') : t('cast.mute')}
          aria-pressed={remote.isMuted}
          onClick={toggleMute}>
          {remote.isMuted ? '🔇' : '🔊'}
        </button>

        <span className="cast-mini__clock" data-testid="cast-mini-clock">
          {formatClock(remote.currentTime)}
          {hasDuration ? ` / ${formatClock(remote.duration)}` : ''}
        </span>

        {hasDuration && (
          <input
            className="cast-mini__scrubber"
            type="range"
            min={0}
            max={Math.floor(remote.duration)}
            value={Math.min(Math.floor(remote.currentTime), Math.floor(remote.duration))}
            aria-label={t('cast.seek')}
            data-testid="cast-mini-seek"
            onChange={(event) => seek(Number(event.target.value))}
          />
        )}

        {/* Receiver volume. Deliberately narrow: it is a trim on what the
            television is doing, not a competitor to the TV's own control. */}
        <input
          className="cast-mini__volume"
          type="range"
          min={0}
          max={100}
          value={Math.round(remote.volumeLevel * 100)}
          aria-label={t('cast.volume')}
          data-testid="cast-mini-volume"
          onChange={(event) => { setVolume(Number(event.target.value) / 100); }}
        />

        <button type="button" className="cast-mini__stop" data-testid="cast-mini-stop"
          onClick={() => { void stopCasting(); }}>
          {t('cast.stop')}
        </button>
      </div>
    </aside>
  );
}
