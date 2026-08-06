import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { Person } from '@nubarca/api-client';
import { useI18n } from '../../i18n';
import { EMPTY_GALLERY_QUERY, type GalleryQuery } from '../galleryQuery';
import { ManualFilterEditor } from './ManualFilterEditor';
import { NaturalFilterEditor } from './NaturalFilterEditor';

type Tab = 'describe' | 'manual';

// One unified filter editor. Opening copies the APPLIED query into an isolated
// draft; edits touch only the draft; Apply commits it, Cancel/Escape discards
// it. Both the Describe (NL) and Manual tabs edit the SAME draft. Focus is
// trapped while open and restored to the trigger on close.
interface Props {
  open: boolean;
  appliedQuery: GalleryQuery;
  people: Person[];
  onApply(next: GalleryQuery): void;
  onClose(): void;
  returnFocusRef: React.RefObject<HTMLElement | null>;
  announce(message: string): void;
  // The header NL input opens the sheet directly in Describe mode with the typed
  // text prefilled (no separate command workflow lives outside the sheet).
  initialTab?: Tab;
  initialCommand?: string;
}

export function GalleryFilterSheet({
  open,
  appliedQuery,
  people,
  onApply,
  onClose,
  returnFocusRef,
  announce,
  initialTab,
  initialCommand,
}: Props) {
  const { t } = useI18n();
  const [tab, setTab] = useState<Tab>('manual');
  const [draft, setDraft] = useState<GalleryQuery>(appliedQuery);
  const [unresolved, setUnresolved] = useState(false);
  // Monotonic count of MANUAL draft edits — the NL editor captures it before an
  // interpret request and re-checks it on response so a stale (late) response
  // can never clobber a newer manual edit.
  const editSeqRef = useRef(0);
  const dialogRef = useRef<HTMLDivElement>(null);

  // Opening: copy applied → draft, reset to a clean state. Reopening always
  // shows the currently applied values (never a stale prior draft).
  useEffect(() => {
    if (open) {
      setDraft(appliedQuery);
      setTab(initialTab ?? 'manual');
      setUnresolved(false);
      editSeqRef.current = 0;
    }
  }, [open, appliedQuery, initialTab]);

  // Focus trap + initial focus + restore-on-close.
  useEffect(() => {
    if (!open) return;
    const node = dialogRef.current;
    const previous = returnFocusRef.current;
    // Focus the first focusable control in the sheet.
    const focusables = () =>
      node
        ? Array.from(
            node.querySelectorAll<HTMLElement>(
              'a[href], button:not([disabled]), textarea, input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])',
            ),
          ).filter((el) => el.offsetParent !== null || el === document.activeElement)
        : [];
    const first = focusables()[0];
    first?.focus();

    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') {
        e.stopPropagation();
        onClose();
        return;
      }
      if (e.key !== 'Tab') return;
      const items = focusables();
      if (items.length === 0) return;
      const firstEl = items[0];
      const lastEl = items[items.length - 1];
      if (e.shiftKey && document.activeElement === firstEl) {
        e.preventDefault();
        lastEl.focus();
      } else if (!e.shiftKey && document.activeElement === lastEl) {
        e.preventDefault();
        firstEl.focus();
      }
    }
    node?.addEventListener('keydown', onKeyDown);
    return () => {
      node?.removeEventListener('keydown', onKeyDown);
      // Restore focus to the trigger (Filters button).
      previous?.focus();
    };
  }, [open, onClose, returnFocusRef]);

  const updateDraftManual = useCallback((next: GalleryQuery) => {
    editSeqRef.current += 1;
    setDraft(next);
  }, []);

  const getSeq = useCallback(() => editSeqRef.current, []);

  const applyDraft = () => {
    if (unresolved) return;
    onApply(draft);
    announce(t('gallery.ws.sr.filtersApplied'));
  };

  const resetDraft = () => {
    editSeqRef.current += 1;
    setDraft({ ...EMPTY_GALLERY_QUERY, limit: draft.limit });
    setUnresolved(false);
  };

  const tabs = useMemo(
    () => [
      { id: 'describe' as Tab, label: t('gallery.ws.tabDescribe') },
      { id: 'manual' as Tab, label: t('gallery.ws.tabManual') },
    ],
    [t],
  );

  if (!open) return null;

  return (
    <div className="ws-sheet-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div
        ref={dialogRef}
        className="ws-sheet"
        role="dialog"
        aria-modal="true"
        aria-label={t('gallery.ws.sheetAria')}
        data-testid="gallery-filter-sheet"
      >
        <header className="ws-sheet-head">
          <h2 className="ws-sheet-title">{t('gallery.ws.filtersTitle')}</h2>
          <button type="button" className="ws-icon-button" aria-label={t('common.close')} onClick={onClose}>
            ×
          </button>
        </header>

        <div className="ws-tabs" role="tablist" aria-label={t('gallery.ws.filtersTitle')}>
          {tabs.map((tabDef) => (
            <button
              key={tabDef.id}
              type="button"
              role="tab"
              aria-selected={tab === tabDef.id}
              className={`ws-tab${tab === tabDef.id ? ' is-active' : ''}`}
              data-testid={`ws-tab-${tabDef.id}`}
              onClick={() => setTab(tabDef.id)}
            >
              {tabDef.label}
            </button>
          ))}
        </div>

        <div className="ws-sheet-body">
          {tab === 'describe' ? (
            <NaturalFilterEditor
              draft={draft}
              initialCommand={initialCommand}
              onResolvedDraft={(next) => setDraft(next)}
              onAddPerson={(mode, personId) =>
                setDraft((prev) =>
                  mode === 'include'
                    ? { ...prev, includePeople: [...prev.includePeople, personId] }
                    : { ...prev, excludePeople: [...prev.excludePeople, personId] },
                )
              }
              getSeq={getSeq}
              onUnresolvedChange={setUnresolved}
              onSwitchToManual={() => setTab('manual')}
              announce={announce}
            />
          ) : (
            <ManualFilterEditor draft={draft} onChange={updateDraftManual} people={people} />
          )}
        </div>

        <footer className="ws-sheet-foot">
          <button type="button" className="row-action" data-testid="ws-reset" onClick={resetDraft}>
            {t('gallery.ws.resetDraft')}
          </button>
          <div className="ws-sheet-foot-right">
            <button type="button" className="row-action" data-testid="ws-cancel" onClick={onClose}>
              {t('common.cancel')}
            </button>
            <button
              type="button"
              className="row-action-primary"
              data-testid="ws-apply"
              disabled={unresolved}
              onClick={applyDraft}
            >
              {t('gallery.ws.apply')}
            </button>
          </div>
        </footer>
      </div>
    </div>
  );
}
