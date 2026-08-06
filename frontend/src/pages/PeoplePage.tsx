import { useCallback, useEffect, useState } from 'react';
import { Link, useLocation, useSearchParams } from 'react-router';
import {
  ApiError,
  assignGroup,
  getGroupFaces,
  ignoreGroup,
  listPeople,
  listSuggestedGroups,
  type Person,
  type SuggestedGroup,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { FaceCrop } from '../components/people/FaceCrop';
import { FaceContextViewer } from '../components/people/FaceContextViewer';
import { FaceAISettings } from '../components/people/FaceAISettings';
import { UnassignedFacesTab } from '../components/people/UnassignedFacesTab';
import { IgnoredFacesTab } from '../components/people/IgnoredFacesTab';
import { VideoFaceReviewTab } from '../components/people/VideoFaceReviewTab';
import { ClusterAssignDialog } from '../components/people/ClusterAssignDialog';
import { useI18n } from '../i18n';
import { type FacesTab, resolveFacesTab } from './facesTabs';

type Tab = FacesTab;

// Tabs that load their own data inside their component (not via `reload`).
const SELF_LOADING_TABS: Tab[] = ['settings', 'unassigned', 'ignored', 'videoFaces'];

// Owner-private Faces page (Volti). Suggested groups → name a person; People →
// browse named clusters; Review → low-confidence groups; Settings → admin
// thresholds. No raw vectors, scores tables, or storage internals are shown.
//
// The selected tab lives in the URL (?tab=), not in state: see facesTabs.ts.
export function PeoplePage() {
  const { state, invalidateAuth } = useAuth();
  const { t } = useI18n();
  const location = useLocation();
  const [searchParams, setSearchParams] = useSearchParams();
  const isAdmin = state.status === 'authed' && state.user.isAdmin;

  const requestedTab = searchParams.get('tab');
  const tab = resolveFacesTab(requestedTab, isAdmin);

  // Normalize an absent, unknown or unauthorized ?tab= to the resolved one.
  // `replace` on purpose: a URL the user never chose must not become a Back
  // stop. Real tab changes below push, so Back walks the tabs they did choose.
  useEffect(() => {
    if (requestedTab === tab) return;
    setSearchParams({ tab }, { replace: true });
  }, [requestedTab, tab, setSearchParams]);

  const selectTab = useCallback((next: Tab) => {
    setSearchParams({ tab: next });
  }, [setSearchParams]);

  const [people, setPeople] = useState<Person[]>([]);
  const [groups, setGroups] = useState<SuggestedGroup[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);
  const [viewer, setViewer] = useState<{ faceIds: string[]; index: number } | null>(null);

  const reload = useCallback(async (which: Tab) => {
    setLoading(true);
    setError(false);
    try {
      if (which === 'people') {
        setPeople(await listPeople());
      } else if (which === 'suggested' || which === 'review') {
        // Load people too: the group cards' "aggiungi a persona esistente" dropdown
        // (and the cluster-merge dialog) need the existing people list. Fetching it
        // only on the People tab left the dropdown empty until the user visited that
        // tab and came back.
        const [g, p] = await Promise.all([
          listSuggestedGroups(which === 'review'),
          listPeople(),
        ]);
        setGroups(g);
        setPeople(p);
      }
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        invalidateAuth();
        return;
      }
      setError(true);
    } finally {
      setLoading(false);
    }
  }, [invalidateAuth]);

  useEffect(() => {
    // Some tabs load their own data inside their components.
    if (!SELF_LOADING_TABS.includes(tab)) {
      void reload(tab);
    } else {
      setLoading(false);
    }
  }, [tab, reload]);

  return (
    <section className="people-page" aria-label={t('people.pageAria')}>
      <header className="people-header">
        <h2>{t('people.heading')}</h2>
      </header>

      <nav className="people-tabs" aria-label={t('people.sectionsAria')}>
        <TabButton active={tab === 'suggested'} onClick={() => selectTab('suggested')}>{t('people.tabSuggested')}</TabButton>
        <TabButton active={tab === 'people'} onClick={() => selectTab('people')}>{t('people.tabPeople')}</TabButton>
        <TabButton active={tab === 'unassigned'} onClick={() => selectTab('unassigned')}>{t('people.tabUnassigned')}</TabButton>
        <TabButton active={tab === 'review'} onClick={() => selectTab('review')}>{t('people.tabReview')}</TabButton>
        <TabButton active={tab === 'videoFaces'} onClick={() => selectTab('videoFaces')}>{t('people.tabVideoFaces')}</TabButton>
        <TabButton active={tab === 'ignored'} onClick={() => selectTab('ignored')}>{t('people.tabIgnored')}</TabButton>
        {isAdmin && (
          <TabButton active={tab === 'settings'} onClick={() => selectTab('settings')}>{t('people.tabSettings')}</TabButton>
        )}
      </nav>

      {tab === 'settings' ? (
        <FaceAISettings />
      ) : tab === 'unassigned' ? (
        <UnassignedFacesTab onOpenFace={openFaces} invalidateAuth={invalidateAuth} />
      ) : tab === 'videoFaces' ? (
        <VideoFaceReviewTab invalidateAuth={invalidateAuth} />
      ) : tab === 'ignored' ? (
        <IgnoredFacesTab onOpenFace={openFaces} invalidateAuth={invalidateAuth} />
      ) : error ? (
        <div className="folder-error" role="alert">
          {t('people.loadError')} <button type="button" className="retry-button" onClick={() => void reload(tab)}>{t('common.tryAgain')}</button>
        </div>
      ) : loading ? (
        <p className="muted" role="status">{t('common.loading')}</p>
      ) : tab === 'people' ? (
        <PeopleGrid people={people} returnTo={`${location.pathname}${location.search}`} />
      ) : (
        <GroupsGrid
          groups={groups}
          existingPeople={people}
          onChanged={() => { void reload(tab); }}
          onReviewGroup={openGroup}
          invalidateAuth={invalidateAuth}
        />
      )}

      {viewer && (
        <FaceContextViewer
          faceIds={viewer.faceIds}
          index={viewer.index}
          onIndexChange={(next) => setViewer((v) => (v ? { ...v, index: next } : v))}
          onClose={() => setViewer(null)}
        />
      )}
    </section>
  );

  // Open the WHOLE suggested/review group in the viewer so the user can page
  // through every member face and evaluate the group (not just one photo).
  async function openGroup(groupId: string) {
    try {
      const faces = await getGroupFaces(groupId);
      if (faces.length > 0) {
        setViewer({ faceIds: faces.map((f) => f.faceId), index: 0 });
      }
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) invalidateAuth();
    }
  }

  function openFaces(faceIds: string[], index: number) {
    setViewer({ faceIds: faceIds.length > 0 ? faceIds : [faceIds[index]], index });
  }
}

