import { useCallback, useRef, useState } from 'react';
import { ActivityIndicator, StyleSheet, Text, View } from 'react-native';
import { FocusableButton } from '../../components/FocusableButton';
import { colors, font, spacing } from '../../theme';
import { useI18n } from '../../i18n';
import {
  cloneGalleryFilters,
  countActiveFilters,
  defaultSort,
  draftToFilters,
  emptyFilters,
  isValidDateInput,
  peopleIds,
  type GalleryFilters,
  type GallerySort,
  type InterpretResponse,
} from '../../personal/galleryQuery';
import { InterpretError, interpretCommand } from '../../api/personalGallery';
import { CycleRow } from './CycleRow';
import { GalleryPeoplePanel } from './GalleryPeoplePanel';
import { PanelShell } from './PanelShell';
import { TvKeyboardPanel } from './TvKeyboardPanel';

type WorkspaceMode = 'describe' | 'manual';
type Editor = 'none' | 'describe' | 'visual' | 'metadata' | 'people' | 'dateFrom' | 'dateTo';

interface Props {
  appliedFilters: GalleryFilters;
  appliedSort: GallerySort;
  resultCount: number;
  onApply: (filters: GalleryFilters, sort: GallerySort) => void;
  onCancel: () => void;
  onAuthError: (err: unknown) => boolean;
}

const SORTS: GallerySort[] = [
  { field: 'created', direction: 'desc' },
  { field: 'created', direction: 'asc' },
  { field: 'datetaken', direction: 'desc' },
  { field: 'datetaken', direction: 'asc' },
  { field: 'name', direction: 'asc' },
  { field: 'name', direction: 'desc' },
  { field: 'size', direction: 'desc' },
  { field: 'size', direction: 'asc' },
];

