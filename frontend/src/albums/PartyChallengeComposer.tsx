import { useMemo, useState } from 'react';
import {
  PARTY_CHALLENGE_OPTION_LIMITS,
  type AlbumItemSummary,
  type PartyChallenge,
  type PartyChallengeOptionWrite,
  type PartyChallengeVotingMode,
} from '@nubarca/api-client';
import { usePartyApi } from '../party/workspace/partyApi';
import { Modal } from '../components/Overlay';
import { PartyChallengeCard } from '../party/PartyChallengeCard';
import {
  PartyImageUploadButton, partyImageUploadErrorKey,
  type PartyImageChoice, type PartyImageUploadError,
} from '../party/PartyImageField';
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

// Named per mode rather than spelled as a ternary, because there are three of
// them now and a nested ternary is where the fourth one goes wrong.
const VOTING_LABELS: Record<PartyChallengeVotingMode, MessageKey> = {
  binary: 'partyComposer.votingBinary',
  choice: 'partyComposer.votingChoice',
  none: 'partyComposer.votingNone',
};
const VOTING_HELP: Record<PartyChallengeVotingMode, MessageKey> = {
  binary: 'partyComposer.votingBinaryHelp',
  choice: 'partyComposer.votingChoiceHelp',
  none: 'partyComposer.votingNoneHelp',
};

const STEPS = ['activity', 'rules', 'preview'] as const;
type Step = (typeof STEPS)[number];

const STEP_LABEL: Record<Step, MessageKey> = {
  activity: 'partyComposer.stepActivity',
  rules: 'partyComposer.stepRules',
  preview: 'partyComposer.stepPreview',
};

// The four durations a host actually reaches for. The number field stays, for
// the fifth.
const DURATION_PRESETS = [30, 60, 120, 300];

// THE CATEGORY IS GONE FROM THE DRAFT, not merely hidden.
//
// `PartyChallenge.kind` still exists — in the domain, the column and the wire —
// because the adaptive game it was designed for will read it. What it never
// was, is a decision the host had to make: a room is shown an activity, not a
// taxonomy, and asking "dare, penalty, guess or custom?" was a required step
// that changed nothing anybody sees. Omitting the field from the write is what
// makes that true on the server too: a new activity becomes `custom`, and an
// existing one keeps whatever it was written with.
interface Draft {
  title: string;
  body: string;
  mediaFileItemId: string | null;
  durationSeconds: number | null;
  votingMode: PartyChallengeVotingMode;
  voteQuestion: string;
  /**
   * The answers, kept for the WHOLE editing session rather than only while
   * `choice` is selected. A host who tries the other modes and comes back has
   * not thrown their answers away — only what is SAVED depends on the mode.
   */
  options: PartyChallengeOptionWrite[];
  isEnabled: boolean;
}

/** Two empty answers: the fewest a choice is made between. */
function blankBallot(): PartyChallengeOptionWrite[] {
  return [{ label: '', outcome: null }, { label: '', outcome: null }];
}

