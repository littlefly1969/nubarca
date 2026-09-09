import { useEffect, useState } from 'react';
import {
  PARTY_GAME_RANGES,
  PARTY_SLIDESHOW_RANGES,
  setPartyGameSettings,
  setPartySlideshowSettings,
  type AlbumPartyStatus,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { PartyChallengeManager } from '../albums/PartyChallengeManager';

// The party's NUMBERS: how long a photo holds the slideshow, what one guest may
// contribute, and how the game paces itself.
//
// MOVED here from AlbumSettingsPanel rather than copied. The panel had grown
// into the whole Party application, and two complete interfaces configuring one
// party is exactly the drift this slice removes — so this is the same draft
// state, the same validation against the same shared ranges, and the same two
// endpoints, mounted where the party now lives.
//
// They stay a DRAFT saved explicitly. A PATCH per keypress would send a
// half-typed "1" on the way to "15", and every one of those is a real setting a
// television adopts on its next poll.

function inRange(raw: string, range: { min: number; max: number }): boolean {
  const value = Number(raw);
  return raw.trim() !== '' && Number.isInteger(value) && value >= range.min && value <= range.max;
}

export function PartySlideshowSettings({
  albumId, party, onUpdated,
}: {
  albumId: string;
  party: AlbumPartyStatus;
  onUpdated(next: AlbumPartyStatus): void;
}) {
  const { t } = useI18n();
  const [saving, setSaving] = useState(false);
  const [status, setStatus] = useState<'idle' | 'saved' | 'invalid' | 'failed'>('idle');
  const [draft, setDraft] = useState({
    photoSlideSeconds: '', maxVideoSlideSeconds: '',
    maxPhotoUploadsPerParticipant: '', maxVideoUploadsPerParticipant: '',
    maxMessagesPerParticipant: '',
  });

  // Seeded from the server whenever it says something different, so the form
  // shows what is actually configured rather than what it last guessed.
  useEffect(() => {
    setDraft({
      photoSlideSeconds: String(party.photoSlideSeconds),
      maxVideoSlideSeconds: String(party.maxVideoSlideSeconds),
      maxPhotoUploadsPerParticipant: String(party.maxPhotoUploadsPerParticipant),
      maxVideoUploadsPerParticipant: String(party.maxVideoUploadsPerParticipant),
      maxMessagesPerParticipant: String(party.maxMessagesPerParticipant ?? 0),
    });
  }, [party.albumId, party.photoSlideSeconds, party.maxVideoSlideSeconds,
    party.maxPhotoUploadsPerParticipant, party.maxVideoUploadsPerParticipant,
    party.maxMessagesPerParticipant]);

  const valid =
    inRange(draft.photoSlideSeconds, PARTY_SLIDESHOW_RANGES.photoSeconds)
    && inRange(draft.maxVideoSlideSeconds, PARTY_SLIDESHOW_RANGES.maxVideoSeconds)
    && inRange(draft.maxPhotoUploadsPerParticipant, PARTY_SLIDESHOW_RANGES.quota)
    && inRange(draft.maxVideoUploadsPerParticipant, PARTY_SLIDESHOW_RANGES.quota)
    && inRange(draft.maxMessagesPerParticipant, PARTY_SLIDESHOW_RANGES.quota);

  async function save() {
    if (!valid) { setStatus('invalid'); return; }
    setSaving(true); setStatus('idle');
    try {
      onUpdated(await setPartySlideshowSettings(albumId, {
        photoSlideSeconds: Number(draft.photoSlideSeconds),
        maxVideoSlideSeconds: Number(draft.maxVideoSlideSeconds),
        maxPhotoUploadsPerParticipant: Number(draft.maxPhotoUploadsPerParticipant),
        maxVideoUploadsPerParticipant: Number(draft.maxVideoUploadsPerParticipant),
        maxMessagesPerParticipant: Number(draft.maxMessagesPerParticipant),
      }));
      setStatus('saved');
    } catch {
      setStatus('failed');
    } finally { setSaving(false); }
  }

  return (
    <div className="party-settings-block" data-testid="party-slideshow-settings">
      <h4>{t('party.slideshowSettingsTitle')}</h4>

      <label className="party-number">
        <span>{t('party.photoSlideSeconds')}</span>
        <input
          type="number" inputMode="numeric"
          min={PARTY_SLIDESHOW_RANGES.photoSeconds.min}
          max={PARTY_SLIDESHOW_RANGES.photoSeconds.max}
          value={draft.photoSlideSeconds} disabled={saving}
          aria-label={t('party.photoSlideSeconds')}
          onChange={(e) => setDraft((d) => ({ ...d, photoSlideSeconds: e.target.value }))}
        />
        <span className="muted">{t('party.secondsSuffix')}</span>
      </label>

      <label className="party-number">
        <span>{t('party.maxVideoSlideSeconds')}</span>
        <input
          type="number" inputMode="numeric"
          min={PARTY_SLIDESHOW_RANGES.maxVideoSeconds.min}
          max={PARTY_SLIDESHOW_RANGES.maxVideoSeconds.max}
          value={draft.maxVideoSlideSeconds} disabled={saving}
          aria-label={t('party.maxVideoSlideSeconds')}
          onChange={(e) => setDraft((d) => ({ ...d, maxVideoSlideSeconds: e.target.value }))}
        />
        <span className="muted">{t('party.secondsSuffix')}</span>
      </label>

      <label className="party-number">
        <span>{t('party.maxPhotosPerParticipant')}</span>
        <input
          type="number" inputMode="numeric"
          min={PARTY_SLIDESHOW_RANGES.quota.min} max={PARTY_SLIDESHOW_RANGES.quota.max}
          value={draft.maxPhotoUploadsPerParticipant} disabled={saving}
          aria-label={t('party.maxPhotosPerParticipant')}
          onChange={(e) => setDraft((d) => ({ ...d, maxPhotoUploadsPerParticipant: e.target.value }))}
        />
        <span className="muted">{t('party.zeroMeansUnlimited')}</span>
      </label>

      <label className="party-number">
        <span>{t('party.maxVideosPerParticipant')}</span>
        <input
          type="number" inputMode="numeric"
          min={PARTY_SLIDESHOW_RANGES.quota.min} max={PARTY_SLIDESHOW_RANGES.quota.max}
          value={draft.maxVideoUploadsPerParticipant} disabled={saving}
          aria-label={t('party.maxVideosPerParticipant')}
          onChange={(e) => setDraft((d) => ({ ...d, maxVideoUploadsPerParticipant: e.target.value }))}
        />
        <span className="muted">{t('party.zeroMeansUnlimited')}</span>
      </label>

      <label className="party-number">
        <span>{t('party.maxMessagesPerParticipant')}</span>
        <input
          type="number" inputMode="numeric"
          min={PARTY_SLIDESHOW_RANGES.quota.min} max={PARTY_SLIDESHOW_RANGES.quota.max}
          value={draft.maxMessagesPerParticipant} disabled={saving}
          aria-label={t('party.maxMessagesPerParticipant')}
          onChange={(e) => setDraft((d) => ({ ...d, maxMessagesPerParticipant: e.target.value }))}
        />
        <span className="muted">{t('party.zeroMeansUnlimited')}</span>
      </label>

      <button
        type="button" data-testid="party-slideshow-save"
        disabled={saving || !valid} onClick={() => void save()}
      >
        {t('party.saveSettings')}
      </button>
      {status === 'saved' && <p className="muted" role="status">{t('party.settingsSaved')}</p>}
      {status === 'invalid' && <p className="inline-error" role="alert">{t('party.settingsInvalid')}</p>}
      {status === 'failed' && <p className="inline-error" role="alert">{t('party.settingsFailed')}</p>}
    </div>
  );
}

export function PartyGameSettings({
  albumId, party, onUpdated,
}: {
  albumId: string;
  party: AlbumPartyStatus;
  onUpdated(next: AlbumPartyStatus): void;
}) {
  const { t } = useI18n();
  const [saving, setSaving] = useState(false);
  const [status, setStatus] = useState<'idle' | 'saved' | 'failed'>('idle');
  const [draft, setDraft] = useState({
    gameEnabled: false, minChallengeIntervalSeconds: '300',
    maxChallengeIntervalSeconds: '540', votesPerGuest: '3', maxChallengesPerSession: '',
  });

  useEffect(() => {
    setDraft({
      gameEnabled: party.gameEnabled ?? false,
      minChallengeIntervalSeconds: String(party.minChallengeIntervalSeconds ?? 300),
      maxChallengeIntervalSeconds: String(party.maxChallengeIntervalSeconds ?? 540),
      votesPerGuest: String(party.votesPerGuest ?? 3),
      maxChallengesPerSession:
        party.maxChallengesPerSession == null ? '' : String(party.maxChallengesPerSession),
    });
  }, [party.albumId, party.gameEnabled, party.minChallengeIntervalSeconds,
    party.maxChallengeIntervalSeconds, party.votesPerGuest, party.maxChallengesPerSession]);

  const min = Number(draft.minChallengeIntervalSeconds);
  const max = Number(draft.maxChallengeIntervalSeconds);
  const sessionMax = draft.maxChallengesPerSession === '' ? null : Number(draft.maxChallengesPerSession);
  const valid = inRange(draft.minChallengeIntervalSeconds, PARTY_GAME_RANGES.intervalSeconds)
    && inRange(draft.maxChallengeIntervalSeconds, PARTY_GAME_RANGES.intervalSeconds)
    && max >= min
    && inRange(draft.votesPerGuest, PARTY_GAME_RANGES.votes)
    && (sessionMax === null || (Number.isInteger(sessionMax)
      && sessionMax >= PARTY_GAME_RANGES.maxPerSession.min
      && sessionMax <= PARTY_GAME_RANGES.maxPerSession.max));

  async function save() {
    if (!valid) return;
    setSaving(true); setStatus('idle');
    try {
      onUpdated(await setPartyGameSettings(albumId, {
        gameEnabled: draft.gameEnabled,
        minChallengeIntervalSeconds: min,
        maxChallengeIntervalSeconds: max,
        votesPerGuest: Number(draft.votesPerGuest),
        maxChallengesPerSession: sessionMax,
      }));
      setStatus('saved');
    } catch { setStatus('failed'); } finally { setSaving(false); }
  }

  return (
    <div className="party-settings-block" data-testid="party-game-settings">
      <h4>{t('partyGame.title')}</h4>
      <p className="muted">{t('partyGame.help')}</p>
      <label className="party-toggle">
        <input
          type="checkbox" checked={draft.gameEnabled}
          aria-label={t('partyGame.enable')}
          onChange={(e) => setDraft((d) => ({ ...d, gameEnabled: e.target.checked }))}
        />
        <span>{t('partyGame.enable')}</span>
      </label>
      <div className="party-game-grid">
        <label>{t('partyGame.minInterval')}
          <input type="number" min="30" max="86400" value={draft.minChallengeIntervalSeconds}
            onChange={(e) => setDraft((d) => ({ ...d, minChallengeIntervalSeconds: e.target.value }))} /></label>
        <label>{t('partyGame.maxInterval')}
          <input type="number" min="30" max="86400" value={draft.maxChallengeIntervalSeconds}
            onChange={(e) => setDraft((d) => ({ ...d, maxChallengeIntervalSeconds: e.target.value }))} /></label>
        <label>{t('partyGame.votesPerGuest')}
          <input type="number" min="1" max="20" value={draft.votesPerGuest}
            onChange={(e) => setDraft((d) => ({ ...d, votesPerGuest: e.target.value }))} /></label>
        <label>{t('partyGame.maxPerSession')}
          <input type="number" min="1" max="100" placeholder={t('partyGame.unlimited')}
            value={draft.maxChallengesPerSession}
            onChange={(e) => setDraft((d) => ({ ...d, maxChallengesPerSession: e.target.value }))} /></label>
      </div>
      {!valid && <p className="inline-error">{t('partyGame.invalid')}</p>}
      <button type="button" disabled={saving || !valid} onClick={() => void save()}>
        {t('partyGame.save')}
      </button>
      {status === 'saved' && <p role="status" className="muted">{t('partyGame.saved')}</p>}
      {status === 'failed' && <p role="alert" className="inline-error">{t('partyGame.error')}</p>}
      {draft.gameEnabled && (
        // Preparing and conducting are different jobs, so they stay different
        // surfaces: the deck is here, the evening is run from its own page.
        <PartyChallengeManager albumId={albumId} />
      )}
    </div>
  );
}
