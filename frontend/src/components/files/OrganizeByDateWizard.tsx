import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError } from '@nubarca/api-client';
import { useAuth } from '../../auth/useAuth';
import { useI18n, type MessageKey } from '../../i18n';
import type { I18nContextValue } from '../../i18n';

type TranslateFn = I18nContextValue['t'];
type PluralFn = I18nContextValue['tn'];
import {
  getOrganizerRunStatus,
  organizerDryRun,
  organizerRun,
  ORGANIZER_TERMINAL,
  type MissingDateBehavior,
  type OrganizerConflictPolicy,
  type OrganizerDryRunResponse,
  type OrganizerRequest,
  type OrganizerRunStatus,
  type OrganizerScope,
  type OrganizerTemplate,
} from '@nubarca/api-client';

interface OrganizeByDateWizardProps {
  currentFolderId: string | null;
  currentFolderName: string;
  selectedFileIds: string[];
  onClose(): void;
  // Bubbles a banner + asks the browser to reload once a run reaches a terminal
  // state (files have moved).
  onDone(message: { tone: 'info' | 'error'; text: string }): void;
}

type Phase = 'configure' | 'preview' | 'running' | 'result';

const TEMPLATES: { value: OrganizerTemplate; labelKey: MessageKey }[] = [
  { value: 'yyyy/yyyy-MM-dd', labelKey: 'organizer.tplYearFullDate' },
  { value: 'yyyy/MM/dd', labelKey: 'organizer.tplYMD' },
  { value: 'yyyy/MM', labelKey: 'organizer.tplYM' },
  { value: 'yyyy', labelKey: 'organizer.tplY' },
];

