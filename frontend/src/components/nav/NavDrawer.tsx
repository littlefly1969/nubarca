import { useEffect, useRef, type KeyboardEvent } from 'react';
import { useLocation } from 'react-router';
import { useI18n } from '../../i18n';
import { BrandMark } from '../../brand/BrandMark';
import { PRODUCT_NAME, SHELL_MARK_VISIBLE_PX, markBoxForVisibleWidth } from '../../brand/brand';
import { Icon } from '../icons/Icon';
import { AppNav } from './AppNav';

interface NavDrawerProps {
  isAdmin: boolean;
  onClose(): void;
  // Focus returns here on close, so keyboard users land back on the trigger.
  returnFocusRef?: React.RefObject<HTMLButtonElement | null>;
}

const FOCUSABLE =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])';

// Mobile navigation drawer. A real modal dialog: focus is trapped inside it,
// Escape closes it, it closes on any route change and on backdrop click, and it
// restores focus to the trigger. It renders the SAME `AppNav` as the desktop
// sidebar, so mobile has no separate information architecture.
export function NavDrawer({ isAdmin, onClose, returnFocusRef }: NavDrawerProps) {
  const { t } = useI18n();
  const panelRef = useRef<HTMLDivElement>(null);
  const location = useLocation();

  // Close whenever the route changes — including a navigation triggered from
  // outside the drawer (back button, a link in the page behind it).
  //
  // This compares against the location captured when the drawer OPENED rather
  // than skipping the first effect run with a boolean ref. That earlier shape
  // was not idempotent: React StrictMode deliberately runs mount effects twice
  // (effect → cleanup → effect), the ref survived because the component
  // instance does, and the second run saw "not the first render" and closed the
  // drawer instantly — so in development it could not be opened at all. A
  // location comparison is a no-op when re-run with an unchanged location, so
  // it behaves identically however many times React invokes it.
  const pathname = location.pathname;
  const search = location.search;
  const openedAt = useRef(`${pathname}${search}`);
  useEffect(() => {
    if (`${pathname}${search}` !== openedAt.current) {
      onClose();
    }
  }, [pathname, search, onClose]);

  // Move focus into the panel on open and restore it on close.
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    panelRef.current?.focus();
    return () => {
      const target = returnFocusRef?.current ?? previous;
      target?.focus?.();
    };
  }, [returnFocusRef]);

  // Escape closes.
  useEffect(() => {
    const onKey = (e: globalThis.KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  // Tab cycles within the panel.
  function onKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    if (e.key !== 'Tab') return;
    const panel = panelRef.current;
    if (!panel) return;
    const focusable = Array.from(panel.querySelectorAll<HTMLElement>(FOCUSABLE));
    if (focusable.length === 0) return;
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    const active = document.activeElement;
    if (e.shiftKey && (active === first || active === panel)) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && active === last) {
      e.preventDefault();
      first.focus();
    }
  }

  return (
    <div className="nav-drawer-backdrop" data-testid="nav-drawer-backdrop" onClick={onClose}>
      <div
        ref={panelRef}
        className="nav-drawer"
        role="dialog"
        aria-modal="true"
        aria-label={t('nav.menuLabel')}
        data-testid="nav-drawer"
        tabIndex={-1}
        onClick={(e) => e.stopPropagation()}
        onKeyDown={onKeyDown}
      >
        <div className="nav-drawer__head">
          <span className="app-brand-lockup">
            <BrandMark size={markBoxForVisibleWidth(SHELL_MARK_VISIBLE_PX.desktop)} clearSpace={false} />
            <span className="app-brand">{PRODUCT_NAME}</span>
          </span>
          <button
            type="button"
            className="icon-button"
            aria-label={t('nav.closeMenu')}
            data-testid="nav-drawer-close"
            onClick={onClose}
          >
            <Icon name="close" />
          </button>
        </div>
        <nav aria-label={t('nav.primary')}>
          <AppNav isAdmin={isAdmin} onNavigate={onClose} />
        </nav>
      </div>
    </div>
  );
}
