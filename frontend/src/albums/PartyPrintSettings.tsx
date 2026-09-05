import { useEffect, useState } from 'react';
import {
  getPartyPrintSettings,
  listPrintStations,
  setPartyPrintSettings,
  type PartyPrintSettings as Settings,
  type PrintStation,
} from '@nubarca/api-client';
import { useI18n, type MessageKey } from '../i18n';

/* The HOST's print settings for one party.
   
   Printing is the only guest capability that spends something physical, so this
   panel is about two decisions: which printer the party prints on, and how much
   of each product guests may spend.

   The two budgets are DELIBERATELY INDEPENDENT and are never shown as one
   total: photo prints and strips cost different things, and a host who has run
   out of one has not run out of the other. The "used" figures beside them are
   HISTORY — a print that came out cannot be un-spent — which is also why the
   server refuses a budget lowered below what has already been printed, rather
   than showing a negative remainder. */

type Status = 'idle' | 'saving' | 'saved' | 'failed';

interface Draft {
  enabled: boolean;
  stationId: string;
  deviceId: string;
  photoEnabled: boolean;
  photoMaxPrints: string;
  stripEnabled: boolean;
  stripMaxPrints: string;
  footerText: string;
}

function toDraft(settings: Settings): Draft {
  return {
    enabled: settings.enabled,
    stationId: settings.printStationId ?? '',
    deviceId: settings.printerDeviceId ?? '',
    photoEnabled: settings.photo.enabled,
    // Budgets are edited as text and sent on save: a PATCH per keypress would
    // send the "1" on the way to "15", and every one is a real budget change.
    photoMaxPrints: String(settings.photo.maxPrints || ''),
    stripEnabled: settings.strip.enabled,
    stripMaxPrints: String(settings.strip.maxPrints || ''),
    footerText: settings.footerText ?? '',
  };
}

/** The server's refusal codes, in the host's language. */
function errorKey(code: string): MessageKey {
  const key = `partyPrintOwner.error.${code}` as MessageKey;
  const known: readonly string[] = [
    'printer_required', 'product_required', 'printer_not_found', 'station_unavailable',
    'format_unsupported', 'photo_budget_range', 'strip_budget_range',
    'photo_budget_below_used', 'strip_budget_below_used', 'footer_too_long',
  ];
  return known.includes(code) ? key : 'partyPrintOwner.error.generic';
}

