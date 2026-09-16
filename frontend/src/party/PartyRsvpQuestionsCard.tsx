import { useState } from 'react';
import {
  PARTY_INVITATION_LIMITS,
  PARTY_RSVP_QUESTION_KINDS,
  codePoints,
  createPartyRsvpQuestion,
  normalizeQuestionOptions,
  normalizeText,
  reorderPartyRsvpQuestions,
  updatePartyRsvpQuestion,
  type PartyGuestListMinimal,
  type PartyRsvpQuestion,
  type PartyRsvpQuestionKind,
} from '@nubarca/api-client';
import { useI18n, type MessageKey } from '../i18n';
import './PartyRsvpQuestions.css';

// The questions every invitation asks — three closed kinds, and nothing that
// grows into a form builder: no sections, no conditions, no field registry.
// Order is up and down, which is all a list of a few questions needs.
//
// A question somebody has answered is FROZEN in what it asks. The form says so
// and offers the one thing still possible — retiring it — rather than letting
// the host type into fields the server will refuse.

interface QuestionDraft {
  prompt: string;
  kind: PartyRsvpQuestionKind;
  required: boolean;
  options: string;
}

const EMPTY: QuestionDraft = { prompt: '', kind: 'short_text', required: false, options: '' };

const ERROR_KEYS: Record<string, MessageKey> = {
  question_locked: 'party.questions.locked',
  too_many_questions: 'party.questions.error.too_many_questions',
};

export function PartyRsvpQuestionsCard({
  partyId, questions, onChanged, onRefused,
}: {
  partyId: string;
  questions: readonly PartyRsvpQuestion[];
  /** The questions as they now are: the console pages the guest list and never reads it whole. */
  onChanged(next: PartyRsvpQuestion[]): void;
  onRefused(err: unknown, fallback: MessageKey): void;
}) {
  const { t } = useI18n();
  const [editing, setEditing] = useState<PartyRsvpQuestion | 'new' | null>(null);
  const [busy, setBusy] = useState(false);

  async function act(action: () => Promise<PartyGuestListMinimal>) {
    setBusy(true);
    try {
      onChanged((await action()).questions);
      setEditing(null);
    } catch (err) {
      const code = (err as { body?: { error?: string } }).body?.error;
      onRefused(err, (code && ERROR_KEYS[code]) || 'party.questions.error.generic');
    } finally {
      setBusy(false);
    }
  }

  const move = (index: number, by: -1 | 1) => {
    const ids = questions.map((q) => q.id);
    [ids[index], ids[index + by]] = [ids[index + by], ids[index]];
    void act(() => reorderPartyRsvpQuestions(partyId, ids));
  };

  const setActive = (q: PartyRsvpQuestion, isActive: boolean) => void act(() => updatePartyRsvpQuestion(partyId, q.id, {
    prompt: q.prompt, kind: q.kind, required: q.required, options: q.options, isActive, version: q.version,
  }));

  return (
    <section className="party-card" data-testid="party-questions" aria-labelledby="party-questions-heading">
      <h3 id="party-questions-heading">{t('party.questions.heading')}</h3>
      <p className="muted">{t('party.questions.help')}</p>

      {questions.length === 0 ? (
        <p className="muted">{t('party.questions.empty')}</p>
      ) : (
        <ol className="party-questions-list">
          {questions.map((q, index) => (
            <li key={q.id} className="party-questions-row" data-testid={`party-question-${q.id}`} data-active={q.isActive}>
              <div className="party-questions-head">
                <strong>{q.prompt}</strong>
                <span className="party-questions-chip">{t(`party.questions.kind.${q.kind}` as MessageKey)}</span>
              </div>
              <p className="muted">
                {q.required && <>{t('party.questions.required')} · </>}
                {q.isActive ? t('party.questions.active') : t('party.questions.inactive')}
                {' · '}{t('party.questions.answerCount', { count: q.answerCount })}
                {q.options.length > 0 && <> · {q.options.join(', ')}</>}
              </p>
              <p className="party-card-actions">
                <button
                  type="button" className="row-action" disabled={busy || index === 0}
                  aria-label={`${t('party.questions.moveUp')}: ${q.prompt}`} onClick={() => move(index, -1)}
                >↑</button>
                <button
                  type="button" className="row-action" disabled={busy || index === questions.length - 1}
                  aria-label={`${t('party.questions.moveDown')}: ${q.prompt}`} onClick={() => move(index, 1)}
                >↓</button>
                <button
                  type="button" className="row-action" disabled={busy}
                  data-testid={`party-question-edit-${q.id}`} onClick={() => setEditing(q)}
                >
                  {t('party.questions.edit')}
                </button>
                <button
                  type="button" className="row-action" disabled={busy}
                  data-testid={`party-question-toggle-${q.id}`} onClick={() => setActive(q, !q.isActive)}
                >
                  {q.isActive ? t('party.questions.deactivate') : t('party.questions.activate')}
                </button>
              </p>
              {editing !== 'new' && editing?.id === q.id && (
                <QuestionForm
                  initial={{ prompt: q.prompt, kind: q.kind, required: q.required, options: q.options.join('\n') }}
                  locked={q.locked} busy={busy}
                  onCancel={() => setEditing(null)}
                  onSave={(draft, options) => void act(() => updatePartyRsvpQuestion(partyId, q.id, {
                    prompt: draft.prompt.trim(), kind: draft.kind, required: draft.required,
                    options, isActive: q.isActive, version: q.version,
                  }))}
                />
              )}
            </li>
          ))}
        </ol>
      )}

      {editing === 'new' ? (
        <QuestionForm
          initial={EMPTY} locked={false} busy={busy}
          onCancel={() => setEditing(null)}
          onSave={(draft, options) => void act(() => createPartyRsvpQuestion(partyId, {
            prompt: draft.prompt.trim(), kind: draft.kind, required: draft.required, options,
          }))}
        />
      ) : (
        <p className="party-card-actions">
          <button
            type="button" className="row-action" data-testid="party-question-add" disabled={busy}
            onClick={() => setEditing('new')}
          >
            {t('party.questions.add')}
          </button>
        </p>
      )}
    </section>
  );
}

