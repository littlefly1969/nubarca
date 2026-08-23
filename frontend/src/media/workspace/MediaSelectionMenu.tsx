import { useCallback, useEffect, useRef, useState } from 'react';
import { Icon } from '../../components/icons/Icon';
import { useI18n } from '../../i18n';
import type { MediaSelectionAction, MediaSelectionActionId } from './mediaSelectionActions';

// One level of menu for the selection dock — "Move to" and "Add to" are two
// instances of this, and there are no submenus: every destination this product
// has fits in one list.
//
// Keyboard is the whole point of writing it rather than reusing a plain
// dropdown: the trigger opens with Enter/Space/ArrowDown, the arrows walk the
// items, Escape closes, an outside click closes, and every exit returns focus to
// the trigger so the reviewer never loses their place mid-selection.

interface Props {
  label: string;
  ariaLabel: string;
  icon: 'move' | 'add';
  actions: MediaSelectionAction[];
  disabled?: boolean;
  testId: string;
  onSelect(id: MediaSelectionActionId): void;
}

const TEST_IDS: Record<MediaSelectionActionId, string> = {
  restore: 'media-sel-restore',
  'remove-from-album': 'media-sel-remove-album',
  personal: 'media-sel-personal',
  excluded: 'media-sel-excluded',
  trash: 'media-sel-trash',
  album: 'media-sel-album',
  plates: 'media-sel-plates',
  'beauty-lab': 'media-sel-beauty',
};

export function actionTestId(id: MediaSelectionActionId): string {
  return TEST_IDS[id];
}

export function MediaSelectionMenu({
  label, ariaLabel, icon, actions, disabled, testId, onSelect,
}: Props) {
  const { t } = useI18n();
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const listRef = useRef<HTMLUListElement>(null);

  const close = useCallback((returnFocus: boolean) => {
    setOpen(false);
    if (returnFocus) triggerRef.current?.focus();
  }, []);

  // Opening moves focus onto the first item, so the menu is immediately
  // operable from the keyboard without a second Tab.
  useEffect(() => {
    if (!open) return;
    listRef.current?.querySelector<HTMLButtonElement>('button')?.focus();
  }, [open]);

  useEffect(() => {
    if (!open) return;
    function onDocPointer(e: MouseEvent) {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
    }
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') { e.stopPropagation(); close(true); }
    }
    document.addEventListener('mousedown', onDocPointer);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDocPointer);
      document.removeEventListener('keydown', onKey);
    };
  }, [open, close]);

  function moveFocus(delta: number) {
    const items = [...(listRef.current?.querySelectorAll<HTMLButtonElement>('button') ?? [])];
    if (items.length === 0) return;
    const at = items.indexOf(document.activeElement as HTMLButtonElement);
    const next = at < 0 ? 0 : (at + delta + items.length) % items.length;
    items[next].focus();
  }

  if (actions.length === 0) return null;

  return (
    <div className="ws-dock-menu" ref={rootRef}>
      <button
        ref={triggerRef}
        type="button"
        className="ws-dock-button"
        data-testid={testId}
        aria-haspopup="menu"
        aria-expanded={open}
        disabled={disabled}
        onClick={() => setOpen((v) => !v)}
        onKeyDown={(e) => {
          if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); setOpen(true); }
        }}
      >
        <Icon name={icon === 'move' ? 'archive' : 'album-add'} size={16} />
        <span className="ws-dock-label">{label}</span>
        <Icon name="chevron-down" size={14} />
      </button>
      {open && (
        <ul
          ref={listRef}
          className="ws-dock-list"
          role="menu"
          aria-label={ariaLabel}
          onKeyDown={(e) => {
            if (e.key === 'ArrowDown') { e.preventDefault(); moveFocus(1); }
            else if (e.key === 'ArrowUp') { e.preventDefault(); moveFocus(-1); }
            else if (e.key === 'Tab') close(false);
          }}
        >
          {actions.map((a) => (
            <li key={a.id} role="none">
              <button
                type="button"
                role="menuitem"
                className={a.destructive ? 'ws-dock-item is-destructive' : 'ws-dock-item'}
                data-testid={actionTestId(a.id)}
                onClick={() => { close(true); onSelect(a.id); }}
              >
                <Icon name={a.icon} size={16} />
                <span>{t(a.labelKey)}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
