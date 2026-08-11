import { useCallback, useEffect, useState } from 'react';
import { Link, useLocation, useNavigate, useParams } from 'react-router';
import {
  ApiError,
  MAX_PERSON_REFERENCE_FACES,
  addFaceToPerson,
  archivePerson,
  getPerson,
  getPersonPhotos,
  getPersonReferenceFaces,
  getPersonSimilarFaces,
  listPeople,
  rebuildPersonReferenceFaces,
  removeFaceFromPerson,
  renamePerson,
  type Person,
  type PersonPhoto,
  type PersonReferenceFace,
  type SimilarFace,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { FaceCrop } from '../components/people/FaceCrop';
import { FaceContextViewer } from '../components/people/FaceContextViewer';
import { AssignToPersonMenu } from '../components/people/AssignToPersonMenu';
import { withoutFace, type FaceViewerSequence } from '../components/people/faceViewerSequence';
import { PersonVideosSection } from '../components/people/PersonVideosSection';
import { useI18n } from '../i18n';
import { FACES_FALLBACK_RETURN, resolveFacesReturn } from './facesTabs';

const MIN_PCT = 20;
const MAX_PCT = 95;
const DEFAULT_PCT = 35;
const DEBOUNCE_MS = 400;

// Owner-private person detail: photos where the person appears + "find more faces
// like this person" with a manual similarity threshold. Owner-scoped, non-vault,
// no raw vectors.
export function PersonDetailPage() {
  const { personId } = useParams();
  const navigate = useNavigate();
  const location = useLocation();
  // The Faces tab that opened this person. A direct link, a new tab or a
  // bookmark carries no state, so this resolves to the named-people tab
  // rather than depending on a history entry that may not exist. That is
  // also why Back is a LINK and not navigate(-1).
  const backTo = resolveFacesReturn(location.state);
  const { invalidateAuth } = useAuth();
  const { t } = useI18n();

  const [person, setPerson] = useState<Person | null>(null);
  const [photos, setPhotos] = useState<PersonPhoto[]>([]);
  const [people, setPeople] = useState<Person[]>([]);
  const [phase, setPhase] = useState<'loading' | 'ready' | 'notfound' | 'error'>('loading');
  const [renaming, setRenaming] = useState('');
  const [viewer, setViewer] = useState<FaceViewerSequence | null>(null);
  // The persisted reference template. Read-only: loading it never builds it, so
  // the panel reports 0/6 for a person nobody has searched yet.
  const [references, setReferences] = useState<PersonReferenceFace[] | null>(null);
  // Bumped by anything that changes which faces this person is made of. The
  // similar-face search reads it, because a correction changes the reference
  // template and therefore the suggestions — leaving them on screen would offer
  // matches computed from evidence the owner has just disowned.
  const [correctionTick, setCorrectionTick] = useState(0);

  const loadReferences = useCallback(async () => {
    if (personId === undefined) return;
    try {
      setReferences(await getPersonReferenceFaces(personId));
    } catch {
      // Observability panel — never break the page over it.
    }
  }, [personId]);

  // Stable identity: SimilarFacesSection keeps this in its search effect's
  // dependencies, so a new function each render would re-run the search on every
  // parent render — including the one this callback itself causes.
  const handleSearched = useCallback(() => { void loadReferences(); }, [loadReferences]);

  const load = useCallback(async () => {
    if (personId === undefined) return;
    setPhase('loading');
    try {
      const [p, ph] = await Promise.all([getPerson(personId), getPersonPhotos(personId)]);
      setPerson(p);
      setRenaming(p.name ?? '');
      setPhotos(ph);
      setPhase('ready');
      // People list for the move/reassign menu — best-effort (never blocks detail).
      void listPeople().then(setPeople).catch(() => { /* non-fatal */ });
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      if (err instanceof ApiError && err.status === 404) { setPhase('notfound'); return; }
      setPhase('error');
    }
  }, [personId, invalidateAuth]);

  useEffect(() => { void load(); }, [load]);
  useEffect(() => { void loadReferences(); }, [loadReferences]);

  // One refresh for every correction, because a correction moves all of it at
  // once: the person's own counts, its photos, the reference template the
  // backend has just rebuilt, and the suggestions that template produces.
  // Reloading only the photos was what left "Volti di riferimento · 6/6" on
  // screen next to a reference the owner had removed a moment earlier.
  const refreshAfterCorrection = useCallback(() => {
    void load();
    void loadReferences();
    setCorrectionTick((n) => n + 1);
  }, [load, loadReferences]);

  // A face the owner ignored (or moved away) stops being part of this person:
  // it leaves the open viewer's sequence — which advances, or closes when that
  // was the last face — and the page reloads around it.
  const dismissFromViewer = useCallback((faceId: string) => {
    setViewer((v) => withoutFace(v, faceId));
    refreshAfterCorrection();
  }, [refreshAfterCorrection]);

  async function handleRename() {
    if (personId === undefined || renaming.trim().length === 0) return;
    try {
      const p = await renamePerson(personId, renaming.trim());
      setPerson(p);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) invalidateAuth();
    }
  }

  async function handleArchive() {
    if (personId === undefined) return;
    try {
      await archivePerson(personId);
      navigate(FACES_FALLBACK_RETURN);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) invalidateAuth();
    }
  }

  async function handleRemoveFace(faceId: string) {
    if (personId === undefined) return;
    try {
      await removeFaceFromPerson(personId, faceId);
      setViewer((v) => withoutFace(v, faceId));
      refreshAfterCorrection();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) invalidateAuth();
    }
  }

  if (phase === 'notfound') {
    return <section className="people-page"><p className="muted">{t('person.notFound')}</p><Link to={backTo}>{t('person.backToPeople')}</Link></section>;
  }
  if (phase === 'error') {
    return (
      <section className="people-page">
        <div className="folder-error" role="alert">
          {t('person.errorLabel')} <button type="button" className="retry-button" onClick={() => void load()}>{t('common.tryAgain')}</button>
        </div>
      </section>
    );
  }

  return (
    <section className="people-page" aria-label={t('person.detailAria')}>
      <header className="people-header">
        <Link to={backTo} className="people-back">{t('person.backToPeople')}</Link>
        <h2>{person?.name ?? t('people.unnamed')}</h2>
      </header>

      <div className="person-actions">
        <input
          type="text" value={renaming} onChange={(e) => setRenaming(e.target.value)}
          aria-label={t('people.personNameAria')} placeholder={t('person.namePlaceholder')}
        />
        <button type="button" onClick={() => void handleRename()}>{t('person.rename')}</button>
        <button type="button" onClick={() => void handleArchive()}>{t('person.removePerson')}</button>
      </div>

      {/* The frequent job on this page is finding and adding NEW faces, so the
          template and the search come first. The already-assigned collection is
          long by nature — putting it above the search meant scrolling past
          everything the person already is to reach the one control that grows
          them. This is DOM order, not `order:`, so keyboard and screen-reader
          traversal match what the eye sees. */}
      {personId !== undefined && (
        <ReferenceFacesSection
          personId={personId}
          references={references}
          onOpenFace={(ids, i) => setViewer({ faceIds: ids, index: i })}
          onRebuilt={(next) => { setReferences(next); setCorrectionTick((n) => n + 1); }}
          invalidateAuth={invalidateAuth}
        />
      )}

      {personId !== undefined && (
        <SimilarFacesSection
          personId={personId}
          correctionTick={correctionTick}
          onAssigned={refreshAfterCorrection}
          onSearched={handleSearched}
          onOpenFace={(ids, i) => setViewer({ faceIds: ids, index: i })}
          invalidateAuth={invalidateAuth}
        />
      )}

      <h3>{t('person.photosHeading', { count: photos.length })}</h3>
      {photos.length === 0 ? (
        <p className="muted">{t('person.noPhotos')}</p>
      ) : (
        (() => {
          const photoFaceIds = photos.flatMap((p) => p.faces.map((fa) => fa.faceId));
          return (
            <ul className="people-grid">
              {photos.flatMap((photo) =>
                photo.faces.map((face) => (
                  <li key={face.faceId} className="people-card">
                    <FaceCrop
                      faceId={face.faceId}
                      fileItemId={photo.fileItemId}
                      box={face.box}
                      alt={photo.name}
                      onClick={() => setViewer({ faceIds: photoFaceIds, index: photoFaceIds.indexOf(face.faceId) })}
                    />
                    <span className="people-card-name">{photo.name}</span>
                    <button type="button" onClick={() => void handleRemoveFace(face.faceId)}>{t('person.removeFace')}</button>
                    <AssignToPersonMenu
                      faceId={face.faceId}
                      people={people}
                      currentPersonId={personId ?? null}
                      currentPersonName={person?.name ?? null}
                      onChanged={refreshAfterCorrection}
                      invalidateAuth={invalidateAuth}
                    />
                  </li>
                )),
              )}
            </ul>
          );
        })()
      )}

      {personId !== undefined && (
        <PersonVideosSection personId={personId} invalidateAuth={invalidateAuth} />
      )}

      {viewer && (
        <FaceContextViewer
          faceIds={viewer.faceIds}
          index={viewer.index}
          onIndexChange={(next) => setViewer((v) => (v ? { ...v, index: next } : v))}
          onClose={() => setViewer(null)}
          onFaceIgnored={dismissFromViewer}
          onFaceRestored={dismissFromViewer}
        />
      )}
    </section>
  );
}