function QuestionForm({
  initial, locked, busy, onSave, onCancel,
}: {
  initial: QuestionDraft;
  locked: boolean;
  busy: boolean;
  onSave(draft: QuestionDraft, options: string[] | null): void;
  onCancel(): void;
}) {
  const { t } = useI18n();
  const [draft, setDraft] = useState<QuestionDraft>(initial);
  const prompt = normalizeText(draft.prompt);
  const promptOk = prompt !== null && codePoints(prompt) <= PARTY_INVITATION_LIMITS.questionPrompt;
  const options = normalizeQuestionOptions(
    draft.kind, draft.kind === 'single_choice' ? draft.options.split('\n') : null);
  const valid = promptOk && options !== null;

  return (
    <form
      className="party-questions-form" data-testid="party-question-form"
      onSubmit={(e) => { e.preventDefault(); if (valid) onSave(draft, draft.kind === 'single_choice' ? options : null); }}
    >
      {locked && <p className="muted" role="note" data-testid="party-question-locked">{t('party.questions.locked')}</p>}
      <label className="party-field">
        <span>{t('party.questions.prompt')}</span>
        <input
          value={draft.prompt} disabled={busy || locked} aria-label={t('party.questions.prompt')}
          onChange={(e) => setDraft((d) => ({ ...d, prompt: e.target.value }))}
        />
      </label>
      <label className="party-field">
        <span>{t('party.questions.kind')}</span>
        <select
          value={draft.kind} disabled={busy || locked} aria-label={t('party.questions.kind')}
          onChange={(e) => setDraft((d) => ({ ...d, kind: e.target.value as PartyRsvpQuestionKind }))}
        >
          {PARTY_RSVP_QUESTION_KINDS.map((kind) => (
            <option key={kind} value={kind}>{t(`party.questions.kind.${kind}` as MessageKey)}</option>
          ))}
        </select>
      </label>
      <label className="party-toggle">
        <input
          type="checkbox" checked={draft.required} disabled={busy || locked}
          onChange={(e) => setDraft((d) => ({ ...d, required: e.target.checked }))}
        />
        <span>{t('party.questions.required')}</span>
      </label>
      {draft.kind === 'single_choice' && (
        <label className="party-field">
          <span>{t('party.questions.options')}</span>
          <textarea
            rows={4} value={draft.options} disabled={busy || locked} aria-label={t('party.questions.options')}
            onChange={(e) => setDraft((d) => ({ ...d, options: e.target.value }))}
          />
        </label>
      )}
      {!promptOk && draft.prompt !== '' && <p className="muted">{t('party.questions.promptInvalid')}</p>}
      {draft.kind === 'single_choice' && options === null && (
        <p className="muted" data-testid="party-question-options-invalid">{t('party.questions.optionsInvalid')}</p>
      )}
      <p className="party-card-actions">
        {!locked && (
          <button type="submit" className="row-action-primary" data-testid="party-question-save" disabled={busy || !valid}>
            {t('party.questions.save')}
          </button>
        )}
        <button type="button" className="row-action" disabled={busy} onClick={onCancel}>
          {t('party.questions.cancel')}
        </button>
      </p>
    </form>
  );
}
