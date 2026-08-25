import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ActivityIndicator,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import { colors, font, spacing } from '../../theme';
import { FocusableButton } from '../../components/FocusableButton';
import { TvKeyboardPanel } from '../gallery/TvKeyboardPanel';
import { FilterRow } from './FilterRow';
import { listPersonalPeople, type TvPersonalPerson } from '../../api/personalPeople';
import { useI18n } from '../../i18n';
import type { PeopleMode } from '../../personal/mediaWorkspaceQuery';
import {
  filterPeopleByName,
  focusAfterSearch,
  clampPeoplePage,
  peoplePage,
  peoplePageCount,
  peoplePageForId,
  personMetaText,
  type PersonSelection,
} from '../../personal/peoplePicker';

// Person-picker BODY for the library filter panel.
//
// LibraryFilterPanel owns the stable PanelShell/Modal across the transition
// from the filter list into People. This component must never mount a second
// panel host for its ordinary body: swapping Android dialogs at that boundary
// was enough for the dismissed filter surface to remain painted in front while
// focus moved through this list behind it on a physical Fire Stick. Its local
// on-screen keyboard may still open a deeper modal and closes back into this
// already-mounted body.
//
// WHAT THIS REPLACES, AND WHY
// ---------------------------
// On a physical Fire Stick the virtualized list mounted focusable rows and
// accepted their selections, but did not paint their contents: only a thin
// strip was visible. The selected-count header changing proved that data,
// focus, and selection were all alive behind a broken native list viewport.
//
// This component therefore has NO scroll or virtualized-list owner. It mounts
// four ordinary rows at a time, with explicit previous/next page controls.
// Every focusable person is consequently a visible child with real geometry.
//
// FINDING PERSON #87
// ------------------
// Explicit pages prevent an eager hundred-row render, while the local name
// search avoids traversing a large owner library one person at a time. Search
// is picker NAVIGATION only: it never touches include/exclude, never reaches
// the backend, and is discarded when the picker closes.
interface Props {
  include: readonly string[];
  exclude: readonly string[];
  mode: PeopleMode;
  onChange: (include: string[], exclude: string[], mode: PeopleMode) => void;
  onClose: () => void;
  // 401/403 bubble up to the screen's shared pairing/lock handling.
  onAuthError: (err: unknown) => boolean;
}

type LoadState =
  | { kind: 'loading' }
  | { kind: 'ready'; people: TvPersonalPerson[] }
  | { kind: 'error' };

// A person id, or one of the fixed control keys. Person ids are server GUIDs so
// they cannot collide with these.
type FocusKey = string;
const SEARCH_KEY = 'search';
const MODE_KEY = 'mode';
const CLEAR_KEY = 'clear';
const PREVIOUS_KEY = 'previous-page';
const NEXT_KEY = 'next-page';
const DONE_KEY = 'done';

