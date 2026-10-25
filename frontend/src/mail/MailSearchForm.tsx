import { useEffect, useId, useState, type FormEvent } from 'react';
import { useI18n } from '../i18n';
import { Icon } from '../components/icons/Icon';
import { EMPTY_FILTERS, hasSearchCriteria, type MailFilters } from './mailQuery';
import type { MailArchive, MailFolder } from './types';

interface MailSearchFormProps {
  filters: MailFilters;
  onSubmit: (next: MailFilters) => void;
  // The documents view scopes by archive and folder in the form itself; the
  // mail view scopes by its sidebar, so it passes neither.
  archives?: readonly MailArchive[];
  folders?: readonly MailFolder[];
  // Asked for when the archive picked IN THE FORM changes, so the folder
  // choices follow it before the search is submitted.
  onArchiveDraftChange?: (archiveId: string | null) => void;
}

// The advanced criteria in force (the free text is in plain sight already).
export function countAdvancedFilters(f: MailFilters): number {
  return [f.from, f.to, f.subject, f.since, f.until, f.hasAttachments].filter(Boolean).length;
}

// Structured search: free text plus sender, recipients, subject, a date range,
// scope and "only with attachments". Editing is local; submitting writes the
// filters to the address bar (through `onSubmit`), which is what runs the
// search, so every search is linkable and Back returns to the previous one.
export function MailSearchForm({ filters, onSubmit, archives, folders, onArchiveDraftChange }: MailSearchFormProps) {
  const { t, tn } = useI18n();
  const uid = useId();
  const [draft, setDraft] = useState<MailFilters>(filters);
  const activeFilters = countAdvancedFilters(filters);
  const advancedSet = activeFilters > 0;
  const [advancedOpen, setAdvancedOpen] = useState(advancedSet);

  // A Back/Forward navigation changes the filters under the form.
  useEffect(() => { setDraft(filters); }, [filters]);
  useEffect(() => { if (advancedSet) setAdvancedOpen(true); }, [advancedSet]);

  const set = <K extends keyof MailFilters>(key: K, value: MailFilters[K]) =>
    setDraft((d) => ({ ...d, [key]: value }));

  function submit(e: FormEvent) {
    e.preventDefault();
    onSubmit({
      ...draft,
      q: draft.q.trim(),
      from: draft.from.trim(),
      to: draft.to.trim(),
      subject: draft.subject.trim(),
    });
  }

  function clear() {
    const next = { ...EMPTY_FILTERS, archiveId: filters.archiveId, folderId: filters.folderId };
    setDraft(next);
    onSubmit(next);
  }

  const showScope = archives !== undefined;
  const scopedFolders = folders ?? [];

  return (
    <form className="mail-search" role="search" aria-label={t('mail.search.label')} onSubmit={submit} data-testid="mail-search">
      <div className="mail-search-main">
        <label className="visually-hidden" htmlFor={`${uid}-q`}>{t('mail.search.q')}</label>
        <input
          id={`${uid}-q`}
          type="search"
          value={draft.q}
          placeholder={t('mail.search.qPlaceholder')}
          onChange={(e) => set('q', e.target.value)}
          maxLength={200}
        />
        <button type="submit" className="row-action-primary mail-search-submit">{t('common.search')}</button>
        <button
          type="button"
          className={advancedSet ? 'files-action mail-filters-toggle is-active' : 'files-action mail-filters-toggle'}
          aria-expanded={advancedOpen}
          aria-controls={`${uid}-advanced`}
          onClick={() => setAdvancedOpen((v) => !v)}
          data-testid="mail-filters-toggle"
        >
          <Icon name="filter" />
          <span>{t('mail.search.advanced')}</span>
          {advancedSet && (
            <span className="mail-filters-badge" data-testid="mail-filters-badge">
              <span aria-hidden="true">{activeFilters}</span>
              <span className="visually-hidden">, {tn(activeFilters, 'mail.search.filtersActive')}</span>
            </span>
          )}
        </button>
        {hasSearchCriteria(filters) && (
          <button type="button" className="files-action" onClick={clear}>{t('mail.search.clear')}</button>
        )}
      </div>

      <div id={`${uid}-advanced`} className="mail-search-advanced" hidden={!advancedOpen}>
        <label className="mail-field">
          <span>{t('mail.search.from')}</span>
          <input type="text" value={draft.from} maxLength={200} onChange={(e) => set('from', e.target.value)} />
        </label>
        <label className="mail-field">
          <span>{t('mail.search.to')}</span>
          <input type="text" value={draft.to} maxLength={200} onChange={(e) => set('to', e.target.value)} />
        </label>
        <label className="mail-field">
          <span>{t('mail.search.subject')}</span>
          <input type="text" value={draft.subject} maxLength={200} onChange={(e) => set('subject', e.target.value)} />
        </label>
        <label className="mail-field">
          <span>{t('mail.search.since')}</span>
          <input type="date" value={draft.since} onChange={(e) => set('since', e.target.value)} />
        </label>
        <label className="mail-field">
          <span>{t('mail.search.until')}</span>
          <input type="date" value={draft.until} min={draft.since || undefined} onChange={(e) => set('until', e.target.value)} />
        </label>
        {showScope && (
          <label className="mail-field">
            <span>{t('mail.search.archive')}</span>
            <select
              value={draft.archiveId ?? ''}
              onChange={(e) => {
                const archiveId = e.target.value || null;
                setDraft((d) => ({ ...d, archiveId, folderId: null }));
                onArchiveDraftChange?.(archiveId);
              }}
            >
              <option value="">{t('mail.search.allArchives')}</option>
              {archives.map((a) => <option key={a.id} value={a.id}>{a.displayName}</option>)}
            </select>
          </label>
        )}
        {showScope && (
          <label className="mail-field">
            <span>{t('mail.search.folder')}</span>
            <select
              value={draft.folderId ?? ''}
              disabled={!draft.archiveId || scopedFolders.length === 0}
              onChange={(e) => set('folderId', e.target.value || null)}
            >
              <option value="">{t('mail.search.allFolders')}</option>
              {scopedFolders.map((f) => (
                <option key={f.id} value={f.id}>{`${' '.repeat(Math.max(0, f.depth))}${f.name}`}</option>
              ))}
            </select>
          </label>
        )}
        <label className="mail-field mail-field-check">
          <input
            type="checkbox"
            checked={draft.hasAttachments}
            onChange={(e) => set('hasAttachments', e.target.checked)}
          />
          <span>{t('mail.search.onlyAttachments')}</span>
        </label>
      </div>
    </form>
  );
}
