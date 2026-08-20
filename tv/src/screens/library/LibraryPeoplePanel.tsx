import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ActivityIndicator,
  FlatList,
  StyleSheet,
  Text,
  View,
  type LayoutChangeEvent,
  type NativeScrollEvent,
  type NativeSyntheticEvent,
} from 'react-native';
import { colors, font, spacing } from '../../theme';
import { FocusableButton } from '../../components/FocusableButton';
import { PanelShell } from '../gallery/PanelShell';
import { TvKeyboardPanel } from '../gallery/TvKeyboardPanel';
import { FilterRow } from './FilterRow';
import { listPersonalPeople, type TvPersonalPerson } from '../../api/personalPeople';
import { useI18n } from '../../i18n';
import type { PeopleMode } from '../../personal/mediaWorkspaceQuery';
import {
  filterPeopleByName,
  focusAfterSearch,
  personItemLayout,
  personMetaText,
  PEOPLE_LIST_TUNING,
  PERSON_ROW_HEIGHT,
  reconcileFocusViewport,
  visibleRowCount,
  type PersonSelection,
} from '../../personal/peoplePicker';

// Person picker for the library filter panel.
//
// WHAT THIS REPLACED, AND WHY IT HAD TO
// -------------------------------------
// The previous version rendered `people.map(...)` inside PanelShell's
// ScrollView. On a demo library that looks fine; on a real one it is several
// hundred focusable rows mounted at once on a Fire Stick, inside a container
// that also wanted to scroll. Three different concerns — how many rows exist,
// who owns scrolling, and where the remote is — were tangled in one JSX
// expression, so none could be reasoned about separately.
//
// Now: ONE FlatList owns the scrolling (PanelShell is in 'custom' body mode and
// does not), row geometry is fixed so `getItemLayout` is exact, and the render
// window is bounded. See personal/peoplePicker.ts for every number.
//
// WHO DECIDES FOCUS
// -----------------
// Android does. There is no nextFocusUp/nextFocusDown graph here, no D-pad
// handling and no debounce. What this component does is REACT to the native
// engine: when a row reports `onFocus`, the list is scrolled only if that row
// has drifted out of a comfortable band of the viewport. That distinction is
// the whole point — a JS navigator would eventually disagree with Android about
// which row is focused, and the highlight and the selection would part company.
//
// FINDING PERSON #87
// ------------------
// Virtualization makes a long list cheap to RENDER; it does nothing about it
// being twenty seconds of D-pad away. So there is a local name search. It is
// picker NAVIGATION only: it never touches include/exclude, never reaches the
// backend, and is discarded when the picker closes.
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
const DONE_KEY = 'done';

