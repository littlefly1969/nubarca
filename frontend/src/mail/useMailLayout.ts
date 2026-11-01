import { useCallback, useState } from 'react';
import { readStoredItem } from '../storage/brandedStorageKey';

// The mail workspace's panel arrangement: which of the three layouts the owner
// prefers, and the sizes they dragged the dividers to.
//
// These are PREFERENCES, not the layout itself. `resolveMailLayout` turns them
// into what fits the workspace actually measured: a narrow window falls back to
// a layout whose panels meet their minimums and every size is clamped, while the
// stored preference stays untouched, so widening the window again brings the
// owner's own arrangement back. Nothing here is in the URL: a layout is how a
// person reads, not what they are reading.

export type MailLayoutMode = 'right' | 'bottom' | 'list';

export const MAIL_LAYOUT_MODES: readonly MailLayoutMode[] = ['right', 'bottom', 'list'];

export const MAIL_LAYOUT_STORAGE_KEY = 'nubarca.mail.layout.v1';

export interface MailLayoutPrefs {
  mode: MailLayoutMode;
  // Docked folder panel width, px. Shared by every layout.
  folderWidth: number;
  // "right": the message list's width, px.
  listWidth: number;
  // "bottom": the message list's share of the height, 0..1.
  listRatio: number;
  // Whether the folder panel is docked, per layout.
  folders: Record<MailLayoutMode, boolean>;
}

// Thickness of a divider, px. Kept in step with `.mail-splitter` in mail.css.
export const SPLITTER_PX = 8;

export const FOLDERS_MIN = 160;
export const FOLDERS_MAX = 420;
export const LIST_MIN = 280;
export const LIST_MAX = 760;
export const READER_MIN = 360;
export const LIST_MIN_HEIGHT = 140;
export const READER_MIN_HEIGHT = 180;
export const RATIO_MIN = 0.15;
export const RATIO_MAX = 0.85;
// Below this workspace width there is room for one pane: the phone layout.
export const COMPACT_WIDTH = 600;
// A list at least this wide reads better as a table than as stacked lines.
export const TABLE_MIN_WIDTH = 640;
// The room a one-row toolbar takes above the panes. Whether "below" fits is
// judged on the WHOLE workspace minus this, never on the panes' measured
// height: the toolbar hides while a message is read in "list only", so the
// panes' height depends on the layout chosen and judging by it could flip the
// layout back and forth on every measure.
export const TOOLBAR_ALLOWANCE = 56;
// What a workspace that has not been measured yet (or cannot be: jsdom) is
// assumed to be. Large enough that nothing falls back.
const UNMEASURED = { width: 1280, height: 720 };

export const DEFAULT_MAIL_LAYOUT: MailLayoutPrefs = {
  mode: 'right',
  folderWidth: 220,
  listWidth: 380,
  listRatio: 0.45,
  folders: { right: true, bottom: false, list: false },
};

function clamp(value: number, min: number, max: number): number {
  return Math.min(Math.max(value, min), Math.max(min, max));
}

function isMode(value: unknown): value is MailLayoutMode {
  return value === 'right' || value === 'bottom' || value === 'list';
}

function num(value: unknown, fallback: number, min: number, max: number): number {
  return typeof value === 'number' && Number.isFinite(value) ? clamp(value, min, max) : fallback;
}

// Whatever storage holds — absent, not JSON, an old shape, hand-edited — comes
// back as a complete, in-range preference. Field by field, so one bad value
// does not throw away the rest.
export function parseMailLayout(raw: string | null): MailLayoutPrefs {
  const d = DEFAULT_MAIL_LAYOUT;
  let value: unknown = null;
  if (raw) {
    try { value = JSON.parse(raw); } catch { value = null; }
  }
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    return { ...d, folders: { ...d.folders } };
  }
  const v = value as Record<string, unknown>;
  const f = (typeof v.folders === 'object' && v.folders !== null ? v.folders : {}) as Record<string, unknown>;
  const flag = (mode: MailLayoutMode) => (typeof f[mode] === 'boolean' ? f[mode] as boolean : d.folders[mode]);
  return {
    mode: isMode(v.mode) ? v.mode : d.mode,
    folderWidth: num(v.folderWidth, d.folderWidth, FOLDERS_MIN, FOLDERS_MAX),
    listWidth: num(v.listWidth, d.listWidth, LIST_MIN, LIST_MAX),
    listRatio: num(v.listRatio, d.listRatio, RATIO_MIN, RATIO_MAX),
    folders: { right: flag('right'), bottom: flag('bottom'), list: flag('list') },
  };
}

export function readMailLayout(): MailLayoutPrefs {
  return parseMailLayout(readStoredItem(MAIL_LAYOUT_STORAGE_KEY));
}

function writeMailLayout(prefs: MailLayoutPrefs): void {
  try {
    window.localStorage.setItem(MAIL_LAYOUT_STORAGE_KEY, JSON.stringify(prefs));
  } catch {
    // Blocked storage: the layout simply does not survive a reload.
  }
}

export interface WorkspaceSize {
  // The workspace's own box: what decides which layouts fit.
  width: number;
  height: number;
  // The panes' row under the toolbar: what the list and reader share in
  // "below". Absent, the toolbar allowance is assumed.
  bodyHeight?: number;
}

