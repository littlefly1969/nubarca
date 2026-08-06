import { useCallback, useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import {
  ApiError,
  assignClusterToPerson,
  createPerson,
  type ClusterAssignSummary,
  type Person,
} from '@nubarca/api-client';
import { useI18n } from '../../i18n';

// Owner-private "Associa cluster a persona…" dialog. Search/select an existing
// person (or create a new one) and associate the whole suggested cluster with it.
// For an existing target it previews the counts via a dry run before confirming.
// Portal-rendered centered modal (Escape closes, focus on search). No internals.
export function ClusterAssignDialog({
  clusterId,
  faceCount,
  people,
  onDone,
  onClose,
  invalidateAuth,
}: {
  clusterId: string;
  faceCount: number;
  people: Person[];
  onDone: (summary: ClusterAssignSummary) => void;
  onClose: () => void;
  invalidateAuth?: () => void;
}) {
  const { t } = useI18n();
  const [query, setQuery] = useState('');
  const [newName, setNewName] = useState('');
  const [target, setTarget] = useState<Person | null>(null);
  const [moveAssigned, setMoveAssigned] = useState(false);
  const [preview, setPreview] = useState<ClusterAssignSummary | null>(null);
  const [busy, setBusy] = useState(false);
  const searchRef = useRef<HTMLInputElement | null>(null);

  const matches = query.trim().length === 0
    ? people.slice(0, 8)
    : people.filter((p) => (p.name ?? '').toLowerCase().includes(query.trim().toLowerCase())).slice(0, 8);

  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') { e.stopPropagation(); onClose(); }
    }
    window.addEventListener('keydown', onKey, true);
    const id = window.setTimeout(() => searchRef.current?.focus(), 0);
    return () => { window.removeEventListener('keydown', onKey, true); window.clearTimeout(id); };
  }, [onClose]);

  // Dry-run preview whenever an existing target or the move flag changes.
  const runPreview = useCallback(async (personId: string, move: boolean) => {
    try {
      setPreview(await assignClusterToPerson(personId, clusterId, { moveAssigned: move, dryRun: true }));
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) invalidateAuth?.();
    }
  }, [clusterId, invalidateAuth]);

  useEffect(() => {
    if (target) void runPreview(target.personId, moveAssigned);
    else setPreview(null);
  }, [target, moveAssigned, runPreview]);

  async function confirm() {
    setBusy(true);
    try {
      let personId = target?.personId;
      if (!personId) {
        const name = newName.trim();
        if (name.length === 0) { setBusy(false); return; }
        personId = (await createPerson(name)).personId;
      }
      const summary = await assignClusterToPerson(personId, clusterId, { moveAssigned, dryRun: false });
      onDone(summary);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) invalidateAuth?.();
    } finally {
      setBusy(false);
    }
  }

  const canConfirm = !busy && (target !== null || newName.trim().length > 0);

  return createPortal(
    <div className="assign-modal-backdrop" onClick={onClose}>
      <div
        className="assign-modal"
        role="dialog"
        aria-modal="true"
        aria-label={t('cluster.title')}
        onClick={(e) => e.stopPropagation()}
      >
        <h3 className="assign-modal-title">{t('cluster.title')}</h3>
        <p className="assign-modal-current muted">{t('cluster.facesInGroup')} <strong>{faceCount}</strong></p>

        {target ? (
          <p className="cluster-assign-target">
            {t('cluster.selectedPerson')} <strong>{target.name ?? t('people.unnamed')}</strong>{' '}
            <button type="button" className="linklike" onClick={() => setTarget(null)} disabled={busy}>{t('cluster.change')}</button>
          </p>
        ) : (
          <>
            <label className="assign-modal-field">
              <span className="visually-hidden">{t('face.searchPerson')}</span>
              <input
                type="text" placeholder={t('face.searchPerson')} aria-label={t('face.searchPerson')}
                value={query} disabled={busy} ref={searchRef}
                onChange={(e) => setQuery(e.target.value)}
              />
            </label>
            <ul className="assign-modal-list">
              {matches.length === 0 && query.trim().length > 0 ? (
                <li className="muted assign-modal-empty">{t('face.noPersonFound')}</li>
              ) : (
                matches.map((p) => (
                  <li key={p.personId}>
                    <button type="button" className="assign-modal-person" disabled={busy} onClick={() => setTarget(p)}>
                      {p.name ?? t('people.unnamed')}
                    </button>
                  </li>
                ))
              )}
            </ul>
            <div className="assign-modal-create">
              <input
                type="text" placeholder={t('face.createNewPerson')} aria-label={t('face.createNewPerson')}
                value={newName} disabled={busy} onChange={(e) => setNewName(e.target.value)}
              />
            </div>
          </>
        )}

        {preview && (
          <ul className="cluster-assign-counts">
            <li>{t('cluster.willAssign')} <strong>{preview.assignedCount}</strong></li>
            {preview.skippedAlreadyAssignedCount > 0 && (
              <li>{t('cluster.skippedAssigned')} {preview.skippedAlreadyAssignedCount}</li>
            )}
            {preview.skippedIgnoredCount > 0 && <li>{t('cluster.skippedIgnored')} {preview.skippedIgnoredCount}</li>}
            {preview.skippedIneligibleCount > 0 && <li>{t('cluster.skippedIneligible')} {preview.skippedIneligibleCount}</li>}
          </ul>
        )}

        {(preview?.skippedAlreadyAssignedCount ?? 0) > 0 && (
          <label className="cluster-assign-move">
            <input type="checkbox" checked={moveAssigned} disabled={busy} onChange={(e) => setMoveAssigned(e.target.checked)} />
            {t('cluster.moveAssigned')}
          </label>
        )}

        <div className="assign-modal-actions">
          <button type="button" className="assign-modal-confirm" disabled={!canConfirm} onClick={() => void confirm()}>
            {t('cluster.associate')}
          </button>
          <button type="button" className="assign-modal-cancel" disabled={busy} onClick={onClose}>{t('common.cancel')}</button>
        </div>
      </div>
    </div>,
    document.body,
  );
}
