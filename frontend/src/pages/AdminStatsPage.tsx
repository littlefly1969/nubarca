import { useCallback, useEffect, useState } from 'react';
import { ApiError } from '@nubarca/api-client';
import {
  getStorageStats,
  getMediumPreviewStatus,
  rebuildMediumPreviews,
  type AdminMediumPreviewStatus,
  type DerivativeDiagnosticSizeStats,
  type StorageStats,
  type StorageStatsDiagnostics,
  type SweeperConfig,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { formatSize } from '../components/format';
import { useI18n } from '../i18n';

type Status =
  | { kind: 'loading' }
  | { kind: 'ready'; data: StorageStats }
  | { kind: 'forbidden' }
  | { kind: 'error'; message: string };

// Minimal "is the deployment OK?" dashboard backed by
// `GET /api/admin/storage-stats`. Aggregate numbers only — never renders
// ids, names, paths, or tokens (the API does not return any of those).
// 401 falls back to invalidateAuth(); 403 shows a friendly message
// (typically when a previously-admin user was demoted out-of-band).
export function AdminStatsPage() {
  const { state, invalidateAuth } = useAuth();
  const { t } = useI18n();
  const [status, setStatus] = useState<Status>({ kind: 'loading' });

  const load = useCallback(
    async (refresh = false, includePhysical = false, signal?: AbortSignal) => {
      setStatus({ kind: 'loading' });
      try {
        const data = await getStorageStats(refresh, includePhysical, signal);
        setStatus({ kind: 'ready', data });
      } catch (err) {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 401) {
          invalidateAuth();
          return;
        }
        if (err instanceof ApiError && err.status === 403) {
          setStatus({ kind: 'forbidden' });
          return;
        }
        setStatus({
          kind: 'error',
          message: t('adminStats.loadError'),
        });
      }
    },
    [invalidateAuth, t],
  );

  useEffect(() => {
    const controller = new AbortController();
    // Fast first load: skip the expensive physical blob-store scan.
    void load(false, false, controller.signal);
    return () => controller.abort();
  }, [load]);

  if (state.status !== 'authed') {
    // ProtectedRoute already enforces this; keeps TS happy.
    return null;
  }

  return (
    <section className="admin-page" aria-busy={status.kind === 'loading'}>
      <header className="admin-header">
        <h2>{t('adminStats.heading')}</h2>
        <button
          type="button"
          className="refresh-button"
          onClick={() => void load(true, false)}
          disabled={status.kind === 'loading'}
        >
          {t('common.refresh')}
        </button>
      </header>

      {status.kind === 'ready' && status.data.diagnostics && (
        <StatsDiagnostics diagnostics={status.data.diagnostics} />
      )}

      {status.kind === 'loading' && (
        <p className="muted" role="status">
          {t('adminStats.loading')}
        </p>
      )}

      {status.kind === 'forbidden' && (
        <div className="folder-error" role="alert">
          {t('adminStats.forbidden')}
        </div>
      )}

      {status.kind === 'error' && (
        <div className="folder-error" role="alert">
          {status.message}
          <button
            type="button"
            className="retry-button"
            onClick={() => void load(true, false)}
          >
            {t('common.tryAgain')}
          </button>
        </div>
      )}

      {status.kind === 'ready' && (
        <StatsBody
          data={status.data}
          onRunIntegrityCheck={() => void load(false, true)}
        />
      )}
    </section>
  );
}

// Slice 84: safe phase-timing + cache banner. Numbers + a timestamp only.
function StatsDiagnostics({ diagnostics: d }: { diagnostics: StorageStatsDiagnostics }) {
  const { t } = useI18n();
  return (
    <p className="muted admin-stats-diagnostics" role="status">
      {d.cached
        ? t('adminStats.diagCached', { age: d.ageSeconds })
        : t('adminStats.diagComputed', { ms: d.totalMillis })}
      {' '}
      {t('adminStats.diagPhases', {
        core: d.coreMillis,
        phys: d.physicalScanMillis,
        deriv: d.derivativeScanMillis,
        meta: d.metadataAggregateMillis,
      })}
    </p>
  );
}