function TabButton({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button type="button" className={active ? 'people-tab is-active' : 'people-tab'} onClick={onClick} aria-pressed={active}>
      {children}
    </button>
  );
}

// `returnTo` travels in router state so the person detail's visible Back
// action lands on the tab that opened it, not on the default landing tab.
function PeopleGrid({ people, returnTo }: { people: Person[]; returnTo: string }) {
  const { t, tn } = useI18n();
  if (people.length === 0) {
    return <p className="muted">{t('people.noPeople')}</p>;
  }
  return (
    <ul className="people-grid">
      {people.map((p) => (
        <li key={p.personId} className="people-card">
          <Link
            to={`/people/${p.personId}`}
            state={{ facesReturn: returnTo }}
            className="people-card-link"
          >
            {p.representative ? (
              <FaceCrop
                faceId={p.representative.faceId}
                fileItemId={p.representative.fileItemId}
                box={p.representative.box}
                alt={p.name ?? t('people.personFallback')}
              />
            ) : (
              <span className="face-crop face-crop-empty" aria-hidden="true" />
            )}
            <span className="people-card-name">{p.name ?? t('people.unnamed')}</span>
            <span className="muted">{tn(p.faceCount, 'people.faceCount')}</span>
          </Link>
        </li>
      ))}
    </ul>
  );
}

