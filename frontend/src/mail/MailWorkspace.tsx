import {
  useEffect, useId, useLayoutEffect, useMemo, useRef, useState, type ReactNode, type RefObject,
} from 'react';
import { useI18n, type MessageKey } from '../i18n';
import { Icon } from '../components/icons/Icon';
import { useAppScrollViewport } from '../components/appScroll';
import { MailSplitter } from './MailSplitter';
import {
  DEFAULT_MAIL_LAYOUT, FOLDERS_MIN, LIST_MIN, LIST_MIN_HEIGHT, MAIL_LAYOUT_MODES, SPLITTER_PX,
  resolveMailLayout, useMailLayout, type MailLayoutMode, type WorkspaceSize,
} from './useMailLayout';

export type MailListPresentation = 'compact' | 'table';

interface MailWorkspaceProps {
  // The archive picker and the folder tree.
  folders: ReactNode;
  search: ReactNode;
  // Rendered for the presentation the layout calls for. Keep it stable
  // (useCallback): it is what spares the list a re-render on every drag step.
  renderList: (presentation: MailListPresentation) => ReactNode;
  reader: ReactNode | null;
  // The open message, or null. A new one starts the reader at its top.
  readerKey: string | null;
  // The archive and folder on screen: changing it closes the folder overlay.
  scopeKey: string;
}

const FOCUSABLE = 'select, button, input, a[href], [tabindex]:not([tabindex="-1"])';

const MODE_LABELS: Record<MailLayoutMode, MessageKey> = {
  right: 'mail.layout.right',
  bottom: 'mail.layout.bottom',
  list: 'mail.layout.list',
};

