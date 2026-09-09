import { useCallback, useEffect, useRef, useState } from 'react';
import { BackHandler, StyleSheet, Text, View } from 'react-native';
import { WebView, type WebViewNavigation } from 'react-native-webview';
import { colors, font, spacing } from '../theme';
import { FocusableButton } from '../components/FocusableButton';

// SPIKE ONLY — not the Party Game solution, and deliberately not wired into the
// Party flow.
//
// This exists to answer ONE question on real Fire TV hardware: can a WebView
// host the canonical Party Game stage reliably enough to be the product's TV
// renderer? Everything below is instrumentation for that question and is
// expected to be deleted, not extended.
//
// It never uses a party capability token. Two probes, neither of which takes the
// `/party/{token}/tv` + guest-cookie shortcut:
//
//   replica — an inline document with no network at all: full-bleed scenes, a
//             large decoded image, a CSS keyframe animation, a 2.5s timer loop
//             matching the stage's poll cadence, and an in-page frame/heap HUD.
//             This is the soak target: it isolates WebView runtime behaviour
//             from server, TLS and token questions.
//   origin  — the real deployed web origin's root document, which exercises the
//             actual CSS/font/bundle pipeline over the real network on the real
//             panel. Still no token, so nothing here is a security shortcut.
//   stress  — allocates until the renderer dies. This exists because THIS
//             OPERATOR HAS NO ADB (docs/current-work.md records it), so
//             "what happens when the WebView is killed" cannot be triggered
//             with `am force-stop` and has to be reachable from the remote.
//   unreachable — a host that cannot resolve, for the error and recovery path.
//
// The HUD is native (outside the WebView) on purpose: if the WebView white-
// screens, crashes or is killed by the OS, a HUD living inside it would vanish
// with the evidence.

type Probe = 'replica' | 'origin' | 'stress' | 'unreachable';

// THE WATCHDOG, and the numbers CHECK 14 is measured against.
//
// A2 in production needs one of these or it does not ship: a renderer the OS
// killed must come back without anybody touching the television, and a renderer
// that is alive but no longer running must be treated as dead. So the spike
// carries the same mechanism, because testing a WebView without it would prove
// nothing about the architecture that would actually be built.
//
// A page posts a heartbeat every 2s. Missing five of them is not a slow frame.
const WEDGE_AFTER_MS = 10_000;
// Long enough that a remount is visibly a remount rather than a flicker, short
// enough that a room watching the screen sees it come back.
const RECOVER_DELAY_MS = 2_000;
// A crash LOOP must end somewhere visible. After this many automatic recoveries
// the shell stops and says so in native UI, rather than cycling for ever behind
// a black rectangle.
const MAX_RECOVERIES = 5;

interface Props {
  baseUrl: string;
  onBack: () => void;
}

/**
 * A self-contained stand-in for the stage, as one document.
 *
 * It deliberately mirrors the shapes the real stage uses rather than its
 * content: a full-bleed dark scene, one very large headline, a decoded raster
 * image, a compositor-driven animation, and a timer that swaps scenes on the
 * same cadence the stage polls at. If this is smooth and stable for an hour on
 * a Fire Stick, the real stage will be too; if it is not, no amount of CSS
 * tuning on the real stage will save it.
 */
// Allocates a megabyte at a time until the renderer process is killed. The
// point is not the allocation; it is what the NATIVE shell does at the moment
// the renderer disappears — which is the whole failure-fallback question, and
// the one thing an operator without ADB could not otherwise provoke.
const STRESS = `<!doctype html><html><head><meta charset="utf-8"><style>
 html,body{height:100%;margin:0;background:#2a0d0d;color:#ffd9d9;
   font-family:system-ui,sans-serif;display:grid;place-items:center;text-align:center}
 h1{font-size:5vw}p{font-size:2.4vw}
</style></head><body><div><h1 id="n">0 MB</h1><p>allocating until the renderer dies</p>
<p>watch the native HUD — it must survive this</p></div>
<script>
 var blocks=[],mb=0;
 setInterval(function(){
  try{
   for(var i=0;i<8;i++){blocks.push(new Uint8Array(1048576).fill(i));mb++;}
   document.getElementById('n').textContent=mb+' MB';
   window.ReactNativeWebView.postMessage(JSON.stringify({ticks:mb,frames:0,up:mb,heap:mb*1048576}));
  }catch(e){}
 },250);
</script></body></html>`;

