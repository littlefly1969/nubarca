import { useI18n } from '../../i18n';
import type { MediaLibraryScope } from './mediaWorkspaceQuery';

// "In libreria | Esclusi" scope selector for the workspace.
//
// This used to render as a second full-width tab row directly under the
// media-kind tabs, so two competing primary navigations sat on top of each
// other. It is now a COMPACT segmented control that lives inside the command
// bar, subordinate to the kind switcher.
//
// Behaviour is unchanged: controlled, two options, and the page still reflects
// the choice in the URL (path for the library, ?scope= for an album) so
// back/forward and deep links keep working.

interface Props {
  value: MediaLibraryScope;
  onChange(scope: MediaLibraryScope): void;
}

export function MediaLibraryScopeTabs({ value, onChange }: Props) {
  const { t } = useI18n();
  return (
    <div
      className="media-scope-tabs"
      role="tablist"
      aria-label={t('mediaScope.tabsAria')}
      data-testid="media-scope-tabs"
    >
      <button
        type="button"
        role="tab"
        aria-selected={value === 'active'}
        tabIndex={value === 'active' ? 0 : -1}
        className={`media-scope-tab${value === 'active' ? ' is-active' : ''}`}
        data-testid="media-scope-tab-active"
        onClick={() => onChange('active')}
      >
        {t('mediaScope.library')}
      </button>
      <button
        type="button"
        role="tab"
        aria-selected={value === 'excluded'}
        tabIndex={value === 'excluded' ? 0 : -1}
        className={`media-scope-tab${value === 'excluded' ? ' is-active' : ''}`}
        data-testid="media-scope-tab-excluded"
        onClick={() => onChange('excluded')}
      >
        {t('mediaScope.excluded')}
      </button>
    </div>
  );
}
