import { useMemo, useRef, type KeyboardEvent } from 'react';
import { useSearchParams } from 'react-router';
import { useI18n } from '../i18n';
import { usePermissions } from '../auth/usePermissions';
import { Icon } from '../components/icons/Icon';
import { PhotoArchiveExportPanel } from '../components/PhotoArchiveExportPanel';
import { OrganizeByDatePanel } from '../cloud/OrganizeByDatePanel';
import { StagingUploadPanel } from '../cloud/StagingUploadPanel';
import { TvDevicesPanel } from '../cloud/TvDevicesPanel';
import { ExactMediaDuplicatesPanel } from '../cloud/ExactMediaDuplicatesPanel';
import { FaceClusterRebuildPanel } from '../cloud/FaceClusterRebuildPanel';
import {
  CLOUD_TOOL_PARAM,
  resolveCloudTool,
  visibleCloudTools,
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
// Reaching this hub is one authority; a given tool may need another. Everything
// below works on the VISIBLE list, never the full catalogue — including the
// roving-tabindex arithmetic, which would otherwise land on an index that
// renders nothing, and the deep link, which must fall back rather than show a
// protected panel to somebody who may not use it.
//
// Private Vault is intentionally absent — it is a primary-navigation
// destination, not an operational tool.

const PANEL_ID = 'cloud-tool-panel';

export function CloudFunctionsPage() {
  const { t } = useI18n();
  const perms = usePermissions();
  const [searchParams, setSearchParams] = useSearchParams();
  const refs = useRef<(HTMLButtonElement | null)[]>([]);

  const tools = useMemo(() => visibleCloudTools(perms.has), [perms]);

  // An unrecognised — or unauthorized — ?tool= value falls back to a tool this
  // user may actually use.
  const active = resolveCloudTool(searchParams, tools);
  const activeTool = tools.find((tool) => tool.id === active) ?? null;

  // A push (not replace) so back/forward walks the tools the user visited.
  const select = (id: CloudToolId) => {
    const next = new URLSearchParams(searchParams);
    next.set(CLOUD_TOOL_PARAM, id);
    setSearchParams(next);
  };

  function onKeyDown(e: KeyboardEvent<HTMLButtonElement>, index: number) {
    if (tools.length === 0) return;
    let nextIndex = index;
    if (e.key === 'ArrowRight' || e.key === 'ArrowDown') nextIndex = (index + 1) % tools.length;
    else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') {
      nextIndex = (index - 1 + tools.length) % tools.length;
    } else if (e.key === 'Home') nextIndex = 0;
    else if (e.key === 'End') nextIndex = tools.length - 1;
    else return;
    e.preventDefault();
    select(tools[nextIndex].id);
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
        {tools.map((tool, i) => {
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

      {activeTool !== null && (
        <div
          id={PANEL_ID}
          role="tabpanel"
          aria-labelledby={`cloud-tool-tab-${activeTool.id}`}
          className="cloud-tool-panel"
          data-testid="cloud-tool-panel"
          data-tool={activeTool.id}
        >
          <div className="cloud-tool-panel__intro">
            <h2>{t(activeTool.titleKey)}</h2>
            <p className="muted">{t(activeTool.descriptionKey)}</p>
          </div>

          {/* The COMPLETE tool, in this page. A key per tool so switching tools
              resets the panel's internal state instead of leaking it across. */}
          {active === 'upload' && <StagingUploadPanel key="upload" />}
          {active === 'organize' && <OrganizeByDatePanel key="organize" />}
          {active === 'dedupe' && <ExactMediaDuplicatesPanel key="dedupe" />}
          {active === 'archive' && <PhotoArchiveExportPanel key="archive" />}
          {active === 'tv-devices' && <TvDevicesPanel key="tv-devices" />}
          {active === 'face-cluster' && <FaceClusterRebuildPanel key="face-cluster" />}
        </div>
      )}
    </section>
  );
}