export function LibraryPeoplePanel({
  include, exclude, mode, onChange, onClose, onAuthError,
}: Props) {
  const { t } = useI18n();
  const [load, setLoad] = useState<LoadState>({ kind: 'loading' });
  const [attempt, setAttempt] = useState(0);
  const [search, setSearch] = useState('');
  const [keyboardOpen, setKeyboardOpen] = useState(false);
  const [remount, setRemount] = useState(0);

  // Where the remote is. A ref, not state: this list is long and moving between
  // people must never re-render the panel.
  const focusRef = useRef<FocusKey | null>(null);
  const listRef = useRef<FlatList<TvPersonalPerson> | null>(null);
  // Viewport geometry, maintained from real layout/scroll events rather than
  // assumed — the reconciliation is only exact if these are.
  const firstVisibleRef = useRef(0);
  const visibleCountRef = useRef(1);

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

  // --- native focus → viewport reconciliation ------------------------------

  const onRowFocus = useCallback((personId: string, index: number) => {
    focusRef.current = personId;
    const request = reconcileFocusViewport({
      focusedIndex: index,
      firstVisibleIndex: firstVisibleRef.current,
      visibleCount: visibleCountRef.current,
      total: visible.length,
    });
    // Null is the common case: the row is already comfortably visible, so a
    // held-down D-pad does not fight the scroller.
    if (request === null) return;
    listRef.current?.scrollToIndex({
      index: request.index,
      viewPosition: request.viewPosition,
      animated: true,
    });
  }, [visible.length]);

  const onListLayout = useCallback((event: LayoutChangeEvent) => {
    visibleCountRef.current = visibleRowCount(event.nativeEvent.layout.height);
  }, []);

  const onListScroll = useCallback((event: NativeSyntheticEvent<NativeScrollEvent>) => {
    firstVisibleRef.current = Math.max(
      0, Math.round(event.nativeEvent.contentOffset.y / PERSON_ROW_HEIGHT),
    );
  }, []);

  // --- search ---------------------------------------------------------------

  const applySearch = useCallback((value: string) => {
    const next = value.trim();
    setSearch(next);
    setKeyboardOpen(false);
    // A narrowed list may no longer contain the focused person. Hand focus on
    // deterministically rather than leave the remote on a row that is gone.
    const nextVisible = filterPeopleByName(people, next, unnamed);
    focusRef.current = focusAfterSearch(nextVisible, focusRef.current, SEARCH_KEY);
    setRemount((k) => k + 1);
  }, [people, unnamed]);

  const title = t('gallery.peopleTitle');

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
      <PanelShell title={title} onBack={onClose} body="fixed">
        <View style={styles.stateBox}>
          <ActivityIndicator size="large" color={colors.accent} />
          <Text style={styles.muted}>{t('filters.peopleLoading')}</Text>
          <FocusableButton label={t('filters.back')} onPress={onClose} hasTVPreferredFocus />
        </View>
      </PanelShell>
    );
  }

  if (load.kind === 'error') {
    return (
      <PanelShell title={title} onBack={onClose} body="fixed">
        <View style={styles.stateBox}>
          <Text style={styles.muted}>{t('gallery.peopleLoadError')}</Text>
          <FocusableButton
            label={t('common.tryAgain')}
            onPress={() => { focusRef.current = null; setAttempt((a) => a + 1); }}
            hasTVPreferredFocus
          />
          <FocusableButton label={t('filters.back')} onPress={onClose} />
        </View>
      </PanelShell>
    );
  }

  if (people.length === 0) {
    return (
      <PanelShell title={title} onBack={onClose} body="fixed">
        <View style={styles.stateBox}>
          <Text style={styles.muted}>{t('gallery.peopleEmpty')}</Text>
          <FocusableButton label={t('filters.back')} onPress={onClose} hasTVPreferredFocus />
        </View>
      </PanelShell>
    );
  }

  const fallbackKey = visible.length > 0 ? visible[0].id : SEARCH_KEY;
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
        : wanted === DONE_KEY || wanted === SEARCH_KEY ? wanted
          : wanted !== null && visible.some((p) => p.id === wanted) ? wanted
            : fallbackKey;

  return (
    <PanelShell title={title} onBack={onClose} body="custom">
      <View key={remount} style={styles.body}>
        {/* Fixed header: summary, search, mode, clear. Not part of the
            scrollable region — these must stay reachable however long the
            person list is. */}
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

        {/* THE ONE SCROLL OWNER. */}
        {visible.length === 0 ? (
          <View style={styles.stateBox}>
            <Text style={styles.muted}>{t('filters.peopleSearchEmpty')}</Text>
          </View>
        ) : (
          <FlatList
            ref={listRef}
            style={styles.list}
            data={visible}
            keyExtractor={(person) => person.id}
            getItemLayout={(_, index) => personItemLayout(index)}
            onLayout={onListLayout}
            onScroll={onListScroll}
            scrollEventThrottle={16}
            showsVerticalScrollIndicator={false}
            initialNumToRender={PEOPLE_LIST_TUNING.initialNumToRender}
            maxToRenderPerBatch={PEOPLE_LIST_TUNING.maxToRenderPerBatch}
            windowSize={PEOPLE_LIST_TUNING.windowSize}
            // MUST stay false: on Android TV a clipped view is detached, and a
            // detached view cannot hold focus.
            removeClippedSubviews={PEOPLE_LIST_TUNING.removeClippedSubviews}
            renderItem={({ item: person, index }) => {
              const state = stateOf(person.id);
              const name = person.name ?? unnamed;
              const meta = personMetaText(state, person.faceCount, stateLabel);
              return (
                <View style={styles.row}>
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
                    onFocus={() => onRowFocus(person.id, index)}
                    onSelect={() => cyclePerson(person.id, fallbackKey)}
                  />
                </View>
              );
            }}
          />
        )}

        <View style={styles.actions}>
          <FocusableButton
            label={t('gallery.done')}
            onPress={onClose}
            hasTVPreferredFocus={focusKey === DONE_KEY}
            onFocusChange={(f) => { if (f) focusRef.current = DONE_KEY; }}
          />
        </View>
      </View>
    </PanelShell>
  );
}

const styles = StyleSheet.create({
  body: { flex: 1, gap: spacing.sm },
  header: { gap: spacing.sm },
  // flex:1 is what bounds the list's height, and a bounded height is what makes
  // virtualization real rather than nominal.
  list: { flex: 1 },
  // Fixed height so getItemLayout is exact — no measurement, no async.
  row: { height: PERSON_ROW_HEIGHT, justifyContent: 'center' },
  stateBox: { flex: 1, alignItems: 'center', justifyContent: 'center', gap: spacing.md },
  muted: { color: colors.muted, fontSize: font.body, textAlign: 'center' },
  hint: { color: colors.muted, fontSize: font.caption },
  actions: { flexDirection: 'row', justifyContent: 'center', paddingTop: spacing.sm },
});
