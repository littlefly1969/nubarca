import { useEffect, useState } from 'react';
import QRCode from 'qrcode';
import type { TvPartyChallenge, TvPartyMessage } from '@nubarca/api-client';
import { useI18n, type MessageKey } from '../../i18n';

// What sits OVER the photographs on a display: the greetings band, the Hero
// card, the challenge card, the face-filter badge, the Guest Hub code — and the
// neutral party surface a display shows when there is no picture to show.
//
// They carry the same information, in the same priority, as the app's own
// components (PartyMessageRibbon, PartyHeroMessage, PartyChallengeHold,
// FaceFilterIndicator, OverlayQrCorners, PartyNativeSurface). The look is the
// browser's; the room must not be able to tell the two apart by what it is told.

/** One greeting at a time, along the bottom, still — never a ticker. */
export function TvRibbon({ message }: { message: TvPartyMessage | null }) {
  const { t } = useI18n();
  if (!message) return null;
  return (
    <div className="tvd-ribbon" key={message.id} data-testid="tv-party-ribbon">
      <span className="tvd-ribbon-author">{message.displayName ?? t('partyMessages.anonymous')}</span>
      <span className="tvd-ribbon-body">{message.text}</span>
    </div>
  );
}

/** A promoted greeting, full screen, between two media. */
export function TvHeroCard({ message }: { message: TvPartyMessage | null }) {
  const { t } = useI18n();
  if (!message) return null;
  return (
    <div className="tvd-hero" key={message.id} data-testid="tv-party-hero">
      <div className="tvd-hero-card">
        <span className="tvd-hero-mark" role="img" aria-label={t('tv.greetingMark')}>✉</span>
        <p className="tvd-hero-body">{message.text}</p>
        <p className="tvd-hero-author">{message.displayName ?? t('partyMessages.anonymous')}</p>
      </div>
    </div>
  );
}

const CHALLENGE_KIND: Record<TvPartyChallenge['kind'], MessageKey> = {
  dare: 'tv.challengeDare',
  penalty: 'tv.challengePenalty',
  guess: 'tv.challengeGuess',
  custom: 'tv.challengeCustom',
};

/**
 * The challenge that HOLDS the wall. Opaque, because a photograph advancing
 * invisibly behind it would be the wall doing something nobody asked for.
 */
export function TvChallengeCard({
  challenge, onNext,
}: { challenge: TvPartyChallenge | null; onNext: () => void }) {
  const { t } = useI18n();
  if (!challenge) return null;
  return (
    <div className="tvd-challenge" data-testid="tv-party-challenge">
      <button type="button" className="tvd-challenge-card" onClick={onNext}>
        {challenge.mediaUrl && <img className="tvd-challenge-image" src={challenge.mediaUrl} alt="" />}
        <span className="tvd-challenge-copy">
          <span className="tvd-challenge-eyebrow">{t(CHALLENGE_KIND[challenge.kind] ?? 'tv.challengeCustom')}</span>
          <span className="tvd-challenge-title">{challenge.title}</span>
          <span className="tvd-challenge-body">{challenge.body}</span>
          <span className="tvd-challenge-hint">{t('tv.challengeNext')}</span>
        </span>
      </button>
    </div>
  );
}

/**
 * The face-filter badge. The small detected-face crop the TV route serves,
 * never a name, a score or the guest's selfie.
 */
export function TvFaceIndicator({
  faceThumbnailUrl, albumName, count, onShowAll,
}: {
  faceThumbnailUrl: string | null;
  albumName: string;
  count: number;
  onShowAll: () => void;
}) {
  const { t, tn } = useI18n();
  return (
    <div className="tv-face-banner" role="status" data-testid="tv-face-indicator">
      {faceThumbnailUrl && <img className="tv-face-banner-thumb" src={faceThumbnailUrl} alt="" />}
      <span className="tv-face-banner-text">
        <span className="tv-face-banner-title">{t('tv.facePerson')}</span>
        <span className="tv-face-banner-album">{albumName}</span>
      </span>
      <span className="tv-face-banner-count">{tn(count, 'partyFace.resultsTitle')}</span>
      <button type="button" className="tv-face-showall" onClick={onShowAll} data-testid="tv-face-showall">
        {t('tv.faceShowAll')}
      </button>
    </div>
  );
}

/** The one canonical Guest Hub QR, bottom-left, fading with the rest of the chrome. */
export function TvGuestHubQr({ partyUrl, hidden }: { partyUrl: string | null; hidden: boolean }) {
  const { t } = useI18n();
  const [svg, setSvg] = useState<string | null>(null);
  useEffect(() => {
    if (!partyUrl) {
      setSvg(null);
      return;
    }
    let cancelled = false;
    void QRCode.toString(`${window.location.origin}${partyUrl}`, { type: 'svg', margin: 1, width: 220 })
      .then((value) => { if (!cancelled) setSvg(value); })
      .catch(() => { if (!cancelled) setSvg(null); });
    return () => { cancelled = true; };
  }, [partyUrl]);
  if (!svg) return null;
  return (
    <div
      className={`tv-party-corner tv-party-corner-left tv-chrome ${hidden ? 'tv-chrome-hidden' : ''}`.trim()}
      data-testid="tv-party-qr"
    >
      <div className="tv-party-qr" aria-label={t('tv.partyGuestHubQr')} dangerouslySetInnerHTML={{ __html: svg }} />
      <p className="tv-party-caption">{t('tv.partyGuestHub')}</p>
    </div>
  );
}

/**
 * The display's own calm card: the party's name and one true sentence —
 * loading, waiting for photographs, unavailable, reconnecting. Never a blank
 * rectangle in somebody's living room, never another party's frame.
 */
export function TvPartySurface({
  albumName, message, busy = false, testId, overlay = false,
}: {
  albumName: string | null;
  message: string | null;
  busy?: boolean;
  testId: string;
  overlay?: boolean;
}) {
  const { t } = useI18n();
  return (
    <div className={`tvd-surface${overlay ? ' tvd-surface--overlay' : ''}`} data-testid={testId}>
      <p className="tvd-surface-brand">{t('tv.partyBrand')}</p>
      {albumName && <h1 className="tvd-surface-title">{albumName}</h1>}
      {busy && <span className="tvd-surface-spinner" aria-hidden="true" />}
      {message && <p className="tvd-surface-message" role="status">{message}</p>}
    </div>
  );
}
