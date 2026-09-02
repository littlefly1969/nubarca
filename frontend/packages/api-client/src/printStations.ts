import { api } from './client';

export type PrintDesiredState = 'running' | 'paused' | 'disabled';
export type PrintStationStatus = 'online' | 'degraded' | 'offline' | 'revoked';

export interface PrintDevice {
  id: string;
  displayName: string;
  manufacturer: string | null;
  model: string | null;
  adapterKind: string;
  observedState: string;
  lastSeenAt: string;
  supportsPhoto10x15: boolean;
}

export interface PrintJobSummary {
  id: string;
  shortCode: string;
  kind: string;
  format: string;
  state: string;
  createdAt: string;
  failureCode: string | null;
}

export interface PrintStation {
  id: string;
  name: string;
  enabled: boolean;
  desiredState: PrintDesiredState;
  status: PrintStationStatus;
  lastSeenAt: string | null;
  agentVersion: string | null;
  createdAt: string;
  revokedAt: string | null;
  devices: PrintDevice[];
  queueCount: number;
  currentJob: PrintJobSummary | null;
  lastError: string | null;
}

export interface PrintStationEnrollment {
  id: string;
  name: string;
  enrollmentToken: string;
  enrollmentExpiresAt: string;
}

export function listPrintStations(signal?: AbortSignal): Promise<PrintStation[]> {
  return api('/api/print/stations', { signal });
}
export function createPrintStation(name: string): Promise<PrintStationEnrollment> {
  return api('/api/print/stations', { method: 'POST', json: { name } });
}
export function renewPrintStationEnrollment(id: string): Promise<PrintStationEnrollment> {
  return api(`/api/print/stations/${encodeURIComponent(id)}/enrollment`, { method: 'POST' });
}
export function setPrintStationDesiredState(id: string, desiredState: PrintDesiredState): Promise<void> {
  return api(`/api/print/stations/${encodeURIComponent(id)}/desired-state`, {
    method: 'PUT', json: { desiredState },
  });
}
export function revokePrintStation(id: string): Promise<void> {
  return api(`/api/print/stations/${encodeURIComponent(id)}`, { method: 'DELETE' });
}
export function createPrintTestJob(stationId: string, printerDeviceId: string): Promise<PrintJobSummary> {
  return api(`/api/print/stations/${encodeURIComponent(stationId)}/test-jobs`, {
    method: 'POST', json: { printerDeviceId },
  });
}
export function cancelPrintJob(jobId: string): Promise<void> {
  return api(`/api/print/jobs/${encodeURIComponent(jobId)}/cancel`, { method: 'POST' });
}
export function retryPrintJob(jobId: string): Promise<void> {
  return api(`/api/print/jobs/${encodeURIComponent(jobId)}/retry`, { method: 'POST' });
}
