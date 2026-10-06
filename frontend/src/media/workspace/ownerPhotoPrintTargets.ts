import type { PrintPaperSize, PrintStation, SharedPrinter } from '@nubarca/api-client';
import type { MessageKey } from '../../i18n';
import { isPrinterOffline } from '../../cloud/PrinterMediaRemaining';

// The printers an owner's direct print can go to: their own, and the ones lent
// to them — one list, one shape. Who may actually print is the server's
// answer (IPrinterAccess); this only decides what to offer, and says why a
// printer that is there cannot take the print right now instead of hiding it.

export interface PrintTarget {
  key: string;
  stationId: string;
  deviceId: string;
  name: string;
  stationName: string;
  /** Set for a printer lent to the reader: whose it is. */
  ownerName: string | null;
  /** The paper the printer has loaded: the print is a sheet of it. */
  paper: PrintPaperSize;
  /** Null when it can take the print now; otherwise why not. */
  unavailable: MessageKey | null;
  /** The printer's own count of prints left on its media. */
  mediaRemaining: number | null;
  mediaObservedAt: string | null;
  offline: boolean;
  /** A loan's sheets still allowed: null is no ceiling, undefined is the reader's own printer. */
  sheetsLeft?: number | null;
  /** The printer cuts a 10x15 into the twin strip's two strips. */
  cutsStrips: boolean;
}

const KEY = (stationId: string, deviceId: string) => `${stationId}:${deviceId}`;

function prints(papers: readonly PrintPaperSize[], loaded: PrintPaperSize): boolean {
  return papers.includes(loaded);
}

export function printTargets(stations: readonly PrintStation[], shared: readonly SharedPrinter[]): PrintTarget[] {
  const own = stations
    .filter((station) => station.revokedAt === null)
    .flatMap((station) => station.devices.map((device): PrintTarget => {
      const paper = device.loadedPaperSize ?? '10x15';
      const offline = isPrinterOffline(station.status, device.observedState);
      const papers = device.papers ?? (device.supportsPhoto10x15 ? ['10x15' as const] : []);
      return {
        key: KEY(station.id, device.id),
        stationId: station.id,
        deviceId: device.id,
        name: device.displayName,
        stationName: station.name,
        ownerName: null,
        paper,
        unavailable: !station.enabled ? 'ownerPrint.unavailable.disabled'
          : offline ? 'ownerPrint.unavailable.offline'
            : !prints(papers, paper) ? 'ownerPrint.unavailable.paper'
              : null,
        mediaRemaining: device.mediaRemainingPrints ?? null,
        mediaObservedAt: device.mediaRemainingObservedAt ?? null,
        offline,
        cutsStrips: device.cutsStrips === true,
      };
    }));
  const lent = shared.map((printer): PrintTarget => {
    const offline = isPrinterOffline(printer.stationStatus, printer.observedState);
    const sheetsLeft = printer.maxSheets === null ? null : Math.max(0, printer.maxSheets - printer.usedSheets);
    return {
      key: KEY(printer.stationId, printer.deviceId),
      stationId: printer.stationId,
      deviceId: printer.deviceId,
      name: printer.displayName,
      stationName: printer.stationName,
      ownerName: printer.ownerName,
      paper: printer.loadedPaperSize,
      unavailable: offline ? 'ownerPrint.unavailable.offline'
        : !prints(printer.papers, printer.loadedPaperSize) ? 'ownerPrint.unavailable.paper'
          : sheetsLeft === 0 ? 'ownerPrint.unavailable.shareExhausted'
            : null,
      mediaRemaining: printer.mediaRemainingPrints ?? null,
      mediaObservedAt: printer.mediaRemainingObservedAt ?? null,
      offline,
      sheetsLeft,
      cutsStrips: printer.cutsStrips === true,
    };
  });
  return [...own, ...lent];
}

/** The printer to start with: the one chosen before if it is still usable, else the only usable one. */
export function initialTarget(targets: readonly PrintTarget[], previous: string | null): string | null {
  if (previous && targets.some((t) => t.key === previous && t.unavailable === null)) return previous;
  const usable = targets.filter((t) => t.unavailable === null);
  return usable.length === 1 ? usable[0].key : null;
}
