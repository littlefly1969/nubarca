import { useEffect, useMemo, useState } from 'react';
import {
  type PartyPrintSettings as Settings,
  type PrintStation,
} from '@nubarca/api-client';
import { usePartyApi } from '../party/workspace/partyApi';
import { useI18n, type MessageKey } from '../i18n';
import {
  Badge, Button, ChoiceCard, ChoiceGroup, Notice, Panel, SwitchRow,
} from '../party/workspace/ui';
import '../party/workspace/PartyWorkspace.css';

/* THE HOST's print settings for one party, in the party's own language.
 *
 * Printing is the only guest capability that spends something physical, so this
 * surface is about two decisions: which printer the party prints on, and how
 * much of each product guests may spend.
 *
 * WHICH PRINTER IS A CHOICE BETWEEN THINGS, not a pair of coupled selects. The
 * previous version asked for a station and then for a device inside it, which
 * is how the hardware is MODELLED and not how a host thinks: they are choosing
 * the printer by the door. So the two are flattened into one shortlist of real
 * printers, each naming where it is and whether it is up, and a station with no
 * usable printer simply contributes nothing rather than becoming a dead second
 * select.
 *
 * The two budgets are DELIBERATELY INDEPENDENT and are never shown as one
 * total: photo prints and strips cost different things, and a host who has run
 * out of one has not run out of the other. The "used" figures beside them are
 * HISTORY — a print that came out cannot be un-spent — which is also why the
 * server refuses a budget lowered below what has already been printed, rather
 * than showing a negative remainder.
 *
 * THE HARDWARE BOUNDARY IS UNCHANGED. A collaborator holding `print.manage`
 * never receives the station list (their route does not exist), never sees a
 * station or device id, and is told what the host configured rather than shown
 * a list they could change. That is not a rendering decision here — the server
 * carries the current hardware over on every crew save. */

type Status = 'idle' | 'saving' | 'saved' | 'failed';

interface Draft {
  enabled: boolean;
  stationId: string;
  deviceId: string;
  photoEnabled: boolean;
  photoMaxPrints: string;
  photoPerGuest: string;
  stripEnabled: boolean;
  stripMaxPrints: string;
  stripPerGuest: string;
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
    photoPerGuest: String(settings.photo.perGuest || ''),
    stripEnabled: settings.strip.enabled,
    stripMaxPrints: String(settings.strip.maxPrints || ''),
    stripPerGuest: String(settings.strip.perGuest || ''),
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
    'photo_per_guest_above_budget', 'strip_per_guest_above_budget',
    'photo_per_guest_range', 'strip_per_guest_range',
  ];
  return known.includes(code) ? key : 'partyPrintOwner.error.generic';
}

/**
 * ONE PRINTER a host can actually choose: a device that does 10x15, and the
 * station it is attached to.
 *
 * The composite key is what the radio group selects on, because a device id is
 * only unique inside its station and the party needs both.
 */
interface PrinterOption {
  key: string;
  stationId: string;
  stationName: string;
  deviceId: string;
  deviceName: string;
  /** Whether a sheet would come out of it right now. */
  reachable: boolean;
  stationStatus: PrintStation['status'];
}

const OPTION_KEY = (stationId: string, deviceId: string) => `${stationId}:${deviceId}`;

/**
 * Every printer the party could use, flattened out of the station tree.
 *
 * Both products compose a 10x15 sheet, so a printer that cannot do that size is
 * not offered at all rather than chosen and then refused by the server. A
 * station that contributes no such printer contributes nothing — which is why
 * an empty list is one honest sentence rather than a station picker followed by
 * a dead printer picker.
 */
export function printerOptions(stations: readonly PrintStation[]): PrinterOption[] {
  return stations
    .filter((station) => station.enabled && station.revokedAt === null)
    .flatMap((station) => station.devices
      .filter((device) => device.supportsPhoto10x15)
      .map((device) => ({
        key: OPTION_KEY(station.id, device.id),
        stationId: station.id,
        stationName: station.name,
        deviceId: device.id,
        deviceName: device.displayName,
        reachable: station.status === 'online',
        stationStatus: station.status,
      })));
}