export function GalleryWorkspacePanel({
  appliedFilters,
  appliedSort,
  resultCount,
  onApply,
  onCancel,
  onAuthError,
}: Props) {
  const { lang } = useI18n();
  const L = useCallback((it: string, en: string) => (lang === 'it' ? it : en), [lang]);
  const [mode, setMode] = useState<WorkspaceMode>('describe');
  const [editor, setEditor] = useState<Editor>('none');
  const [draftFilters, setDraftFilters] = useState(() => cloneGalleryFilters(appliedFilters));
  const [draftSort, setDraftSort] = useState<GallerySort>(() => ({ ...appliedSort }));
  const [command, setCommand] = useState('');
  const [interpreting, setInterpreting] = useState(false);
  const [interpretResult, setInterpretResult] = useState<InterpretResponse | null>(null);
  const [choices, setChoices] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [applyPressed, setApplyPressed] = useState(false);
  const editRevisionRef = useRef(0);
  const applyPressedRef = useRef(false);

  const touchFilters = useCallback((update: (current: GalleryFilters) => GalleryFilters) => {
    editRevisionRef.current += 1;
    setDraftFilters((current) => update(cloneGalleryFilters(current)));
  }, []);

  const touchSort = useCallback((next: GallerySort) => {
    editRevisionRef.current += 1;
    setDraftSort(next);
  }, []);

  const runInterpret = useCallback(async (value: string) => {
    const text = value.trim();
    setEditor('none');
    setCommand(text);
    if (text.length === 0 || interpreting) return;
    const revision = editRevisionRef.current;
    setInterpreting(true);
    setError(null);
    try {
      const result = await interpretCommand(text, draftFilters, draftSort, lang);
      // A parser response belongs to the exact draft revision it was sent
      // with. A newer manual edit wins and the stale response is discarded.
      if (revision !== editRevisionRef.current) return;
      const mapped = draftToFilters(result.draft);
      setDraftFilters(cloneGalleryFilters(mapped.filters));
      setDraftSort({ ...mapped.sort });
      setInterpretResult(result);
      setChoices({});
      editRevisionRef.current += 1;
    } catch (err) {
      if (onAuthError(err)) return;
      const kind = err instanceof InterpretError ? err.kind : 'failed';
      setError(kind === 'busy'
        ? L('Il motore locale è occupato. Riprova.', 'The local engine is busy. Try again.')
        : kind === 'unavailable'
          ? L('Il motore locale non è disponibile.', 'The local engine is unavailable.')
          : L('Non sono riuscito a interpretare la frase. Puoi usare i filtri manuali.',
            'I could not interpret that phrase. You can use manual filters.'));
    } finally {
      setInterpreting(false);
    }
  }, [draftFilters, draftSort, interpreting, lang, L, onAuthError]);

  const chooseAmbiguity = useCallback((text: string, personId: string, modeValue: 'include' | 'exclude') => {
    const previous = choices[text];
    setChoices((current) => ({ ...current, [text]: personId }));
    touchFilters((current) => {
      if (previous) delete current.people[previous];
      current.people[personId] = modeValue;
      return current;
    });
  }, [choices, touchFilters]);

  const resetDraft = useCallback(() => {
    editRevisionRef.current += 1;
    setDraftFilters(cloneGalleryFilters(emptyFilters));
    setDraftSort({ ...defaultSort });
    setInterpretResult(null);
    setChoices({});
    setCommand('');
    setError(null);
  }, []);

  const unresolved = interpretResult?.ambiguities.some((item) => !choices[item.text]) ?? false;
  const datesValid = (draftFilters.dateFrom === '' || isValidDateInput(draftFilters.dateFrom))
    && (draftFilters.dateTo === '' || isValidDateInput(draftFilters.dateTo))
    && (draftFilters.dateFrom === '' || draftFilters.dateTo === ''
      || draftFilters.dateFrom <= draftFilters.dateTo);

  const apply = useCallback(() => {
    if (unresolved || !datesValid || applyPressedRef.current) return;
    applyPressedRef.current = true;
    setApplyPressed(true);
    onApply(cloneGalleryFilters(draftFilters), { ...draftSort });
  }, [unresolved, datesValid, onApply, draftFilters, draftSort]);

  if (editor === 'describe' || editor === 'visual' || editor === 'metadata') {
    const visual = editor === 'visual';
    const describing = editor === 'describe';
    return (
      <TvKeyboardPanel
        title={describing
          ? L('Descrivi cosa cerchi', 'Describe what you are looking for')
          : visual
            ? L('Contenuto visivo', 'Visual content')
            : L('Testo e metadati', 'Text and metadata')}
        mode="text"
        initialValue={describing ? command : visual ? draftFilters.semanticQuery : draftFilters.q}
        onSubmit={(value) => {
          if (describing) {
            void runInterpret(value);
          } else {
            touchFilters((current) => ({
              ...current,
              ...(visual
                ? { semanticQuery: value.trim(), semanticTopK: 0 }
                : { q: value.trim() }),
            }));
            setEditor('none');
          }
        }}
        onCancel={() => setEditor('none')}
      />
    );
  }

  if (editor === 'people') {
    return (
      <GalleryPeoplePanel
        people={draftFilters.people}
        mode={draftFilters.includePeopleMode}
        onChange={(people, includePeopleMode) => touchFilters((current) => ({
          ...current, people, includePeopleMode,
        }))}
        onClose={() => setEditor('none')}
        onAuthError={onAuthError}
      />
    );
  }

  if (editor === 'dateFrom' || editor === 'dateTo') {
    const from = editor === 'dateFrom';
    return (
      <TvKeyboardPanel
        title={from ? L('Data iniziale', 'Start date') : L('Data finale', 'End date')}
        mode="date"
        initialValue={from ? draftFilters.dateFrom : draftFilters.dateTo}
        onSubmit={(value) => {
          touchFilters((current) => ({ ...current, [from ? 'dateFrom' : 'dateTo']: value }));
          setEditor('none');
        }}
        onCancel={() => setEditor('none')}
      />
    );
  }

  const appliedCount = countActiveFilters(appliedFilters);
  const draftCount = countActiveFilters(draftFilters);
  const summary = summarize(draftFilters, draftSort, L);
  const includeCount = peopleIds(draftFilters, 'include').length;
  const peopleCount = includeCount + peopleIds(draftFilters, 'exclude').length;
  const sortIndex = SORTS.findIndex((item) => item.field === draftSort.field
    && item.direction === draftSort.direction);

  return (
    <PanelShell title={L('Ricerca e filtri', 'Search and filters')} onBack={onCancel}>
      <View style={styles.statusRow}>
        <Text style={styles.statusText}>
          {L(`${appliedCount} applicati · ${resultCount} risultati`,
            `${appliedCount} applied · ${resultCount} results`)}
        </Text>
        <Text style={styles.draftText}>{L(`Bozza: ${draftCount} filtri`, `Draft: ${draftCount} filters`)}</Text>
      </View>

      <View style={styles.modeRail}>
        <FocusableButton
          label={`${mode === 'describe' ? '✓ ' : ''}${L('Descrivi', 'Describe')}`}
          onPress={() => setMode('describe')}
          hasTVPreferredFocus
        />
        <FocusableButton
          label={`${mode === 'manual' ? '✓ ' : ''}${L('Filtri manuali', 'Manual filters')}`}
          onPress={() => setMode('manual')}
        />
      </View>

      {mode === 'describe' ? (
        <View style={styles.editor}>
          <Text style={styles.sectionTitle}>{L('Descrivi la scena, le persone o il periodo',
            'Describe the scene, people, or period')}</Text>
          <FocusableButton
            label={command || L('Apri tastiera', 'Open keyboard')}
            onPress={() => setEditor('describe')}
          />
          {interpreting && <ActivityIndicator color={colors.accent} />}
          {error !== null && <Text style={styles.error}>{error}</Text>}
          {summary.map((line) => <Text key={line} style={styles.summaryLine}>• {line}</Text>)}
          {interpretResult?.ambiguities.map((ambiguity) => (
            <View key={ambiguity.text} style={styles.ambiguity}>
              <Text style={styles.sectionTitle}>
                {L(`Quale “${ambiguity.text}”?`, `Which “${ambiguity.text}”?`)}
              </Text>
              <View style={styles.choiceRow}>
                {ambiguity.candidates.map((candidate) => (
                  <FocusableButton
                    key={candidate.personId}
                    label={`${choices[ambiguity.text] === candidate.personId ? '✓ ' : ''}${candidate.name ?? ambiguity.text}`}
                    onPress={() => chooseAmbiguity(
                      ambiguity.text, candidate.personId, ambiguity.mode === 'exclude' ? 'exclude' : 'include')}
                  />
                ))}
              </View>
            </View>
          ))}
          {unresolved && <Text style={styles.error}>
            {L('Risolvi le persone ambigue prima di applicare.', 'Resolve ambiguous people before applying.')}
          </Text>}
        </View>
      ) : (
        <View style={styles.editor}>
          <Text style={styles.sectionTitle}>{L('Contenuto', 'Content')}</Text>
          <CycleRow
            label={L('Contenuto visivo', 'Visual content')}
            value={draftFilters.semanticQuery || L('Nessuno', 'None')}
            onCycle={() => setEditor('visual')}
          />
          <CycleRow
            label={L('Testo e metadati', 'Text and metadata')}
            value={draftFilters.q || L('Nessuno', 'None')}
            onCycle={() => setEditor('metadata')}
          />

          <Text style={styles.sectionTitle}>{L('Persone', 'People')}</Text>
          <CycleRow
            label={L('Includi / escludi', 'Include / exclude')}
            value={peopleCount === 0 ? L('Nessuna', 'None') : L(`${peopleCount} persone`, `${peopleCount} people`)}
            onCycle={() => setEditor('people')}
          />
          {includeCount >= 2 && (
            <CycleRow
              label={L('Corrispondenza', 'Matching')}
              value={draftFilters.includePeopleMode === 'all' ? L('Tutte', 'All') : L('Almeno una', 'Any')}
              onCycle={() => touchFilters((current) => ({
                ...current, includePeopleMode: current.includePeopleMode === 'all' ? 'any' : 'all',
              }))}
            />
          )}

          <Text style={styles.sectionTitle}>{L('Periodo', 'Period')}</Text>
          <CycleRow label={L('Dal', 'From')} value={draftFilters.dateFrom || '—'} onCycle={() => setEditor('dateFrom')} />
          <CycleRow label={L('Al', 'To')} value={draftFilters.dateTo || '—'} onCycle={() => setEditor('dateTo')} />
          {!datesValid && <Text style={styles.error}>{L('Intervallo di date non valido.', 'Invalid date range.')}</Text>}

          <Text style={styles.sectionTitle}>{L('Attributi', 'Attributes')}</Text>
          <CycleRow
            label={L('Preferite', 'Favorites')}
            value={draftFilters.favorite === null ? L('Qualsiasi', 'Any')
              : draftFilters.favorite ? L('Solo preferite', 'Favorites only') : L('Non preferite', 'Not favorites')}
            onCycle={() => touchFilters((current) => ({
              ...current, favorite: current.favorite === null ? true : current.favorite ? false : null,
            }))}
          />
          <CycleRow
            label={L('Valutazione minima', 'Minimum rating')}
            value={draftFilters.minRating === null ? L('Qualsiasi', 'Any') : `★ ${draftFilters.minRating}+`}
            onCycle={() => touchFilters((current) => ({
              ...current, minRating: current.minRating === null ? 1 : current.minRating >= 5 ? null : current.minRating + 1,
            }))}
          />
          <CycleRow
            label="GPS"
            value={draftFilters.hasGps === null ? L('Qualsiasi', 'Any')
              : draftFilters.hasGps ? L('Presente', 'Present') : L('Assente', 'Absent')}
            onCycle={() => touchFilters((current) => ({
              ...current, hasGps: current.hasGps === null ? true : current.hasGps ? false : null,
            }))}
          />
          <CycleRow
            label={L('Duplicati', 'Duplicates')}
            value={draftFilters.collapseDuplicates ? L('Raggruppati', 'Collapsed') : L('Mostrati', 'Shown')}
            onCycle={() => touchFilters((current) => ({
              ...current, collapseDuplicates: !current.collapseDuplicates,
            }))}
          />

          <Text style={styles.sectionTitle}>{L('Ordinamento', 'Ordering')}</Text>
          {draftFilters.semanticQuery.trim() !== '' ? (
            <Text style={styles.summaryLine}>{L('Rilevanza · ordine semantico', 'Relevance · semantic order')}</Text>
          ) : (
            <CycleRow
              label={L('Ordine', 'Order')}
              value={sortLabel(draftSort, L)}
              onCycle={() => touchSort(SORTS[(sortIndex + 1 + SORTS.length) % SORTS.length])}
            />
          )}
        </View>
      )}

      <View style={styles.actions}>
        <FocusableButton label={L('Azzera bozza', 'Reset draft')} onPress={resetDraft} />
        <FocusableButton label={L('Annulla', 'Cancel')} onPress={onCancel} />
        <FocusableButton
          label={L('Applica filtri', 'Apply filters')}
          onPress={apply}
          disabled={unresolved || !datesValid || applyPressed || interpreting}
        />
      </View>
    </PanelShell>
  );
}

