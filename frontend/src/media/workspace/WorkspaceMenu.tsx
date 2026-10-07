import { useEffect, useRef, useState, type RefObject } from 'react';
import { Link } from 'react-router';
import { Icon, type IconName } from '../../components/icons/Icon';

export interface WorkspaceMenuAction {
  id: string;
  label: string;
  icon?: IconName;
  onSelect?(): void;
  href?: string;
  testId?: string;
  checked?: boolean;
  checkRole?: 'radio' | 'checkbox';
}

// Shared overflow for context and workspace tools. The commands keep their own
// meaning; this owns only dismissal and keyboard focus.
export function WorkspaceMenu({ label, actions, testId, buttonRef, className = '' }: {
  label: string;
  actions: WorkspaceMenuAction[];
  testId: string;
  buttonRef?: RefObject<HTMLButtonElement | null>;
  className?: string;
}) {
  const [open, setOpen] = useState(false);
  const root = useRef<HTMLDivElement>(null);
  const fallback = useRef<HTMLButtonElement>(null);
  const trigger = buttonRef ?? fallback;
  const menu = useRef<HTMLDivElement>(null);
  const first = useRef<'first' | 'last'>('first');
  const items = () => [...(menu.current?.querySelectorAll<HTMLElement>('[role^="menuitem"]') ?? [])];

  function close(restore: boolean) {
    setOpen(false);
    if (restore) trigger.current?.focus();
  }

  useEffect(() => {
    if (!open) return;
    const entries = items();
    entries[first.current === 'last' ? entries.length - 1 : 0]?.focus();
    const outside = (event: PointerEvent) => {
      if (!root.current?.contains(event.target as Node)) setOpen(false);
    };
    const escape = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      event.preventDefault(); event.stopPropagation();
      setOpen(false); trigger.current?.focus();
    };
    document.addEventListener('pointerdown', outside);
    document.addEventListener('keydown', escape, true);
    return () => {
      document.removeEventListener('pointerdown', outside);
      document.removeEventListener('keydown', escape, true);
    };
  }, [open, trigger]);

  return <div className={`workspace-overflow ${className}`} ref={root} onBlur={(event) => {
    if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setOpen(false);
  }}>
    <button type="button" ref={trigger} className="ws-tool-button workspace-overflow__trigger"
      data-testid={testId} aria-label={label} aria-haspopup="menu" aria-expanded={open}
      onClick={() => { first.current = 'first'; setOpen((value) => !value); }}
      onKeyDown={(event) => {
        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
          event.preventDefault(); first.current = event.key === 'ArrowUp' ? 'last' : 'first'; setOpen(true);
        }
      }}><Icon name="more" /></button>
    {open && <div className="workspace-overflow__menu" ref={menu} role="menu" aria-label={label}
      onKeyDown={(event) => {
        const entries = items();
        const at = entries.indexOf(document.activeElement as HTMLElement);
        const next = event.key === 'Home' ? 0 : event.key === 'End' ? entries.length - 1
          : event.key === 'ArrowDown' ? (at + 1) % entries.length
            : event.key === 'ArrowUp' ? (at - 1 + entries.length) % entries.length : null;
        if (next !== null) { event.preventDefault(); entries[next]?.focus(); }
        if (event.key === 'Tab') {
          // Dismissing removes the currently focused menu item. Choose the
          // neighbor of the trigger before that removal can strand focus on body.
          event.preventDefault();
          const stops = [...document.querySelectorAll<HTMLElement>(
            'button, a[href], input, select, textarea, [tabindex], [contenteditable="true"]',
          )].filter((node) => {
            if (node.tabIndex < 0 || node.matches(':disabled') || node.closest('[hidden], [inert]') || menu.current?.contains(node)) return false;
            for (let parent: HTMLElement | null = node; parent; parent = parent.parentElement) {
              const style = getComputedStyle(parent);
              if (style.display === 'none' || style.visibility === 'hidden') return false;
            }
            return true;
          });
          const at = stops.indexOf(trigger.current!);
          close(false);
          (stops[at + (event.shiftKey ? -1 : 1)] ?? trigger.current)?.focus();
        }
      }}>
      {actions.map((action) => {
        const props = {
          role: action.checked === undefined ? 'menuitem' : action.checkRole === 'radio' ? 'menuitemradio' : 'menuitemcheckbox',
          tabIndex: -1,
          'aria-checked': action.checked,
          className: 'workspace-overflow__item',
          'data-testid': action.testId,
          onClick: () => { close(true); action.onSelect?.(); },
        };
        const content = <>{action.icon && <Icon name={action.icon} />}<span>{action.label}</span>
          {action.checked && <Icon name="check" />}</>;
        return action.href ? <Link key={action.id} to={action.href} {...props}>{content}</Link>
          : <button key={action.id} type="button" {...props}>{content}</button>;
      })}
    </div>}
  </div>;
}