const REPLICA = `<!doctype html><html><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<style>
 *{margin:0;padding:0;box-sizing:border-box}
 html,body{height:100%;background:#070a12;color:#f8fafc;overflow:hidden;
   font-family:system-ui,-apple-system,sans-serif}
 .stage{position:fixed;inset:0;display:grid;place-items:center;text-align:center;padding:4vh 6vw}
 .eyebrow{font-size:2.2vw;letter-spacing:.4em;text-transform:uppercase;color:#9db8ff}
 h1{font-size:7vw;line-height:1.05;font-weight:900;margin:2vh 0}
 .pct{font-size:16vw;font-weight:900;color:#7ee2a8;
   animation:pop .6s cubic-bezier(.2,.9,.3,1.4) both}
 @keyframes pop{from{transform:scale(.7);opacity:0}to{transform:scale(1);opacity:1}}
 .bar{position:fixed;left:0;bottom:0;height:1.2vh;background:#0a84ff;width:0;
   animation:run 2.5s linear infinite}
 @keyframes run{to{width:100%}}
 img{width:34vw;height:auto;border-radius:2vw;margin-bottom:2vh}
 .hud{position:fixed;top:0;right:0;font-size:1.3vw;color:#9ca9bf;padding:1vh 1vw;
   text-align:right;font-variant-numeric:tabular-nums}
</style></head><body>
<div class="stage" id="s"></div><div class="bar"></div><div class="hud" id="h"></div>
<script>
 // A real decoded raster, not a gradient: image decode is where a weak SoC
 // actually spends its memory.
 var IMG='data:image/svg+xml;base64,'+btoa('<svg xmlns="http://www.w3.org/2000/svg" width="900" height="600"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="%236f8dd8"/><stop offset="1" stop-color="%23141b2b"/></linearGradient></defs><rect width="900" height="600" fill="url(%23g)"/></svg>');
 var SCENES=[
  {e:'ATTIVITA 1 DI 4',h:'Canta sul tavolo',img:true},
  {e:'VOTAZIONE APERTA',h:'VOTA ORA',img:false},
  {e:'VOTAZIONE CHIUSA',h:'8 / 12 hanno votato',img:false},
  {e:'IL PUBBLICO HA DECISO',h:'',pct:'82%'},
  {e:'PROSSIMA ATTIVITA',h:'Ballo di gruppo',img:true}
 ];
 var i=0,frames=0,ticks=0,started=Date.now();
 function draw(){
  var s=SCENES[i%SCENES.length];i++;ticks++;
  document.getElementById('s').innerHTML=
   (s.img?'<div><img src="'+IMG+'">':'<div>')+
   '<p class="eyebrow">'+s.e+'</p>'+
   (s.pct?'<p class="pct">'+s.pct+'</p>':'<h1>'+s.h+'</h1>')+'</div>';
 }
 function hud(){
  var m=(performance.memory&&performance.memory.usedJSHeapSize)||0;
  document.getElementById('h').innerHTML=
   'scene '+ticks+'<br>frames '+frames+
   '<br>up '+Math.round((Date.now()-started)/1000)+'s'+
   (m?'<br>heap '+Math.round(m/1048576)+'MB':'');
 }
 function raf(){frames++;requestAnimationFrame(raf)}
 draw();raf();setInterval(draw,2500);setInterval(hud,1000);
 // Report to the native shell so the HUD survives a WebView that dies.
 setInterval(function(){
  try{window.ReactNativeWebView.postMessage(JSON.stringify(
    {ticks:ticks,frames:frames,up:Math.round((Date.now()-started)/1000),
     heap:(performance.memory&&performance.memory.usedJSHeapSize)||0}));}catch(e){}
 },2000);
</script></body></html>`;

