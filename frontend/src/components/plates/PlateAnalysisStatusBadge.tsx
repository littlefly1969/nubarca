import { useI18n } from '../../i18n';
import type { MessageKey } from '../../i18n';

// Maps the product analysis status to a localized, colour + text badge (never
// colour-only — the label carries the meaning).
const STATUS: Record<string, { key: MessageKey; cls: string }> = {
  not_started: { key: 'plates.analysis.notStarted', cls: 'is-idle' },
  pending: { key: 'plates.analysis.pending', cls: 'is-pending' },
  running: { key: 'plates.analysis.running', cls: 'is-running' },
  completed: { key: 'plates.analysis.completed', cls: 'is-completed' },
  failed: { key: 'plates.analysis.failed', cls: 'is-failed' },
};

export function PlateAnalysisStatusBadge({ status }: { status: string }) {
  const { t } = useI18n();
  const meta = STATUS[status] ?? STATUS.not_started;
  return (
    <span className={`plate-status-badge ${meta.cls}`} data-status={status}>
      {t(meta.key)}
    </span>
  );
}
