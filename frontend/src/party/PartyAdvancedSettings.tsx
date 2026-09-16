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
    <div className="pw-settings-block" data-testid="party-slideshow-settings">
      <h4 className="pw-panel-title">{t('party.slideshowSettingsTitle')}</h4>
      <div className="pw-numbers">

      <label className="pw-number">
        <span>{t('party.photoSlideSeconds')}</span>
        <input
          type="number" inputMode="numeric"
          min={PARTY_SLIDESHOW_RANGES.photoSeconds.min}
          max={PARTY_SLIDESHOW_RANGES.photoSeconds.max}
          value={draft.photoSlideSeconds} disabled={saving}
          aria-label={t('party.photoSlideSeconds')}
          onChange={(e) => setDraft((d) => ({ ...d, photoSlideSeconds: e.target.value }))}
        />
        <span className="pw-number-suffix">{t('party.secondsSuffix')}</span>
      </label>

      <label className="pw-number">
        <span>{t('party.maxVideoSlideSeconds')}</span>
        <input
          type="number" inputMode="numeric"
          min={PARTY_SLIDESHOW_RANGES.maxVideoSeconds.min}
          max={PARTY_SLIDESHOW_RANGES.maxVideoSeconds.max}
          value={draft.maxVideoSlideSeconds} disabled={saving}
          aria-label={t('party.maxVideoSlideSeconds')}
          onChange={(e) => setDraft((d) => ({ ...d, maxVideoSlideSeconds: e.target.value }))}
        />
        <span className="pw-number-suffix">{t('party.secondsSuffix')}</span>
      </label>

      <label className="pw-number">
        <span>{t('party.maxPhotosPerParticipant')}</span>
        <input
          type="number" inputMode="numeric"
          min={PARTY_SLIDESHOW_RANGES.quota.min} max={PARTY_SLIDESHOW_RANGES.quota.max}
          value={draft.maxPhotoUploadsPerParticipant} disabled={saving}
          aria-label={t('party.maxPhotosPerParticipant')}
          onChange={(e) => setDraft((d) => ({ ...d, maxPhotoUploadsPerParticipant: e.target.value }))}
        />
        <span className="pw-number-suffix">{t('party.zeroMeansUnlimited')}</span>
      </label>

      <label className="pw-number">
        <span>{t('party.maxVideosPerParticipant')}</span>
        <input
          type="number" inputMode="numeric"
          min={PARTY_SLIDESHOW_RANGES.quota.min} max={PARTY_SLIDESHOW_RANGES.quota.max}
          value={draft.maxVideoUploadsPerParticipant} disabled={saving}
          aria-label={t('party.maxVideosPerParticipant')}
          onChange={(e) => setDraft((d) => ({ ...d, maxVideoUploadsPerParticipant: e.target.value }))}
        />
        <span className="pw-number-suffix">{t('party.zeroMeansUnlimited')}</span>
      </label>

      <label className="pw-number">
        <span>{t('party.maxMessagesPerParticipant')}</span>
        <input
          type="number" inputMode="numeric"
          min={PARTY_SLIDESHOW_RANGES.quota.min} max={PARTY_SLIDESHOW_RANGES.quota.max}
          value={draft.maxMessagesPerParticipant} disabled={saving}
          aria-label={t('party.maxMessagesPerParticipant')}
          onChange={(e) => setDraft((d) => ({ ...d, maxMessagesPerParticipant: e.target.value }))}
        />
        <span className="pw-number-suffix">{t('party.zeroMeansUnlimited')}</span>
      </label>

      </div>

      <div className="pw-form-foot">
        <button
          type="button" className="pw-btn pw-btn--primary" data-testid="party-slideshow-save"
          disabled={saving || !valid} aria-busy={saving || undefined} onClick={() => void save()}
        >
          {t('party.saveSettings')}
        </button>
        <span aria-live="polite">
          {status === 'saved' && (
            <span className="pw-small pw-muted" role="status">{t('party.settingsSaved')}</span>
          )}
        </span>
      </div>
      {status === 'invalid' && <p className="pw-field-error" role="alert">{t('party.settingsInvalid')}</p>}
      {status === 'failed' && <p className="pw-field-error" role="alert">{t('party.settingsFailed')}</p>}
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
    gameEnabled: false, priorityVotingEnabled: false, minChallengeIntervalSeconds: '300',
    maxChallengeIntervalSeconds: '540', votesPerGuest: '3', maxChallengesPerSession: '',
  });

  useEffect(() => {
    setDraft({
      gameEnabled: party.gameEnabled ?? false,
      priorityVotingEnabled: party.priorityVotingEnabled ?? false,
      minChallengeIntervalSeconds: String(party.minChallengeIntervalSeconds ?? 300),
      maxChallengeIntervalSeconds: String(party.maxChallengeIntervalSeconds ?? 540),
      votesPerGuest: String(party.votesPerGuest ?? 3),
      maxChallengesPerSession:
        party.maxChallengesPerSession == null ? '' : String(party.maxChallengesPerSession),
    });
  }, [party.albumId, party.gameEnabled, party.priorityVotingEnabled,
    party.minChallengeIntervalSeconds,
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
        priorityVotingEnabled: draft.priorityVotingEnabled,
        minChallengeIntervalSeconds: min,
        maxChallengeIntervalSeconds: max,
        votesPerGuest: Number(draft.votesPerGuest),
        maxChallengesPerSession: sessionMax,
      }));
      setStatus('saved');
    } catch { setStatus('failed'); } finally { setSaving(false); }
  }

  return (
    <div className="pw-settings-block" data-testid="party-game-settings">
      <div className="pw-rows">
        <div className="pw-row">
          <span className="pw-row-text">
            <span className="pw-row-label">{t('partyGame.enable')}</span>
            <span className="pw-row-note">{t('partyGame.help')}</span>
          </span>
          <span className="pw-row-control">
            <button
              type="button" role="switch" className="pw-switch"
              aria-checked={draft.gameEnabled} aria-label={t('partyGame.enable')}
              data-testid="party-game-enable"
              onClick={() => setDraft((d) => ({ ...d, gameEnabled: !d.gameEnabled }))}
            />
          </span>
        </div>

        {/* The guests' pre-game preferences. ADVISORY, and the copy says so:
            they tell the host what the room wants and choose nothing by
            themselves. Only two decisions — whether to ask, and how many each
            guest may pick — because that is the whole of the feature. */}
        <div className="pw-row">
          <span className="pw-row-text">
            <span className="pw-row-label">{t('partyGame.priorityVoting')}</span>
            <span className="pw-row-note">{t('partyGame.priorityVotingHelp')}</span>
          </span>
          <span className="pw-row-control">
            <button
              type="button" role="switch" className="pw-switch"
              aria-checked={draft.priorityVotingEnabled} aria-label={t('partyGame.priorityVoting')}
              data-testid="party-game-priority-voting"
              onClick={() => setDraft((d) => ({
                ...d, priorityVotingEnabled: !d.priorityVotingEnabled,
              }))}
            />
          </span>
        </div>

        {draft.priorityVotingEnabled && (
          <div className="pw-row">
            <span className="pw-row-text">
              <span className="pw-row-label">{t('partyGame.votesPerGuest')}</span>
            </span>
            <span className="pw-row-control">
              <label className="pw-number pw-number--tight">
                <span className="visually-hidden">{t('partyGame.votesPerGuest')}</span>
                <input
                  type="number" inputMode="numeric" min="1" max="20" value={draft.votesPerGuest}
                  aria-label={t('partyGame.votesPerGuest')}
                  onChange={(e) => setDraft((d) => ({ ...d, votesPerGuest: e.target.value }))}
                />
              </label>
            </span>
          </div>
        )}
      </div>

      {!valid && <p className="pw-field-error" role="alert">{t('partyGame.invalid')}</p>}

      <div className="pw-form-foot">
        <button
          type="button" className="pw-btn pw-btn--primary" data-testid="party-game-save"
          disabled={saving || !valid} aria-busy={saving || undefined} onClick={() => void save()}
        >
          {t('partyGame.save')}
        </button>
        <span aria-live="polite">
          {status === 'saved' && (
            <span className="pw-small pw-muted" role="status">{t('partyGame.saved')}</span>
          )}
        </span>
      </div>
      {status === 'failed' && <p role="alert" className="pw-field-error">{t('partyGame.error')}</p>}

      {draft.gameEnabled && (
        // Preparing and conducting are different jobs, so they stay different
        // surfaces: the deck is here, the evening is run from its own page.
        <PartyChallengeManager albumId={albumId} />
      )}
    </div>
  );
}