export interface ResolvedMailLayout {
  // The layout actually drawn.
  mode: MailLayoutMode;
  // Which layouts fit the workspace right now.
  available: Record<MailLayoutMode, boolean>;
  // One pane at a time (a phone).
  compact: boolean;
  // Folders docked beside the list. When false they open as an overlay.
  foldersDocked: boolean;
  // Whether the current layout could dock them at all at this width.
  foldersCanDock: boolean;
  folderWidth: number;
  folderMax: number;
  listWidth: number;
  listMax: number;
  listHeight: number;
  listHeightMax: number;
  // The list as a table rather than stacked lines.
  table: boolean;
}

// The arrangement to draw for these preferences in a workspace of this size.
export function resolveMailLayout(prefs: MailLayoutPrefs, size: WorkspaceSize | null): ResolvedMailLayout {
  const measured = size && size.width > 0 && size.height > 0 ? size : null;
  const { width: w, height: h } = measured ?? UNMEASURED;
  const body = measured?.bodyHeight && measured.bodyHeight > 0 ? measured.bodyHeight : h - TOOLBAR_ALLOWANCE;
  const splitPair = LIST_MIN + SPLITTER_PX + READER_MIN;
  const compact = w < COMPACT_WIDTH;
  const available = {
    right: !compact && w >= splitPair,
    bottom: !compact && h - TOOLBAR_ALLOWANCE >= LIST_MIN_HEIGHT + SPLITTER_PX + READER_MIN_HEIGHT,
    list: true,
  };
  let mode: MailLayoutMode = prefs.mode;
  if (!available[mode]) {
    const other: MailLayoutMode = mode === 'right' ? 'bottom' : 'right';
    mode = mode !== 'list' && available[other] ? other : 'list';
  }

  // The folder panel may take only what the layout's own panes do not need.
  const mainMin = mode === 'right' ? splitPair : LIST_MIN;
  const folderMax = Math.min(FOLDERS_MAX, w - mainMin - SPLITTER_PX);
  const foldersCanDock = !compact && folderMax >= FOLDERS_MIN;
  const foldersDocked = foldersCanDock && prefs.folders[mode];
  const folderWidth = clamp(prefs.folderWidth, FOLDERS_MIN, folderMax);
  const mainWidth = w - (foldersDocked ? folderWidth + SPLITTER_PX : 0);

  const listMax = Math.min(LIST_MAX, mainWidth - SPLITTER_PX - READER_MIN);
  const listWidth = clamp(prefs.listWidth, LIST_MIN, listMax);

  const usableHeight = body - SPLITTER_PX;
  const listHeightMax = usableHeight - READER_MIN_HEIGHT;
  const listHeight = clamp(Math.round(prefs.listRatio * usableHeight), LIST_MIN_HEIGHT, listHeightMax);

  return {
    mode,
    available,
    compact,
    foldersDocked,
    foldersCanDock,
    folderWidth,
    folderMax: Math.max(FOLDERS_MIN, folderMax),
    listWidth,
    listMax: Math.max(LIST_MIN, listMax),
    listHeight,
    listHeightMax: Math.max(LIST_MIN_HEIGHT, listHeightMax),
    table: mode !== 'right' && mainWidth >= TABLE_MIN_WIDTH,
  };
}

export interface MailLayoutControls {
  prefs: MailLayoutPrefs;
  setMode: (mode: MailLayoutMode) => void;
  setFolderWidth: (px: number) => void;
  setListWidth: (px: number) => void;
  // The list's height in px, against the height it shares with the reader.
  setListHeight: (px: number, usableHeight: number) => void;
  setFoldersDocked: (mode: MailLayoutMode, docked: boolean) => void;
}

// The owner's layout preference, read once and written back on every change.
export function useMailLayout(): MailLayoutControls {
  const [prefs, setPrefs] = useState<MailLayoutPrefs>(readMailLayout);

  const update = useCallback((change: (p: MailLayoutPrefs) => MailLayoutPrefs) => {
    setPrefs((prev) => {
      const next = change(prev);
      writeMailLayout(next);
      return next;
    });
  }, []);

  const setMode = useCallback((mode: MailLayoutMode) => update((p) => ({ ...p, mode })), [update]);
  const setFolderWidth = useCallback(
    (px: number) => update((p) => ({ ...p, folderWidth: clamp(Math.round(px), FOLDERS_MIN, FOLDERS_MAX) })),
    [update],
  );
  const setListWidth = useCallback(
    (px: number) => update((p) => ({ ...p, listWidth: clamp(Math.round(px), LIST_MIN, LIST_MAX) })),
    [update],
  );
  const setListHeight = useCallback((px: number, usableHeight: number) => {
    if (!(usableHeight > 0)) return;
    update((p) => ({ ...p, listRatio: clamp(px / usableHeight, RATIO_MIN, RATIO_MAX) }));
  }, [update]);
  const setFoldersDocked = useCallback(
    (mode: MailLayoutMode, docked: boolean) => update((p) => ({ ...p, folders: { ...p.folders, [mode]: docked } })),
    [update],
  );

  return { prefs, setMode, setFolderWidth, setListWidth, setListHeight, setFoldersDocked };
}