export function LibraryPeoplePanel({
  include, exclude, mode, onChange, onClose, onAuthError,
}: Props) {
  const { t } = useI18n();
  const [load, setLoad] = useState<LoadState>({ kind: 'loading' });
  const [attempt, setAttempt] = useState(0);
  const [search, setSearch] = useState('');
  const [pageIndex, setPageIndex] = useState(0);
  const [keyboardOpen, setKeyboardOpen] = useState(false);
  const [remount, setRemount] = useState(0);

  // Where the remote is. A ref avoids rerendering the panel on every focus
  // movement; state changes only for a deliberate page/search transition.
  const focusRef = useRef<FocusKey | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoad({ kind: 'loading' });
    listPersonalPeople()
      .then((people) => { if (!cancelled) setLoad({ kind: 'ready', people }); })
      .catch((err: unknown) => {
        if (cancelled) return;
        if (onAuthError(err)) return;
        setLoad({ kind: 'error' });
      });
    return () => { cancelled = true; };
  }, [attempt, onAuthError]);

  const stateOf = useCallback((personId: string): PersonSelection => {
    if (include.includes(personId)) return 'include';
    if (exclude.includes(personId)) return 'exclude';
    return 'off';
  }, [include, exclude]);

  const stateLabel = useCallback((state: PersonSelection): string => {
    if (state === 'include') return t('gallery.personInclude');
    if (state === 'exclude') return t('gallery.personExclude');
    return t('gallery.personOff');
  }, [t]);

  const people = load.kind === 'ready' ? load.people : [];
  const unnamed = t('gallery.unnamedPerson');

  // The rows actually on screen. Search narrows the ALREADY-LOADED owner
  // projection; it never changes what is selected.
  const visible = useMemo(
    () => filterPeopleByName(people, search, unnamed),
    [people, search, unnamed],
  );

  const commit = useCallback((
    nextInclude: string[], nextExclude: string[], nextMode: PeopleMode, fallback: string,
  ) => {
    const focused = focusRef.current;
    const keepsMode = nextInclude.length >= 2;
    const keepsClear = nextInclude.length + nextExclude.length > 0;
    if ((focused === MODE_KEY && !keepsMode) || (focused === CLEAR_KEY && !keepsClear)) {
      focusRef.current = fallback;
      setRemount((k) => k + 1);
    }
    onChange(nextInclude, nextExclude, nextMode);
  }, [onChange]);

  const cyclePerson = useCallback((personId: string, fallback: string) => {
    const current = stateOf(personId);
    const nextInclude = include.filter((id) => id !== personId);
    const nextExclude = exclude.filter((id) => id !== personId);
    if (current === 'off') nextInclude.push(personId);
    else if (current === 'include') nextExclude.push(personId);
    commit(nextInclude, nextExclude, mode, fallback);
  }, [stateOf, include, exclude, mode, commit]);

  // --- search ---------------------------------------------------------------

  const applySearch = useCallback((value: string) => {
    const next = value.trim();
    setSearch(next);
    setKeyboardOpen(false);
    // A narrowed list may no longer contain the focused person. Hand focus on
    // deterministically rather than leave the remote on a row that is gone.
    const nextVisible = filterPeopleByName(people, next, unnamed);
    const nextFocus = focusAfterSearch(nextVisible, focusRef.current, SEARCH_KEY);
    focusRef.current = nextFocus;
    setPageIndex(peoplePageForId(nextVisible, nextFocus));
    setRemount((k) => k + 1);
  }, [people, unnamed]);

  if (keyboardOpen) {
    return (
      <TvKeyboardPanel
        title={t('filters.peopleSearch')}
        mode="text"
        initialValue={search}
        onSubmit={applySearch}
        onCancel={() => setKeyboardOpen(false)}
      />
    );
  }

  if (load.kind === 'loading') {
    return (
      <View style={styles.stateBox}>
        <ActivityIndicator size="large" color={colors.accent} />
        <Text style={styles.muted}>{t('filters.peopleLoading')}</Text>
        <FocusableButton label={t('filters.back')} onPress={onClose} hasTVPreferredFocus />
      </View>
    );
  }

  if (load.kind === 'error') {
    return (
      <View style={styles.stateBox}>
        <Text style={styles.muted}>{t('gallery.peopleLoadError')}</Text>
        <FocusableButton
          label={t('common.tryAgain')}
          onPress={() => { focusRef.current = null; setAttempt((a) => a + 1); }}
          hasTVPreferredFocus
        />
        <FocusableButton label={t('filters.back')} onPress={onClose} />
      </View>
    );
  }

  if (people.length === 0) {
    return (
      <View style={styles.stateBox}>
        <Text style={styles.muted}>{t('gallery.peopleEmpty')}</Text>
        <FocusableButton label={t('filters.back')} onPress={onClose} hasTVPreferredFocus />
      </View>
    );
  }

  const safePageIndex = clampPeoplePage(pageIndex, visible.length);
  const totalPages = peoplePageCount(visible.length);
  const pagePeople = peoplePage(visible, safePageIndex);
  const fallbackKey = pagePeople.length > 0 ? pagePeople[0].id : SEARCH_KEY;
  const hasPreviousPage = safePageIndex > 0;
  const hasNextPage = safePageIndex + 1 < totalPages;
  const includeCount = include.length;
  const selectedCount = includeCount + exclude.length;
  const showMode = includeCount >= 2;
  const showClear = selectedCount > 0;

  // A selected person the projection no longer returns — deleted, merged, or
  // reclustered away. The id STAYS in the draft: dropping it silently would
  // edit a filter the user did not ask to change. The count then exceeds the
  // visible selected rows, so the difference is stated rather than hidden.
  // Computed against the WHOLE projection, never the search-narrowed view —
  // otherwise typing a name would invent stale selections.
  const known = new Set(people.map((person) => person.id));
  const staleCount = [...include, ...exclude].filter((id) => !known.has(id)).length;

  const wanted = focusRef.current;
  const focusKey: FocusKey =
    wanted === MODE_KEY && showMode ? MODE_KEY
      : wanted === CLEAR_KEY && showClear ? CLEAR_KEY
        : wanted === PREVIOUS_KEY && hasPreviousPage ? PREVIOUS_KEY
          : wanted === NEXT_KEY && hasNextPage ? NEXT_KEY
            : wanted === DONE_KEY || wanted === SEARCH_KEY ? wanted
              : wanted !== null && pagePeople.some((p) => p.id === wanted) ? wanted
            : fallbackKey;

  const goToPage = (requestedPage: number) => {
    const nextPage = clampPeoplePage(requestedPage, visible.length);
    const nextPeople = peoplePage(visible, nextPage);
    focusRef.current = nextPeople[0]?.id ?? SEARCH_KEY;
    setPageIndex(nextPage);
    setRemount((k) => k + 1);
  };

  return (
    <View key={remount} style={styles.body}>
        {/* Fixed header: summary, search, mode, clear. */}
        <View style={styles.header}>
          <Text style={styles.hint}>
            {selectedCount === 0
              ? t('filters.peopleNone')
              : staleCount === 0
                ? t('filters.peopleSelected', { count: String(selectedCount) })
                : t('filters.peopleSelectedStale', {
                  count: String(selectedCount), stale: String(staleCount),
                })}
          </Text>

          <FilterRow
            label={t('filters.peopleSearch')}
            value={search.length > 0 ? search : t('filters.any')}
            active={search.length > 0}
            opensEditor
            accessibilityLabel={t('filters.rowA11y', {
              label: t('filters.peopleSearch'),
              value: search.length > 0 ? search : t('filters.any'),
            })}
            hasTVPreferredFocus={focusKey === SEARCH_KEY}
            onFocus={() => { focusRef.current = SEARCH_KEY; }}
            onSelect={() => setKeyboardOpen(true)}
          />

          {showMode && (
            <FilterRow
              label={t('filters.peopleMode')}
              value={mode === 'all' ? t('gallery.peopleModeAll') : t('gallery.peopleModeAny')}
              active={mode === 'any'}
              opensEditor={false}
              accessibilityLabel={t('filters.rowA11y', {
                label: t('filters.peopleMode'),
                value: mode === 'all' ? t('gallery.peopleModeAll') : t('gallery.peopleModeAny'),
              })}
              hasTVPreferredFocus={focusKey === MODE_KEY}
              onFocus={() => { focusRef.current = MODE_KEY; }}
              onSelect={() => commit(
                [...include], [...exclude], mode === 'all' ? 'any' : 'all', fallbackKey,
              )}
            />
          )}

          {showClear && (
            <FilterRow
              label={t('filters.peopleClear')}
              value=""
              active={false}
              opensEditor={false}
              accessibilityLabel={t('filters.peopleClear')}
              hasTVPreferredFocus={focusKey === CLEAR_KEY}
              onFocus={() => { focusRef.current = CLEAR_KEY; }}
              onSelect={() => commit([], [], mode, fallbackKey)}
            />
          )}
        </View>

        {visible.length === 0 ? (
          <View style={styles.stateBox}>
            <Text style={styles.muted}>{t('filters.peopleSearchEmpty')}</Text>
          </View>
        ) : (
          <View style={styles.pageList}>
            {pagePeople.map((person) => {
              const state = stateOf(person.id);
              const name = person.name ?? unnamed;
              const meta = personMetaText(state, person.faceCount, stateLabel);
              return (
                <View key={person.id} style={styles.row}>
                  <FilterRow
                    variant="person"
                    // The NAME alone. The face count belongs in the trailing
                    // meta, not concatenated here, or it becomes part of the
                    // truncatable string and a long name loses it entirely.
                    label={name}
                    value={meta}
                    active={state !== 'off'}
                    opensEditor={false}
                    // Screen readers get the FULL name even when the visible
                    // text is ellipsized.
                    accessibilityLabel={t('filters.rowA11y', { label: name, value: meta })}
                    hasTVPreferredFocus={focusKey === person.id}
                    onFocus={() => { focusRef.current = person.id; }}
                    onSelect={() => cyclePerson(person.id, fallbackKey)}
                  />
                </View>
              );
            })}
          </View>
        )}

        <View style={styles.actions}>
          {totalPages > 1 && (
            <>
              <FocusableButton
                label={t('filters.peoplePrevious')}
                onPress={() => goToPage(safePageIndex - 1)}
                disabled={!hasPreviousPage}
                hasTVPreferredFocus={focusKey === PREVIOUS_KEY}
                onFocusChange={(focused) => {
                  if (focused) focusRef.current = PREVIOUS_KEY;
                }}
              />
              <Text style={styles.pageLabel}>
                {t('filters.peoplePage', {
                  page: String(safePageIndex + 1), total: String(totalPages),
                })}
              </Text>
              <FocusableButton
                label={t('filters.peopleNext')}
                onPress={() => goToPage(safePageIndex + 1)}
                disabled={!hasNextPage}
                hasTVPreferredFocus={focusKey === NEXT_KEY}
                onFocusChange={(focused) => {
                  if (focused) focusRef.current = NEXT_KEY;
                }}
              />
            </>
          )}
          <FocusableButton
            label={t('gallery.done')}
            onPress={onClose}
            hasTVPreferredFocus={focusKey === DONE_KEY}
            onFocusChange={(f) => { if (f) focusRef.current = DONE_KEY; }}
          />
        </View>
    </View>
  );
}

const styles = StyleSheet.create({
  body: { flex: 1, gap: spacing.sm },
  header: { gap: spacing.sm },
  pageList: { flex: 1, gap: spacing.xs },
  row: { minHeight: 64, justifyContent: 'center' },
  stateBox: { flex: 1, alignItems: 'center', justifyContent: 'center', gap: spacing.md },
  muted: { color: colors.muted, fontSize: font.body, textAlign: 'center' },
  hint: { color: colors.muted, fontSize: font.caption },
  pageLabel: { minWidth: 132, color: colors.muted, fontSize: font.body, textAlign: 'center' },
  actions: {
    flexDirection: 'row', alignItems: 'center', justifyContent: 'center',
    gap: spacing.sm, paddingTop: spacing.sm,
  },
});
