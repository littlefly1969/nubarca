import { useEffect, useMemo, useState } from 'react';
import { listPeople, type Person } from '@nubarca/api-client';

// Loads the owner's people once and exposes both the list and a fast id→name
// lookup. Shared by the filter sheet's people combobox and the active-filter
// chips (which resolve include/exclude person ids to display names). Only
// owner-private person ids and display names cross the boundary — never faces,
// clusters or vectors.
export interface PeopleIndex {
  people: Person[];
  loaded: boolean;
  nameOf(personId: string): string | null;
}

export function usePeopleIndex(): PeopleIndex {
  const [people, setPeople] = useState<Person[]>([]);
  const [loaded, setLoaded] = useState(false);

  useEffect(() => {
    const ctrl = new AbortController();
    listPeople(ctrl.signal)
      .then((list) => {
        setPeople(list);
        setLoaded(true);
      })
      .catch(() => {
        // Non-critical: chips fall back to a generic label, combobox is empty.
      });
    return () => ctrl.abort();
  }, []);

  const byId = useMemo(() => {
    const map = new Map<string, string | null>();
    for (const p of people) map.set(p.personId, p.name);
    return map;
  }, [people]);

  return useMemo(
    () => ({
      people,
      loaded,
      nameOf: (id: string) => byId.get(id) ?? null,
    }),
    [people, loaded, byId],
  );
}
