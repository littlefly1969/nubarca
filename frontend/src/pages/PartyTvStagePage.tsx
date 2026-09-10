import { useEffect, useState } from 'react';
import { useParams } from 'react-router';
import QRCode from 'qrcode';
import { PartyTvStage } from '../party/PartyTvStage';
import { stageScene } from '../party/stageScene';
import { usePartyGameSnapshot } from '../party/usePartyGameSnapshot';

// The PUBLIC party television, at /party/{token}/tv.
//
// It is now an ENVELOPE and nothing else: it authorises with the party's own
// view token, polls the public snapshot, builds the lobby QR from the token it
// already holds, and hands all of it to the canonical stage. Every scene, every
// class name and every card lives in PartyTvStage, which the paired-display
// surface renders too — so the monitor in the corner and a NubArca TV cannot
// drift apart, because there is only one of them.
//
// `stageScene` is imported rather than redefined for the same reason: the
// television has no state machine of its own, and adding a second copy here is
// exactly how it would acquire one.
export function PartyTvStagePage() {
  const { token } = useParams<{ token: string }>();
  // A television never joins — a display that mints a participant inflates the
  // very count it is showing — but it does SAY it is a display, which is what
  // lets the control room answer "is a screen showing this".
  const { snapshot, connection, stale } = usePartyGameSnapshot(token, {
    join: false, asDisplay: true,
  });
  const qr = useGameQr(token, stageScene(snapshot) === 'lobby');

  return (
    <PartyTvStage
      snapshot={snapshot}
      connection={connection}
      stale={stale}
      lobbyQr={qr}
    />
  );
}

/**
 * The join code, built from the token this route already has.
 *
 * The display surface deliberately cannot do this — it holds no party token —
 * and asks the server for the same picture instead. That difference is the
 * whole reason `lobbyQr` is a prop rather than something the stage works out.
 */
function useGameQr(token: string | undefined, wanted: boolean): string | null {
  const [svg, setSvg] = useState<string | null>(null);
  useEffect(() => {
    if (!token || !wanted) { setSvg(null); return; }
    let cancelled = false;
    void QRCode.toString(`${window.location.origin}/party/${token}/game`,
      { type: 'svg', margin: 1, width: 260 })
      .then((value) => { if (!cancelled) setSvg(value); })
      .catch(() => { if (!cancelled) setSvg(null); });
    return () => { cancelled = true; };
  }, [token, wanted]);
  return svg;
}

// Re-exported so the projection has one definition but its existing importers
// keep working. `stageScene` lives in ../party/stageScene, shared with the
// display surface.
export { stageScene } from '../party/stageScene';
export type { StageScene } from '../party/stageScene';