function IntegrityCard({
  data, onRunIntegrityCheck,
}: {
  data: StorageStats;
  onRunIntegrityCheck: () => void;
}) {
  const { t } = useI18n();
  const ran = data.diagnostics?.physicalScanIncluded === true;
  return (
    <StatCard title={t('adminStats.integrityTitle')}>
      {ran ? (
        <>
          <Row label={t('adminStats.physicalBlobs')} value={data.blobs.physicalBlobCount ?? -1} />
          <Row label={t('adminStats.missingOnDisk')} value={data.blobs.missingPhysicalBlobCount ?? -1} />
          <Row label={t('adminStats.unreferencedOnDisk')} value={data.blobs.unreferencedPhysicalBlobCount ?? -1} />
          {data.derivedReadiness && (
            <>
              <Row
                label={t('adminStats.derivativesInDerivedRoot')}
                value={`${data.derivedReadiness.presentInDerivedRoot} / ${data.derivedReadiness.thumbnailRowsTotal}`}
              />
              <Row
                label={t('adminStats.onlyInOriginalRoot')}
                value={data.derivedReadiness.onlyInOriginalRoot}
              />
              <Row
                label={t('adminStats.derivativeBytesMissing')}
                value={data.derivedReadiness.missingFromBoth}
              />
              {data.derivedReadiness.onlyInOriginalRoot > 0 && (
                <p className="muted">
                  {t('adminStats.displacedNote')}{' '}
                  <code>media derivatives repair-bytes</code> {t('adminStats.displacedNoteAfter')}
                </p>
              )}
            </>
          )}
          {data.referenceIntegrity && (
            <>
              <Row
                label={t('adminStats.refcountMismatches')}
                value={data.referenceIntegrity.refcountMismatchCount}
              />
              <Row
                label={t('adminStats.leakedRefs')}
                value={data.referenceIntegrity.orphanedNonzeroRefcountCount}
              />
              <Row
                label={t('adminStats.zeroRefWithOwners')}
                value={data.referenceIntegrity.zeroRefWithRealReferencesCount}
              />
              {data.referenceIntegrity.refcountMismatchCount > 0 && (
                <p className="muted">
                  {t('adminStats.refDriftNote')}{' '}
                  <code>storage blobs repair-references</code> {t('adminStats.refDriftNoteAfter')}
                </p>
              )}
            </>
          )}
        </>
      ) : (
        <>
          <p className="muted">
            {t('adminStats.physScanSkipped')}
          </p>
          <button type="button" className="row-action-primary" onClick={onRunIntegrityCheck}>
            {t('adminStats.runIntegrityCheck')}
          </button>
        </>
      )}
    </StatCard>
  );
}

function MediumPreviewAdminCard() {
  const { t } = useI18n();
  const [status, setStatus] = useState<
    | { kind: 'loading' }
    | { kind: 'ready'; data: AdminMediumPreviewStatus }
    | { kind: 'queued'; edge: number; jobStatus: string }
    | { kind: 'error' }
  >({ kind: 'loading' });
  const [busy, setBusy] = useState(false);
  const [confirming, setConfirming] = useState(false);

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      setStatus({ kind: 'ready', data: await getMediumPreviewStatus(signal) });
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      setStatus({ kind: 'error' });
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  async function rebuild() {
    setBusy(true);
    try {
      const result = await rebuildMediumPreviews();
      setStatus({
        kind: 'queued',
        edge: result.mediumPreviewMaxEdge,
        jobStatus: result.status,
      });
    } catch {
      setStatus({ kind: 'error' });
    } finally {
      setConfirming(false);
      setBusy(false);
    }
  }

  const edge = status.kind === 'ready'
    ? status.data.mediumPreviewMaxEdge
    : status.kind === 'queued'
      ? status.edge
      : null;
  const jobStatus = status.kind === 'ready'
    ? status.data.job?.status ?? null
    : status.kind === 'queued'
      ? status.jobStatus
      : null;
  const active = jobStatus === 'queued' || jobStatus === 'running';

  return (
    <StatCard title={t('adminStats.mediumPreviewTitle')}>
      {edge !== null && (
        <p className="muted">
          {t('adminStats.mediumPreviewMaxEdge', { edge })}
        </p>
      )}
      {status.kind === 'loading' && (
        <p className="muted" role="status">{t('adminStats.mediumPreviewLoading')}</p>
      )}
      {jobStatus && (
        <p className="muted" role="status">
          {t('adminStats.mediumPreviewJobStatus', { status: jobStatus })}
        </p>
      )}
      {status.kind === 'error' && (
        <p className="folder-error">
          {t('adminStats.mediumPreviewError')}
        </p>
      )}
      <button
        type="button"
        className="row-action-primary"
        onClick={() => setConfirming(true)}
        disabled={busy || active}
      >
        {busy ? t('adminStats.mediumPreviewRequesting') : t('adminStats.mediumPreviewButton')}
      </button>
      {confirming && (
        <div
          className="admin-confirm-dialog"
          role="dialog"
          aria-modal="true"
          aria-label={t('adminStats.mediumPreviewButton')}
        >
          <p>{t('adminStats.mediumPreviewConfirm')}</p>
          <div className="admin-import-actions">
            <button type="button" className="row-action" onClick={() => setConfirming(false)}>
              {t('common.cancel')}
            </button>
            <button
              type="button"
              className="row-action-primary"
              onClick={() => void rebuild()}
              disabled={busy}
            >
              {t('adminStats.mediumPreviewConfirmAction')}
            </button>
          </div>
        </div>
      )}
    </StatCard>
  );
}

