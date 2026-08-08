import { useCallback, useEffect, useId, useRef, type KeyboardEvent, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { useI18n } from '../i18n';
import { Icon } from './icons/Icon';

// The two overlay surfaces the administration screens use, over one behaviour.
//
// `Modal` is a centred dialog for creating something. `Sheet` slides in from the
// trailing edge for managing something that already exists. They differ only in
// where the panel sits; everything an overlay has to get right is shared:
//
//   * rendered through a PORTAL to document.body, so it can never end up
//     underneath the list it was opened from, whatever the page's stacking
//     context or overflow happens to be;
//   * position: fixed against the VIEWPORT, so it is visible without scrolling;
//   * the background scroll is locked while it is open;
//   * focus moves in on open, is trapped while open, and returns to the element
//     that opened it on close;
//   * Escape closes, and so does the backdrop — but only when closing is safe,
//     which the caller decides with `dismissable`.
//
// The previous Create User "dialog" was a div in the page flow with role="dialog"
// on it. It scrolled away with the page and could render below the user list.
// Announcing a dialog is not the same as being one.

const FOCUSABLE =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]),' +
  ' textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

interface OverlayProps {
  title: string;
  // Rendered under the title in the sticky header — an email, a role summary.
  subtitle?: ReactNode;
  onClose: () => void;
  // False while a form is dirty or a request is in flight: Escape and the
  // backdrop stop closing, and the explicit Cancel/Close button stays the way
  // out. Never disables the close BUTTON.
  dismissable?: boolean;
  children: ReactNode;
  // The action row, pinned to the bottom so it is reachable without scrolling
  // the whole dialog.
  footer?: ReactNode;
  testId?: string;
}

function useOverlayBehaviour(onClose: () => void, dismissable: boolean) {
  const panelRef = useRef<HTMLDivElement>(null);

  // Lock the background scroll for as long as ANY overlay is open. Counted
  // rather than set/unset, so a sheet opening a nested confirm does not release
  // the lock when only the inner one closes.
  useEffect(() => {
    const body = document.body;
    const open = Number(body.dataset.overlayCount ?? '0') + 1;
    body.dataset.overlayCount = String(open);
    const previous = body.style.overflow;
    body.style.overflow = 'hidden';
    return () => {
      const remaining = Number(body.dataset.overlayCount ?? '1') - 1;
      body.dataset.overlayCount = String(Math.max(remaining, 0));
      if (remaining <= 0) {
        body.style.overflow = previous;
        delete body.dataset.overlayCount;
      }
    };
  }, []);

  // Focus the first useful field on open — not the close button, which would
  // make Enter dismiss a form the operator just started filling in — and give
  // it back to whatever opened this on close.
  useEffect(() => {
    const opener = document.activeElement as HTMLElement | null;
    const panel = panelRef.current;
    const first = panel?.querySelector<HTMLElement>(
      'input:not([disabled]):not([type="hidden"]), select:not([disabled]), textarea:not([disabled])',
    );
    (first ?? panel)?.focus();
    return () => opener?.focus?.();
  }, []);

  useEffect(() => {
    const onKey = (e: globalThis.KeyboardEvent) => {
      if (e.key === 'Escape' && dismissable) {
        e.stopPropagation();
        onClose();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose, dismissable]);

  const onKeyDown = useCallback((e: KeyboardEvent<HTMLDivElement>) => {
    if (e.key !== 'Tab') return;
    const panel = panelRef.current;
    if (!panel) return;
    const focusable = Array.from(panel.querySelectorAll<HTMLElement>(FOCUSABLE))
      .filter((el) => el.offsetParent !== null || el === document.activeElement);
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
  }, []);

  return { panelRef, onKeyDown };
}

function OverlayShell({
  variant, title, subtitle, onClose, dismissable = true, children, footer, testId,
}: OverlayProps & { variant: 'modal' | 'sheet' }) {
  const { t } = useI18n();
  const titleId = useId();
  const { panelRef, onKeyDown } = useOverlayBehaviour(onClose, dismissable);

  return createPortal(
    <div
      className={`overlay-backdrop overlay-backdrop--${variant}`}
      data-testid={testId ? `${testId}-backdrop` : undefined}
      onMouseDown={(e) => {
        // mousedown, not click: a click whose press started INSIDE the panel
        // (dragging to select text, releasing over the backdrop) must not be
        // read as "the operator clicked away".
        if (e.target === e.currentTarget && dismissable) onClose();
      }}
    >
      <div
        ref={panelRef}
        className={`overlay-panel overlay-panel--${variant}`}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        data-testid={testId}
        tabIndex={-1}
        onKeyDown={onKeyDown}
      >
        <header className="overlay-head">
          <div className="overlay-head__text">
            <h2 id={titleId}>{title}</h2>
            {subtitle && <div className="overlay-head__subtitle">{subtitle}</div>}
          </div>
          <button
            type="button"
            className="icon-button"
            aria-label={t('common.close')}
            data-testid={testId ? `${testId}-close` : undefined}
            onClick={onClose}
          >
            <Icon name="close" />
          </button>
        </header>

        <div className="overlay-body">{children}</div>

        {footer && <footer className="overlay-foot">{footer}</footer>}
      </div>
    </div>,
    document.body,
  );
}

export function Modal(props: OverlayProps) {
  return <OverlayShell {...props} variant="modal" />;
}

export function Sheet(props: OverlayProps) {
  return <OverlayShell {...props} variant="sheet" />;
}