// "Organize photos by date" wizard: configure → preview (dry-run) → run
// (background job) → result. A single scrollable panel (full-screen sheet on
// mobile). Never renders storage internals — only logical paths + counts.
export function OrganizeByDateWizard({
  currentFolderId,
  currentFolderName,
  selectedFileIds,
  onClose,
  onDone,
}: OrganizeByDateWizardProps) {
  const { invalidateAuth } = useAuth();
  const { t, tn } = useI18n();
  const hasSelection = selectedFileIds.length > 0;

  const [phase, setPhase] = useState<Phase>('configure');
  const [scope, setScope] = useState<OrganizerScope>(hasSelection ? 'selected' : 'folder');
  const [template, setTemplate] = useState<OrganizerTemplate>('yyyy/yyyy-MM-dd');
  const [missing, setMissing] = useState<MissingDateBehavior>('skip');
  const [conflict, setConflict] = useState<OrganizerConflictPolicy>('keep_both');
  const [targetRootName, setTargetRootName] = useState('Photos');
  const [useCurrentBase, setUseCurrentBase] = useState(false);

  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [dryRun, setDryRun] = useState<OrganizerDryRunResponse | null>(null);
  const [runId, setRunId] = useState<string | null>(null);
  const [status, setStatus] = useState<OrganizerRunStatus | null>(null);

  const buildRequest = useCallback((): OrganizerRequest => ({
    scope,
    folderId: scope === 'folder' || scope === 'folder_recursive' ? currentFolderId : undefined,
    fileIds: scope === 'selected' ? selectedFileIds : undefined,
    targetRootFolderId: useCurrentBase ? currentFolderId : null,
    targetRootName: targetRootName.trim().length > 0 ? targetRootName.trim() : null,
    template,
    missingDateBehavior: missing,
    conflictPolicy: conflict,
  }), [scope, currentFolderId, selectedFileIds, useCurrentBase, targetRootName, template, missing, conflict]);

  function handleError(err: unknown, fallback: string) {
    if (err instanceof ApiError && err.status === 401) {
      invalidateAuth();
      return;
    }
    if (err instanceof ApiError) {
      const fromBody =
        typeof err.body === 'object' && err.body !== null && 'error' in err.body
          ? (err.body as { error?: unknown }).error
          : undefined;
      setError(typeof fromBody === 'string' && fromBody.length > 0 ? fromBody : fallback);
      return;
    }
    setError(fallback);
  }

  async function onPreview() {
    setBusy(true);
    setError(null);
    try {
      const result = await organizerDryRun(buildRequest());
      setDryRun(result);
      setPhase('preview');
    } catch (err) {
      handleError(err, t('organizer.previewError'));
    } finally {
      setBusy(false);
    }
  }

  async function onExecute() {
    setBusy(true);
    setError(null);
    try {
      const result = await organizerRun(buildRequest());
      setRunId(result.runId);
      setPhase('running');
    } catch (err) {
      handleError(err, t('organizer.startError'));
    } finally {
      setBusy(false);
    }
  }

  // Poll the run status while running. Stops on terminal state.
  const pollTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => {
    if (phase !== 'running' || runId === null) return;
    let cancelled = false;

    const poll = async () => {
      try {
        const s = await getOrganizerRunStatus(runId);
        if (cancelled) return;
        setStatus(s);
        if (ORGANIZER_TERMINAL.has(s.status)) {
          setPhase('result');
          onDone({
            tone: s.status === 'failed' ? 'error' : 'info',
            text: resultBanner(t, tn, s),
          });
          return;
        }
      } catch (err) {
        if (cancelled) return;
        if (err instanceof ApiError && err.status === 401) {
          invalidateAuth();
          return;
        }
        // transient — keep polling
      }
      pollTimer.current = setTimeout(() => void poll(), 1500);
    };
    void poll();

    return () => {
      cancelled = true;
      if (pollTimer.current) clearTimeout(pollTimer.current);
    };
  }, [phase, runId, onDone, invalidateAuth, t, tn]);

  const folderLabel = currentFolderId === null ? t('common.home') : currentFolderName;

  return (
    <div className="files-modal" role="dialog" aria-modal="true" aria-label={t('organizer.dialogAria')}>
      <div className="files-modal-backdrop" onClick={() => !busy && onClose()} />
      <div className="files-modal-content organizer-wizard">
        <div className="organizer-head">
          <h2>{t('organizer.heading')}</h2>
          <button type="button" className="details-panel-close" aria-label={t('common.close')} onClick={onClose}>✕</button>
        </div>

        {phase === 'configure' && (
          <div className="organizer-form">
            <fieldset className="organizer-field">
              <legend>{t('organizer.whichPhotos')}</legend>
              {hasSelection && (
                <label className="organizer-radio">
                  <input type="radio" name="scope" checked={scope === 'selected'} onChange={() => setScope('selected')} />
                  {t('organizer.scopeSelected', { count: selectedFileIds.length })}
                </label>
              )}
              <label className="organizer-radio">
                <input type="radio" name="scope" checked={scope === 'folder'} onChange={() => setScope('folder')} />
                {t('organizer.scopeFolder', { folder: folderLabel })}
              </label>
              <label className="organizer-radio">
                <input type="radio" name="scope" checked={scope === 'folder_recursive'} onChange={() => setScope('folder_recursive')} />
                {t('organizer.scopeFolderRecursive')}
              </label>
              <label className="organizer-radio">
                <input type="radio" name="scope" checked={scope === 'media_library'} onChange={() => setScope('media_library')} />
                {t('organizer.scopeMediaLibrary')}
              </label>
              <label className="organizer-radio">
                <input type="radio" name="scope" checked={scope === 'all'} onChange={() => setScope('all')} />
                {t('organizer.scopeAll')}
              </label>
            </fieldset>

            <div className="organizer-field">
              <label htmlFor="org-root">{t('organizer.targetRootLabel')}</label>
              <input
                id="org-root"
                type="text"
                value={targetRootName}
                maxLength={255}
                onChange={(e) => setTargetRootName(e.target.value)}
                placeholder="Photos"
              />
              <label className="organizer-checkbox">
                <input type="checkbox" checked={useCurrentBase} onChange={(e) => setUseCurrentBase(e.target.checked)} />
                {t('organizer.createInsideCurrent', { folder: folderLabel })}
              </label>
            </div>

            <div className="organizer-field">
              <label htmlFor="org-template">{t('organizer.folderStructure')}</label>
              <select id="org-template" value={template} onChange={(e) => setTemplate(e.target.value as OrganizerTemplate)}>
                {TEMPLATES.map((tpl) => <option key={tpl.value} value={tpl.value}>{t(tpl.labelKey)}</option>)}
              </select>
            </div>

            <fieldset className="organizer-field">
              <legend>{t('organizer.photosWithoutDate')}</legend>
              <label className="organizer-radio">
                <input type="radio" name="missing" checked={missing === 'skip'} onChange={() => setMissing('skip')} />
                {t('organizer.missingSkip')}
              </label>
              <label className="organizer-radio">
                <input type="radio" name="missing" checked={missing === 'file_created'} onChange={() => setMissing('file_created')} />
                {t('organizer.missingUpload')}
              </label>
              <label className="organizer-radio">
                <input type="radio" name="missing" checked={missing === 'unknown_folder'} onChange={() => setMissing('unknown_folder')} />
                {t('organizer.missingUnknown')}
              </label>
            </fieldset>

            <fieldset className="organizer-field">
              <legend>{t('organizer.ifNameExists')}</legend>
              <label className="organizer-radio">
                <input type="radio" name="conflict" checked={conflict === 'keep_both'} onChange={() => setConflict('keep_both')} />
                {t('organizer.conflictKeepBoth')}
              </label>
              <label className="organizer-radio">
                <input type="radio" name="conflict" checked={conflict === 'skip'} onChange={() => setConflict('skip')} />
                {t('organizer.conflictSkip')}
              </label>
            </fieldset>

            {error !== null && <p className="row-inline-error" role="alert">{error}</p>}

            <div className="organizer-actions">
              <button type="button" className="row-action-primary" onClick={() => void onPreview()} disabled={busy}>
                {busy ? t('organizer.previewing') : t('organizer.preview')}
              </button>
              <button type="button" className="files-action" onClick={onClose} disabled={busy}>{t('common.cancel')}</button>
            </div>
          </div>
        )}

        {phase === 'preview' && dryRun !== null && (
          <div className="organizer-preview">
            <dl className="organizer-summary">
              <div><dt>{t('organizer.sumInScope')}</dt><dd>{dryRun.summary.candidateCount}</dd></div>
              <div><dt>{t('organizer.sumWithDate')}</dt><dd>{dryRun.summary.withDateCount}</dd></div>
              <div><dt>{t('organizer.sumMissingDate')}</dt><dd>{dryRun.summary.missingDateCount}</dd></div>
              <div><dt>{t('organizer.sumWillMove')}</dt><dd>{dryRun.summary.toMoveCount}</dd></div>
              <div><dt>{t('organizer.sumAlready')}</dt><dd>{dryRun.summary.alreadyOrganizedCount}</dd></div>
              <div><dt>{t('organizer.sumSkippedNoDate')}</dt><dd>{dryRun.summary.skippedMissingCount}</dd></div>
              <div><dt>{t('organizer.sumSkippedConflict')}</dt><dd>{dryRun.summary.skippedConflictCount}</dd></div>
              <div><dt>{t('organizer.sumFoldersToCreate')}</dt><dd>{dryRun.summary.foldersToCreateCount}</dd></div>
            </dl>

            {dryRun.samples.length > 0 && (
              <div className="organizer-samples">
                <h3>{t('organizer.examples')}</h3>
                <ul>
                  {dryRun.samples.slice(0, 8).map((s, i) => (
                    <li key={i}>
                      <span className="organizer-sample-name" title={s.name}>{s.name}</span>
                      <span className="organizer-sample-arrow" aria-hidden="true"> → </span>
                      <span className="organizer-sample-target" title={s.targetPath}>
                        {s.action === 'move' ? s.targetPath : `(${actionLabel(t, s.action)})`}
                      </span>
                    </li>
                  ))}
                </ul>
              </div>
            )}

            {error !== null && <p className="row-inline-error" role="alert">{error}</p>}

            <div className="organizer-actions">
              <button type="button" className="files-action" onClick={() => setPhase('configure')} disabled={busy}>{t('common.back')}</button>
              <button
                type="button"
                className="row-action-primary"
                onClick={() => void onExecute()}
                disabled={busy || dryRun.summary.toMoveCount === 0}
              >
                {busy ? t('organizer.starting') : tn(dryRun.summary.toMoveCount, 'organizer.organizeN')}
              </button>
            </div>
          </div>
        )}

        {phase === 'running' && (
          <div className="organizer-running" role="status" aria-live="polite">
            <p>{t('organizer.runningNote')}</p>
            {status !== null && (
              <p className="organizer-progress">
                {t('organizer.progress', {
                  moved: status.movedCount,
                  already: status.alreadyOrganizedCount,
                  skipped: status.skippedMissingDateCount + status.skippedConflictCount,
                })}
                {status.candidateCount > 0 && ` · ${status.movedCount + status.alreadyOrganizedCount + status.skippedMissingDateCount + status.skippedConflictCount + status.failedCount}/${status.candidateCount}`}
              </p>
            )}
            <div className="organizer-actions">
              <button type="button" className="files-action" onClick={onClose}>{t('common.close')}</button>
            </div>
          </div>
        )}

        {phase === 'result' && status !== null && (
          <div className="organizer-result">
            <p className="organizer-result-headline">{resultBanner(t, tn, status)}</p>
            <dl className="organizer-summary">
              <div><dt>{t('organizer.resMoved')}</dt><dd>{status.movedCount}</dd></div>
              <div><dt>{t('organizer.resAlready')}</dt><dd>{status.alreadyOrganizedCount}</dd></div>
              <div><dt>{t('organizer.resSkippedNoDate')}</dt><dd>{status.skippedMissingDateCount}</dd></div>
              <div><dt>{t('organizer.resSkippedConflict')}</dt><dd>{status.skippedConflictCount}</dd></div>
              <div><dt>{t('organizer.resFailed')}</dt><dd>{status.failedCount}</dd></div>
              <div><dt>{t('organizer.resFoldersCreated')}</dt><dd>{status.foldersCreatedCount}</dd></div>
            </dl>
            <div className="organizer-actions">
              <button type="button" className="row-action-primary" onClick={onClose}>{t('organizer.done')}</button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}

function actionLabel(t: TranslateFn, action: string): string {
  switch (action) {
    case 'already': return t('organizer.actAlready');
    case 'skip_missing': return t('organizer.actSkipMissing');
    case 'skip_conflict': return t('organizer.actSkipConflict');
    default: return action;
  }
}

function resultBanner(t: TranslateFn, tn: PluralFn, s: OrganizerRunStatus): string {
  if (s.status === 'cancelled') return tn(s.movedCount, 'organizer.bannerCancelled');
  if (s.status === 'failed') return t('organizer.bannerFailed');
  const base = tn(s.movedCount, 'organizer.bannerOrganized');
  return s.failedCount > 0 ? `${base}${t('organizer.bannerFailedSuffix', { count: s.failedCount })}` : base;
}
