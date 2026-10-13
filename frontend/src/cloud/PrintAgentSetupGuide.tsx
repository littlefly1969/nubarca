import { useEffect, useState } from 'react';
import type { PrintAgentBundle, PrintStationEnrollment } from '@nubarca/api-client';
import { useI18n } from '../i18n';

type Platform = 'ubuntu' | 'windows';

/** The commands a Print Box runs, filled in with this installation and this station. */
export function printBoxCommands(origin: string, stationId: string, bundle: PrintAgentBundle | null) {
  const download = bundle?.downloadPath && bundle.fileName && bundle.sha256
    ? `curl -fLO ${origin}${bundle.downloadPath}\necho "${bundle.sha256}  ${bundle.fileName}" | sha256sum -c -`
    : null;
  const install = bundle?.fileName
    ? [
      // A running agent holds its files: stopped first, or the extraction fails.
      'sudo systemctl stop nubarca-print-agent@box 2>/dev/null || true',
      `sudo python3 -m zipfile -e ${bundle.fileName} /opt/nubarca-print-agent`,
      `sudo bash /opt/nubarca-print-agent/linux/install-print-box.sh --server ${origin} --station ${stationId}`,
    ].join('\n')
    : null;
  return { download, install };
}

function remainingOf(expiresAt: string, now: number): number {
  return Math.max(0, Math.floor((Date.parse(expiresAt) - now) / 1000));
}

/**
 * How to connect a Print Box to a station — or update the agent of one already
 * connected — from a terminal, step by step, with every command filled in.
 *
 * The one-shot activation code is never part of a command: the installer asks
 * for it without echoing it, so it stays out of the shell's history and the
 * process table. The download is checked against the hash NubArca publishes
 * before anything is installed.
 */
export function PrintAgentSetupGuide({
  stationName, stationId, enrollment, bundle, connectedVersion, onNewCode, onDone, onOpenStation,
}: {
  stationName: string;
  stationId: string;
  /** The activation code to connect a new box; null to update one already connected. */
  enrollment: PrintStationEnrollment | null;
  bundle: PrintAgentBundle | null;
  /** Set once the box has connected, for a station that had no agent yet. */
  connectedVersion: string | null;
  onNewCode: () => void;
  onDone: () => void;
  onOpenStation: () => void;
}) {
  const { t } = useI18n();
  const [platform, setPlatform] = useState<Platform>('ubuntu');
  const [copied, setCopied] = useState<string | null>(null);
  const [now, setNow] = useState(() => Date.now());
  const origin = window.location.origin;
  const { download, install } = printBoxCommands(origin, stationId, bundle);

  useEffect(() => {
    if (!enrollment) return undefined;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [enrollment]);

  async function copy(key: string, text: string) {
    try {
      await navigator.clipboard.writeText(text);
      setCopied(key);
      window.setTimeout(() => setCopied((current) => (current === key ? null : current)), 2000);
    } catch { /* The text stays selectable by hand. */ }
  }

  const command = (key: string, text: string) => (
    <div className="print-guide-command">
      <pre data-testid={`print-guide-${key}`}><code>{text}</code></pre>
      <button type="button" onClick={() => void copy(key, text)}>
        {t(copied === key ? 'print.guide.copied' : 'print.guide.copy')}
      </button>
    </div>
  );

  const remaining = enrollment ? remainingOf(enrollment.enrollmentExpiresAt, now) : 0;
  const clock = `${Math.floor(remaining / 60)}:${String(remaining % 60).padStart(2, '0')}`;

  return (
    <section className="print-guide" data-testid="print-guide" aria-labelledby="print-guide-title">
      <h3 id="print-guide-title">
        {t(enrollment ? 'print.guide.title' : 'print.guide.updateTitle', { name: stationName })}
      </h3>

      <div className="print-guide-platforms" role="radiogroup" aria-label={t('print.guide.platform')}>
        {(['ubuntu', 'windows'] as const).filter((p) => p === 'ubuntu' || enrollment).map((option) => (
          <label key={option} className="print-guide-platform">
            <input type="radio" name="print-guide-platform" value={option}
              checked={platform === option} onChange={() => setPlatform(option)} />
            <span>{t(option === 'ubuntu' ? 'print.guide.ubuntu' : 'print.guide.windows')}</span>
          </label>
        ))}
      </div>

      {platform === 'ubuntu' ? (
        <>
          <p>{t('print.guide.intro')}</p>
          <h4>{t('print.guide.download')}</h4>
          {download ? (
            <>
              {command('download', download)}
              <p className="muted">{t('print.guide.downloadHelp')}</p>
            </>
          ) : (
            <p className="print-guide-blocked" role="status" data-testid="print-guide-unavailable">
              {t(bundle?.canManage ? 'print.guide.unavailableAdmin' : 'print.guide.unavailable')}
            </p>
          )}
          {install && (
            <>
              <h4>{t('print.guide.install')}</h4>
              {command('install', install)}
              <p className="muted">{t(enrollment ? 'print.guide.installHelp' : 'print.guide.updateHelp')}</p>
            </>
          )}
        </>
      ) : enrollment && (
        <>
          <p>{t('print.guide.windowsIntro')}</p>
          {command('windows', `NubArca.PrintAgent.exe enroll --server ${origin} --station ${stationId} --token ${enrollment.enrollmentToken}`)}
        </>
      )}

      {enrollment && (
        <div className="print-guide-code" data-testid="print-enrollment">
          <span className="muted">{t('print.guide.code')}</span>
          {remaining > 0 ? (
            <>
              {command('code', enrollment.enrollmentToken)}
              <span className="muted" role="timer">{t('print.guide.codeExpires', { time: clock })}</span>
            </>
          ) : (
            <p className="print-guide-blocked" role="status">{t('print.guide.codeExpired')}</p>
          )}
          <button type="button" onClick={onNewCode}>{t('print.guide.newCode')}</button>
        </div>
      )}

      {connectedVersion ? (
        <p className="print-guide-connected" role="status" data-testid="print-guide-connected">
          {t('print.guide.connected', { version: connectedVersion })}
          {' '}
          <button type="button" className="primary-button" onClick={onOpenStation}>{t('print.guide.openStation')}</button>
        </p>
      ) : enrollment ? (
        <p className="muted" role="status" data-testid="print-guide-waiting">{t('print.guide.waiting')}</p>
      ) : null}

      <div className="print-station-actions">
        <button type="button" onClick={onDone}>{t('print.guide.done')}</button>
      </div>
    </section>
  );
}
