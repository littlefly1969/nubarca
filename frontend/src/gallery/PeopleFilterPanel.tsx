import { useEffect, useMemo, useState } from 'react';
import { ApiError, listPeople, type Person } from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';

// Owner-private People filter for the gallery. Include (contains) with an
// all/any mode, plus exclude (does not contain). Person ids are the safe
// owner-private identifiers already used by the authenticated People routes;
// no face ids, cluster ids, vectors, or scores are ever surfaced here.
export type PeopleMode = 'all' | 'any';

export interface PeopleFilterValue {
  include: string[];
  exclude: string[];
  mode: PeopleMode;
}

export interface PeopleFilterPanelProps {
  value: PeopleFilterValue;
  onChange(next: PeopleFilterValue): void;
}

export function PeopleFilterPanel({ value, onChange }: PeopleFilterPanelProps) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const [people, setPeople] = useState<Person[]>([]);
  const [loaded, setLoaded] = useState(false);

  useEffect(() => {
    const ctrl = new AbortController();
    listPeople(ctrl.signal)
      .then((list) => { setPeople(list); setLoaded(true); })
      .catch((err) => {
        if ((err as Error).name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        setLoaded(true);
      });
    return () => ctrl.abort();
  }, [invalidateAuth]);

  const nameById = useMemo(() => {
    const m = new Map<string, string>();
    for (const p of people) m.set(p.personId, p.name ?? t('peopleFilter.unnamed'));
    return m;
  }, [people, t]);

  // People not already used in either list are addable to a given list.
  function available(exclude: Set<string>): Person[] {
    return people.filter((p) => !exclude.has(p.personId));
  }

  const includeSet = new Set(value.include);
  const excludeSet = new Set(value.exclude);
  const usedSet = new Set([...value.include, ...value.exclude]);

  function addInclude(id: string) {
    if (!id || includeSet.has(id)) return;
    onChange({ ...value, include: [...value.include, id] });
  }
  function removeInclude(id: string) {
    onChange({ ...value, include: value.include.filter((x) => x !== id) });
  }
  function addExclude(id: string) {
    if (!id || excludeSet.has(id)) return;
    onChange({ ...value, exclude: [...value.exclude, id] });
  }
  function removeExclude(id: string) {
    onChange({ ...value, exclude: value.exclude.filter((x) => x !== id) });
  }
  function setMode(mode: PeopleMode) {
    onChange({ ...value, mode });
  }

  if (loaded && people.length === 0) {
    return (
      <div className="people-filter" aria-label={t('peopleFilter.title')}>
        <span className="gallery-filter-label">{t('peopleFilter.title')}</span>
        <p className="muted">{t('peopleFilter.noPeople')}</p>
      </div>
    );
  }

  const includeAvailable = available(usedSet);
  const excludeAvailable = available(usedSet);

  return (
    <div className="people-filter" aria-label={t('peopleFilter.title')} data-testid="people-filter">
      <span className="gallery-filter-label">{t('peopleFilter.title')}</span>

      <div className="people-filter-group">
        <div className="people-filter-row">
          <span className="people-filter-sublabel">{t('peopleFilter.contains')}</span>
          <label className="people-filter-mode">
            <span className="visually-hidden">{t('peopleFilter.modeLabel')}</span>
            <select
              className="gallery-select"
              value={value.mode}
              onChange={(e) => setMode(e.target.value as PeopleMode)}
              aria-label={t('peopleFilter.modeLabel')}
              data-testid="people-filter-mode"
            >
              <option value="all">{t('peopleFilter.all')}</option>
              <option value="any">{t('peopleFilter.any')}</option>
            </select>
          </label>
          <select
            className="gallery-select"
            value=""
            onChange={(e) => addInclude(e.target.value)}
            aria-label={t('peopleFilter.addContains')}
            data-testid="people-filter-add-include"
          >
            <option value="">{t('peopleFilter.choosePerson')}</option>
            {includeAvailable.map((p) => (
              <option key={p.personId} value={p.personId}>{p.name ?? t('peopleFilter.unnamed')}</option>
            ))}
          </select>
        </div>
        {value.include.length > 0 && (
          <ul className="people-filter-chips" data-testid="people-filter-include-chips">
            {value.include.map((id) => (
              <li key={id} className="people-chip people-chip-include">
                <span>{nameById.get(id) ?? t('peopleFilter.unnamed')}</span>
                <button
                  type="button"
                  className="people-chip-remove"
                  onClick={() => removeInclude(id)}
                  aria-label={t('peopleFilter.remove', { name: nameById.get(id) ?? t('peopleFilter.unnamed') })}
                >
                  ×
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>

      <div className="people-filter-group">
        <div className="people-filter-row">
          <span className="people-filter-sublabel">{t('peopleFilter.notContains')}</span>
          <select
            className="gallery-select"
            value=""
            onChange={(e) => addExclude(e.target.value)}
            aria-label={t('peopleFilter.addNotContains')}
            data-testid="people-filter-add-exclude"
          >
            <option value="">{t('peopleFilter.choosePerson')}</option>
            {excludeAvailable.map((p) => (
              <option key={p.personId} value={p.personId}>{p.name ?? t('peopleFilter.unnamed')}</option>
            ))}
          </select>
        </div>
        {value.exclude.length > 0 && (
          <ul className="people-filter-chips" data-testid="people-filter-exclude-chips">
            {value.exclude.map((id) => (
              <li key={id} className="people-chip people-chip-exclude">
                <span>{nameById.get(id) ?? t('peopleFilter.unnamed')}</span>
                <button
                  type="button"
                  className="people-chip-remove"
                  onClick={() => removeExclude(id)}
                  aria-label={t('peopleFilter.remove', { name: nameById.get(id) ?? t('peopleFilter.unnamed') })}
                >
                  ×
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}
