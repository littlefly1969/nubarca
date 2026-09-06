import { useMemo, useState } from 'react';
import {
  createPartyChallenge, updatePartyChallenge,
  type AlbumItemSummary, type PartyChallenge, type PartyChallengeKind,
  type PartyChallengeVotingMode,
} from '@nubarca/api-client';
import { Modal } from '../components/Overlay';
import { PartyChallengeCard } from '../party/PartyChallengeCard';
import { useI18n, type MessageKey } from '../i18n';
import './PartyDeck.css';

// Preparing ONE activity, in three steps: what it is, how it is played, how it
// will look.
//
// The thing it replaces was a column of eight controls with a preview stuck to
// the bottom, inside a settings drawer, and it read like administering a table.
// The steps exist to make the shape of the decision visible — a host is writing
// something for a room, then deciding how the room answers, then looking at it.
//
// Three rules hold it together.
//
// The preview in step 3 is THE renderer (PartyChallengeCard), not a drawing of
// it, so what the host approves is what the television will show.
//
// The rules in step 2 are FIELDS. Nothing is serialised into the instructions:
// a runtime that had to read prose to learn whether an activity is timed would
// be one edit away from being wrong.
//
// Unsaved work is never lost to a stray Escape or a misplaced click. While the
// draft is dirty the overlay stops being dismissable, and closing deliberately
// asks once, in place.

const STEPS = ['activity', 'rules', 'preview'] as const;
type Step = (typeof STEPS)[number];

const STEP_LABEL: Record<Step, MessageKey> = {
  activity: 'partyComposer.stepActivity',
  rules: 'partyComposer.stepRules',
  preview: 'partyComposer.stepPreview',
};

const KINDS: PartyChallengeKind[] = ['dare', 'penalty', 'guess', 'custom'];
const KIND_LABEL: Record<PartyChallengeKind, MessageKey> = {
  dare: 'partyChallenges.kind.dare',
  penalty: 'partyChallenges.kind.penalty',
  guess: 'partyChallenges.kind.guess',
  custom: 'partyChallenges.kind.custom',
};

// The four durations a host actually reaches for. The number field stays, for
// the fifth.
const DURATION_PRESETS = [30, 60, 120, 300];

interface Draft {
  kind: PartyChallengeKind;
  title: string;
  body: string;
  mediaFileItemId: string | null;
  durationSeconds: number | null;
  votingMode: PartyChallengeVotingMode;
  voteQuestion: string;
  isEnabled: boolean;
}

function draftFrom(challenge: PartyChallenge | null): Draft {
  return {
    kind: challenge?.kind ?? 'dare',
    title: challenge?.title ?? '',
    body: challenge?.body ?? '',
    mediaFileItemId: challenge?.mediaFileItemId ?? null,
    durationSeconds: challenge?.durationSeconds ?? null,
    votingMode: challenge?.votingMode ?? 'binary',
    voteQuestion: challenge?.voteQuestion ?? '',
    isEnabled: challenge?.isEnabled ?? true,
  };
}

export interface PartyChallengeComposerProps {
  albumId: string;
  /** Album members that have a thumbnail — the pool the photo picker offers. */
  media: AlbumItemSummary[];
  /** null creates a new activity; a value edits that one. */
  challenge: PartyChallenge | null;
  /** Where this activity sits in the deck, 1-based, and how many there are. */
  position: number;
  total: number;
  onClose(): void;
  onSaved(): void;
}

