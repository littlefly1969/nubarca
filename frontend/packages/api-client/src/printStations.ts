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
  /** Who last set the loaded paper — the owner or the person it is lent to — and when. */
  loadedPaperChangedBy?: string | null;
  loadedPaperChangedAt?: string | null;
  /** The owner's view only: whom this printer is lent to now. */
  shares?: PrinterShare[] | null;
  /** The owner's view only: sheets per person, across the whole history. */
  usage?: PrinterUsage[] | null;
}

/** A live loan of a printer, as its owner sees it. */
export interface PrinterShare {
  id: string;
  granteeName: string;
  granteeEmail: string;
  /** The sheets this loan may take; null is no ceiling. */
  maxSheets: number | null;
  usedSheets: number;
  createdAt: string;
}

/** One person's sheets on one printer; never what was on them. */
export interface PrinterUsage {
  /** Null when that account no longer has a name to show. */
  name: string | null;
  isYou: boolean;
  /** Sheets accepted — what a ceiling counts. */
  sheets: number;
  /** Sheets the printer finished. */
  completed: number;
  byPaper: Record<string, number>;
  parties: number;
  album: number;
  tests: number;
}

/** A printer lent to the reader, and nothing else of its owner's. */
export interface SharedPrinter {
  shareId: string;
  stationId: string;
  stationName: string;
  stationStatus: PrintStationStatus;
  deviceId: string;
  displayName: string;
  observedState: string;
  ownerName: string;
  loadedPaperSize: PrintPaperSize;
  papers: PrintPaperSize[];
  supportsPhoto10x15: boolean;
  cutsStrips: boolean;
  loadedPaperChangedBy: string | null;
  loadedPaperChangedAt: string | null;
  maxSheets: number | null;
  usedSheets: number;
}

/** Share refusals the server names. */
export type PrinterShareError =
  | 'recipient_not_found' | 'recipient_is_owner' | 'already_shared'
  | 'invalid_ceiling' | 'ceiling_below_used';
/** Bounds of a loan's sheet ceiling, as the server enforces them. */
export const PRINTER_SHARE_MAX_SHEETS = 5000;

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
  /** The paper a ready job waits for, when its printer has another in. */
  waitingForPaper?: PrintPaperSize | null;
  /** Who sent it, when that is not the printer's owner — a job from a loan. */
  ownerName?: string | null;
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
  /** What is waiting, oldest first, whoever sent it. */
  queue?: PrintJobSummary[] | null;
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

/** Printers other people lend to you. */
export function listSharedPrinters(signal?: AbortSignal): Promise<SharedPrinter[]> {
  return api('/api/print/shared-printers', { signal });
}
/** Lend a printer to one person, by the email of their account. */
export function sharePrinter(
  stationId: string, deviceId: string, email: string, maxSheets: number | null,
): Promise<PrinterShare> {
  return api(`/api/print/stations/${encodeURIComponent(stationId)}/devices/${encodeURIComponent(deviceId)}/shares`, {
    method: 'POST', json: { email, maxSheets },
  });
}
export function updatePrinterShare(shareId: string, maxSheets: number | null): Promise<PrinterShare> {
  return api(`/api/print/shares/${encodeURIComponent(shareId)}`, { method: 'PUT', json: { maxSheets } });
}
/** End a loan: what is queued still prints, nothing new is accepted. */
export function revokePrinterShare(shareId: string): Promise<void> {
  return api(`/api/print/shares/${encodeURIComponent(shareId)}`, { method: 'DELETE' });
}
