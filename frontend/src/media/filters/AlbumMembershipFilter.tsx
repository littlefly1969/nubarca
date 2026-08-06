import { useId } from 'react';
import type { AlbumMembership } from '@nubarca/api-client';
import { useI18n } from '../../i18n';

// "Album" filter shared verbatim by the photo and video galleries:
//   [ All ] [ In an album ] [ Not in an album ]
//
// One backend concept (AlbumMembershipFilter) → one component → one wording, so
// the two galleries cannot drift apart. It is a gallery-organisation control and
// is deliberately NOT used by the ordinary file browser.
//
// Accessibility: a radiogroup rather than a row of toggle buttons, because the
// three options are mutually exclusive. Arrow keys move between options natively
// and the applied value is announced as the checked radio.

const OPTIONS: ReadonlyArray<{ value: AlbumMembership; labelKey: 'mediaFilters.albumAny' | 'mediaFilters.albumAssigned' | 'mediaFilters.albumUnassigned' }> = [
  { value: 'any', labelKey: 'mediaFilters.albumAny' },
  { value: 'assigned', labelKey: 'mediaFilters.albumAssigned' },
  { value: 'unassigned', labelKey: 'mediaFilters.albumUnassigned' },
];

interface Props {
  value: AlbumMembership;
  onChange(next: AlbumMembership): void;
  disabled?: boolean;
}

export function AlbumMembershipFilter({ value, onChange, disabled = false }: Props) {
  const { t } = useI18n();
  const groupName = useId();

  return (
    <fieldset className="ws-field media-album-filter" data-testid="album-membership-filter">
      <legend>{t('mediaFilters.album')}</legend>
      <div className="media-album-filter-options" role="radiogroup" aria-label={t('mediaFilters.album')}>
        {OPTIONS.map((option) => (
          <label key={option.value} className="media-album-filter-option">
            <input
              type="radio"
              name={groupName}
              value={option.value}
              checked={value === option.value}
              disabled={disabled}
              data-testid={`album-membership-${option.value}`}
              onChange={() => onChange(option.value)}
            />
            <span>{t(option.labelKey)}</span>
          </label>
        ))}
      </div>
    </fieldset>
  );
}