export function PartyChallengeComposer({
  albumId, media, challenge, position, total, onClose, onSaved,
}: PartyChallengeComposerProps) {
  const { t } = useI18n();
  const initial = useMemo(() => draftFrom(challenge), [challenge]);
  const [draft, setDraft] = useState<Draft>(initial);
  const [step, setStep] = useState<Step>('activity');
  const [saving, setSaving] = useState(false);
  const [failed, setFailed] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);

  const set = <K extends keyof Draft>(key: K, value: Draft[K]) =>
    setDraft((current) => ({ ...current, [key]: value }));

  const dirty = useMemo(
    () => (Object.keys(initial) as (keyof Draft)[]).some((key) => draft[key] !== initial[key]),
    [draft, initial],
  );
  const activityValid = draft.title.trim().length > 0 && draft.body.trim().length > 0;
  const selectedMedia = media.find((x) => x.fileItemId === draft.mediaFileItemId);
  const index = STEPS.indexOf(step);

  const close = () => {
    if (dirty && !saving) { setConfirmDiscard(true); return; }
    onClose();
  };

  const save = async () => {
    if (!activityValid || saving) return;
    setSaving(true);
    setFailed(false);
    const value = {
      title: draft.title.trim(),
      body: draft.body.trim(),
      kind: draft.kind,
      mediaFileItemId: draft.mediaFileItemId,
      isEnabled: draft.isEnabled,
      durationSeconds: draft.durationSeconds,
      votingMode: draft.votingMode,
      voteQuestion: draft.voteQuestion.trim() || null,
    };
    try {
      if (challenge) await updatePartyChallenge(albumId, challenge.id, value);
      else await createPartyChallenge(albumId, value);
      onSaved();
    } catch {
      setFailed(true);
    } finally {
      setSaving(false);
    }
  };

  const footer = confirmDiscard ? (
    <div className="party-composer-discard" role="alert">
      <p>{t('partyComposer.unsaved')}</p>
      <div className="party-composer-actions">
        <button type="button" onClick={() => setConfirmDiscard(false)}>
          {t('partyComposer.keepEditing')}
        </button>
        <button type="button" className="btn-danger" onClick={onClose}>
          {t('partyComposer.discard')}
        </button>
      </div>
    </div>
  ) : (
    <div className="party-composer-actions">
      {index > 0 && (
        <button type="button" onClick={() => setStep(STEPS[index - 1])}>
          {t('partyComposer.back')}
        </button>
      )}
      {index < STEPS.length - 1 ? (
        <button
          type="button"
          className="party-composer-primary"
          disabled={!activityValid}
          onClick={() => setStep(STEPS[index + 1])}
        >
          {t('partyComposer.next')}
        </button>
      ) : (
        <button
          type="button"
          className="party-composer-primary"
          data-testid="party-composer-save"
          disabled={!activityValid || saving}
          onClick={() => void save()}
        >
          {saving ? t('partyComposer.saving') : t('partyComposer.save')}
        </button>
      )}
    </div>
  );

  return (
    <Modal
      title={t(challenge ? 'partyComposer.editTitle' : 'partyComposer.createTitle')}
      onClose={close}
      // A dirty draft refuses Escape and the backdrop. The close button always
      // works and asks; nothing is ever discarded by a stray keystroke.
      dismissable={!dirty && !saving}
      ownsKeyboard
      layer="workspace"
      testId="party-composer"
      footer={footer}
    >
      <ol className="party-composer-steps" aria-label={t('partyComposer.stepsLabel')}>
        {STEPS.map((value, at) => (
          <li key={value} className={value === step ? 'is-active' : undefined}
            aria-current={value === step ? 'step' : undefined}>
            <button type="button" disabled={at > index && !activityValid}
              onClick={() => setStep(value)}>
              {t(STEP_LABEL[value])}
            </button>
          </li>
        ))}
      </ol>

      {step === 'activity' && (
        <div className="form-grid">
          <div className="field">
            <span className="field__label" id="party-composer-kind">{t('partyGame.kind')}</span>
            <div className="media-kind-tabs" role="radiogroup" aria-labelledby="party-composer-kind">
              {KINDS.map((kind) => (
                <button
                  key={kind}
                  type="button"
                  role="radio"
                  aria-checked={draft.kind === kind}
                  className={`media-kind-tab${draft.kind === kind ? ' is-active' : ''}`}
                  onClick={() => set('kind', kind)}
                >
                  {t(KIND_LABEL[kind])}
                </button>
              ))}
            </div>
          </div>

          <label className="field">
            <span className="field__label">{t('partyGame.challengeTitle')}</span>
            <input
              maxLength={100}
              value={draft.title}
              onChange={(e) => set('title', e.target.value)}
            />
            <span className="field__help">{t('partyComposer.titleHelp')}</span>
          </label>

          <label className="field">
            <span className="field__label">{t('partyGame.challengeBody')}</span>
            <textarea
              maxLength={500}
              rows={4}
              value={draft.body}
              onChange={(e) => set('body', e.target.value)}
            />
            <span className="field__help">{t('partyComposer.bodyHelp')}</span>
          </label>

          <div className="field">
            <span className="field__label">{t('partyGame.photo')}</span>
            {media.length === 0 ? (
              <p className="field__help">{t('partyComposer.photoEmpty')}</p>
            ) : (
              // A grid of the album's own photographs. Choosing a picture from a
              // list of filenames was the previous answer, and it asked a host to
              // remember what IMG_4821.jpg looks like.
              <ul className="party-photo-picker" data-testid="party-photo-picker">
                <li>
                  <button
                    type="button"
                    className={`party-photo-none${draft.mediaFileItemId === null ? ' is-selected' : ''}`}
                    aria-pressed={draft.mediaFileItemId === null}
                    onClick={() => set('mediaFileItemId', null)}
                  >
                    {t('partyGame.noPhoto')}
                  </button>
                </li>
                {media.map((item) => (
                  <li key={item.fileItemId}>
                    <button
                      type="button"
                      className={`party-photo-choice${draft.mediaFileItemId === item.fileItemId ? ' is-selected' : ''}`}
                      aria-pressed={draft.mediaFileItemId === item.fileItemId}
                      aria-label={item.name}
                      onClick={() => set('mediaFileItemId', item.fileItemId)}
                    >
                      <img src={item.thumbnailUrl!} alt="" loading="lazy" />
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </div>

          {!activityValid && <p className="field__help">{t('partyComposer.required')}</p>}
        </div>
      )}

      {step === 'rules' && (
        <div className="form-grid">
          <div className="field">
            <span className="field__label" id="party-composer-duration">
              {t('partyComposer.durationLabel')}
            </span>
            <div className="party-composer-presets" role="group" aria-labelledby="party-composer-duration">
              <button
                type="button"
                aria-pressed={draft.durationSeconds === null}
                className={draft.durationSeconds === null ? 'is-selected' : undefined}
                onClick={() => set('durationSeconds', null)}
              >
                {t('partyComposer.noLimit')}
              </button>
              {DURATION_PRESETS.map((seconds) => (
                <button
                  key={seconds}
                  type="button"
                  aria-pressed={draft.durationSeconds === seconds}
                  className={draft.durationSeconds === seconds ? 'is-selected' : undefined}
                  onClick={() => set('durationSeconds', seconds)}
                >
                  {seconds < 60
                    ? t('partyComposer.presetSeconds', { count: seconds })
                    : t('partyComposer.presetMinutes', { count: seconds / 60 })}
                </button>
              ))}
            </div>
            <span className="field__help">{t('partyComposer.durationHelp')}</span>
          </div>

          <div className="field">
            <span className="field__label" id="party-composer-voting">
              {t('partyComposer.votingLabel')}
            </span>
            <div className="party-composer-choices" role="radiogroup" aria-labelledby="party-composer-voting">
              {(['binary', 'none'] as PartyChallengeVotingMode[]).map((mode) => (
                <button
                  key={mode}
                  type="button"
                  role="radio"
                  aria-checked={draft.votingMode === mode}
                  className={draft.votingMode === mode ? 'is-selected' : undefined}
                  onClick={() => set('votingMode', mode)}
                >
                  <strong>{t(mode === 'binary' ? 'partyComposer.votingBinary' : 'partyComposer.votingNone')}</strong>
                  <span>{t(mode === 'binary' ? 'partyComposer.votingBinaryHelp' : 'partyComposer.votingNoneHelp')}</span>
                </button>
              ))}
            </div>
          </div>

          {draft.votingMode === 'binary' && (
            <label className="field">
              <span className="field__label">{t('partyComposer.questionLabel')}</span>
              <input
                maxLength={120}
                placeholder={t('partyComposer.questionPlaceholder')}
                value={draft.voteQuestion}
                onChange={(e) => set('voteQuestion', e.target.value)}
              />
              <span className="field__help">{t('partyComposer.questionHelp')}</span>
            </label>
          )}
        </div>
      )}

      {step === 'preview' && (
        <div className="form-grid">
          {/* THE renderer, at TV proportions. Deliberately its own region above
              the two remaining decisions: looking at the activity and editing it
              are different things. */}
          <PartyChallengeCard
            mode="preview"
            testId="party-composer-preview"
            titlePlaceholder={t('partyGame.challengeTitle')}
            context={{ round: position, total }}
            voting={draft.votingMode === 'binary' ? 'closed' : null}
            challenge={{
              kind: draft.kind,
              title: draft.title,
              body: draft.body,
              mediaUrl: selectedMedia?.thumbnailUrl ?? null,
              durationSeconds: draft.durationSeconds,
            }}
          />

          <label className="field party-composer-toggle">
            <input
              type="checkbox"
              checked={draft.isEnabled}
              onChange={(e) => set('isEnabled', e.target.checked)}
            />
            <span>
              <span className="field__label">{t('partyComposer.enabledLabel')}</span>
              <span className="field__help">{t('partyComposer.enabledHelp')}</span>
            </span>
          </label>

          <p className="field__help">
            {t(challenge ? 'partyComposer.positionExisting' : 'partyComposer.positionNew',
              { round: position, total })}
          </p>

          {failed && <p className="inline-error" role="alert">{t('partyGame.error')}</p>}
        </div>
      )}
    </Modal>
  );
}