// A window on person_face_references — the faces the matcher actually queries
// with, in their stored slot order. It shows persisted state and nothing else:
// it never triggers the bootstrap, which is why an unsearched person reads 0/6
// with an explanation rather than an error.
//
// The one thing it can DO is ask for the template to be reselected. Corrections
// already rebuild it automatically, so this is the safety net for a set that
// looks wrong: no confirmation, because the reference set is derived state and
// the confirmed assignments are not touched.
function ReferenceFacesSection({
  personId, references, onOpenFace, onRebuilt, invalidateAuth,
}: {
  personId: string;
  references: PersonReferenceFace[] | null;
  onOpenFace: (faceIds: string[], index: number) => void;
  onRebuilt: (next: PersonReferenceFace[]) => void;
  invalidateAuth: () => void;
}) {
  const { t } = useI18n();
  const [rebuilding, setRebuilding] = useState(false);

  async function rebuild() {
    setRebuilding(true);
    try {
      onRebuilt(await rebuildPersonReferenceFaces(personId));
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) invalidateAuth();
    } finally {
      setRebuilding(false);
    }
  }

  if (references === null) {
    return null; // not loaded yet — say nothing rather than flash "0/6"
  }

  const faceIds = references.map((r) => r.faceId);
  return (
    <section className="reference-faces" aria-label={t('person.referenceFacesAria')}>
      <div className="reference-faces-head">
        <h3>{t('person.referenceFacesHeading', { count: references.length, max: MAX_PERSON_REFERENCE_FACES })}</h3>
        <button
          type="button"
          className="reference-faces-rebuild linklike"
          disabled={rebuilding}
          title={t('person.rebuildReferencesHelp')}
          onClick={() => void rebuild()}
        >
          {rebuilding ? t('person.rebuildingReferences') : t('person.rebuildReferences')}
        </button>
      </div>
      {references.length === 0 ? (
        <p className="muted">
          {t('person.referenceFacesEmpty')} {t('person.referenceFacesEmptyHint')}
        </p>
      ) : (
        <ul className="reference-faces-grid">
          {references.map((face, i) => (
            <li key={face.faceId} className="reference-face">
              <FaceCrop
                faceId={face.faceId}
                fileItemId={face.fileItemId}
                box={face.box}
                size={72}
                alt={face.name}
                onClick={() => onOpenFace(faceIds, i)}
              />
              <span className="reference-face-slot" aria-label={t('person.referenceFaceSlot', { n: face.ordinal + 1 })}>
                #{face.ordinal + 1}
              </span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

function SimilarFacesSection({
  personId, correctionTick, onAssigned, onSearched, onOpenFace, invalidateAuth,
}: {
  personId: string;
  // Bumped whenever the person's confirmed faces change. The suggestions come
  // from the reference template, which the backend has just rebuilt, so they
  // have to be re-asked for rather than left on screen.
  correctionTick: number;
  onAssigned: () => void;
  onSearched: () => void;
  onOpenFace: (faceIds: string[], index: number) => void;
  invalidateAuth: () => void;
}) {
  const { t } = useI18n();
  const [pct, setPct] = useState(DEFAULT_PCT);
  const [debounced, setDebounced] = useState(DEFAULT_PCT);
  const [items, setItems] = useState<SimilarFace[]>([]);
  const [status, setStatus] = useState<'loading' | 'ready' | 'unavailable' | 'error'>('loading');

  useEffect(() => {
    const t = setTimeout(() => setDebounced(pct), DEBOUNCE_MS);
    return () => clearTimeout(t);
  }, [pct]);

  useEffect(() => {
    const controller = new AbortController();
    setStatus('loading');
    (async () => {
      try {
        const page = await getPersonSimilarFaces(personId, debounced / 100, 40, null, controller.signal);
        if (controller.signal.aborted) return;
        if (!page.profileAvailable) {
          setStatus('unavailable');
          setItems([]);
          return;
        }
        setItems(page.items);
        setStatus('ready');
        // The search is what BUILDS the reference set on first use, so this is
        // the moment the panel above can stop saying 0/6.
        onSearched();
      } catch (err) {
        if (controller.signal.aborted) return;
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        setStatus('error');
      }
    })();
    return () => controller.abort();
  }, [personId, debounced, correctionTick, invalidateAuth, onSearched]);

  async function add(faceId: string) {
    try {
      await addFaceToPerson(personId, faceId);
      setItems((prev) => prev.filter((i) => i.faceId !== faceId));
      onAssigned();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) invalidateAuth();
    }
  }

  return (
    <section className="similar-faces" aria-label={t('person.similarAria')}>
      <h3>{t('person.similarHeading')}</h3>
      <div className="similar-explorer-filter">
        <div className="similar-explorer-filter-row">
          <label htmlFor="face-sim-slider">{t('person.threshold')}</label>
          <div className="similar-explorer-value">{pct}%</div>
        </div>
        <input
          id="face-sim-slider" type="range" min={MIN_PCT} max={MAX_PCT} step={1} value={pct}
          onChange={(e) => setPct(Number(e.target.value))} aria-label={t('person.threshold')}
        />
        <input
          type="number" min={MIN_PCT} max={MAX_PCT} step={1} value={pct}
          onChange={(e) => setPct(Math.min(MAX_PCT, Math.max(MIN_PCT, Number(e.target.value))))}
          aria-label={t('person.thresholdValueAria')}
        />
      </div>

      {status === 'loading' && <p className="muted" role="status">{t('person.searching')}</p>}
      {status === 'unavailable' && <p className="muted">{t('person.searchUnavailable')}</p>}
      {status === 'error' && <p className="folder-error" role="alert">{t('person.searchError')}</p>}
      {status === 'ready' && items.length === 0 && <p className="muted">{t('person.noMoreSimilar')}</p>}
      {status === 'ready' && items.length > 0 && (
        <ul className="people-grid">
          {items.map((face) => {
            // A candidate already on another person is deliberately still
            // proposed (it is how a past mistake is corrected), so it must never
            // look like a free one: it carries the current name and its action
            // says it MOVES the face rather than adding it.
            const assignedTo = face.assignedPersonId !== null ? face.assignedPersonName ?? t('people.unnamed') : null;
            return (
              <li key={face.faceId} className={assignedTo !== null ? 'people-card people-card-assigned' : 'people-card'}>
                <FaceCrop
                  faceId={face.faceId}
                  fileItemId={face.fileItemId}
                  box={face.box}
                  alt={face.name}
                  onClick={() => onOpenFace(items.map((it) => it.faceId), items.findIndex((it) => it.faceId === face.faceId))}
                />
                <span className="muted">{t('person.similarityScore', { pct: Math.round(face.score * 100) })}</span>
                {assignedTo !== null && (
                  <span className="people-card-badge">{t('person.alreadyAssignedTo', { name: assignedTo })}</span>
                )}
                <button type="button" onClick={() => void add(face.faceId)}>
                  {assignedTo !== null ? t('person.moveHere') : t('person.add')}
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
