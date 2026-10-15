import { api } from './client';
import type { PrintAgentStatus } from './printStations';

// The administrator's Updates page: this installation's release, the host
// updater that installs new ones, and the Print Boxes that follow the server.
// The api only drops one request in the updater's mailbox; every gate (the
// signed release, migrations, backup, smoke checks, rollback) runs on the host.

export type SystemUpdateState = 'idle' | 'checking' | 'applying' | string;

export interface SystemUpdateRun {
  action: 'check' | 'apply' | string | null;
  /** ready, up-to-date, not-deployable, deployed, failed, interrupted… */
  outcome: string | null;
  message: string | null;
  sha: string | null;
  /** The updater's own scheduled check, not an administrator's. */
  automatic: boolean;
  startedAt: string | null;
  finishedAt: string | null;
}

export interface SystemUpdateReport {
  /** ready, up-to-date or not-deployable. */
  outcome: string;
  /** Why it cannot be installed here: release-not-published, manual-migration, tv-artifact-missing. */
  reason: string | null;
  checkedAt: string | null;
  candidateSha: string | null;
  candidateVersion: string | null;
  signed: boolean;
  /** The exact release the check found (signed manifest and TV bundles); an install confirms it. */
  releaseId: string | null;
  currentVersion: string | null;
  apiVariant: string | null;
  components: {
    backend: boolean;
    frontend: boolean;
    printAgent: boolean;
    printAgentVersion: string | null;
    tv: boolean;
    tvEnabled: boolean;
  };
  migrations: string[];
  /** Installing applies database migrations: the administrator confirms them. */
  confirmMigrations: boolean;
  commits: { sha: string; subject: string }[];
}

export interface SystemUpdate {
  runningVersion: string | null;
  runningSha: string | null;
  /** The host updater is installed and alive. */
  updaterConnected: boolean;
  state: SystemUpdateState | null;
  heartbeatAt: string | null;
  requestPending: boolean;
  current: SystemUpdateRun | null;
  last: SystemUpdateRun | null;
  lastCheckAt: string | null;
  autoCheckHours: number | null;
  report: SystemUpdateReport | null;
}

export interface PrintBoxUpdate {
  stationId: string;
  name: string;
  ownerName: string;
  status: 'online' | 'degraded' | 'offline' | 'revoked' | string;
  agent: PrintAgentStatus;
  /** Self-updating, behind the published bundle, and not already asked. */
  canUpdate: boolean;
}

export async function getSystemUpdate(signal?: AbortSignal): Promise<SystemUpdate> {
  return api('/api/admin/system-update', { signal });
}

export async function requestSystemUpdateCheck(): Promise<SystemUpdate> {
  return api('/api/admin/system-update/check', { method: 'POST' });
}

/** Installs exactly the release the last check found, with that check's migration answer. */
export async function requestSystemUpdateApply(
  sha: string,
  releaseId: string,
  confirmMigrations: boolean,
): Promise<SystemUpdate> {
  return api('/api/admin/system-update/apply', { method: 'POST', json: { sha, releaseId, confirmMigrations } });
}

export async function listPrintBoxUpdates(signal?: AbortSignal): Promise<PrintBoxUpdate[]> {
  return api('/api/admin/system-update/print-boxes', { signal });
}

export async function requestAllPrintBoxUpdates(): Promise<{ requested: number; version: string | null }> {
  return api('/api/admin/system-update/print-boxes', { method: 'POST' });
}

export async function requestPrintBoxUpdate(stationId: string): Promise<{ requested: number; version: string }> {
  return api(`/api/admin/system-update/print-boxes/${encodeURIComponent(stationId)}`, { method: 'POST' });
}

export async function cancelPrintBoxUpdate(stationId: string): Promise<void> {
  return api(`/api/admin/system-update/print-boxes/${encodeURIComponent(stationId)}`, { method: 'DELETE' });
}
