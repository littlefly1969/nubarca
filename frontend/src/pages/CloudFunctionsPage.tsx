import { useRef, type KeyboardEvent } from 'react';
import { useSearchParams } from 'react-router';
import { useI18n } from '../i18n';
import { Icon } from '../components/icons/Icon';
import { PhotoArchiveExportPanel } from '../components/PhotoArchiveExportPanel';
import { OrganizeByDatePanel } from '../cloud/OrganizeByDatePanel';
import { StagingUploadPanel } from '../cloud/StagingUploadPanel';
import { TvDevicesPanel } from '../cloud/TvDevicesPanel';
import {
  CLOUD_TOOLS,
  CLOUD_TOOL_PARAM,
  cloudToolFromParams,
  findCloudTool,
  type CloudToolId,
} from '../cloud/cloudTools';

// Cloud Functions — the single home for the owner's operational tools.
//
// The page used to show a grid of cards, three of which only linked elsewhere,
// plus one detached panel underneath. It is now a tool switcher with the
// COMPLETE selected tool rendered directly below it: an accessible tablist
// whose selection lives in the URL (?tool=…), so a tool is deep-linkable and
// browser back/forward moves between tools.
//
// Private Vault is intentionally absent — it is a primary-navigation
// destination, not an operational tool.

const PANEL_ID = 'cloud-tool-panel';

export function CloudFunctionsPage() {
  const { t } = useI18n();
  const [searchParams, setSearchParams] = useSearchParams();
  const refs = useRef<(HTMLButtonElement | null)[]>([]);

  // An unrecognised ?tool= value falls back to the default tool.
  const active = cloudToolFromParams(searchParams);
  const activeTool = findCloudTool(active);

  // A push (not replace) so back/forward walks the tools the user visited.
  const select = (id: CloudToolId) => {
    const next = new URLSearchParams(searchParams);
    next.set(CLOUD_TOOL_PARAM, id);
    setSearchParams(next);
  };

  function onKeyDown(e: KeyboardEvent<HTMLButtonElement>, index: number) {
    let nextIndex = index;
    if (e.key === 'ArrowRight' || e.key === 'ArrowDown') nextIndex = (index + 1) % CLOUD_TOOLS.length;
    else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') {
      nextIndex = (index - 1 + CLOUD_TOOLS.length) % CLOUD_TOOLS.length;
    } else if (e.key === 'Home') nextIndex = 0;
    else if (e.key === 'End') nextIndex = CLOUD_TOOLS.length - 1;
    else return;
    e.preventDefault();
    select(CLOUD_TOOLS[nextIndex].id);
    refs.current[nextIndex]?.focus();
  }

  return (
    <section className="cloud-functions">
      <header className="cloud-functions__header">
        <h1>{t('cloud.heading')}</h1>
        <p className="muted">{t('cloud.intro')}</p>
      </header>

      {/* Horizontally scrollable on narrow viewports so no label is clipped. */}
      <div
        className="cloud-tool-tabs"
        role="tablist"
        aria-label={t('cloud.toolTabsAria')}
        data-testid="cloud-tool-tabs"
      >
        {CLOUD_TOOLS.map((tool, i) => {
          const selected = tool.id === active;
          return (
            <button
              key={tool.id}
              ref={(el) => { refs.current[i] = el; }}
              type="button"
              role="tab"
              id={`cloud-tool-tab-${tool.id}`}
              aria-selected={selected}
              aria-controls={PANEL_ID}
              tabIndex={selected ? 0 : -1}
              className={`cloud-tool-tab${selected ? ' is-active' : ''}`}
              data-testid={`cf-tool-${tool.id}`}
              onClick={() => select(tool.id)}
              onKeyDown={(e) => onKeyDown(e, i)}
            >
              <Icon name={tool.icon} size={20} />
              <span className="cloud-tool-tab__label">{t(tool.titleKey)}</span>
              {/* Not colour alone: the selected tab also carries a marker. */}
              <span className="cloud-tool-tab__marker" aria-hidden="true" />
            </button>
          );
        })}
      </div>

      <div
        id={PANEL_ID}
        role="tabpanel"
        aria-labelledby={`cloud-tool-tab-${active}`}
        className="cloud-tool-panel"
        data-testid="cloud-tool-panel"
        data-tool={active}
      >
        <div className="cloud-tool-panel__intro">
          <h2>{t(activeTool.titleKey)}</h2>
          <p className="muted">{t(activeTool.descriptionKey)}</p>
        </div>

        {/* The COMPLETE tool, in this page. A key per tool so switching tools
            resets the panel's internal state instead of leaking it across. */}
        {active === 'upload' && <StagingUploadPanel key="upload" />}
        {active === 'organize' && <OrganizeByDatePanel key="organize" />}
        {active === 'archive' && <PhotoArchiveExportPanel key="archive" />}
        {active === 'tv-devices' && <TvDevicesPanel key="tv-devices" />}
      </div>
    </section>
  );
}