// The mail application: folders, message list and reader in one of three
// arrangements, filling exactly the room the NubArca shell gives the page.
//
// Every pane scrolls on its own and nothing inside one can grow the workspace:
// the panes have definite sizes (a docked width, a dragged width or height,
// the rest by flex with zero minimums) and are each their own scroll
// container. While the workspace is mounted it also tells `.app-main` to stop
// being the page's scroller (`app-main--mail`), so a long list or a long
// message never turns the page into a long document. Documents and Imports
// never mount it and keep the ordinary scrolling page.
//
// Sizes follow the workspace's own measured box, not the window, because the
// NubArca navigation beside it can be wide, a rail, or gone.
export function MailWorkspace({ folders, search, renderList, reader, readerKey, scopeKey }: MailWorkspaceProps) {
  const { t } = useI18n();
  const uid = useId();
  const ids = { folders: `${uid}-folders`, list: `${uid}-list`, reader: `${uid}-reader` };
  const controls = useMailLayout();
  const { prefs } = controls;
  const rootRef = useRef<HTMLDivElement>(null);
  const bodyRef = useRef<HTMLDivElement>(null);
  const foldersRef = useRef<HTMLElement>(null);
  const listRef = useRef<HTMLElement>(null);
  const readerRef = useRef<HTMLElement>(null);
  const toggleRef = useRef<HTMLButtonElement>(null);
  const size = useWorkspaceSize(rootRef, bodyRef);
  const layout = resolveMailLayout(prefs, size);
  const { mode } = layout;
  const [resizing, setResizing] = useState(false);
  const [overlayOpen, setOverlayOpen] = useState(false);
  const hasReader = reader !== null;

  useMailViewportFill();

  // The folder overlay is a way to pick a folder: picking one closes it.
  useEffect(() => { setOverlayOpen(false); }, [scopeKey]);

  const foldersOverlay = !layout.foldersDocked && overlayOpen;
  const foldersVisible = layout.foldersDocked || foldersOverlay;

  function toggleFolders() {
    if (layout.foldersCanDock) controls.setFoldersDocked(mode, !layout.foldersDocked);
    else setOverlayOpen((open) => !open);
  }

  function closeOverlay() {
    setOverlayOpen(false);
  }

  // The overlay is modal: while it is open the rest of the workspace is inert
  // (the scrim already blocks the pointer; this blocks the keyboard too), focus
  // starts inside it, Escape closes it wherever focus is, and closing hands
  // focus back to the toggle that opened it.
  const overlayWasOpen = useRef(false);
  useEffect(() => {
    if (!foldersOverlay) {
      if (overlayWasOpen.current) toggleRef.current?.focus({ preventScroll: true });
      overlayWasOpen.current = false;
      return;
    }
    overlayWasOpen.current = true;
    const focusables = () => Array.from(foldersRef.current?.querySelectorAll<HTMLElement>(FOCUSABLE) ?? [])
      .filter((el) => !el.hasAttribute('disabled'));
    (focusables()[0] ?? foldersRef.current)?.focus({ preventScroll: true });
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.preventDefault();
        setOverlayOpen(false);
        return;
      }
      // Tab cycles inside the panel: the page around the workspace (its tabs,
      // the NubArca navigation) is not inert, so it is kept out here.
      if (e.key !== 'Tab') return;
      const items = focusables();
      if (items.length === 0) { e.preventDefault(); return; }
      const first = items[0];
      const last = items[items.length - 1];
      const active = document.activeElement;
      const inside = active instanceof HTMLElement && foldersRef.current?.contains(active);
      if (!inside || (e.shiftKey && active === first) || (!e.shiftKey && active === last)) {
        e.preventDefault();
        (e.shiftKey ? last : first).focus({ preventScroll: true });
      }
    };
    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
  }, [foldersOverlay]);

  // "Solo elenco" shows the list OR the message. A hidden box keeps no scroll
  // offset, so the list's is kept here and put back when it returns.
  const listHidden = mode === 'list' && hasReader;
  const readerHidden = mode === 'list' && !hasReader;
  const listScroll = useRef(0);
  useLayoutEffect(() => {
    if (!listHidden && listRef.current) listRef.current.scrollTop = listScroll.current;
  }, [listHidden]);

  // Each message starts at its top; the list keeps its place.
  useLayoutEffect(() => {
    if (readerRef.current) readerRef.current.scrollTop = 0;
  }, [readerKey]);

  const presentation: MailListPresentation = layout.table ? 'table' : 'compact';
  const listNode = useMemo(() => renderList(presentation), [renderList, presentation]);

  const usableHeight = (size?.bodyHeight ?? 0) - SPLITTER_PX;
  const paneStyle = mode === 'right'
    ? { width: layout.listWidth }
    : mode === 'bottom' ? { height: layout.listHeight } : undefined;

  const className = [
    'mail-workspace',
    `is-${mode}`,
    layout.compact ? 'is-compact' : '',
    resizing ? 'is-resizing' : '',
    hasReader ? 'has-reader' : '',
  ].filter(Boolean).join(' ');

  return (
    <div ref={rootRef} className={className} data-testid="mail-browser" data-layout={mode}>
      <div className="mail-toolbar" inert={foldersOverlay} data-testid="mail-toolbar">
        <button
          ref={toggleRef}
          type="button"
          className="files-action mail-folders-toggle"
          aria-expanded={foldersVisible}
          aria-controls={ids.folders}
          onClick={toggleFolders}
          data-testid="mail-folders-toggle"
        >
          <Icon name="archive" />
          <span>{foldersVisible ? t('mail.folders.hide') : t('mail.folders.show')}</span>
        </button>
        <div className="mail-toolbar-search">{search}</div>
        <LayoutSwitch
          name={`${uid}-layout`}
          value={mode}
          available={layout.available}
          onChange={controls.setMode}
        />
      </div>

      <div className="mail-workspace-body" ref={bodyRef}>
        {foldersOverlay && <div className="mail-folders-scrim" aria-hidden="true" onClick={closeOverlay} />}
        <aside
          ref={foldersRef}
          id={ids.folders}
          className={foldersOverlay ? 'mail-folders-pane is-overlay' : 'mail-folders-pane'}
          aria-label={t('mail.sidebar.label')}
          role={foldersOverlay ? 'dialog' : undefined}
          aria-modal={foldersOverlay ? true : undefined}
          tabIndex={foldersOverlay ? -1 : undefined}
          hidden={!foldersVisible}
          style={layout.foldersDocked ? { width: layout.folderWidth } : undefined}
          data-testid="mail-folders-pane"
        >
          {folders}
        </aside>
        {layout.foldersDocked && (
          <MailSplitter
            orientation="vertical"
            value={layout.folderWidth}
            min={FOLDERS_MIN}
            max={layout.folderMax}
            defaultValue={DEFAULT_MAIL_LAYOUT.folderWidth}
            onChange={controls.setFolderWidth}
            onDragChange={setResizing}
            label={t('mail.splitter.folders')}
            controls={ids.folders}
            testId="mail-splitter-folders"
          />
        )}

        <div className="mail-panes" inert={foldersOverlay} data-testid="mail-panes">
          <section
            id={ids.list}
            ref={listRef}
            className="mail-list-pane"
            aria-label={t('mail.list.paneLabel')}
            hidden={listHidden}
            style={paneStyle}
            onScroll={(e) => { if (!listHidden) listScroll.current = e.currentTarget.scrollTop; }}
            data-mail-scroll=""
            data-testid="mail-list-pane"
          >
            {listNode}
          </section>
          {mode === 'right' && (
            <MailSplitter
              orientation="vertical"
              value={layout.listWidth}
              min={LIST_MIN}
              max={layout.listMax}
              defaultValue={DEFAULT_MAIL_LAYOUT.listWidth}
              onChange={controls.setListWidth}
              onDragChange={setResizing}
              label={t('mail.splitter.list')}
              controls={ids.list}
              testId="mail-splitter-list"
            />
          )}
          {mode === 'bottom' && (
            <MailSplitter
              orientation="horizontal"
              value={layout.listHeight}
              min={LIST_MIN_HEIGHT}
              max={layout.listHeightMax}
              defaultValue={Math.round(DEFAULT_MAIL_LAYOUT.listRatio * usableHeight)}
              onChange={(px) => controls.setListHeight(px, usableHeight)}
              onDragChange={setResizing}
              label={t('mail.splitter.list')}
              controls={ids.list}
              testId="mail-splitter-list"
            />
          )}
          <section
            id={ids.reader}
            ref={readerRef}
            className="mail-reader-pane"
            aria-label={t('mail.reader.paneLabel')}
            hidden={readerHidden}
            data-testid="mail-reader-pane"
          >
            {reader ?? (
              <div className="mail-reader-placeholder" data-testid="mail-reader-placeholder">
                <Icon name="mail" size={40} />
                <p>{t('mail.reader.placeholder')}</p>
                <p className="muted">{t('mail.reader.placeholderHint')}</p>
              </div>
            )}
          </section>
        </div>
      </div>
    </div>
  );
}