function summarize(
  filters: GalleryFilters,
  sort: GallerySort,
  L: (it: string, en: string) => string,
): string[] {
  const lines: string[] = [];
  if (filters.semanticQuery.trim()) lines.push(`${L('Contenuto', 'Content')}: ${filters.semanticQuery.trim()}`);
  if (filters.q) lines.push(`${L('Testo', 'Text')}: ${filters.q}`);
  const included = peopleIds(filters, 'include').length;
  const excluded = peopleIds(filters, 'exclude').length;
  if (included || excluded) lines.push(L(`${included} incluse · ${excluded} escluse`, `${included} included · ${excluded} excluded`));
  if (filters.dateFrom || filters.dateTo) lines.push(`${L('Periodo', 'Period')}: ${filters.dateFrom || '…'} → ${filters.dateTo || '…'}`);
  if (filters.favorite === true) lines.push(L('Solo preferite', 'Favorites only'));
  if (filters.favorite === false) lines.push(L('Solo non preferite', 'Not favorites'));
  if (filters.minRating !== null) lines.push(`★ ${filters.minRating}+`);
  if (filters.hasGps !== null) lines.push(filters.hasGps ? L('GPS presente', 'GPS present') : L('GPS assente', 'GPS absent'));
  if (filters.collapseDuplicates) lines.push(L('Duplicati raggruppati', 'Duplicates collapsed'));
  lines.push(filters.semanticQuery.trim() ? L('Ordine: rilevanza', 'Order: relevance') : sortLabel(sort, L));
  return lines;
}

