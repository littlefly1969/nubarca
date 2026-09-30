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
  /** The printer cuts a strip sheet into two 2x6 strips itself. */
  cutsStrips?: boolean;
  /** The owner's tone compensation for this printer; every factor 1 when neutral. */
  calibration?: PrintCalibration;
  /** The paper the operator says is loaded; guests are offered what it can make. */
  loadedPaperSize?: PrintPaperSize;
  /** The papers the Print Agent reports this printer can print. */
  papers?: PrintPaperSize[];
}

/** DNP's 4x6, 5x7 and 6x8 inch media, under their photo trade names. */
export type PrintPaperSize = '10x15' | '13x18' | '20x15';
export const PRINT_PAPER_SIZES: readonly PrintPaperSize[] = ['10x15', '13x18', '20x15'] as const;

export interface PrintCalibration {
  brightness: number;
  contrast: number;
  /** Above 1 the midtones print lighter; black and white stay put. */
  gamma: number;
  saturation: number;
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
export function setPrinterCalibration(
  stationId: string, deviceId: string, calibration: PrintCalibration,
): Promise<PrintDevice> {
  return api(`/api/print/stations/${encodeURIComponent(stationId)}/devices/${encodeURIComponent(deviceId)}/calibration`, {
    method: 'PUT', json: calibration,
  });
}
/** Record which paper is now in the printer. */
export function setPrinterPaper(
  stationId: string, deviceId: string, paperSize: PrintPaperSize,
): Promise<PrintDevice> {
  return api(`/api/print/stations/${encodeURIComponent(stationId)}/devices/${encodeURIComponent(deviceId)}/paper`, {
    method: 'PUT', json: { paperSize },
  });
}
export function cancelPrintJob(jobId: string): Promise<void> {
  return api(`/api/print/jobs/${encodeURIComponent(jobId)}/cancel`, { method: 'POST' });
}
export function retryPrintJob(jobId: string): Promise<void> {
  return api(`/api/print/jobs/${encodeURIComponent(jobId)}/retry`, { method: 'POST' });
}
