import { useId, useState } from 'react';
import { useI18n } from '../../i18n';

// Chip-based tag editor shared by the photo and video metadata editors.
//
// It mirrors the BACKEND normalisation (MetadataTags.NormalizeToJson) rather
// than inventing its own: trim, drop blanks, case-insensitive dedupe keeping
// the FIRST form entered, at most MAX_TAGS tags of at most MAX_TAG_LENGTH
// characters. Doing the same thing client-side means the user sees the outcome
// before saving; the server stays the authority and still rejects anything that
// slips through. No autocomplete, synonyms or taxonomy — out of scope.
export const MAX_TAGS = 32;
export const MAX_TAG_LENGTH = 64;

interface Props {
  tags: string[];
  onChange(next: string[]): void;
  disabled?: boolean;
}

export function MediaTagEditor({ tags, onChange, disabled = false }: Props) {
  const { t } = useI18n();
  const inputId = useId();
  const [draft, setDraft] = useState('');
  const [error, setError] = useState<string | null>(null);

  const atCapacity = tags.length >= MAX_TAGS;

  function commitDraft() {
    const candidate = draft.trim();
    if (candidate.length === 0) return;

    if (candidate.length > MAX_TAG_LENGTH) {
      setError(t('mediaMeta.tagTooLong', { max: MAX_TAG_LENGTH }));
      return;
    }
    // Case-insensitive duplicate check, first form wins (backend rule).
    if (tags.some((existing) => existing.toLowerCase() === candidate.toLowerCase())) {
      setError(t('mediaMeta.tagDuplicate', { tag: candidate }));
      return;
    }
    if (atCapacity) {
      setError(t('mediaMeta.tagLimitReached', { max: MAX_TAGS }));
      return;
    }

    onChange([...tags, candidate]);
    setDraft('');
    setError(null);
  }

  function removeAt(index: number) {
    onChange(tags.filter((_, i) => i !== index));
    setError(null);
  }

  return (
    <div className="media-tag-editor" data-testid="media-tag-editor">
      <label htmlFor={inputId}>
        {t('mediaMeta.tags')}{' '}
        <span className="muted" data-testid="media-tag-count">
          {t('mediaMeta.tagCount', { count: tags.length, max: MAX_TAGS })}
        </span>
      </label>

      {tags.length > 0 && (
        <ul className="media-tag-chips" data-testid="media-tag-chips">
          {tags.map((tag, index) => (
            <li key={tag.toLowerCase()} className="chip media-tag-chip">
              <span>{tag}</span>
              <button
                type="button"
                className="media-tag-chip-remove"
                aria-label={t('mediaMeta.removeTag', { tag })}
                disabled={disabled}
                onClick={() => removeAt(index)}
              >
                <span aria-hidden="true">✕</span>
              </button>
            </li>
          ))}
        </ul>
      )}

      <input
        id={inputId}
        type="text"
        value={draft}
        disabled={disabled || atCapacity}
        maxLength={MAX_TAG_LENGTH}
        placeholder={atCapacity ? t('mediaMeta.tagLimitReached', { max: MAX_TAGS }) : t('mediaMeta.tagInputHint')}
        aria-describedby={error !== null ? `${inputId}-error` : undefined}
        onChange={(e) => { setDraft(e.target.value); setError(null); }}
        onKeyDown={(e) => {
          if (e.key === 'Enter') {
            // The editor lives inside a form: Enter must add a tag, never submit.
            e.preventDefault();
            commitDraft();
          } else if (e.key === 'Backspace' && draft.length === 0 && tags.length > 0) {
            removeAt(tags.length - 1);
          }
        }}
        // Committing on blur too, so a typed-but-not-confirmed tag is not
        // silently dropped when the user goes straight for Save.
        onBlur={commitDraft}
      />

      {error !== null && (
        <p className="metadata-edit-error" role="alert" id={`${inputId}-error`}>{error}</p>
      )}
    </div>
  );
}
