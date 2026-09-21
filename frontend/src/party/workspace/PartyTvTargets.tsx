import { useCallback, useEffect, useState } from 'react';
import {
  ApiError,
  listTvDevices,
  setTvDeviceAssignment,
  type TvDevice,
} from '@nubarca/api-client';
import { useAuth } from '../../auth/useAuth';
import { useI18n } from '../../i18n';
import { cloudToolUrl } from '../../cloud/cloudTools';
import { Button, ButtonLink, EmptyState, Notice, Panel, PanelSkeleton, SwitchRow } from './ui';

// WHICH TELEVISION SHOWS THIS PARTY — asked and answered where it is asked.
//
// Pointing a screen at tonight used to mean leaving the party for the account's
// TV tools, finding the right television among every one ever paired, and
// picking this party out of a list of parties. That is the right page for
// MANAGING televisions — pairing one, naming it, revoking it — and it stays.
// It is the wrong page for a host who is standing in the venue with one
// question: "does that screen show this?"
//
// So this panel answers only that. One row per television, a switch that means
// "show this party here", and the current destination said in words underneath
// so turning one on is never a guess. Pairing is deliberately NOT here: it is a
// different job, done once, and duplicating it would be the second surface this
// panel exists to avoid.
//
// Televisions belong to the INSTALLATION, not to the party, so the caller
// renders this for an owner only — the same rule the old link row followed.

/** A television that can show anything at all. */
function isUsable(device: TvDevice): boolean {
  return device.status === 'active';
}

type State =
  | { kind: 'loading' }
  | { kind: 'ready'; devices: readonly TvDevice[] }
  | { kind: 'error' };

export function PartyTvTargets({ albumId }: { albumId: string }) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const [state, setState] = useState<State>({ kind: 'loading' });
  const [busyId, setBusyId] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);

  const load = useCallback((signal?: AbortSignal) => {
    setState({ kind: 'loading' });
    listTvDevices(signal)
      .then((devices) => {
        if (signal?.aborted) return;
        setState({ kind: 'ready', devices: devices.filter(isUsable) });
      })
      .catch((err: unknown) => {
        if (signal?.aborted) return;
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        setState({ kind: 'error' });
      });
  }, [invalidateAuth]);

  useEffect(() => {
    const controller = new AbortController();
    load(controller.signal);
    return () => controller.abort();
  }, [load]);

  /**
   * Point one television here, or let it go back to the general experience.
   *
   * The answer the server returns IS the new assignment, so the row adopts it
   * rather than asking for the list again: a host flipping three screens in a
   * row should not wait for three reloads, and a refetch would also throw away
   * any switch they flipped while it was in flight.
   */
  async function point(device: TvDevice, here: boolean) {
    setBusyId(device.id); setFailed(false);
    try {
      const assignment = await setTvDeviceAssignment(device.id, here ? albumId : null);
      setState((prev) => (prev.kind === 'ready'
        ? {
          kind: 'ready',
          devices: prev.devices.map((d) => (d.id === device.id ? { ...d, assignment } : d)),
        }
        : prev));
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setFailed(true);
    } finally { setBusyId(null); }
  }

  /** What this television is showing right now, in the host's own words. */
  function destination(device: TvDevice): string {
    const assignment = device.assignment;
    // A missing assignment IS the general experience — the wire omits it on a
    // server that predates assignments, and a television with none is general.
    if (!assignment || assignment.kind !== 'party') return t('tvDevices.useGeneral');
    if (assignment.albumId === albumId) return t('party.screens.tvHere');
    return t('party.screens.tvElsewhere', {
      name: assignment.albumName ?? t('tvDevices.partyFallback'),
    });
  }

  const body = state.kind === 'loading' ? <PanelSkeleton rows={2} />
    : state.kind === 'error' ? (
      <Notice
        tone="error"
        testId="party-tv-targets-error"
        actions={<Button onClick={() => load()}>{t('common.retry')}</Button>}
      >
        <p>{t('tvDevices.loadError')}</p>
      </Notice>
    ) : state.devices.length === 0 ? (
      // Pairing lives on the account's TV page, so the empty state points there
      // — the one case where leaving this section is the right answer.
      <EmptyState
        testId="party-tv-targets-empty"
        title={t('party.screens.tvNoneTitle')}
        body={t('party.screens.tvNoneBody')}
        action={<ButtonLink to={cloudToolUrl('tv-devices')}>{t('party.screens.tvPair')}</ButtonLink>}
      />
    ) : (
      <div className="pw-rows">
        {state.devices.map((device) => (
          <SwitchRow
            key={device.id}
            testId={`party-tv-target-${device.id}`}
            label={device.deviceLabel ?? t('tvDevices.deviceFallback')}
            note={destination(device)}
            checked={device.assignment?.kind === 'party'
              && device.assignment.albumId === albumId}
            disabled={busyId !== null}
            onChange={(next) => void point(device, next)}
          />
        ))}
      </div>
    );

  return (
    <Panel
      title={t('party.screens.tvTargets')}
      note={t('party.screens.tvTargetsNote')}
      testId="party-tv-targets"
    >
      {body}
      {failed && (
        <Notice tone="error" testId="party-tv-targets-failed">
          <p>{t('tvDevices.assignmentError')}</p>
        </Notice>
      )}
    </Panel>
  );
}
