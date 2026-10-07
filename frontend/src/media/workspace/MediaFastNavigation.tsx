import { useCallback, useEffect, useRef, useState, type PointerEvent, type KeyboardEvent } from 'react';
import { ApiError, getMediaNavigation, type MediaNavigationBucket } from '@nubarca/api-client';
import { Sheet } from '../../components/Overlay';
import { useAppScrollViewport } from '../../components/appScroll';
import { useI18n } from '../../i18n';
import { isSemanticActive, queryFingerprint, queryToWire, type MediaWorkspaceIdentity } from './mediaWorkspaceQuery';
import { navigationIndexAt, navigationLabel } from './mediaNavigation';
import './MediaFastNavigation.css';

export function canFastNavigate(identity: MediaWorkspaceIdentity): boolean {
  return identity.sort !== 'size' && !isSemanticActive(identity) && !identity.filters.photo.similarTo;
}

interface Props {
  identity: MediaWorkspaceIdentity;
  revision: number;
  currentKey: string | null;
  busy: boolean;
  onJump(key: string): Promise<boolean>;
  onAuthError(): void;
}

export function MediaFastNavigation({ identity, revision, currentKey, busy, onJump, onAuthError }: Props) {
  const { t, tn, lang, formatNumber } = useI18n();
  const [buckets, setBuckets] = useState<MediaNavigationBucket[]>([]);
  const [indexError, setIndexError] = useState(false);
  const [reload, setReload] = useState(0);
  const [activeIndex, setActiveIndex] = useState<number | null>(null);
  const [feedback, setFeedback] = useState<'idle' | 'loading' | 'error'>('idle');
  const [choosing, setChoosing] = useState(false);
  const [choice, setChoice] = useState('');
  const [dragging, setDragging] = useState(false);
  const drag = useRef<number | null>(null);
  const pending = useRef(0);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const hideTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const request = useRef(0);
  const committed = useRef<string | null>(null);
  const rail = useRef<HTMLElement | null>(null);
  const dragGeometry = useRef<{ top: number; height: number } | null>(null);
  const viewportRef = useAppScrollViewport();
  const jump = useRef(onJump);
  jump.current = onJump;
  const fingerprint = queryFingerprint(identity);
  const enabled = canFastNavigate(identity);

  useEffect(() => {
    const node = rail.current;
    if (!node) return;
    const viewport = viewportRef?.current;
    let frame: number | null = null;
    const measure = () => {
      frame = null;
      if (drag.current !== null) return;
      const rect = node.getBoundingClientRect();
      const bottom = viewport ? viewport.getBoundingClientRect().bottom : window.innerHeight;
      const header = node.querySelector('button')?.getBoundingClientRect().height ?? 48;
      node.style.setProperty('--media-navigation-track-height', `${Math.max(80, bottom - rect.top - header - 24)}px`);
    };
    const schedule = () => { if (frame === null) frame = requestAnimationFrame(measure); };
    measure();
    const target = viewport ?? window;
    target.addEventListener('scroll', schedule, { passive: true });
    window.addEventListener('resize', schedule);
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(schedule);
    observer?.observe(node);
    return () => {
      target.removeEventListener('scroll', schedule);
      window.removeEventListener('resize', schedule);
      observer?.disconnect();
      if (frame !== null) cancelAnimationFrame(frame);
    };
  }, [viewportRef, buckets.length, dragging]);

  useEffect(() => {
    request.current += 1;
    committed.current = null;
    setBuckets([]);
    setIndexError(false);
    setActiveIndex(null);
    setFeedback('idle');
    setChoosing(false);
    setDragging(false);
    drag.current = null;
    if (!enabled) return;
    let disposed = false;
    const controller = new AbortController();
    const deadline = window.setTimeout(() => controller.abort(), 30_000);
    void getMediaNavigation(identity.source.kind === 'album' ? identity.source.albumId : null,
      queryToWire(identity, null), controller.signal)
      .then((data) => {
        if (!controller.signal.aborted) setBuckets(Array.isArray(data.buckets) ? data.buckets : []);
      })
      .catch((err: unknown) => {
        if (disposed) return;
        if (err instanceof ApiError && err.status === 401) { onAuthError(); return; }
        setIndexError(true);
      }).finally(() => window.clearTimeout(deadline));
    return () => {
      disposed = true;
      request.current += 1;
      controller.abort();
      window.clearTimeout(deadline);
      if (timer.current) clearTimeout(timer.current);
      if (hideTimer.current) clearTimeout(hideTimer.current);
    };
    // The canonical fingerprint includes the source and every active filter.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fingerprint, revision, reload, enabled]);

  const current = Math.max(0, buckets.findIndex((bucket) => bucket.key === currentKey));
  const selected = Math.min(buckets.length - 1, activeIndex ?? current);
  const selectedBucket = buckets[selected];

  const commit = useCallback(async (index: number, retry = false) => {
    const key = buckets[index]?.key;
    if (key === undefined || (!retry && committed.current === key)) return;
    committed.current = key;
    const id = ++request.current;
    setActiveIndex(index);
    setFeedback('loading');
    if (hideTimer.current) clearTimeout(hideTimer.current);
    const ok = await jump.current(key);
    if (id !== request.current) return;
    setFeedback(ok ? 'idle' : 'error');
    if (ok) hideTimer.current = setTimeout(() => {
      if (drag.current === null) setActiveIndex(null);
    }, 1200);
  }, [buckets]);

  function stage(event: PointerEvent<HTMLDivElement>) {
    const rect = dragGeometry.current ?? event.currentTarget.getBoundingClientRect();
    const inset = Number.parseFloat(getComputedStyle(document.documentElement).fontSize) || 16;
    pending.current = navigationIndexAt(event.clientY, rect.top + inset, rect.height - inset * 2, buckets.length);
    setActiveIndex(pending.current);
    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(() => { void commit(pending.current); }, 180);
  }

  function keyboard(event: KeyboardEvent<HTMLDivElement>) {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault(); event.stopPropagation(); openChooser(); return;
    }
    const steps: Record<string, number> = { ArrowDown: 1, ArrowUp: -1, PageDown: 5, PageUp: -5 };
    const index = event.key === 'Home' ? 0 : event.key === 'End' ? buckets.length - 1
      : event.key in steps ? Math.max(0, Math.min(buckets.length - 1, selected + steps[event.key])) : null;
    if (index !== null) {
      event.preventDefault(); event.stopPropagation();
      committed.current = null;
      void commit(index);
    }
  }

  function openChooser() { setChoice(selectedBucket?.key ?? buckets[0]?.key ?? ''); setChoosing(true); }

  if (!enabled || (buckets.length < 2 && !indexError)) return null;
  if (indexError) return <aside className="media-fast-nav"><button type="button" className="media-fast-nav__retry"
    onClick={() => setReload((v) => v + 1)} aria-label={t('mediaNav.indexRetry')}>↻</button></aside>;

  const labels = [...new Set([0, Math.floor((buckets.length - 1) / 2), buckets.length - 1])];
  const years = [...new Set(buckets.map((bucket) => bucket.key.slice(0, 4)))];
  const dateMode = identity.sort !== 'name';
  const choiceYear = choice.slice(0, 4);

  return (
    <aside ref={rail} className="media-fast-nav" data-dragging={dragging} aria-label={t('mediaNav.label')}>
      <button type="button" className="media-fast-nav__choose" onClick={openChooser}
        aria-label={t('mediaNav.goTo')} title={t('mediaNav.goTo')}>
        {dateMode ? <><span>{selectedBucket.key.slice(0, 4)}</span><span>{navigationLabel(selectedBucket.key, lang, true).replace(selectedBucket.key.slice(0, 4), '').trim()}</span></>
          : navigationLabel(selectedBucket.key, lang)}
      </button>
      <div className="media-fast-nav__track" role="slider" tabIndex={0}
        aria-label={t(dateMode ? 'mediaNav.timeline' : 'mediaNav.alphabet')}
        aria-orientation="vertical" aria-valuemin={0} aria-valuemax={buckets.length - 1}
        aria-valuenow={selected} aria-valuetext={navigationLabel(selectedBucket.key, lang)}
        onKeyDown={keyboard}
        onPointerDown={(event) => {
          if (!event.isPrimary || event.button !== 0) return;
          event.preventDefault();
          event.currentTarget.focus();
          drag.current = event.pointerId;
          dragGeometry.current = event.currentTarget.getBoundingClientRect();
          setDragging(true);
          committed.current = null;
          event.currentTarget.setPointerCapture(event.pointerId);
          stage(event);
        }}
        onPointerMove={(event) => { if (drag.current === event.pointerId) stage(event); }}
        onPointerUp={(event) => {
          if (drag.current !== event.pointerId) return;
          if (timer.current) clearTimeout(timer.current);
          drag.current = null; setDragging(false);
          dragGeometry.current = null;
          event.currentTarget.releasePointerCapture(event.pointerId);
          void commit(pending.current);
        }}
        onPointerCancel={() => {
          if (timer.current) clearTimeout(timer.current);
          drag.current = null; setDragging(false); setActiveIndex(null);
          dragGeometry.current = null;
        }}>
        <span className="media-fast-nav__line" aria-hidden="true" />
        {labels.map((index) => <span className="media-fast-nav__tick" key={index}
          style={{ top: `calc(1rem + (100% - 2rem) * ${index / (buckets.length - 1)})` }} aria-hidden="true">
          {dateMode && years.length > 2 ? buckets[index].key.slice(0, 4)
            : dateMode ? navigationLabel(buckets[index].key, lang, true).replace(buckets[index].key.slice(0, 4), '').trim()
              : navigationLabel(buckets[index].key, lang)}
        </span>)}
        <span className="media-fast-nav__thumb" aria-hidden="true"
          style={{ top: `calc(1rem + (100% - 2rem) * ${selected / (buckets.length - 1)})` }} />
        {(activeIndex !== null || busy || feedback === 'error') && <div className="media-fast-nav__bubble"
          style={{ top: `calc(1rem + (100% - 2rem) * ${selected / (buckets.length - 1)})` }}>
          <button type="button" className="media-fast-nav__bubble-label"
            onPointerDown={(event) => event.stopPropagation()} onClick={openChooser}>
            <strong>{navigationLabel(selectedBucket.key, lang)}</strong>
          </button>
          <span>{tn(selectedBucket.count, 'mediaNav.items', { n: formatNumber(selectedBucket.count) })}</span>
          <span role="status">{feedback === 'error' ? t('mediaNav.failed') : feedback === 'loading' ? t('mediaNav.loading') : ''}</span>
          {feedback === 'error' && <button type="button" onPointerDown={(event) => event.stopPropagation()}
            onClick={() => { void commit(selected, true); }}>{t('common.tryAgain')}</button>}
        </div>}
      </div>
      {choosing && <Sheet title={t('mediaNav.goTo')} onClose={() => setChoosing(false)} layer="workspace" ownsKeyboard
        className="media-navigation-sheet" testId="media-navigation-sheet">
        <form className="media-fast-nav__form" onSubmit={(event) => {
          event.preventDefault(); setChoosing(false); committed.current = null;
          void commit(buckets.findIndex((bucket) => bucket.key === choice));
        }}>
          {dateMode && <label>{t('mediaNav.year')}<select value={choiceYear}
            onChange={(event) => setChoice(buckets.find((bucket) => bucket.key.startsWith(event.target.value + '-'))!.key)}>
            {years.map((year) => <option value={year} key={year}>{year}</option>)}
          </select></label>}
          <label>{t(dateMode ? 'mediaNav.month' : 'mediaNav.letter')}<select value={choice} onChange={(event) => setChoice(event.target.value)}>
            {buckets.filter((bucket) => !dateMode || bucket.key.startsWith(choiceYear + '-')).map((bucket) =>
              <option key={bucket.key} value={bucket.key}>{navigationLabel(bucket.key, lang)} · {formatNumber(bucket.count)}</option>)}
          </select></label>
          <button type="submit" className="row-action-primary">{t('mediaNav.go')}</button>
        </form>
      </Sheet>}
    </aside>
  );
}