export function PartyPrintSettings({ albumId }: { albumId: string }) {
  const { t } = useI18n();
  const [settings, setSettings] = useState<Settings | null>(null);
  const [stations, setStations] = useState<PrintStation[]>([]);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [status, setStatus] = useState<Status>('idle');
  const [error, setError] = useState<MessageKey | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    void Promise.all([
      getPartyPrintSettings(albumId, controller.signal),
      listPrintStations(controller.signal),
    ]).then(([loaded, allStations]) => {
      setSettings(loaded);
      setDraft(toDraft(loaded));
      // Only stations that could actually print: a revoked one is not a choice.
      setStations(allStations.filter((s) => s.enabled && s.revokedAt === null));
    }).catch(() => { /* The section stays closed rather than showing a broken form. */ });
    return () => controller.abort();
  }, [albumId]);

  if (!settings || !draft) return null;

  const station = stations.find((s) => s.id === draft.stationId) ?? null;
  // Both products compose a 10x15 sheet, so a printer that cannot do that size
  // is not offered at all rather than chosen and then refused.
  const printers = (station?.devices ?? []).filter((d) => d.supportsPhoto10x15);

  const update = (patch: Partial<Draft>) => {
    setDraft((prev) => (prev ? { ...prev, ...patch } : prev));
    setStatus('idle');
    setError(null);
  };

  const save = async () => {
    setStatus('saving');
    setError(null);
    try {
      const saved = await setPartyPrintSettings(albumId, {
        enabled: draft.enabled,
        ...(draft.stationId ? { printStationId: draft.stationId } : {}),
        ...(draft.deviceId ? { printerDeviceId: draft.deviceId } : {}),
        photoEnabled: draft.photoEnabled,
        photoMaxPrints: Number(draft.photoMaxPrints || 0),
        stripEnabled: draft.stripEnabled,
        stripMaxPrints: Number(draft.stripMaxPrints || 0),
        footerText: draft.footerText,
      });
      setSettings(saved);
      setDraft(toDraft(saved));
      setStatus('saved');
    } catch (err: unknown) {
      const code = err && typeof err === 'object' && 'body' in err
        && typeof (err as { body?: unknown }).body === 'object'
        && (err as { body: { error?: unknown } }).body?.error;
      setError(errorKey(typeof code === 'string' ? code : ''));
      setStatus('failed');
    }
  };

  const product = (
    which: 'photo' | 'strip',
    enabled: boolean,
    max: string,
    onEnabled: (value: boolean) => void,
    onMax: (value: string) => void,
  ) => {
    const usage = which === 'photo' ? settings.photo : settings.strip;
    return (
      <div className="album-party-print-product" data-testid={`party-print-${which}`}>
        <label className="album-tv-label">
          <input
            type="checkbox"
            checked={enabled}
            onChange={(e) => onEnabled(e.target.checked)}
          />
          <span>{t(`partyPrintOwner.${which}`)}</span>
        </label>
        <label className="album-party-number">
          <span>{t('partyPrintOwner.budget')}</span>
          <input
            type="number"
            inputMode="numeric"
            min={settings.minBudget}
            max={settings.maxBudget}
            value={max}
            disabled={!enabled}
            aria-label={`${t(`partyPrintOwner.${which}`)} — ${t('partyPrintOwner.budget')}`}
            onChange={(e) => onMax(e.target.value)}
          />
        </label>
        {/* What has already come out of the printer. Never reset, and never
            added to the other product's count. */}
        {usage.maxPrints > 0 && (
          <p className="muted" data-testid={`party-print-${which}-usage`}>
            {t('partyPrintOwner.used', {
              used: usage.used, max: usage.maxPrints, remaining: usage.remaining,
            })}
          </p>
        )}
      </div>
    );
  };

  return (
    <div className="album-party-print" data-testid="party-print-settings">
      <h4>{t('partyPrintOwner.title')}</h4>
      <p className="muted">{t('partyPrintOwner.help')}</p>

      {stations.length === 0 ? (
        // No station, no printing. Said plainly instead of offering an empty
        // select and a switch that cannot be turned on.
        <p className="muted" data-testid="party-print-no-stations">
          {t('partyPrintOwner.noStations')}
        </p>
      ) : (
        <>
          <label className="album-tv-label">
            <input
              type="checkbox"
              checked={draft.enabled}
              aria-label={t('partyPrintOwner.enable')}
              onChange={(e) => update({ enabled: e.target.checked })}
            />
            <span>{t('partyPrintOwner.enable')}</span>
          </label>

          <label className="album-party-number">
            <span>{t('partyPrintOwner.station')}</span>
            <select
              value={draft.stationId}
              onChange={(e) => update({ stationId: e.target.value, deviceId: '' })}
            >
              <option value="">—</option>
              {stations.map((s) => (
                <option key={s.id} value={s.id}>{s.name}</option>
              ))}
            </select>
          </label>

          <label className="album-party-number">
            <span>{t('partyPrintOwner.printer')}</span>
            <select
              value={draft.deviceId}
              disabled={printers.length === 0}
              onChange={(e) => update({ deviceId: e.target.value })}
            >
              <option value="">—</option>
              {printers.map((d) => (
                <option key={d.id} value={d.id}>{d.displayName}</option>
              ))}
            </select>
          </label>
          {station !== null && printers.length === 0 && (
            <p className="muted" data-testid="party-print-no-printers">
              {t('partyPrintOwner.noPrinters')}
            </p>
          )}

          <p className="muted">{t('partyPrintOwner.budgetHelp')}</p>
          {product('photo', draft.photoEnabled, draft.photoMaxPrints,
            (v) => update({ photoEnabled: v }), (v) => update({ photoMaxPrints: v }))}
          {product('strip', draft.stripEnabled, draft.stripMaxPrints,
            (v) => update({ stripEnabled: v }), (v) => update({ stripMaxPrints: v }))}

          <label className="album-party-number">
            <span>{t('partyPrintOwner.footer')}</span>
            <input
              type="text"
              maxLength={settings.footerMaxLength}
              value={draft.footerText}
              onChange={(e) => update({ footerText: e.target.value })}
            />
          </label>
          <p className="muted">
            {t('partyPrintOwner.footerHelp', { max: settings.footerMaxLength })}
          </p>

          <button type="button" disabled={status === 'saving'} onClick={() => void save()}>
            {t('partyPrintOwner.save')}
          </button>
          {status === 'saved' && (
            <p role="status" className="muted">{t('partyPrintOwner.saved')}</p>
          )}
          {error && (
            <p role="alert" className="inline-error">
              {t(error, { min: settings.minBudget, max: settings.maxBudget })}
            </p>
          )}
        </>
      )}
    </div>
  );
}
