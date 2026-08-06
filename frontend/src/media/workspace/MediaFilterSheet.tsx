import { useEffect, useRef, useState, type KeyboardEvent } from 'react';
import type { Person } from '@nubarca/api-client';
import { useI18n } from '../../i18n';
import { PeopleCombobox } from '../../gallery/workspace/PeopleCombobox';
import {
  dateInputToIso,
  isoToDateInput,
  type MediaKindScope,
  type MediaWorkspaceFilters,
  type PeopleMode,
} from './mediaWorkspaceQuery';

// One adaptive filter sheet for all three tabs. The Common section is always
// shown; the Photo section only on kind=image and the Video section only on
// kind=video — so incompatible controls are never rendered (let alone applied).
// The draft is separate from the applied filters: only Apply commits. Escape /
// Cancel discard; Reset clears the sections shown. Focus is trapped while open
// and restored to the trigger on close.

type Tri = boolean | null;

interface Props {
  open: boolean;
  mediaKind: MediaKindScope;
  applied: MediaWorkspaceFilters;
  // Owner-private people for the Foto-tab People filter combobox.
  people: Person[];
  // VSEM-03: whether visual (semantic) search applies to the current tab +
  // source. False inside an album on the "Tutti"/"Video" tabs, where the
  // unified semantic endpoint has no album scope — the control is then not
  // rendered at all rather than accepted and silently ignored.
  showVisualQuery?: boolean;
  onApply(next: MediaWorkspaceFilters): void;
  onClose(): void;
  returnFocusRef?: React.RefObject<HTMLButtonElement | null>;
}

function triValue(v: Tri): 'any' | 'yes' | 'no' {
  return v === true ? 'yes' : v === false ? 'no' : 'any';
}
function triParse(v: string): Tri {
  return v === 'yes' ? true : v === 'no' ? false : null;
}