function sortLabel(sort: GallerySort, L: (it: string, en: string) => string): string {
  const field = sort.field === 'created' ? L('Aggiunta', 'Added')
    : sort.field === 'datetaken' ? L('Data scatto', 'Date taken')
      : sort.field === 'name' ? L('Nome', 'Name') : L('Dimensione', 'Size');
  return `${field} · ${sort.direction === 'asc' ? L('crescente', 'ascending') : L('decrescente', 'descending')}`;
}

const styles = StyleSheet.create({
  statusRow: { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center' },
  statusText: { color: colors.text, fontSize: font.body, fontWeight: '700' },
  draftText: { color: colors.accent, fontSize: font.body, fontWeight: '700' },
  modeRail: { flexDirection: 'row', justifyContent: 'center', gap: spacing.md, marginVertical: spacing.md },
  editor: { gap: spacing.sm },
  sectionTitle: { color: colors.text, fontSize: font.body, fontWeight: '800', marginTop: spacing.md },
  summaryLine: { color: colors.text, fontSize: font.body },
  error: { color: '#ff8f8f', fontSize: font.body, fontWeight: '700' },
  ambiguity: { gap: spacing.sm, paddingVertical: spacing.sm },
  choiceRow: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm },
  actions: { flexDirection: 'row', justifyContent: 'space-between', gap: spacing.md, marginTop: spacing.xl },
});
