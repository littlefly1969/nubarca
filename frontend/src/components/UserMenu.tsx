import { useEffect, useRef, useState } from 'react';
import { NavLink } from 'react-router';
import { updateMyLanguage } from '@nubarca/api-client';
import { useI18n, type Language } from '../i18n';
import { ThemeSwitcher } from '../theme';
import { LanguageSwitcher } from './LanguageSwitcher';
import { Icon } from './icons/Icon';

interface UserMenuProps {
  displayName: string;
  email: string;
  // Pushed into auth state by the caller; the provider re-applies the language.
  onUserUpdated(user: unknown): void;
}

// The single coherent user area: identity, account, language, theme, sign out.
//
// Previously these were four unrelated controls sitting in the header next to
// the nav. They now live in one popover behind an accessible trigger, so the
// utility bar stays compact and the account/appearance settings are found in
// one place.
export function UserMenu({ displayName, email, onUserUpdated, onSignOut }: UserMenuProps & {
  onSignOut(): void;
}) {
  const { t } = useI18n();
  const [open, setOpen] = useState(false);
  const [langError, setLangError] = useState(false);
  const [langBusy, setLangBusy] = useState(false);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const popoverRef = useRef<HTMLDivElement>(null);

  // Escape closes and returns focus to the trigger.
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { setOpen(false); triggerRef.current?.focus(); }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open]);

  // A click anywhere outside the popover (and outside the trigger) closes it.
  useEffect(() => {
    if (!open) return;
    const onPointerDown = (e: MouseEvent) => {
      const target = e.target as Node;
      if (popoverRef.current?.contains(target) || triggerRef.current?.contains(target)) return;
      setOpen(false);
    };
    document.addEventListener('mousedown', onPointerDown);
    return () => document.removeEventListener('mousedown', onPointerDown);
  }, [open]);

  const handleLanguage = (next: Language) => {
    setLangError(false);
    setLangBusy(true);
    // Unchanged server persistence: PUT /api/auth/me/language, then the fresh
    // user is pushed into auth state.
    updateMyLanguage(next)
      .then((user) => onUserUpdated(user))
      .catch(() => setLangError(true))
      .finally(() => setLangBusy(false));
  };

  // First grapheme of the display name, for the avatar chip. Uses the string
  // iterator so an emoji / non-BMP initial is not split into half a surrogate
  // pair.
  const initial = [...displayName][0]?.toUpperCase() ?? '?';

  return (
    <div className="user-menu">
      <button
        ref={triggerRef}
        type="button"
        className="user-menu__trigger"
        aria-expanded={open}
        aria-haspopup="dialog"
        data-testid="user-menu-trigger"
        onClick={() => setOpen((v) => !v)}
      >
        <span className="user-menu__avatar" aria-hidden="true">{initial}</span>
        <span className="user-menu__name">{displayName}</span>
        <Icon name={open ? 'chevron-left' : 'chevron-right'} size={16} />
        <span className="visually-hidden">{t('nav.userMenu')}</span>
      </button>

      {open && (
        <div
          ref={popoverRef}
          className="user-menu__popover"
          role="dialog"
          aria-label={t('nav.userMenu')}
          data-testid="user-menu-popover"
        >
          <div className="user-menu__identity">
            <span className="user-menu__identity-name" aria-label={t('nav.signedInAs')}>
              {displayName}
            </span>
            {/* The full address lives here rather than in the header bar. */}
            <span className="user-menu__identity-email" title={email}>{email}</span>
          </div>

          <div className="user-menu__section">
            <NavLink to="/account" className="user-menu__item" onClick={() => setOpen(false)}>
              <Icon name="account" />
              <span>{t('nav.account')}</span>
            </NavLink>
          </div>

          <div className="user-menu__section">
            <LanguageSwitcher onSelect={handleLanguage} disabled={langBusy} className="language-switcher language-switcher--stacked" />
            {langError && (
              <span className="language-switcher-error" role="alert">
                {t('language.updateError')}
              </span>
            )}
          </div>

          <div className="user-menu__section">
            <ThemeSwitcher />
          </div>

          <div className="user-menu__section">
            <button
              type="button"
              className="user-menu__item user-menu__item--danger logout"
              data-testid="user-menu-signout"
              onClick={() => { setOpen(false); onSignOut(); }}
            >
              <Icon name="signout" />
              <span>{t('nav.signOut')}</span>
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