function LayoutSwitch({ name, value, available, onChange }: {
  name: string;
  value: MailLayoutMode;
  available: Record<MailLayoutMode, boolean>;
  onChange: (mode: MailLayoutMode) => void;
}) {
  const { t } = useI18n();
  return (
    <fieldset className="mail-layout-switch" data-testid="mail-layout-switch">
      <legend className="visually-hidden">{t('mail.layout.label')}</legend>
      {MAIL_LAYOUT_MODES.map((m) => {
        const label = t(MODE_LABELS[m]);
        return (
          <label
            key={m}
            className={m === value ? 'mail-layout-option is-active' : 'mail-layout-option'}
            title={available[m] ? label : `${label} — ${t('mail.layout.unavailable')}`}
          >
            <input
              type="radio"
              name={name}
              value={m}
              checked={m === value}
              disabled={!available[m]}
              onChange={() => onChange(m)}
              className="visually-hidden"
            />
            <LayoutGlyph mode={m} />
            <span className="visually-hidden">{label}</span>
          </label>
        );
      })}
    </fieldset>
  );
}

function LayoutGlyph({ mode }: { mode: MailLayoutMode }) {
  return (
    <svg viewBox="0 0 20 16" width="20" height="16" aria-hidden="true" focusable="false" className="mail-layout-glyph">
      <rect x="1" y="1" width="18" height="14" rx="2" fill="none" stroke="currentColor" strokeWidth="1.5" />
      {mode === 'right' && <path d="M8 1.5v13" stroke="currentColor" strokeWidth="1.5" />}
      {mode === 'right' && <path d="M3 5h3M3 8h3M3 11h3" stroke="currentColor" strokeWidth="1.2" />}
      {mode === 'bottom' && <path d="M1.5 8h17" stroke="currentColor" strokeWidth="1.5" />}
      {mode === 'bottom' && <path d="M4 4h12" stroke="currentColor" strokeWidth="1.2" />}
      {mode === 'list' && <path d="M4 5h12M4 8h12M4 11h12" stroke="currentColor" strokeWidth="1.2" />}
    </svg>
  );
}

// The workspace's own box (which layouts fit) and the panes' row under its
// toolbar (what they share), kept current as the window, the NubArca
// navigation or the toolbar change. Null until measured (and where it cannot
// be: jsdom), which the layout reads as "plenty of room".
function useWorkspaceSize(
  rootRef: RefObject<HTMLElement | null>,
  bodyRef: RefObject<HTMLElement | null>,
): WorkspaceSize | null {
  const [size, setSize] = useState<WorkspaceSize | null>(null);
  useLayoutEffect(() => {
    const root = rootRef.current;
    const body = bodyRef.current;
    if (!root || !body) return;
    const measure = () => {
      const width = Math.round(root.clientWidth);
      const height = Math.round(root.clientHeight);
      const bodyHeight = Math.round(body.clientHeight);
      setSize((prev) => (prev && prev.width === width && prev.height === height && prev.bodyHeight === bodyHeight
        ? prev
        : { width, height, bodyHeight }));
    };
    measure();
    if (typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(measure);
    ro.observe(root);
    ro.observe(body);
    return () => ro.disconnect();
  }, [rootRef, bodyRef]);
  return size;
}

// While the workspace is on screen, `.app-main` lays the page out to its own
// height instead of scrolling it (mail.css, `.app-main--mail`).
function useMailViewportFill() {
  const viewport = useAppScrollViewport();
  useLayoutEffect(() => {
    const main = viewport?.current;
    if (!main) return;
    main.classList.add('app-main--mail');
    main.scrollTop = 0;
    return () => main.classList.remove('app-main--mail');
  }, [viewport]);
}

// Focus an element inside one of the workspace's panes and bring it into that
// pane's view — without letting the browser scroll anything else (`.app-main`,
// the other panes) to get there.
export function focusInPane(el: HTMLElement): void {
  el.focus({ preventScroll: true });
  const pane = el.closest<HTMLElement>('[data-mail-scroll]');
  if (!pane) return;
  const box = pane.getBoundingClientRect();
  const r = el.getBoundingClientRect();
  // A sticky table header covers the top of the pane.
  const head = pane.querySelector<HTMLElement>('.mail-list-head');
  const top = box.top + (head ? head.getBoundingClientRect().height : 0);
  if (r.top < top) pane.scrollTop -= top - r.top;
  else if (r.bottom > box.bottom) pane.scrollTop += r.bottom - box.bottom;
}
