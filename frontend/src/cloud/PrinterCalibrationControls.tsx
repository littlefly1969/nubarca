import { useEffect, useState } from 'react';
import { setPrinterCalibration, type PrintCalibration, type PrintDevice } from '@nubarca/api-client';
import { useI18n, type MessageKey } from '../i18n';

const NEUTRAL: PrintCalibration = { brightness: 1, contrast: 1, gamma: 1, saturation: 1 };

// The same bounds the server enforces (PrintCalibration.cs): gentle
// corrections for a printer, never a creative filter.
const CONTROLS: Array<{ key: keyof PrintCalibration; label: MessageKey; min: number; max: number }> = [
  { key: 'gamma', label: 'print.calibration.midtones', min: 0.6, max: 1.6 },
  { key: 'brightness', label: 'print.calibration.brightness', min: 0.7, max: 1.3 },
  { key: 'contrast', label: 'print.calibration.contrast', min: 0.7, max: 1.3 },
  { key: 'saturation', label: 'print.calibration.saturation', min: 0.5, max: 1.5 },
];

function percent(value: number): string {
  const delta = Math.round((value - 1) * 100);
  return delta > 0 ? `+${delta}%` : `${delta}%`;
}

/**
 * One printer's tone compensation, adjusted from here and applied by the
 * server to every sheet it renders for that printer — guest prints and the
 * test page alike — so the owner never has to touch the driver.
 */
export function PrinterCalibrationControls({ stationId, device, onSaved }: {
  stationId: string;
  device: PrintDevice;
  onSaved: () => Promise<void>;
}) {
  const { t } = useI18n();
  const saved = device.calibration ?? NEUTRAL;
  const [open, setOpen] = useState(false);
  const [values, setValues] = useState<PrintCalibration>(saved);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ text: string; error: boolean } | null>(null);

  // A reload after saving brings the stored values back; follow them while closed.
  useEffect(() => {
    if (!open) setValues(device.calibration ?? NEUTRAL);
  }, [device.calibration, open]);

  async function save() {
    setBusy(true); setMessage(null);
    try {
      await setPrinterCalibration(stationId, device.id, values);
      setMessage({ text: t('print.calibration.saved'), error: false });
      await onSaved();
    } catch {
      setMessage({ text: t('print.calibration.error'), error: true });
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="print-calibration" data-testid="print-calibration">
      <button type="button" className="print-calibration-toggle" aria-expanded={open}
        onClick={() => setOpen((v) => !v)}>
        {t('print.calibration.toggle')}
      </button>
      {open && (
        <div className="print-calibration-body">
          <p className="muted">{t('print.calibration.help')}</p>
          {CONTROLS.map(({ key, label, min, max }) => {
            const id = `calibration-${device.id}-${key}`;
            return (
              <div key={key} className="print-calibration-row">
                <label htmlFor={id}>{t(label)}</label>
                <input id={id} type="range" min={min} max={max} step={0.05} value={values[key]}
                  onChange={(e) => setValues((v) => ({ ...v, [key]: Number(e.target.value) }))} />
                <output htmlFor={id}>{percent(values[key])}</output>
              </div>
            );
          })}
          <div className="print-station-actions">
            <button type="button" disabled={busy} onClick={() => void save()}>
              {t('print.calibration.save')}
            </button>
            <button type="button" disabled={busy} onClick={() => setValues(NEUTRAL)}>
              {t('print.calibration.reset')}
            </button>
          </div>
          {message && (
            <p role={message.error ? 'alert' : 'status'} className={message.error ? 'folder-error' : 'muted'}>
              {message.text}
            </p>
          )}
        </div>
      )}
    </section>
  );
}
