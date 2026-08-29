import { useState } from 'react';
import {
  ApiError,
  PARTY_MESSAGE_LIMITS,
  isPartyMessageSubmittable,
  partyDisplayNameRemaining,
  partyMessageRemaining,
  submitPartyMessage,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';

// PUBLIC, unauthenticated. The written half of a guest's contribution: a short
// greeting that reaches the party TV as a ribbon, and occasionally as a
// full-screen Hero card if the host promotes it.
//
// The counter is computed with the SAME rules the server validates by
// (PARTY_MESSAGE_LIMITS / partyMessageRemaining, mirroring PartyMessageText in
// the backend), so a guest never watches it say "3 left" and the submit fail.
// Whitespace and zero-width padding collapse before counting, which is why the
// number can stop moving while the guest keeps typing spaces.
//
// The guest cannot ask for a Hero, and does not see any moderation state beyond
// whether their own message is live or waiting.

interface Props {
  uploadToken: string;
  // Called after a successful send, so the host page can refresh anything it
  // shows about this guest's contributions. Optional.
  onSent?: () => void;
}

type Phase =
  | { kind: 'writing' }
  | { kind: 'sending' }
  | { kind: 'sent'; pending: boolean };

export function PartyGuestMessageForm({ uploadToken, onSent }: Props) {
  const { t } = useI18n();
  const [displayName, setDisplayName] = useState('');
  const [text, setText] = useState('');
  const [phase, setPhase] = useState<Phase>({ kind: 'writing' });
  const [error, setError] = useState<string | null>(null);

  const remaining = partyMessageRemaining(text);
  const nameRemaining = partyDisplayNameRemaining(displayName);
  const canSend = isPartyMessageSubmittable(text, displayName);

  const send = async () => {
    if (!canSend) return;
    setPhase({ kind: 'sending' });
    setError(null);
    try {
      const result = await submitPartyMessage(uploadToken, {
        // Sent raw: the SERVER normalises and is the authority on what is
        // stored. Trimming here as well would only create a second opinion.
        displayName: displayName.length > 0 ? displayName : null,
        text,
      });
      setPhase({ kind: 'sent', pending: result.status === 'pending' });
      setDisplayName('');
      setText('');
      onSent?.();
    } catch (err: unknown) {
      setPhase({ kind: 'writing' });
      if (err instanceof ApiError && err.status === 400) {
        setError(t('partyMessage.rejected'));
      } else if (err instanceof ApiError && err.status === 429) {
        setError(t('partyMessage.tooMany'));
      } else {
        setError(t('partyMessage.failed'));
      }
    }
  };

  if (phase.kind === 'sent') {
    return (
      <div className="party-message-form" data-testid="party-message-sent">
        <p role="status" className="party-message-sent">
          {phase.pending ? t('partyMessage.sentPending') : t('partyMessage.sentVisible')}
        </p>
        <button type="button" onClick={() => setPhase({ kind: 'writing' })}>
          {t('partyMessage.sendAnother')}
        </button>
      </div>
    );
  }

  const busy = phase.kind === 'sending';

  return (
    <form
      className="party-message-form"
      onSubmit={(e) => { e.preventDefault(); void send(); }}
    >
      <p className="muted">{t('partyMessage.intro')}</p>

      <label className="party-message-field">
        <span>{t('partyMessage.nameLabel')}</span>
        <input
          type="text"
          value={displayName}
          onChange={(e) => setDisplayName(e.target.value)}
          placeholder={t('partyMessage.namePlaceholder')}
          disabled={busy}
          // No maxLength: the browser counts UTF-16 units and would cut an
          // emoji in half at the boundary. The counter and the server agree on
          // code points instead.
          autoComplete="off"
        />
      </label>
      {nameRemaining < 0 && (
        <p className="inline-error" role="alert">
          {t('partyMessage.nameOverLimit', { max: String(PARTY_MESSAGE_LIMITS.displayName) })}
        </p>
      )}

      <label className="party-message-field">
        <span>{t('partyMessage.textLabel')}</span>
        <textarea
          value={text}
          onChange={(e) => setText(e.target.value)}
          placeholder={t('partyMessage.textPlaceholder')}
          rows={3}
          disabled={busy}
        />
      </label>

      <p
        className={remaining < 0 ? 'party-message-counter over' : 'party-message-counter'}
        data-testid="party-message-counter"
        aria-live="polite"
      >
        {remaining >= 0
          ? t('partyMessage.remaining', { count: String(remaining) })
          : t('partyMessage.overLimit', { max: String(PARTY_MESSAGE_LIMITS.text) })}
      </p>

      <button type="submit" disabled={busy || !canSend}>
        {busy ? t('partyMessage.sending') : t('partyMessage.send')}
      </button>

      {error && <p className="inline-error" role="alert">{error}</p>}
    </form>
  );
}
