import { useId, useMemo, useRef, useState } from 'react';
import type { Person } from '@nubarca/api-client';
import { useI18n } from '../../i18n';

// A small accessible searchable combobox for picking owner-private people.
// Replaces the long native <select> the old panel used. Keyboard: type to
// filter, ArrowDown/Up to move, Enter to add, Escape to close the list. Selected
// people render as removable chips managed by the parent. No backend semantics
// change — it only edits an ordered array of person ids.
interface Props {
  label: string;
  people: Person[];
  selected: string[];
  otherGroup: string[]; // ids already used by the opposite group (include/exclude) to hide
  onAdd(personId: string): void;
  onRemove(personId: string): void;
  variant: 'include' | 'exclude';
}

export function PeopleCombobox({ label, people, selected, otherGroup, onAdd, onRemove, variant }: Props) {
  const { t } = useI18n();
  const [query, setQuery] = useState('');
  const [open, setOpen] = useState(false);
  const [activeIndex, setActiveIndex] = useState(0);
  const listId = useId();
  const inputRef = useRef<HTMLInputElement>(null);

  const nameOf = (p: Person) => (p.name && p.name.trim().length > 0 ? p.name : t('peopleFilter.unnamed'));

  const candidates = useMemo(() => {
    const taken = new Set([...selected, ...otherGroup]);
    const q = query.trim().toLowerCase();
    return people
      .filter((p) => !taken.has(p.personId))
      .filter((p) => q.length === 0 || nameOf(p).toLowerCase().includes(q))
      .slice(0, 8);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [people, selected, otherGroup, query]);

  function commit(index: number) {
    const chosen = candidates[index];
    if (!chosen) return;
    onAdd(chosen.personId);
    setQuery('');
    setActiveIndex(0);
    setOpen(false);
    inputRef.current?.focus();
  }

  function onKeyDown(e: React.KeyboardEvent<HTMLInputElement>) {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      setOpen(true);
      setActiveIndex((i) => Math.min(i + 1, candidates.length - 1));
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      setActiveIndex((i) => Math.max(i - 1, 0));
    } else if (e.key === 'Enter') {
      if (open && candidates.length > 0) {
        e.preventDefault();
        commit(activeIndex);
      }
    } else if (e.key === 'Escape') {
      if (open) {
        e.stopPropagation(); // don't close the whole sheet
        setOpen(false);
      }
    }
  }

  const selectedPeople = selected
    .map((id) => people.find((p) => p.personId === id))
    .filter((p): p is Person => p !== undefined);

  return (
    <div className="ws-combobox">
      <span className="ws-field-label" id={`${listId}-label`}>{label}</span>
      <div className="ws-combobox-input-row">
        <input
          ref={inputRef}
          type="text"
          role="combobox"
          aria-expanded={open}
          aria-controls={listId}
          aria-autocomplete="list"
          aria-labelledby={`${listId}-label`}
          className="ws-input"
          placeholder={t('gallery.ws.peopleSearch')}
          value={query}
          onChange={(e) => { setQuery(e.target.value); setOpen(true); setActiveIndex(0); }}
          onFocus={() => setOpen(true)}
          onKeyDown={onKeyDown}
        />
        {open && query.trim().length > 0 && (
          <ul className="ws-combobox-list" id={listId} role="listbox">
            {candidates.length === 0 && (
              <li className="ws-combobox-empty" role="presentation">{t('gallery.ws.peopleNoResults')}</li>
            )}
            {candidates.map((p, i) => (
              <li
                key={p.personId}
                role="option"
                aria-selected={i === activeIndex}
                className={`ws-combobox-option${i === activeIndex ? ' is-active' : ''}`}
                // onMouseDown (not click) so the input's blur doesn't close the
                // list before the selection is committed.
                onMouseDown={(e) => { e.preventDefault(); commit(i); }}
                onMouseEnter={() => setActiveIndex(i)}
              >
                {nameOf(p)}
              </li>
            ))}
          </ul>
        )}
      </div>
      {selectedPeople.length > 0 && (
        <ul className="ws-chip-list" aria-label={label}>
          {selectedPeople.map((p) => (
            <li key={p.personId}>
              <span className={`ws-person-chip ws-person-chip-${variant}`}>
                {nameOf(p)}
                <button
                  type="button"
                  className="ws-chip-remove"
                  aria-label={t('peopleFilter.remove', { name: nameOf(p) })}
                  onClick={() => onRemove(p.personId)}
                >
                  ×
                </button>
              </span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