export function MediaFilterSheet({
  open, mediaKind, applied, people, showVisualQuery = true, onApply, onClose, returnFocusRef,
}: Props) {
  const { t } = useI18n();
  const [draft, setDraft] = useState<MediaWorkspaceFilters>(applied);
  const dialogRef = useRef<HTMLDivElement>(null);

  useEffect(() => { if (open) setDraft(applied); }, [open, applied]);

  // Focus the first control on open; restore focus to the trigger on close.
  useEffect(() => {
    if (open) {
      const first = dialogRef.current?.querySelector<HTMLElement>('input, select, button');
      first?.focus();
    } else {
      returnFocusRef?.current?.focus();
    }
  }, [open, returnFocusRef]);

  if (!open) return null;

  const showPhoto = mediaKind === 'image';
  const showVideo = mediaKind === 'video';

  function onKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    if (e.key === 'Escape') { e.stopPropagation(); onClose(); return; }
    if (e.key !== 'Tab') return;
    // Minimal focus trap: keep Tab inside the dialog.
    const focusables = dialogRef.current?.querySelectorAll<HTMLElement>(
      'input, select, button, [tabindex]:not([tabindex="-1"])',
    );
    if (!focusables || focusables.length === 0) return;
    const list = Array.from(focusables);
    const first = list[0];
    const last = list[list.length - 1];
    if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
  }

  const setCommon = <K extends keyof MediaWorkspaceFilters['common']>(
    key: K, value: MediaWorkspaceFilters['common'][K],
  ) => setDraft((d) => ({ ...d, common: { ...d.common, [key]: value } }));
  const setPhoto = <K extends keyof MediaWorkspaceFilters['photo']>(
    key: K, value: MediaWorkspaceFilters['photo'][K],
  ) => setDraft((d) => ({ ...d, photo: { ...d.photo, [key]: value } }));
  const setVideo = <K extends keyof MediaWorkspaceFilters['video']>(
    key: K, value: MediaWorkspaceFilters['video'][K],
  ) => setDraft((d) => ({ ...d, video: { ...d.video, [key]: value } }));

  function reset() {
    setDraft((d) => ({
      common: { ...d.common, metadataQuery: '', favorite: null, minRating: null, dateTakenFrom: '', dateTakenTo: '' },
      photo: showPhoto
        ? {
            ...d.photo, visualQuery: '', semanticTopK: 0, hasGps: null,
            collapseDuplicates: false, similarTo: '', includePeople: [], excludePeople: [],
            includePeopleMode: 'all' as PeopleMode,
          }
        // The visual query lives in the Common section on every tab (VSEM-03),
        // so Reset clears it everywhere; other photo filters stay retained.
        : { ...d.photo, visualQuery: '', semanticTopK: 0 },
      video: showVideo
        ? { ...d.video, durationMinSeconds: null, durationMaxSeconds: null, minHeight: null, codec: '', hasAudio: null }
        : d.video,
    }));
  }

  return (
    <div
      className="ws-sheet-backdrop"
      data-testid="media-filter-sheet-backdrop"
      onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}
    >
      <div
        ref={dialogRef}
        className="ws-sheet"
        role="dialog"
        aria-modal="true"
        aria-label={t('mediaFilter.title')}
        data-testid="media-filter-sheet"
        onKeyDown={onKeyDown}
      >
        <header className="ws-sheet-head">
          <h2 className="ws-sheet-title">{t('mediaFilter.title')}</h2>
          <button type="button" className="ws-icon-button" aria-label={t('albumSettings.close')} onClick={onClose}>✕</button>
        </header>

        <div className="ws-sheet-body">
        <fieldset className="ws-filter-section">
          <legend>{t('mediaFilter.sectionCommon')}</legend>
          <label>
            {t('mediaFilter.metadata')}
            <input
              type="search"
              data-testid="filter-metadata"
              value={draft.common.metadataQuery}
              onChange={(e) => setCommon('metadataQuery', e.target.value)}
            />
          </label>
          {/* Visual (semantic) search — VSEM-03: applies on every tab where
              the workspace can route it (photos via the dedicated photo path,
              "Tutti"/"Video" via the unified /api/media/semantic). Distinct
              label + placeholder so it is not confused with the metadata
              search above. */}
          {showVisualQuery && (
            <label>
              {t('gallery.ws.visualContent')}
              <input
                type="search"
                data-testid="filter-visual"
                placeholder={t('gallery.ws.visualPlaceholder')}
                value={draft.photo.visualQuery}
                onChange={(e) => setPhoto('visualQuery', e.target.value)}
              />
              <span className="ws-help">{t('gallery.ws.visualHelp')}</span>
            </label>
          )}
          <label>
            <input
              type="checkbox"
              data-testid="filter-favorite"
              checked={draft.common.favorite === true}
              onChange={(e) => setCommon('favorite', e.target.checked ? true : null)}
            />
            {t('mediaFilter.favorite')}
          </label>
          <label>
            {t('mediaFilter.minRating')}
            <select
              data-testid="filter-min-rating"
              value={draft.common.minRating ?? 0}
              onChange={(e) => setCommon('minRating', Number(e.target.value) || null)}
            >
              {[0, 1, 2, 3, 4, 5].map((r) => <option key={r} value={r}>{r === 0 ? t('mediaFilter.any') : `≥ ${r}`}</option>)}
            </select>
          </label>
          <label>
            {t('mediaFilter.dateFrom')}
            <input
              type="date"
              data-testid="filter-date-from"
              value={isoToDateInput(draft.common.dateTakenFrom)}
              onChange={(e) => setCommon('dateTakenFrom', dateInputToIso(e.target.value))}
            />
          </label>
          <label>
            {t('mediaFilter.dateTo')}
            <input
              type="date"
              data-testid="filter-date-to"
              value={isoToDateInput(draft.common.dateTakenTo)}
              onChange={(e) => setCommon('dateTakenTo', dateInputToIso(e.target.value))}
            />
          </label>
        </fieldset>

        {showPhoto && (
          <fieldset className="ws-filter-section" data-testid="filter-section-photo">
            <legend>{t('mediaFilter.sectionPhoto')}</legend>
            <label>
              {t('mediaFilter.hasGps')}
              <select
                data-testid="filter-has-gps"
                value={triValue(draft.photo.hasGps)}
                onChange={(e) => setPhoto('hasGps', triParse(e.target.value))}
              >
                <option value="any">{t('mediaFilter.any')}</option>
                <option value="yes">{t('mediaFilter.hasGps')}</option>
                <option value="no">—</option>
              </select>
            </label>
            <label>
              <input
                type="checkbox"
                data-testid="filter-collapse"
                checked={draft.photo.collapseDuplicates}
                onChange={(e) => setPhoto('collapseDuplicates', e.target.checked)}
              />
              {t('mediaFilter.collapseDuplicates')}
            </label>

            {/* People filter — owner-private, reuses the gallery combobox. */}
            <div className="ws-filter-people" data-testid="filter-people">
              <span className="ws-field-label">{t('mediaFilter.sectionPeople')}</span>
              <PeopleCombobox
                label={t('gallery.ws.peopleInclude')}
                variant="include"
                people={people}
                selected={draft.photo.includePeople}
                otherGroup={draft.photo.excludePeople}
                onAdd={(id) => setPhoto('includePeople', [...draft.photo.includePeople, id])}
                onRemove={(id) => setPhoto('includePeople', draft.photo.includePeople.filter((x) => x !== id))}
              />
              {draft.photo.includePeople.length > 1 && (
                <div className="ws-radio-group" role="radiogroup" aria-label={t('peopleFilter.modeLabel')} data-testid="filter-people-mode">
                  {(['all', 'any'] as PeopleMode[]).map((mode) => (
                    <label key={mode}>
                      <input
                        type="radio"
                        name="media-people-mode"
                        checked={draft.photo.includePeopleMode === mode}
                        onChange={() => setPhoto('includePeopleMode', mode)}
                      />
                      <span>{t(mode === 'all' ? 'gallery.ws.peopleMatchAll' : 'gallery.ws.peopleMatchAny')}</span>
                    </label>
                  ))}
                </div>
              )}
              <PeopleCombobox
                label={t('gallery.ws.peopleExclude')}
                variant="exclude"
                people={people}
                selected={draft.photo.excludePeople}
                otherGroup={draft.photo.includePeople}
                onAdd={(id) => setPhoto('excludePeople', [...draft.photo.excludePeople, id])}
                onRemove={(id) => setPhoto('excludePeople', draft.photo.excludePeople.filter((x) => x !== id))}
              />
            </div>

            {/* Similarity anchor: read-only status + a remove control. It is set
                from a real image (viewer action), never a pasted id. */}
            {draft.photo.similarTo.length > 0 && (
              <div className="ws-filter-similar" data-testid="filter-similar">
                <span>{t('mediaFilter.similarActive')}</span>
                <button
                  type="button"
                  className="row-action"
                  data-testid="filter-remove-similar"
                  onClick={() => setPhoto('similarTo', '')}
                >
                  {t('mediaFilter.removeSimilar')}
                </button>
              </div>
            )}
          </fieldset>
        )}

        {showVideo && (
          <fieldset className="ws-filter-section" data-testid="filter-section-video">
            <legend>{t('mediaFilter.sectionVideo')}</legend>
            <label>
              {t('mediaFilter.durationMin')}
              <input
                type="number"
                min={0}
                data-testid="filter-duration-min"
                value={draft.video.durationMinSeconds ?? ''}
                onChange={(e) => setVideo('durationMinSeconds', e.target.value === '' ? null : Number(e.target.value))}
              />
            </label>
            <label>
              {t('mediaFilter.durationMax')}
              <input
                type="number"
                min={0}
                data-testid="filter-duration-max"
                value={draft.video.durationMaxSeconds ?? ''}
                onChange={(e) => setVideo('durationMaxSeconds', e.target.value === '' ? null : Number(e.target.value))}
              />
            </label>
            <label>
              {t('mediaFilter.minHeight')}
              <select
                data-testid="filter-min-height"
                value={draft.video.minHeight ?? 0}
                onChange={(e) => setVideo('minHeight', Number(e.target.value) || null)}
              >
                <option value={0}>{t('mediaFilter.any')}</option>
                <option value={720}>720p</option>
                <option value={1080}>1080p</option>
                <option value={2160}>2160p</option>
              </select>
            </label>
            <label>
              {t('mediaFilter.codec')}
              <input
                type="text"
                data-testid="filter-codec"
                value={draft.video.codec}
                onChange={(e) => setVideo('codec', e.target.value)}
              />
            </label>
            <label>
              {t('mediaFilter.hasAudio')}
              <select
                data-testid="filter-has-audio"
                value={triValue(draft.video.hasAudio)}
                onChange={(e) => setVideo('hasAudio', triParse(e.target.value))}
              >
                <option value="any">{t('mediaFilter.any')}</option>
                <option value="yes">{t('mediaFilter.hasAudio')}</option>
                <option value="no">—</option>
              </select>
            </label>
          </fieldset>
        )}
        </div>

        <footer className="ws-sheet-foot">
          <button type="button" className="row-action" data-testid="filter-reset" onClick={reset}>{t('mediaFilter.reset')}</button>
          <div className="ws-sheet-foot-right">
            <button type="button" className="row-action" data-testid="filter-cancel" onClick={onClose}>{t('mediaFilter.cancel')}</button>
            <button
              type="button"
              className="row-action-primary"
              data-testid="filter-apply"
              onClick={() => onApply(draft)}
            >
              {t('mediaFilter.apply')}
            </button>
          </div>
        </footer>
      </div>
    </div>
  );
}
