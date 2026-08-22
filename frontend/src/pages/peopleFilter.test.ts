import { describe, expect, it } from 'vitest';
import type { Person } from '@nubarca/api-client';
import { filterPeopleByName } from './peopleFilter';

const person = (personId: string, name: string | null): Person =>
  ({ personId, name, faceCount: 0, representative: null } as unknown as Person);

describe('filterPeopleByName', () => {
  const people = [
    person('1', 'Marco'),
    person('2', 'Maria'),
    person('3', 'Gianmarco'),
    person('4', 'Lucia'),
    person('5', null),
  ];

  it('matches anywhere in the name, not only at the start', () => {
    // "Gianmarco" is the case that a startsWith filter would silently lose.
    expect(filterPeopleByName(people, 'mar').map((p) => p.personId))
      .toEqual(['1', '2', '3']);
  });

  it('ignores case and surrounding spaces', () => {
    expect(filterPeopleByName(people, '  MARIA ').map((p) => p.personId)).toEqual(['2']);
  });

  it('returns everyone for an empty or blank query', () => {
    expect(filterPeopleByName(people, '')).toHaveLength(people.length);
    expect(filterPeopleByName(people, '   ')).toHaveLength(people.length);
  });

  it('never matches an unnamed person against a real query', () => {
    // They would otherwise appear in every search, because "" is a substring of
    // nothing but is trivially contained in the empty needle.
    expect(filterPeopleByName(people, 'a').map((p) => p.personId)).not.toContain('5');
    expect(filterPeopleByName(people, '').map((p) => p.personId)).toContain('5');
  });

  it('does not mutate or alias the input', () => {
    const original = [...people];
    const result = filterPeopleByName(people, '');
    result.pop();
    expect(people).toEqual(original);
  });
});