function draftFrom(challenge: PartyChallenge | null): Draft {
  return {
    title: challenge?.title ?? '',
    body: challenge?.body ?? '',
    mediaFileItemId: challenge?.mediaFileItemId ?? null,
    durationSeconds: challenge?.durationSeconds ?? null,
    votingMode: challenge?.votingMode ?? 'binary',
    voteQuestion: challenge?.voteQuestion ?? '',
    options: challenge?.options?.length
      ? challenge.options.map((o) => ({ label: o.label, outcome: o.outcome }))
      : blankBallot(),
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
  const api = usePartyApi();
  const initial = useMemo(() => draftFrom(challenge), [challenge]);
  const [draft, setDraft] = useState<Draft>(initial);
  const [step, setStep] = useState<Step>('activity');
  const [saving, setSaving] = useState(false);
  const [failed, setFailed] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);

  const set = <K extends keyof Draft>(key: K, value: Draft[K]) =>
    setDraft((current) => ({ ...current, [key]: value }));

  const setOption = (index: number, patch: Partial<PartyChallengeOptionWrite>) =>
    setDraft((current) => ({
      ...current,
      options: current.options.map((o, i) => (i === index ? { ...o, ...patch } : o)),
    }));

  const addOption = () =>
    setDraft((current) => (current.options.length >= PARTY_CHALLENGE_OPTION_LIMITS.max
      ? current
      : { ...current, options: [...current.options, { label: '', outcome: null }] }));

  const removeOption = (index: number) =>
    setDraft((current) => (current.options.length <= PARTY_CHALLENGE_OPTION_LIMITS.min
      ? current
      : { ...current, options: current.options.filter((_, i) => i !== index) }));

  // The ballot is an ARRAY, so identity is the wrong question for it: every
  // edit replaces it, and a draft that was never touched still holds the very
  // array `initial` does. Comparing its contents is what keeps "unsaved
  // changes" honest for a choice round.
  const dirty = useMemo(
    () => (Object.keys(initial) as (keyof Draft)[]).some((key) => (key === 'options'
      ? JSON.stringify(draft.options) !== JSON.stringify(initial.options)
      : draft[key] !== initial[key])),
    [draft, initial],
  );
  const activityValid = draft.title.trim().length > 0 && draft.body.trim().length > 0;
  // A picture need not be one of the album's. The host may upload a graphic
  // here, or the activity may already carry one chosen before: it is the
  // activity's picture all the same, and the grid and the preview show it —
  // without it ever joining the album or the slideshow.
  const [extra, setExtra] = useState<PartyImageChoice | null>(() =>
    challenge?.mediaFileItemId && challenge.mediaUrl
      && !media.some((x) => x.fileItemId === challenge.mediaFileItemId)
      ? { fileItemId: challenge.mediaFileItemId, previewUrl: challenge.mediaUrl }
      : null);
  const [uploadError, setUploadError] = useState<PartyImageUploadError | null>(null);
  const selectedPreview =
    media.find((x) => x.fileItemId === draft.mediaFileItemId)?.thumbnailUrl
    ?? (extra && extra.fileItemId === draft.mediaFileItemId ? extra.previewUrl : null);
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
      mediaFileItemId: draft.mediaFileItemId,
      isEnabled: draft.isEnabled,
      durationSeconds: draft.durationSeconds,
      votingMode: draft.votingMode,
      voteQuestion: draft.voteQuestion.trim() || null,
      // Only the mode that is ANSWERED with a ballot sends one. An empty array
      // for the other modes is deliberate and not the same as omitting the
      // field: it says "this activity has no answers", which is what clears a
      // ballot a host has moved away from.
      options: draft.votingMode === 'choice'
        ? draft.options
          .filter((o) => o.label.trim() !== '')
          .map((o) => ({ label: o.label.trim(), outcome: o.outcome?.trim() || null }))
        : [],
    };
    try {
      if (challenge) await api.updatePartyChallenge(albumId, challenge.id, value);
      else await api.createPartyChallenge(albumId, value);
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
            {media.length === 0 && !extra ? (
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
                {extra && (
                  <li key={extra.fileItemId}>
                    <button
                      type="button"
                      className={`party-photo-choice${draft.mediaFileItemId === extra.fileItemId ? ' is-selected' : ''}`}
                      aria-pressed={draft.mediaFileItemId === extra.fileItemId}
                      aria-label={t('partyContent.image')}
                      data-testid="party-photo-extra"
                      onClick={() => set('mediaFileItemId', extra.fileItemId)}
                    >
                      <img src={extra.previewUrl} alt="" loading="lazy" />
                    </button>
                  </li>
                )}
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
            {/* A picture that is not in the album — a graphic, a poster — goes
                to the host's library through the ordinary upload and is filed
                nowhere, so it can never turn up in the slideshow. */}
            <div className="party-photo-upload">
              <PartyImageUploadButton
                testId="party-photo-upload"
                disabled={saving}
                onUploaded={(choice) => {
                  setUploadError(null);
                  setExtra(choice);
                  set('mediaFileItemId', choice.fileItemId);
                }}
                onError={setUploadError}
              />
              <span className="field__help">{t('partyContent.imageHelp')}</span>
            </div>
            {uploadError && (
              <p className="inline-error" role="alert">{t(partyImageUploadErrorKey(uploadError))}</p>
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
              {(['binary', 'choice', 'none'] as PartyChallengeVotingMode[]).map((mode) => (
                <button
                  key={mode}
                  type="button"
                  role="radio"
                  data-testid={`party-composer-voting-${mode}`}
                  aria-checked={draft.votingMode === mode}
                  className={draft.votingMode === mode ? 'is-selected' : undefined}
                  onClick={() => set('votingMode', mode)}
                >
                  <strong>{t(VOTING_LABELS[mode])}</strong>
                  <span>{t(VOTING_HELP[mode])}</span>
                </button>
              ))}
            </div>
          </div>

          {draft.votingMode !== 'none' && (
            <label className="field">
              <span className="field__label">{t('partyComposer.questionLabel')}</span>
              <input
                maxLength={120}
                placeholder={t(draft.votingMode === 'choice'
                  ? 'partyComposer.questionChoicePlaceholder'
                  : 'partyComposer.questionPlaceholder')}
                value={draft.voteQuestion}
                onChange={(e) => set('voteQuestion', e.target.value)}
              />
              <span className="field__help">{t('partyComposer.questionHelp')}</span>
            </label>
          )}

          {/* THE BALLOT. Each answer is what the room taps, and beside it what
              happens if the room picks it — the two belong together on one row
              because a host writes them in one thought: "if they say this, then
              that". An answer with no consequence is allowed; an answer with no
              words is not, and is simply dropped on save. */}
          {draft.votingMode === 'choice' && (
            <div className="field" data-testid="party-composer-ballot">
              <span className="field__label">{t('partyComposer.optionsLabel')}</span>
              <span className="field__help">{t('partyComposer.optionsHelp')}</span>
              <ol className="party-composer-ballot">
                {draft.options.map((option, index) => (
                  // The index IS the identity here: these rows have no id until
                  // the server mints one, and two blank answers are genuinely
                  // the same value. Reordering is not offered, so nothing can
                  // make the index wrong under a row.
                  // eslint-disable-next-line react/no-array-index-key
                  <li key={index}>
                    <input
                      aria-label={t('partyComposer.optionLabel', { n: index + 1 })}
                      data-testid={`party-composer-option-${index}`}
                      maxLength={PARTY_CHALLENGE_OPTION_LIMITS.labelLength}
                      placeholder={t('partyComposer.optionPlaceholder')}
                      value={option.label}
                      onChange={(e) => setOption(index, { label: e.target.value })}
                    />
                    <input
                      aria-label={t('partyComposer.optionOutcome', { n: index + 1 })}
                      data-testid={`party-composer-outcome-${index}`}
                      maxLength={PARTY_CHALLENGE_OPTION_LIMITS.outcomeLength}
                      placeholder={t('partyComposer.outcomePlaceholder')}
                      value={option.outcome ?? ''}
                      onChange={(e) => setOption(index, { outcome: e.target.value })}
                    />
                    {draft.options.length > PARTY_CHALLENGE_OPTION_LIMITS.min && (
                      <button
                        type="button"
                        className="party-composer-ballot-remove"
                        aria-label={t('partyComposer.optionRemove', { n: index + 1 })}
                        data-testid={`party-composer-option-remove-${index}`}
                        onClick={() => removeOption(index)}
                      >×</button>
                    )}
                  </li>
                ))}
              </ol>
              {/* Absent at six rather than disabled: there is nothing to
                  explain, and a control that never works is noise. */}
              {draft.options.length < PARTY_CHALLENGE_OPTION_LIMITS.max && (
                <button
                  type="button"
                  className="party-composer-ballot-add"
                  data-testid="party-composer-option-add"
                  onClick={addOption}
                >
                  {t('partyComposer.optionAdd')}
                </button>
              )}
            </div>
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
              title: draft.title,
              body: draft.body,
              mediaUrl: selectedPreview ?? null,
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
