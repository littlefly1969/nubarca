import { useCallback, useEffect, useState } from 'react';
import { Link, useLocation, useNavigate, useParams } from 'react-router';
import {
  ApiError,
  addFaceToPerson,
  archivePerson,
  getPerson,
  getPersonPhotos,
  getPersonSimilarFaces,
  listPeople,
  removeFaceFromPerson,
  renamePerson,
  type Person,
  type PersonPhoto,
  type SimilarFace,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { FaceCrop } from '../components/people/FaceCrop';
import { FaceContextViewer } from '../components/people/FaceContextViewer';
import { AssignToPersonMenu } from '../components/people/AssignToPersonMenu';
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
  const [viewer, setViewer] = useState<{ faceIds: string[]; index: number } | null>(null);

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
      await load();
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
                      onChanged={() => void load()}
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

      {personId !== undefined && (
        <SimilarFacesSection
          personId={personId}
          onAssigned={() => void load()}
          onOpenFace={(ids, i) => setViewer({ faceIds: ids, index: i })}
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
}

function SimilarFacesSection({
  personId, onAssigned, onOpenFace, invalidateAuth,
}: {
  personId: string;
  onAssigned: () => void;
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
      } catch (err) {
        if (controller.signal.aborted) return;
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        setStatus('error');
      }
    })();
    return () => controller.abort();
  }, [personId, debounced, invalidateAuth]);

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
          {items.map((face) => (
            <li key={face.faceId} className="people-card">
              <FaceCrop
                faceId={face.faceId}
                fileItemId={face.fileItemId}
                box={face.box}
                alt={face.name}
                onClick={() => onOpenFace(items.map((it) => it.faceId), items.findIndex((it) => it.faceId === face.faceId))}
              />
              <span className="muted">{t('person.similarityScore', { pct: Math.round(face.score * 100) })}</span>
              <button type="button" onClick={() => void add(face.faceId)}>{t('person.add')}</button>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
