import { useMemo } from 'react';
import { useI18n } from '../i18n';

interface TimeZoneSelectProps {
  id: string;
  label: string;
  value: string;
  onChange(value: string): void;
  disabled?: boolean;
}

// Named IANA time zones, taken from the RUNTIME rather than a hand-kept list.
// `Intl.supportedValuesOf('timeZone')` is the browser's own tz database, so the
// options can never drift from what the platform recognises; the server
// validates the chosen id against .NET's TimeZoneInfo before storing it, so
// both ends agree on what a valid zone is.
//
// An older engine without supportedValuesOf falls back to a free-text input
// rather than an empty select — a user must never be stuck unable to set a
// zone because their browser lacks an enumeration API.
function supportedTimeZones(): string[] | null {
  const intl = Intl as typeof Intl & { supportedValuesOf?: (key: string) => string[] };
  if (typeof intl.supportedValuesOf !== 'function') return null;
  try {
    return intl.supportedValuesOf('timeZone');
  } catch {
    return null;
  }
}

export function TimeZoneSelect({ id, label, value, onChange, disabled }: TimeZoneSelectProps) {
  const { t } = useI18n();
  const zones = useMemo(supportedTimeZones, []);

  if (zones === null) {
    return (
      <label htmlFor={id}>
        {label}
        <input
          id={id}
          type="text"
          value={value}
          onChange={(e) => onChange(e.target.value)}
          disabled={disabled}
          placeholder="Europe/Rome"
        />
      </label>
    );
  }

  // A zone the runtime does not enumerate (an alias, or a value written by
  // another client) still has to be selectable, or opening this form would
  // silently reset it.
  const options = value && !zones.includes(value) ? [value, ...zones] : zones;

  return (
    <label htmlFor={id}>
      {label}
      <select
        id={id}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        disabled={disabled}
      >
        <option value="">{t('account.timeZoneAuto')}</option>
        {options.map((zone) => (
          <option key={zone} value={zone}>{zone}</option>
        ))}
      </select>
    </label>
  );
}