function StatsBody({
  data, onRunIntegrityCheck,
}: {
  data: StorageStats;
  onRunIntegrityCheck: () => void;
}) {
  const { t } = useI18n();
  return (
    <div className="admin-grid">
      <StatCard title={t('adminStats.cardUsers')}>
        <Row label={t('adminStats.total')} value={data.users.total} />
        <Row label={t('adminStats.active')} value={data.users.active} />
        <Row label={t('adminStats.disabled')} value={data.users.disabled} />
      </StatCard>

      <StatCard title={t('adminStats.cardFolders')}>
        <Row label={t('adminStats.total')} value={data.folders.total} />
        <Row label={t('adminStats.active')} value={data.folders.active} />
        <Row label={t('adminStats.inTrash')} value={data.folders.softDeleted} />
      </StatCard>

      <StatCard title={t('adminStats.cardFiles')}>
        <Row label={t('adminStats.total')} value={data.files.total} />
        <Row label={t('adminStats.active')} value={data.files.active} />
        <Row label={t('adminStats.inTrash')} value={data.files.softDeleted} />
        <Row
          label={t('adminStats.logicalActive')}
          value={formatSize(data.files.logicalBytesTotal)}
        />
        <Row
          label={t('adminStats.logicalInclTrash')}
          value={formatSize(data.files.logicalBytesIncludingTrash)}
        />
      </StatCard>

      <StatCard title={t('adminStats.cardBlobs')}>
        <Row label={t('adminStats.total')} value={data.blobs.total} />
        <Row label={t('adminStats.zeroReference')} value={data.blobs.zeroReference} />
        <Row
          label={t('adminStats.zeroRefBeyondGrace')}
          value={data.blobs.zeroReferenceBeyondGrace}
        />
        <Row
          label={t('adminStats.physicalBytes')}
          value={formatSize(data.blobs.physicalBytesTotal)}
        />
      </StatCard>

      <IntegrityCard data={data} onRunIntegrityCheck={onRunIntegrityCheck} />

      <StatCard title={t('adminStats.cardImages')}>
        <Row label={t('adminStats.imageFiles')} value={data.images.imageFilesCount} />
        <Row
          label={t('adminStats.withDimensions')}
          value={data.images.filesWithDimensionsCount}
        />
        <Row label={t('adminStats.thumbnails')} value={data.images.thumbnailCount} />
        <Row
          label={t('adminStats.thumbnailBytes')}
          value={formatSize(data.images.thumbnailBlobBytes)}
        />
      </StatCard>

      <StatCard title={t('adminStats.cardShareLinks')}>
        <Row label={t('adminStats.total')} value={data.shareLinks.total} />
        <Row label={t('adminStats.active')} value={data.shareLinks.active} />
        <Row label={t('adminStats.revoked')} value={data.shareLinks.revoked} />
        <Row label={t('adminStats.expired')} value={data.shareLinks.expired} />
        <Row label={t('adminStats.exhausted')} value={data.shareLinks.exhausted} />
      </StatCard>

      <StatCard title={t('adminStats.cardAudit')}>
        <Row label={t('adminStats.auditRows')} value={data.audit.total} />
      </StatCard>

      {/* ---- Slice 64: media / metadata diagnostics ---------------------- */}

      <StatCard title={t('adminStats.cardMedia')}>
        <Row label={t('adminStats.imagesDetected')} value={data.media.imagesCount} />
        <Row label={t('adminStats.videosDetected')} value={data.media.videosCount} />
        <Row label={t('adminStats.audio')} value={data.media.audioCount} />
        <Row label={t('adminStats.documents')} value={data.media.documentsCount} />
        <Row label={t('adminStats.otherUnknown')} value={data.media.otherCount} />
      </StatCard>

      <StatCard title={t('adminStats.cardMetadataExtraction')}>
        <Row label={t('adminStats.completed')} value={data.extraction.completed} />
        <Row label={t('adminStats.pending')} value={data.extraction.pending} />
        <Row label={t('adminStats.skipped')} value={data.extraction.skipped} />
        <Row label={t('adminStats.failed')} value={data.extraction.failed} />
        <Row label={t('adminStats.extractorVersion')} value={data.extraction.currentVersion} />
        <Row label={t('adminStats.atCurrentVersion')} value={data.extraction.atCurrentVersion} />
        <Row label={t('adminStats.belowCurrentVersion')} value={data.extraction.belowCurrentVersion} />
        <Row label={t('adminStats.errUnsupportedFormat')} value={data.extraction.unsupportedFormatErrors} />
        <Row label={t('adminStats.errIoError')} value={data.extraction.ioErrors} />
        <Row label={t('adminStats.errUnexpected')} value={data.extraction.unexpectedErrors} />
        <Row label={t('adminStats.errRawTruncated')} value={data.extraction.rawTruncatedErrors} />
      </StatCard>

      <StatCard title={t('adminStats.cardDerivedArtifacts')}>
        <Row label={t('adminStats.smallThumbnails')} value={data.derivatives.smallThumbnailCount} />
        <Row label={t('adminStats.mediumPreviews')} value={data.derivatives.mediumPreviewCount} />
        <Row label={t('adminStats.videoPosters')} value={data.derivatives.videoPosterCount} />
        <Row label={t('adminStats.imagesMissingSmall')} value={data.derivatives.imagesMissingSmall} />
        <Row label={t('adminStats.imagesMissingMedium')} value={data.derivatives.imagesMissingMedium} />
        <Row label={t('adminStats.videosMissingPoster')} value={data.derivatives.videosMissingPoster} />
      </StatCard>

      <MediumPreviewAdminCard />

      {data.derivativeDiagnostics && (
        <StatCard title={t('adminStats.cardDerivativeDiagnostics')}>
          <DiagnosticSizeBlock label={t('adminStats.sizeSmall')} size={data.derivativeDiagnostics.small} />
          <DiagnosticSizeBlock label={t('adminStats.sizeMedium')} size={data.derivativeDiagnostics.medium} />
          <DiagnosticSizeBlock label={t('adminStats.sizePoster')} size={data.derivativeDiagnostics.poster} />
        </StatCard>
      )}

      <StatCard title={t('adminStats.cardUserMetadata')}>
        <Row label={t('adminStats.rows')} value={data.userMetadata.totalRows} />
        <Row label={t('adminStats.withTitle')} value={data.userMetadata.withTitle} />
        <Row label={t('adminStats.withDescription')} value={data.userMetadata.withDescription} />
        <Row label={t('adminStats.withTags')} value={data.userMetadata.withTags} />
        <Row label={t('adminStats.withRating')} value={data.userMetadata.withRating} />
        <Row label={t('adminStats.favorites')} value={data.userMetadata.favorites} />
        <Row label={t('adminStats.withDateTakenOverride')} value={data.userMetadata.withDateTakenOverride} />
        <Row label={t('adminStats.withLocationOverride')} value={data.userMetadata.withLocationOverride} />
      </StatCard>

      <StatCard title={t('adminStats.cardPrivacyAggregates')}>
        <Row label={t('adminStats.blobsWithGps')} value={data.sensitiveAggregates.blobsWithGps} />
        <Row label={t('adminStats.blobsWithRawDoc')} value={data.sensitiveAggregates.blobsWithRawDocument} />
        <Row label={t('adminStats.blobsWithBodySerial')} value={data.sensitiveAggregates.blobsWithBodySerial} />
        <Row label={t('adminStats.blobsWithLensSerial')} value={data.sensitiveAggregates.blobsWithLensSerial} />
        <Row label={t('adminStats.metadataEdits')} value={data.sensitiveAggregates.metadataUpdates} />
        <Row label={t('adminStats.metadataStrips')} value={data.sensitiveAggregates.metadataStripEvents} />
      </StatCard>

      <StatCard title={t('adminStats.cardCleanupConfig')}>
        <SweeperBlock
          label="FileItem sweeper"
          config={data.cleanup.fileItemSweeper}
        />
        <SweeperBlock
          label="Blob janitor"
          config={data.cleanup.blobJanitor}
        />
        {/* Operator hint surfaced inline. The janitor cannot purge until
            FileItemSweeper has cleared the soft-deleted FileItem rows; if
            its grace is GREATER than the janitor's, the janitor will just
            skip every tick. */}
        {data.cleanup.fileItemSweeper.graceMinutes
          > data.cleanup.blobJanitor.graceMinutes && (
          <p className="admin-warning" role="alert">
            {t('adminStats.graceWarning', {
              fs: data.cleanup.fileItemSweeper.graceMinutes,
              bj: data.cleanup.blobJanitor.graceMinutes,
            })}
          </p>
        )}
      </StatCard>
    </div>
  );
}

