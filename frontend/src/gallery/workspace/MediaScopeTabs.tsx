import { Link } from 'react-router';
import type { MediaGalleryScope } from '@nubarca/api-client';
import { useI18n } from '../../i18n';

// Slice 3: the "Libreria / Esclusi" tab bar shared by the photo and video
// galleries. Two links to the active / excluded routes; the current scope is
// marked with aria-current. Route-based (not local state) so back/forward and
// deep links behave, and switching remounts the page → selection + cursor reset.
interface Props {
  scope: MediaGalleryScope;
  activePath: string;
  excludedPath: string;
}

export function MediaScopeTabs({ scope, activePath, excludedPath }: Props) {
  const { t } = useI18n();
  return (
    <nav className="media-scope-tabs" aria-label={t('mediaScope.tabsAria')} data-testid="media-scope-tabs">
      <Link
        to={activePath}
        className={`media-scope-tab${scope === 'active' ? ' is-active' : ''}`}
        aria-current={scope === 'active' ? 'page' : undefined}
        data-testid="media-scope-tab-active"
      >
        {t('mediaScope.library')}
      </Link>
      <Link
        to={excludedPath}
        className={`media-scope-tab${scope === 'excluded' ? ' is-active' : ''}`}
        aria-current={scope === 'excluded' ? 'page' : undefined}
        data-testid="media-scope-tab-excluded"
      >
        {t('mediaScope.excluded')}
      </Link>
    </nav>
  );
}
