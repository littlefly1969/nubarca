import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  getFaceClusterRebuildStatus,
  startFaceClusterRebuild,
  type FaceClusterRebuildStatus,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';
import { Modal } from '../components/Overlay';

// "Ricalcola cluster volti" — the owner rebuilding their OWN automatic face
// groups.
//
// What it starts is a single owner-scoped job, not the administrative backfill
// that walks every account, so this panel needs no administration authority to
// watch it: /api/people/cluster-rebuild/{id} answers for the caller's own job
// and 404s for anybody else's.
//
// It asks for confirmation because the run replaces a whole derived layer of
// this account's People and can take a while — through the shared Overlay, so
// the confirmation inherits the focus trap, the scroll lock and the keyboard
// ownership every other dialog has rather than reimplementing them here.

// Slow enough not to hammer the API while a long clustering runs, quick enough
// that a short one does not look stuck.
const POLL_MS = 3000;

type Phase =
  | { kind: 'idle' }
  | { kind: 'starting' }
  | { kind: 'watching'; jobId: string; status: FaceClusterRebuildStatus | null; queuedOnly: boolean }
  | { kind: 'done' }
  | { kind: 'failed'; code: string | null }
  | { kind: 'unavailable'; reason: string | null };

export function FaceClusterRebuildPanel() {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const [phase, setPhase] = useState<Phase>({ kind: 'idle' });
  const [confirming, setConfirming] = useState(false);
  // The poll must stop when this panel goes away — switching Cloud tools
  // unmounts it, and a timer that outlived it would keep asking about a job
  // nobody is looking at.
  const alive = useRef(true);
  useEffect(() => () => { alive.current = false; }, []);

  const watch = useCallback(async (jobId: string, controller: AbortController) => {
    try {
      const status = await getFaceClusterRebuildStatus(jobId, controller.signal);
      if (!alive.current || controller.signal.aborted) return;
      if (status.status === 'succeeded') { setPhase({ kind: 'done' }); return; }
      if (status.status === 'failed' || status.status === 'cancelled') {
        setPhase({ kind: 'failed', code: status.lastErrorCode });
        return;
      }
      setPhase({ kind: 'watching', jobId, status, queuedOnly: status.status === 'queued' });
    } catch (err) {
      if (controller.signal.aborted || !alive.current) return;
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      // A status read that fails is not a failed CLUSTERING — the job may well
      // be running. Keep watching rather than reporting an outcome that has not
      // happened.
    }
  }, [invalidateAuth]);

  useEffect(() => {
    if (phase.kind !== 'watching') return;
    const controller = new AbortController();
    const id = window.setTimeout(() => { void watch(phase.jobId, controller); }, POLL_MS);
    return () => { controller.abort(); window.clearTimeout(id); };
  }, [phase, watch]);

  async function start() {
    setConfirming(false);
    setPhase({ kind: 'starting' });
    try {
      const started = await startFaceClusterRebuild();
      if (!alive.current) return;
      setPhase({
        kind: 'watching',
        jobId: started.jobId,
        status: null,
        queuedOnly: started.status === 'queued',
      });
    } catch (err) {
      if (!alive.current) return;
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      if (err instanceof ApiError && err.status === 409) {
        // The installation cannot cluster at all. A stable machine token, not a
        // server message — the copy is ours to write.
        const reason = typeof (err.body as { reason?: unknown } | null)?.reason === 'string'
          ? (err.body as { reason: string }).reason
          : null;
        setPhase({ kind: 'unavailable', reason });
        return;
      }
      setPhase({ kind: 'failed', code: null });
    }
  }

  const busy = phase.kind === 'starting' || phase.kind === 'watching';

  return (
    <div className="face-cluster-rebuild" data-testid="face-cluster-rebuild">
      <p>{t('faceCluster.explain')}</p>
      <p className="muted">{t('faceCluster.preserved')}</p>

      <button
        type="button"
        className="row-action"
        data-testid="face-cluster-start"
        disabled={busy}
        onClick={() => setConfirming(true)}
      >
        {t('faceCluster.action')}
      </button>

      <p className="face-cluster-rebuild__status" role="status" data-testid="face-cluster-status">
        {phase.kind === 'starting' && t('faceCluster.queued')}
        {phase.kind === 'watching' && (
          phase.queuedOnly ? t('faceCluster.queued') : t('faceCluster.running')
        )}
        {phase.kind === 'done' && t('faceCluster.done')}
        {phase.kind === 'failed' && (
          phase.code
            ? t('faceCluster.failedWithCode', { code: phase.code })
            : t('faceCluster.failed')
        )}
        {phase.kind === 'unavailable' && t('faceCluster.unavailable')}
      </p>

      {confirming && (
        <Modal
          title={t('faceCluster.confirmTitle')}
          onClose={() => setConfirming(false)}
          testId="face-cluster-confirm"
          footer={(
            <>
              <button
                type="button"
                className="row-action"
                data-testid="face-cluster-confirm-cancel"
                onClick={() => setConfirming(false)}
              >
                {t('common.cancel')}
              </button>
              <button
                type="button"
                className="row-action row-action--primary"
                data-testid="face-cluster-confirm-run"
                onClick={() => void start()}
              >
                {t('faceCluster.action')}
              </button>
            </>
          )}
        >
          <p>{t('faceCluster.confirmBody')}</p>
          <p className="muted">{t('faceCluster.preserved')}</p>
        </Modal>
      )}
    </div>
  );
}
