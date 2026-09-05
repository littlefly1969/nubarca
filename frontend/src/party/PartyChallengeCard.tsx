import type { PartyChallengeKind } from '@nubarca/api-client';
import { useI18n, type MessageKey } from '../i18n';
import './PartyChallengeCard.css';

// THE renderer for a party activity. There is exactly one, and everything that
// shows an activity to a person goes through it: the composer's preview, the
// television, the owner's control room.
//
// The three modes are three presentations of ONE markup, not three components.
// The DOM below is identical in every mode; what changes is a `data-mode`
// attribute and the CSS behind it. That is what makes "the preview looks like
// the TV" a structural fact rather than a promise somebody has to keep in two
// files — and it is why `preview` and `tv` share not just the markup but the
// same sizing rule, expressed in container units, so the preview is a scale
// model of the screen rather than an impression of one.
//
//   preview — the composer's 16:9 TV preview, sized from its container.
//   tv      — the same composition filling a television.
//   compact — a row: an activity in a list, or "coming up next".
export type PartyChallengeCardMode = 'preview' | 'tv' | 'compact';

/**
 * The voting state, when the surface has one to show. Presentational only: the
 * card never asks whether voting is open, it is told.
 */
export type PartyChallengeVotingState = 'open' | 'closed';

/**
 * Where this activity sits in the evening. Numbers rather than a formatted
 * string, so the copy stays inside the component with every other string.
 */
export interface PartyChallengeCardContext {
  round: number;
  total: number;
}

/**
 * What the card needs to draw. Deliberately a plain shape rather than one of the
 * API DTOs: the owner deck, the guest snapshot and the TV snapshot each carry a
 * slightly different envelope around the same four fields, and the renderer must
 * not have an opinion about which one it was handed.
 */
export interface PartyChallengeCardChallenge {
  kind: PartyChallengeKind;
  title: string;
  body: string;
  mediaUrl?: string | null;
  /** The activity's own time limit, when it has one. */
  durationSeconds?: number | null;
}

export interface PartyChallengeCardProps {
  challenge: PartyChallengeCardChallenge;
  mode: PartyChallengeCardMode;
  context?: PartyChallengeCardContext | null;
  voting?: PartyChallengeVotingState | null;
  /** Shown in place of an empty title while the composer is still being filled in. */
  titlePlaceholder?: string;
  className?: string;
  testId?: string;
}

const KIND_LABEL: Record<PartyChallengeKind, MessageKey> = {
  dare: 'partyChallenges.kind.dare',
  penalty: 'partyChallenges.kind.penalty',
  guess: 'partyChallenges.kind.guess',
  custom: 'partyChallenges.kind.custom',
};

/**
 * A duration in the units a host thinks in.
 *
 * Pure and exported so it can be tested without a DOM, and so a timer elsewhere
 * can render the same string as the card does. Anything not a positive finite
 * number is "no duration" — a card must never print `NaN s` because a server
 * sent null through a field that was typed as a number.
 */
export function splitActivityDuration(
  seconds: number | null | undefined,
): { minutes: number; seconds: number } | null {
  if (typeof seconds !== 'number' || !Number.isFinite(seconds) || seconds <= 0) return null;
  const whole = Math.round(seconds);
  return { minutes: Math.floor(whole / 60), seconds: whole % 60 };
}

function Duration({ seconds }: { seconds: number | null | undefined }) {
  const { tn } = useI18n();
  const split = splitActivityDuration(seconds);
  if (split === null) return null;
  const text = split.minutes === 0
    ? tn(split.seconds, 'partyActivity.durationSeconds')
    : split.seconds === 0
      ? tn(split.minutes, 'partyActivity.durationMinutes')
      : `${tn(split.minutes, 'partyActivity.durationMinutes')} ${tn(split.seconds, 'partyActivity.durationSeconds')}`;
  return <span className="party-activity-chip party-activity-duration">{text}</span>;
}

export function PartyChallengeCard({
  challenge, mode, context, voting, titlePlaceholder, className, testId,
}: PartyChallengeCardProps) {
  const { t } = useI18n();
  const title = challenge.title.trim() || titlePlaceholder || '';
  const hasMedia = Boolean(challenge.mediaUrl);
  const meta = splitActivityDuration(challenge.durationSeconds) !== null || Boolean(voting);

  return (
    <div
      className={`party-activity${className ? ` ${className}` : ''}`}
      data-mode={mode}
      data-kind={challenge.kind}
      data-media={hasMedia ? 'true' : 'false'}
      data-testid={testId ?? 'party-activity-card'}
    >
      <article className="party-activity-card">
        {hasMedia && (
          <div className="party-activity-media">
            {/* Decorative: the title is what names this activity, and a party
                photograph has no alternative text anybody could write. */}
            <img src={challenge.mediaUrl!} alt="" loading="lazy" />
          </div>
        )}
        <div className="party-activity-copy">
          <p className="party-activity-eyebrow">
            <span className="party-activity-kind">{t(KIND_LABEL[challenge.kind])}</span>
            {context && (
              <span className="party-activity-context">
                {t('partyActivity.round', { round: context.round, total: context.total })}
              </span>
            )}
          </p>
          <h2 className="party-activity-title">{title}</h2>
          {challenge.body.trim() && <p className="party-activity-body">{challenge.body}</p>}
          {meta && (
            <p className="party-activity-meta">
              <Duration seconds={challenge.durationSeconds} />
              {voting && (
                <span
                  className="party-activity-chip party-activity-voting"
                  data-state={voting}
                >
                  {t(voting === 'open' ? 'partyActivity.votingOpen' : 'partyActivity.votingClosed')}
                </span>
              )}
            </p>
          )}
        </div>
      </article>
    </div>
  );
}
