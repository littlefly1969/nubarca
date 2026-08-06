import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  createPhotoExport,
  getPhotoExport,
  revokePhotoExport,
  type PhotoExportStatus,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';

// "Download photo archive" UI. Creates a read-only export session, polls its
// build status, and shows a Windows-friendly PowerShell command that downloads
// every photo preserving the NubArca folder tree (no ZIP, originals only).
// The token is held in memory for the session view only and never logged.

function formatBytes(n: number): string {
  if (n <= 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  const i = Math.min(units.length - 1, Math.floor(Math.log(n) / Math.log(1024)));
  return `${(n / 1024 ** i).toFixed(i === 0 ? 0 : 1)} ${units[i]}`;
}

// PowerShell single-quoted literal: a path is safe verbatim except that a
// literal ' is escaped by doubling it. Backslashes stay literal (good for
// Windows paths).
function psQuote(value: string): string {
  return `'${value.replace(/'/g, "''")}'`;
}

// The default export folder, under the user's Windows profile.
export const DEFAULT_EXPORT_FOLDER = 'NubArcaExport';

// The `$Dest` assignment: a user-chosen absolute path when provided, else the
// default in the user's profile folder (built with Join-Path so $HOME expands —
// never a literal "%USERPROFILE%"). An explicit path is used verbatim.
//
// The generated script skips files it already has (by size), so pointing an
// existing archive at its own folder resumes rather than re-downloads.
function destLine(destDir: string): string {
  const trimmed = destDir.trim();
  if (trimmed.length > 0) return `$Dest    = ${psQuote(trimmed)}`;
  return `$Dest    = Join-Path $HOME '${DEFAULT_EXPORT_FOLDER}'`;
}

// Parallel PowerShell script (RunspacePool, PS 5.x / PS 7 compatible).
// -LiteralPath throughout so filenames with [ ] are handled correctly.
// `destDir` is the folder ON THE WINDOWS PC (empty = default under $HOME).
function powershellScript(origin: string, sessionId: string, token: string, destDir: string): string {
  return `# NubArca photo archive export — Windows PowerShell (PS 5.x / PS 7).
$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'
$Base      = '${origin}'
$Session   = '${sessionId}'
$Token     = '${token}'
${destLine(destDir)}
$Parallel  = 6   # connessioni simultanee (aumenta a 8-10 su fibra)
$Headers   = @{ Authorization = "Bearer $Token" }

New-Item -ItemType Directory -Force -Path $Dest | Out-Null

# Manifest — PS 5.x restituisce Content come Byte[] per MIME non testuali.
Write-Host "Download manifest..."
$raw  = Invoke-WebRequest -Uri "$Base/api/photo-exports/$Session/manifest" -Headers $Headers -UseBasicParsing
$text = if ($raw.Content -is [byte[]]) { [System.Text.Encoding]::UTF8.GetString($raw.Content) } else { $raw.Content }
$entries = ($text -split "\`n") | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json }
$n = $entries.Count
Write-Host "Manifest: $n foto da scaricare"

# Scriptblock eseguito in ogni thread del pool.
$sb = {
  param($Base, $Headers, $Dest, $e)
  $ProgressPreference = 'SilentlyContinue'
  $target = Join-Path $Dest ($e.relativePath -replace '/', '\\')
  New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
  # -LiteralPath: le parentesi quadre nei nomi file non vengono trattate come wildcard.
  if ((Test-Path -LiteralPath $target) -and ((Get-Item -LiteralPath $target).Length -eq $e.size)) {
    return "skip|$($e.relativePath)|0"
  }
  for ($t = 1; $t -le 3; $t++) {
    try {
      Invoke-WebRequest -Uri "$Base$($e.downloadUrl)" -Headers $Headers -OutFile $target -UseBasicParsing
      return "ok|$($e.relativePath)|0"
    } catch {
      $c = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 'net' }
      if ($t -eq 3) { return "fail|$($e.relativePath)|$c" }
      Start-Sleep -Seconds ($t * 2)
    }
  }
}

$pool = [RunspaceFactory]::CreateRunspacePool(1, $Parallel)
$pool.Open()
$active = [System.Collections.Generic.List[psobject]]::new()
$idx = 0; $done = 0; $fails = 0

while ($idx -lt $n -or $active.Count -gt 0) {
  while ($active.Count -lt $Parallel -and $idx -lt $n) {
    $e  = $entries[$idx++]
    $ps = [PowerShell]::Create()
    $ps.RunspacePool = $pool
    [void]$ps.AddScript($sb).AddArgument($Base).AddArgument($Headers).AddArgument($Dest).AddArgument($e)
    $active.Add([pscustomobject]@{ ps = $ps; h = $ps.BeginInvoke() })
  }
  $fin = @($active | Where-Object { $_.h.IsCompleted })
  foreach ($j in $fin) {
    $out = ($j.ps.EndInvoke($j.h) -join '')
    $j.ps.Dispose(); [void]$active.Remove($j)
    $done++
    $p = $out -split '\|', 3
    switch ($p[0]) {
      'ok'   { Write-Host    "[$done/$n] ok   $($p[1])" }
      'skip' { Write-Host    "[$done/$n] skip $($p[1])" }
      'fail' { $fails++; Write-Warning "[$done/$n] FAIL HTTP $($p[2]) $($p[1])"; Add-Content "$Dest\\export-errors.log" $p[1] }
    }
  }
  if ($fin.Count -eq 0) { Start-Sleep -Milliseconds 100 }
}

$pool.Close(); $pool.Dispose()
if ($fails) { Write-Warning "$fails file falliti — vedi $Dest\\export-errors.log" }
Write-Host "Done. $done file in $Dest"`;
}

function CopyButton({ text, label }: { text: string; label: string }) {
  const { t } = useI18n();
  const [copied, setCopied] = useState(false);
  return (
    <button
      type="button"
      className="row-action"
      onClick={() => {
        void navigator.clipboard?.writeText(text);
        setCopied(true);
        setTimeout(() => setCopied(false), 1500);
      }}
    >
      {copied ? t('export.copied') : label}
    </button>
  );
}

type Phase =
  | { kind: 'idle' }
  | { kind: 'creating' }
  | { kind: 'active'; sessionId: string; token: string; status: PhotoExportStatus | null }
  | { kind: 'error'; message: string };

export function PhotoArchiveExportPanel() {
  const { invalidateAuth } = useAuth();
  const { t, formatDate } = useI18n();
  const [phase, setPhase] = useState<Phase>({ kind: 'idle' });
  // Download folder ON THE WINDOWS PC where the command runs. Chosen at creation
  // time; empty = default under the user's profile. Client-side only — the
  // server never writes the archive, so this is not sent to the API.
  const [destDir, setDestDir] = useState('');
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null);
  const origin = typeof window !== 'undefined' ? window.location.origin : '';

  const stopPolling = useCallback(() => {
    if (pollRef.current !== null) {
      clearInterval(pollRef.current);
      pollRef.current = null;
    }
  }, []);

  useEffect(() => stopPolling, [stopPolling]);

  async function create() {
    setPhase({ kind: 'creating' });
    try {
      const created = await createPhotoExport();
      setPhase({ kind: 'active', sessionId: created.sessionId, token: created.token, status: null });
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        invalidateAuth();
        return;
      }
      setPhase({ kind: 'error', message: t('export.createError') });
    }
  }

  // Poll status while a session is active and not yet terminal.
  useEffect(() => {
    if (phase.kind !== 'active') return;
    let cancelled = false;
    const tick = async () => {
      try {
        const status = await getPhotoExport(phase.sessionId);
        if (cancelled) return;
        setPhase((p) => (p.kind === 'active' ? { ...p, status } : p));
        if (['ready', 'failed', 'revoked', 'expired'].includes(status.status)) {
          stopPolling();
        }
      } catch {
        /* transient; keep polling */
      }
    };
    void tick();
    pollRef.current = setInterval(() => void tick(), 2000);
    return () => {
      cancelled = true;
      stopPolling();
    };
    // Re-arm only when the session id changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [phase.kind === 'active' ? phase.sessionId : null]);

  async function revoke() {
    if (phase.kind !== 'active') return;
    try {
      await revokePhotoExport(phase.sessionId);
    } catch {
      /* best effort */
    }
    stopPolling();
    setPhase({ kind: 'idle' });
  }

  return (
    <section className="export-panel" aria-label={t('export.aria')}>
      <p className="muted">{t('export.intro')}</p>
      <ul className="export-notes">
        <li>{t('export.noteNotZip')}</li>
        <li>{t('export.noteOriginals')}</li>
        <li>{t('export.notePrivateVault')}</li>
      </ul>

      <div className="export-dest-row">
        <label htmlFor="export-dest-dir" className="export-dest-label">
          {t('export.destLabel')}
        </label>
        <input
          id="export-dest-dir"
          type="text"
          className="export-dest-input"
          placeholder={String.raw`C:\Users\yourname\Photos`}
          value={destDir}
          onChange={(e) => setDestDir(e.target.value)}
          spellCheck={false}
          autoComplete="off"
        />
        <p className="muted export-dest-hint">
          {t('export.destHintPre')}<code>{`%USERPROFILE%\\${DEFAULT_EXPORT_FOLDER}`}</code>{t('export.destHintPost')}
        </p>
      </div>

      {phase.kind === 'idle' && (
        <button type="button" className="row-action-primary" onClick={() => void create()}>
          {t('export.createSession')}
        </button>
      )}
      {phase.kind === 'creating' && <p className="muted" role="status">{t('export.creating')}</p>}
      {phase.kind === 'error' && (
        <div className="folder-error" role="alert">
          {phase.message}
          <button type="button" className="retry-button" onClick={() => void create()}>
            {t('common.tryAgain')}
          </button>
        </div>
      )}

      {phase.kind === 'active' && (
        <div className="export-active">
          <dl className="export-status">
            <dt>{t('common.status')}</dt>
            <dd data-testid="export-status">{phase.status?.status ?? 'building'}</dd>
            <dt>{t('export.filesLabel')}</dt>
            <dd>{phase.status?.fileCount ?? '…'}</dd>
            <dt>{t('export.totalSize')}</dt>
            <dd>{phase.status ? formatBytes(phase.status.totalBytes) : '…'}</dd>
            <dt>{t('export.expires')}</dt>
            <dd>{phase.status ? formatDate(phase.status.expiresAt) : '…'}</dd>
          </dl>

          {phase.status?.status === 'building' || phase.status === null ? (
            <p className="muted" role="status">{t('export.building')}</p>
          ) : phase.status.status === 'ready' ? (
            <>
              <h4>{t('export.psHeading')}</h4>
              <p className="muted">
                {t('export.psIntro1')}<code>.ps1</code>{t('export.psIntro2')}<code>{`%USERPROFILE%\\${DEFAULT_EXPORT_FOLDER}`}</code>{t('export.psIntro3')}
              </p>
              <textarea
                className="export-command"
                data-testid="export-powershell"
                readOnly
                rows={8}
                value={powershellScript(origin, phase.sessionId, phase.token, destDir)}
              />
              <div className="row-actions">
                <CopyButton
                  text={powershellScript(origin, phase.sessionId, phase.token, destDir)}
                  label={t('export.copyPowershell')}
                />
                <a
                  className="row-action"
                  href={`${origin}/api/photo-exports/${phase.sessionId}/manifest`}
                  target="_blank"
                  rel="noreferrer"
                >
                  {t('export.viewManifest')}
                </a>
                <button type="button" className="row-action-destructive" onClick={() => void revoke()}>
                  {t('export.revokeSession')}
                </button>
              </div>
              <p className="muted export-rclone-note">
                {t('export.rcloneNote')}
              </p>
            </>
          ) : (
            <div className="folder-error" role="alert">
              {phase.status.status === 'failed'
                ? t('export.buildFailed')
                : t('export.sessionIs', { status: phase.status.status })}
              <button type="button" className="retry-button" onClick={() => setPhase({ kind: 'idle' })}>
                {t('export.startOver')}
              </button>
            </div>
          )}
        </div>
      )}
    </section>
  );
}