function GroupsGrid({
  groups, existingPeople, onChanged, onReviewGroup, invalidateAuth,
}: {
  groups: SuggestedGroup[];
  existingPeople: Person[];
  onChanged: () => void;
  onReviewGroup: (groupId: string) => void;
  invalidateAuth: () => void;
}) {
  const { t } = useI18n();
  if (groups.length === 0) {
    return <p className="muted">{t('people.noGroups')}</p>;
  }
  return (
    <ul className="people-grid">
      {groups.map((g) => (
        <GroupCard key={g.groupId} group={g} existingPeople={existingPeople} onChanged={onChanged} onReviewGroup={onReviewGroup} invalidateAuth={invalidateAuth} />
      ))}
    </ul>
  );
}

function GroupCard({
  group, existingPeople, onChanged, onReviewGroup, invalidateAuth,
}: {
  group: SuggestedGroup;
  existingPeople: Person[];
  onChanged: () => void;
  onReviewGroup: (groupId: string) => void;
  invalidateAuth: () => void;
}) {
  const { t, tn } = useI18n();
  const [name, setName] = useState('');
  const [busy, setBusy] = useState(false);
  const [merge, setMerge] = useState(false);

  async function run(fn: () => Promise<unknown>) {
    setBusy(true);
    try {
      await fn();
      onChanged();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) invalidateAuth();
    } finally {
      setBusy(false);
    }
  }

  return (
    <li className="people-card">
      {group.representative ? (
        <FaceCrop
          faceId={group.representative.faceId}
          fileItemId={group.representative.fileItemId}
          box={group.representative.box}
          alt={t('people.reviewGroup')}
          onClick={() => onReviewGroup(group.groupId)}
        />
      ) : (
        <span className="face-crop face-crop-empty" aria-hidden="true" />
      )}
      <button type="button" className="group-review-link linklike" onClick={() => onReviewGroup(group.groupId)}>
        {t('people.reviewGroup')}
      </button>
      <span className="muted">{tn(group.faceCount, 'people.faceCount')}</span>
      {group.confidence != null && (
        <span className="muted">{t('people.confidence', { pct: Math.round(group.confidence * 100) })}</span>
      )}
      <form
        className="people-assign"
        onSubmit={(e) => {
          e.preventDefault();
          if (name.trim().length > 0) void run(() => assignGroup(group.groupId, { name: name.trim() }));
        }}
      >
        <input
          type="text" placeholder={t('people.assignNamePlaceholder')} value={name}
          onChange={(e) => setName(e.target.value)} aria-label={t('people.personNameAria')} disabled={busy}
        />
        <button type="submit" disabled={busy || name.trim().length === 0}>{t('people.assign')}</button>
      </form>
      {existingPeople.length > 0 && (
        <select
          aria-label={t('people.addToExistingAria')}
          disabled={busy}
          defaultValue=""
          onChange={(e) => {
            const personId = e.target.value;
            if (personId) void run(() => assignGroup(group.groupId, { personId }));
          }}
        >
          <option value="">{t('people.orAddTo')}</option>
          {existingPeople.map((p) => (
            <option key={p.personId} value={p.personId}>{p.name ?? t('people.unnamed')}</option>
          ))}
        </select>
      )}
      <button type="button" onClick={() => setMerge(true)} disabled={busy}>
        {t('people.associateCluster')}
      </button>
      <button
        type="button"
        className="group-ignore-button"
        disabled={busy}
        onClick={() => {
          if (window.confirm(t('people.confirmIgnoreGroup', { count: group.faceCount }))) {
            void run(() => ignoreGroup(group.groupId));
          }
        }}
      >
        {t('people.ignoreGroup')}
      </button>
      {merge && (
        <ClusterAssignDialog
          clusterId={group.groupId}
          faceCount={group.faceCount}
          people={existingPeople}
          onDone={() => { setMerge(false); onChanged(); }}
          onClose={() => setMerge(false)}
          invalidateAuth={invalidateAuth}
        />
      )}
    </li>
  );
}