function StatCard({
  title,
  children,
}: {
  title: string;
  children: React.ReactNode;
}) {
  return (
    <section className="admin-card" aria-label={title}>
      <h3>{title}</h3>
      <dl className="admin-card-list">{children}</dl>
    </section>
  );
}

function Row({ label, value }: { label: string; value: number | string }) {
  return (
    <>
      <dt>{label}</dt>
      <dd>{typeof value === 'number' ? value.toLocaleString() : value}</dd>
    </>
  );
}

// Per-size derivative-diagnostic breakdown. Counts only — codes / MIME types
// are safe to render; the API never sends names, paths, ids, or keys here.
function DiagnosticSizeBlock({
  label,
  size,
}: {
  label: string;
  size: DerivativeDiagnosticSizeStats;
}) {
  const { t } = useI18n();
  const codes = size.byErrorCode.map((c) => `${c.code} ${c.count}`).join(', ');
  const formats = size.topFormats
    .map((f) => `${f.detectedContentType} ${f.count}`)
    .join(', ');
  return (
    <>
      <dt>{label}</dt>
      <dd>
        <div>
          {t('adminStats.diagSizeLine', {
            na: size.neverAttempted.toLocaleString(),
            fp: size.failedPermanent.toLocaleString(),
            ft: size.failedTransient.toLocaleString(),
            ne: size.notEligible.toLocaleString(),
            sk: size.skipped.toLocaleString(),
            rt: size.retryableNow.toLocaleString(),
          })}
        </div>
        {codes && <div className="muted">{t('adminStats.codes', { codes })}</div>}
        {formats && <div className="muted">{t('adminStats.formats', { formats })}</div>}
      </dd>
    </>
  );
}

function SweeperBlock({
  label,
  config,
}: {
  label: string;
  config: SweeperConfig;
}) {
  const { t } = useI18n();
  return (
    <div className="admin-sweeper">
      <strong>{label}</strong>{' '}
      <span
        className={`sweeper-pill sweeper-pill-${config.enabled ? 'on' : 'off'}`}
      >
        {config.enabled ? t('adminStats.sweeperEnabled') : t('adminStats.sweeperDisabled')}
      </span>
      <div className="admin-sweeper-detail muted">
        {t('adminStats.sweeperDetail', { interval: config.intervalMinutes, grace: config.graceMinutes })}
      </div>
    </div>
  );
}
