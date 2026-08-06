import { useEffect, useRef, useState } from 'react';
import { useI18n } from '../../i18n';

// A small local action registry for "Add to …" destinations (Beauty Lab,
// Plates, and future ones) — one menu instead of a permanent toolbar button per
// destination. GalleryPage owns each action's side effects (selection + notice);
// the menu only renders available entries and invokes `run`.
export interface GalleryDestinationAction {
  id: string;
  label: string; // already localized
  isAvailable: boolean;
  run(): void;
}

interface Props {
  actions: GalleryDestinationAction[];
  disabled?: boolean;
  // Every prop below defaults to the original "Add to…" wording/test ids, so
  // existing call sites are unaffected. A second instance (e.g. "Move to…")
  // overrides them to stay visually and semantically distinct.
  label?: string;
  ariaLabel?: string;
  menuTestId?: string;
  itemTestIdPrefix?: string;
}

export function DestinationMenu({
  actions,
  disabled,
  label,
  ariaLabel,
  menuTestId = 'ws-add-to',
  itemTestIdPrefix = 'ws-dest-',
}: Props) {
  const { t } = useI18n();
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const available = actions.filter((a) => a.isAvailable);

  // Closing the menu (Escape, outside click, or picking an item) always
  // returns focus to the trigger button — the user never loses their place.
  function closeAndReturnFocus() {
    setOpen(false);
    triggerRef.current?.focus();
  }

  useEffect(() => {
    if (!open) return;
    function onDocClick(e: MouseEvent) {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
    }
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') closeAndReturnFocus();
    }
    document.addEventListener('mousedown', onDocClick);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDocClick);
      document.removeEventListener('keydown', onKey);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  if (available.length === 0) return null;

  return (
    <div className="ws-menu" ref={rootRef}>
      <button
        ref={triggerRef}
        type="button"
        className="row-action"
        data-testid={menuTestId}
        aria-haspopup="menu"
        aria-expanded={open}
        disabled={disabled}
        onClick={() => setOpen((v) => !v)}
      >
        {label ?? t('gallery.ws.addTo')} ▾
      </button>
      {open && (
        <ul className="ws-menu-list" role="menu" aria-label={ariaLabel ?? t('gallery.ws.addToAria')}>
          {available.map((a) => (
            <li key={a.id} role="none">
              <button
                type="button"
                role="menuitem"
                className="ws-menu-item"
                data-testid={`${itemTestIdPrefix}${a.id}`}
                onClick={() => { closeAndReturnFocus(); a.run(); }}
              >
                {a.label}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