export function WebViewSpikeScreen({ baseUrl, onBack }: Props) {
  const [probe, setProbe] = useState<Probe | null>(null);
  const [status, setStatus] = useState('idle');
  const [loads, setLoads] = useState(0);
  const [errors, setErrors] = useState<string[]>([]);
  const [beat, setBeat] = useState<{ ticks: number; frames: number; up: number; heap: number } | null>(null);
  const [lastBeatAt, setLastBeatAt] = useState<number | null>(null);
  const [now, setNow] = useState(Date.now());
  // Changing this remounts the WebView: a dead renderer cannot be revived, only
  // replaced, so recovery IS a new instance.
  const [generation, setGeneration] = useState(0);
  const [recoveries, setRecoveries] = useState(0);
  const [gaveUp, setGaveUp] = useState(false);
  const mountedAt = useRef(Date.now());
  const webRef = useRef<WebView>(null);

  // A clock the WebView cannot stop. "Last heartbeat 47s ago" is the single most
  // useful reading on this screen: it is how a white screen, an OS-killed
  // renderer and a wedged JS context all announce themselves.
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1_000);
    return () => clearInterval(timer);
  }, []);

  // BACK tears the WebView down and returns to the native shell — probe 1 and
  // probe 14 in one gesture.
  useEffect(() => {
    const sub = BackHandler.addEventListener('hardwareBackPress', () => {
      if (probe !== null) {
        setProbe(null); setBeat(null); setLastBeatAt(null);
        setRecoveries(0); setGaveUp(false); setGeneration(0);
        return true;
      }
      onBack();
      return true;
    });
    return () => sub.remove();
  }, [probe, onBack]);

  // One recovery: a new WebView instance, after a visible pause, up to the cap.
  const recover = useCallback((why: string) => {
    setRecoveries((n) => {
      if (n >= MAX_RECOVERIES) { setGaveUp(true); return n; }
      setStatus(`recovering (${why})`);
      setTimeout(() => {
        setGeneration((g) => g + 1);
        setLastBeatAt(null);
        setBeat(null);
        setStatus('starting');
      }, RECOVER_DELAY_MS);
      return n + 1;
    });
  }, []);

  const note = useCallback((message: string) => {
    setErrors((prev) => [`${new Date().toLocaleTimeString()} ${message}`, ...prev].slice(0, 6));
  }, []);

  // A view that is mounted and claims to have loaded, but whose page stopped
  // beating, is the failure a status field cannot see. Treated exactly like a
  // dead renderer.
  useEffect(() => {
    if (probe === null || gaveUp || lastBeatAt === null) return;
    if (now - lastBeatAt <= WEDGE_AFTER_MS) return;
    note('wedged — no heartbeat');
    recover('wedged');
  }, [now, lastBeatAt, probe, gaveUp, note, recover]);

  if (probe === null) {
    return (
      <View style={styles.menu}>
        <Text style={styles.title}>WebView spike</Text>
        <Text style={styles.note}>
          Technical probe only. No party token is used by either option.
        </Text>
        <View style={styles.options}>
          <FocusableButton
            label="Replica stage (offline soak)"
            onPress={() => { setProbe('replica'); setStatus('starting'); }}
            hasTVPreferredFocus
          />
          <FocusableButton
            label={`Live origin (${baseUrl || 'unset'})`}
            onPress={() => { setProbe('origin'); setStatus('starting'); }}
          />
          <FocusableButton
            label="Kill the renderer (memory stress)"
            onPress={() => { setProbe('stress'); setStatus('starting'); }}
          />
          <FocusableButton
            label="Unreachable host (error + recovery)"
            onPress={() => { setProbe('unreachable'); setStatus('starting'); }}
          />
          <FocusableButton label="Back" onPress={onBack} />
        </View>
        {errors.length > 0 && (
          <View style={styles.log}>
            {errors.map((line) => <Text key={line} style={styles.logLine}>{line}</Text>)}
          </View>
        )}
      </View>
    );
  }

  const silentFor = lastBeatAt === null ? null : Math.round((now - lastBeatAt) / 1000);
  // The failure signal the whole spike exists to catch: the view is mounted and
  // claims to have loaded, but nothing inside it is running any more.
  const wedged = (probe === 'replica' || probe === 'stress')
    && silentFor !== null && silentFor * 1_000 > WEDGE_AFTER_MS;

  return (
    <View style={styles.fill}>
      {/* `key` is the recovery mechanism: a new generation is a new WebView. */}
      {!gaveUp && <WebView
        key={generation}
        ref={webRef}
        style={styles.fill}
        source={
          probe === 'replica' ? { html: REPLICA }
            : probe === 'stress' ? { html: STRESS }
              : probe === 'unreachable'
                ? { uri: 'http://nubarca-spike-unreachable.invalid/' }
                : { uri: baseUrl }
        }
        // A stage is a display. Nothing here may be focused, scrolled or typed
        // into, which is also what keeps the D-pad entirely with the native
        // shell (probe 4).
        scrollEnabled={false}
        overScrollMode="never"
        focusable={false}
        // Fire TV panels: never let the page decide a smaller viewport.
        scalesPageToFit={false}
        mediaPlaybackRequiresUserAction={false}
        allowsInlineMediaPlayback
        androidLayerType="hardware"
        cacheEnabled
        onLoadStart={() => setStatus('loading')}
        onLoadEnd={() => { setStatus('loaded'); setLoads((n) => n + 1); }}
        onNavigationStateChange={(nav: WebViewNavigation) => setStatus(nav.loading ? 'loading' : 'loaded')}
        onError={(e) => { setStatus('error'); note(`error ${e.nativeEvent.description}`); }}
        onHttpError={(e) => note(`http ${e.nativeEvent.statusCode}`)}
        onRenderProcessGone={() => {
          setStatus('renderer-gone'); note('render process gone'); recover('renderer gone');
        }}
        onContentProcessDidTerminate={() => {
          setStatus('renderer-gone'); note('content process terminated'); recover('terminated');
        }}
        onMessage={(e) => {
          try {
            setBeat(JSON.parse(e.nativeEvent.data) as typeof beat);
            setLastBeatAt(Date.now());
          } catch { /* a malformed beat is itself a finding, logged by silence */ }
        }}
      />}

      {/* The native fallback. A2's whole failure story is that the shell
          survives its renderer and says something true, rather than leaving a
          black rectangle in somebody's living room. */}
      {gaveUp && (
        <View style={styles.menu}>
          <Text style={styles.title}>Renderer gave up</Text>
          <Text style={styles.note}>
            {MAX_RECOVERIES} automatic recoveries were not enough. The native shell is
            still running — this text is drawn by it. Press BACK.
          </Text>
        </View>
      )}

      {/* Native HUD, outside the WebView on purpose: it must survive the thing
          it is measuring. */}
      <View style={styles.hud} pointerEvents="none">
        <Text style={styles.hudLine}>
          {probe} · {status} · loads {loads} · recoveries {recoveries}/{MAX_RECOVERIES}
          {' '}· shell up {Math.round((now - mountedAt.current) / 1000)}s
        </Text>
        {beat && (
          <Text style={styles.hudLine}>
            page up {beat.up}s · scenes {beat.ticks} · frames {beat.frames}
            {beat.heap ? ` · heap ${Math.round(beat.heap / 1048576)}MB` : ''}
          </Text>
        )}
        {silentFor !== null && (
          <Text style={[styles.hudLine, wedged && styles.hudAlarm]}>
            last beat {silentFor}s ago{wedged ? '  ← WEDGED' : ''}
          </Text>
        )}
        {errors[0] && <Text style={styles.hudAlarm}>{errors[0]}</Text>}
        <Text style={styles.hudLine}>BACK = teardown → native shell</Text>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  fill: { flex: 1, backgroundColor: colors.bg },
  menu: {
    flex: 1, alignItems: 'center', justifyContent: 'center',
    backgroundColor: colors.bg, padding: spacing.xl, gap: spacing.lg,
  },
  title: { color: colors.text, fontSize: font.heading, fontWeight: '700' },
  note: { color: colors.muted, fontSize: font.body, textAlign: 'center', maxWidth: 720 },
  options: { gap: spacing.md, minWidth: 560 },
  log: { marginTop: spacing.lg, gap: 4 },
  logLine: { color: colors.danger, fontSize: font.caption },
  hud: {
    position: 'absolute', top: 0, left: 0, padding: spacing.sm,
    backgroundColor: 'rgba(0,0,0,0.55)', borderBottomRightRadius: 12,
  },
  hudLine: { color: colors.muted, fontSize: 16, fontVariant: ['tabular-nums'] },
  hudAlarm: { color: colors.danger, fontSize: 16, fontWeight: '700' },
});