export function PartyPrintSettings({ albumId }: { albumId: string }) {
  const { t } = useI18n();
  const api = usePartyApi();
  const [settings, setSettings] = useState<Settings | null>(null);
  const [stations, setStations] = useState<PrintStation[]>([]);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [status, setStatus] = useState<Status>('idle');
  const [error, setError] = useState<MessageKey | null>(null);
  // Three states, not two: this panel used to swallow a failed read, and a
  // silent failure is indistinguishable from a slow one for as long as the
  // host is willing to wait.
  const [load, setLoad] = useState<'loading' | 'ready' | 'failed'>('loading');
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    setLoad('loading');
    void Promise.all([
      api.getPartyPrintSettings(albumId, controller.signal),
      // Resolves empty for a collaborator: there is no crew route for the
      // installation's hardware, and asking for one would be the request this
      // boundary exists to prevent.
      api.listPrintStations(controller.signal),
    ]).then(([loaded, allStations]) => {
      if (controller.signal.aborted) return;
      setSettings(loaded);
      setDraft(toDraft(loaded));
      setStations(allStations);
      setLoad('ready');
    }).catch(() => { if (!controller.signal.aborted) setLoad('failed'); });
    return () => controller.abort();
  }, [albumId, attempt, api]);

  const options = useMemo(() => printerOptions(stations), [stations]);

  if (load === 'failed') {
    return (
      <Panel title={t('partyPrintOwner.title')} testId="party-print-failed">
        <Notice
          tone="error"
          actions={<Button onClick={() => setAttempt((n) => n + 1)}>{t('common.retry')}</Button>}
        >
          <p>{t('partyPrintOwner.loadFailed')}</p>
        </Notice>
      </Panel>
    );
  }

  // A panel that renders NOTHING while it loads reads as a broken section —
  // its heading sits above empty space. Content-shaped loading instead, so
  // what arrives does not move the page either.
  if (!settings || !draft) {
    return (
      <Panel title={t('partyPrintOwner.title')} testId="party-print-loading">
        <div className="pw-skeleton pw-skeleton--line" style={{ width: '60%' }} />
        <div className="pw-skeleton pw-skeleton--panel" style={{ height: '4rem' }} />
        <span className="visually-hidden" role="status">{t('common.loading')}</span>
      </Panel>
    );
  }

  const chosen = draft.stationId && draft.deviceId
    ? OPTION_KEY(draft.stationId, draft.deviceId)
    : '';
  const chosenOption = options.find((option) => option.key === chosen) ?? null;

  const update = (patch: Partial<Draft>) => {
    setDraft((prev) => (prev ? { ...prev, ...patch } : prev));
    setStatus('idle');
    setError(null);
  };

  const save = async () => {
    setStatus('saving');
    setError(null);
    try {
      const saved = await api.setPartyPrintSettings(albumId, {
        enabled: draft.enabled,
        // THE HARDWARE IS THE HOST'S TO NAME. A collaborator never sends
        // these — the crew request shape has no such fields and the server
        // carries the current values over — so they are not merely empty here,
        // they are absent.
        ...(api.isOwner && draft.stationId ? { printStationId: draft.stationId } : {}),
        ...(api.isOwner && draft.deviceId ? { printerDeviceId: draft.deviceId } : {}),
        photoEnabled: draft.photoEnabled,
        photoMaxPrints: Number(draft.photoMaxPrints || 0),
        photoPrintsPerGuest: Number(draft.photoPerGuest || 0),
        stripEnabled: draft.stripEnabled,
        stripMaxPrints: Number(draft.stripMaxPrints || 0),
        stripPrintsPerGuest: Number(draft.stripPerGuest || 0),
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

  /**
   * WHAT THE PARTY'S PRINTING IS DOING, in one line at the top.
   *
   * Three states and not two, because "switched on" and "will actually print"
   * are different: a party pointed at a printer nobody has plugged in is on and
   * not ready, and a host should learn that here rather than from a guest.
   */
  const readiness: { kind: 'ready' | 'blocked' | 'off'; key: MessageKey } =
    !draft.enabled
      ? { kind: 'off', key: 'partyPrintOwner.state.off' }
      : !chosenOption
        ? { kind: 'blocked', key: 'partyPrintOwner.state.noPrinter' }
        : !chosenOption.reachable
          ? { kind: 'blocked', key: 'partyPrintOwner.state.printerOffline' }
          : !draft.photoEnabled && !draft.stripEnabled
            ? { kind: 'blocked', key: 'partyPrintOwner.state.noProduct' }
            : { kind: 'ready', key: 'partyPrintOwner.state.ready' };

  return (
    <div className="pw-panels" data-testid="party-print-settings">
      <Panel
        title={t('partyPrintOwner.title')}
        note={t('partyPrintOwner.help')}
        testId="party-print-state"
        aside={(
          <Badge
            kind={readiness.kind === 'ready' ? 'ok' : readiness.kind === 'blocked' ? 'warn' : 'draft'}
            testId="party-print-readiness"
          >
            {t(readiness.key)}
          </Badge>
        )}
      >
        {api.isOwner && options.length > 0 && (
          <div className="pw-rows">
            <SwitchRow
              testId="party-print-enabled"
              label={t('partyPrintOwner.enable')}
              note={t('partyPrintOwner.enableNote')}
              checked={draft.enabled}
              disabled={status === 'saving'}
              onChange={(next) => update({ enabled: next })}
            />
          </div>
        )}
      </Panel>

      {/* THE PRINTER. Owner-only, and absent rather than disabled for a
          collaborator: which printers this installation has is the host
          administering their own equipment, not this evening's configuration. */}
      <Panel title={t('partyPrintOwner.printer')} testId="party-print-printer">
        {!api.isOwner ? (
          <p className="pw-small pw-muted" data-testid="party-print-crew-station">
            {t('partyPrintOwner.stationIsHosts')}
          </p>
        ) : options.length === 0 ? (
          // No printer, no printing. Said plainly instead of offering an empty
          // picker and a switch that cannot be turned on.
          <Notice tone="info" testId="party-print-no-stations">
            <p>{t('partyPrintOwner.noPrintersAnywhere')}</p>
          </Notice>
        ) : (
          <ChoiceGroup
            label={t('partyPrintOwner.chooseLabel')}
            hint={t('partyPrintOwner.chooseHint')}
            testId="party-print-choices"
          >
            {options.map((option) => (
              <ChoiceCard
                key={option.key}
                name="party-printer"
                value={option.key}
                checked={chosen === option.key}
                disabled={status === 'saving'}
                testId={`party-print-option-${option.deviceId}`}
                title={option.deviceName}
                meta={t('partyPrintOwner.atStation', { station: option.stationName })}
                note={option.reachable ? undefined : t('partyPrintOwner.offlineNote')}
                status={(
                  <Badge kind={option.reachable ? 'ok' : 'warn'}>
                    {t(option.reachable
                      ? 'partyPrintOwner.online'
                      : `partyPrintOwner.status.${option.stationStatus}` as MessageKey)}
                  </Badge>
                )}
                onSelect={() => update({
                  stationId: option.stationId, deviceId: option.deviceId,
                })}
              />
            ))}
          </ChoiceGroup>
        )}
      </Panel>

      <Panel
        title={t('partyPrintOwner.budgets')}
        note={t('partyPrintOwner.budgetHelp')}
        testId="party-print-budgets"
      >
        <p className="pw-small pw-muted">{t('partyPrintOwner.perGuestHelp')}</p>
        <Product
          which="photo"
          settings={settings}
          enabled={draft.photoEnabled}
          max={draft.photoMaxPrints}
          perGuest={draft.photoPerGuest}
          busy={status === 'saving'}
          onEnabled={(v) => update({ photoEnabled: v })}
          onMax={(v) => update({ photoMaxPrints: v })}
          onPerGuest={(v) => update({ photoPerGuest: v })}
        />
        <Product
          which="strip"
          settings={settings}
          enabled={draft.stripEnabled}
          max={draft.stripMaxPrints}
          perGuest={draft.stripPerGuest}
          busy={status === 'saving'}
          onEnabled={(v) => update({ stripEnabled: v })}
          onMax={(v) => update({ stripMaxPrints: v })}
          onPerGuest={(v) => update({ stripPerGuest: v })}
        />
      </Panel>

      <Panel
        title={t('partyPrintOwner.footer')}
        note={t('partyPrintOwner.footerHelp', { max: settings.footerMaxLength })}
        testId="party-print-footer"
        actions={(
          <>
            <Button
              tone="primary"
              busy={status === 'saving'}
              data-testid="party-print-save"
              onClick={() => void save()}
            >
              {t('partyPrintOwner.save')}
            </Button>
            {status === 'saved' && (
              <span role="status" className="pw-small pw-muted" data-testid="party-print-saved">
                {t('partyPrintOwner.saved')}
              </span>
            )}
          </>
        )}
      >
        <label className="pw-field">
          <span className="pw-field-label">{t('partyPrintOwner.footerLabel')}</span>
          <input
            type="text"
            maxLength={settings.footerMaxLength}
            value={draft.footerText}
            onChange={(e) => update({ footerText: e.target.value })}
          />
        </label>
        {error && (
          <Notice tone="error" testId="party-print-error">
            <p>{t(error, { min: settings.minBudget, max: settings.maxBudget })}</p>
          </Notice>
        )}
      </Panel>
    </div>
  );
}

function Product({
  which, settings, enabled, max, perGuest, busy, onEnabled, onMax, onPerGuest,
}: {
  which: 'photo' | 'strip';
  settings: Settings;
  enabled: boolean;
  max: string;
  perGuest: string;
  busy: boolean;
  onEnabled: (value: boolean) => void;
  onMax: (value: string) => void;
  onPerGuest: (value: string) => void;
}) {
  const { t } = useI18n();
  const usage = which === 'photo' ? settings.photo : settings.strip;
  const name = t(`partyPrintOwner.${which}` as MessageKey);

  return (
    <div className="pw-product" data-testid={`party-print-${which}`}>
      <div className="pw-rows">
        <SwitchRow
          testId={`party-print-${which}-enabled`}
          label={name}
          note={t(`partyPrintOwner.${which}Note` as MessageKey)}
          checked={enabled}
          disabled={busy}
          onChange={onEnabled}
        />
      </div>
      <div className="pw-product-numbers">
        <label className="pw-field">
          <span className="pw-field-label">{t('partyPrintOwner.budget')}</span>
          <input
            type="number"
            inputMode="numeric"
            min={settings.minBudget}
            max={settings.maxBudget}
            value={max}
            disabled={!enabled || busy}
            aria-label={`${name} — ${t('partyPrintOwner.budget')}`}
            onChange={(e) => onMax(e.target.value)}
          />
        </label>
        {/* The limit that actually makes the paper last: a party-wide budget
            alone is spent by whoever reaches the studio first. */}
        <label className="pw-field">
          <span className="pw-field-label">{t('partyPrintOwner.perGuest')}</span>
          <input
            type="number"
            inputMode="numeric"
            min={0}
            max={settings.maxBudget}
            value={perGuest}
            disabled={!enabled || busy}
            aria-label={`${name} — ${t('partyPrintOwner.perGuest')}`}
            onChange={(e) => onPerGuest(e.target.value)}
          />
        </label>
      </div>
      {/* What has already come out of the printer. Never reset, and never
          added to the other product's count. */}
      {usage.maxPrints > 0 && (
        <p className="pw-small pw-muted" data-testid={`party-print-${which}-usage`}>
          {t('partyPrintOwner.used', {
            used: usage.used, max: usage.maxPrints, remaining: usage.remaining,
          })}
        </p>
      )}
    </div>
  );
}
